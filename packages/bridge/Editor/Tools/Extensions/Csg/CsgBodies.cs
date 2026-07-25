#nullable enable
using System.Globalization;

namespace GodotOpenMcp.Bridge.Editor
{
    // ===========================================================================
    // P12.5 CSG pack request bodies.
    //
    // Each tool that needs structured extraction gets a tiny sealed body type. They
    // mirror the hand-rolled `IndexOf`-substring style already used by the P12.1
    // tilemap bodies, the P12.2 navigation bodies, the P12.3 particles bodies, and
    // the P12.4 animation bodies (see packages/bridge/AGENTS.md §Transport: the
    // bridge deliberately carries no typed JSON DOM dependency on the hot path).
    // Pure-managed (no Godot API surface, no `#if TOOLS`), so the parsing logic is
    // unit-testable in the binary-less xUnit host.
    //
    // The shared extraction primitives live in the P12.1 `JsonScalar` static class
    // (GodotOpenMcp.Bridge.Editor namespace, sibling file). P12.5 is the "fifth
    // family" — it reuses ExtractString / ExtractFloat / ExtractIntOrNull /
    // ExtractBool.
    //
    // Kind + Operation handling: every create / defaults tool accepts a `kind`
    // string ("box" | "sphere" | "cylinder" | "combiner"). set_operation accepts
    // an `operation` string ("union" | "intersection" | "subtraction"). The body
    // parsers record the raw values; the handlers normalize via
    // <see cref="CsgKindParser"/> / <see cref="CsgOperationParser"/> — Unknown when
    // absent or not one of the valid tokens, surfaced as `invalid_parameter` by
    // the handler.
    //
    // Clamping: the CSG pack's scalar allow-list is fixed per kind (box: size;
    // sphere: radius / radial_segments / rings / smooth_faces; cylinder: radius /
    // height / sides / cone / smooth_faces). The body parsers record raw values;
    // clamping is centralized in <see cref="CsgPropertyClamp"/> so the clamp table
    // is unit-testable without the editor (mirrors the P12.3 particles pack's
    // design decision §2). The handler echoes the clamped result.
    // ===========================================================================

    /// <summary>
    /// Normalized kind token extracted from a request body. <see cref="Unknown"/>
    /// covers both "absent" and "not a valid token"; the handler turns that into
    /// <c>invalid_parameter</c> so an agent cannot create a half-resolved primitive.
    /// </summary>
    internal enum CsgKind
    {
        Unknown = 0,
        Box = 1,
        Sphere = 2,
        Cylinder = 3,
        Combiner = 4,
    }

    /// <summary>
    /// Map a raw <c>kind</c> string ("box" | "sphere" | "cylinder" | "combiner") to a
    /// <see cref="CsgKind"/>. Returns <see cref="CsgKind.Unknown"/> for null / empty /
    /// unrecognized tokens so the handler can surface a single <c>invalid_parameter</c>
    /// error. Case-insensitive to tolerate an agent sending "Box" / "SPHERE".
    /// </summary>
    internal static class CsgKindParser
    {
        internal static CsgKind Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return CsgKind.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "box": return CsgKind.Box;
                case "sphere": return CsgKind.Sphere;
                case "cylinder": return CsgKind.Cylinder;
                case "combiner": return CsgKind.Combiner;
                default: return CsgKind.Unknown;
            }
        }
    }

    /// <summary>
    /// Normalized boolean-operation token extracted from a request body. The string
    /// values mirror the MCP schema enum (union / intersection / subtraction) and the
    /// camelCase shape an agent re-reads via `csg_get`. <see cref="Unknown"/> covers
    /// both "absent" and "not a valid token" — the handler surfaces
    /// <c>invalid_parameter</c>. The mapping to Godot's <c>CsgShape3D.OperationEnum</c>
    /// lives in the editor-only handler (the parser is pure-managed and cannot
    /// reference the Godot enum without dragging the editor dependency in).
    /// </summary>
    internal enum CsgOperation
    {
        Unknown = 0,
        Union = 1,
        Intersection = 2,
        Subtraction = 3,
    }

    /// <summary>
    /// Map a raw <c>operation</c> string ("union" | "intersection" | "subtraction") to
    /// a <see cref="CsgOperation"/>. Returns <see cref="CsgOperation.Unknown"/> for
    /// null / empty / unrecognized tokens. Case-insensitive.
    /// </summary>
    internal static class CsgOperationParser
    {
        internal static CsgOperation Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return CsgOperation.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "union": return CsgOperation.Union;
                case "intersection": return CsgOperation.Intersection;
                case "subtraction": return CsgOperation.Subtraction;
                default: return CsgOperation.Unknown;
            }
        }

        /// <summary>
        /// Render a <see cref="CsgOperation"/> back to its MCP schema string. Used by
        /// the read paths (<c>defaults</c> / <c>get</c>) so the JSON keys an agent
        /// reads round-trip into <c>set_operation</c> / create <c>operation</c>.
        /// Returns <c>"union"</c> for <see cref="CsgOperation.Unknown"/> so a never-set
        /// shape still reports a valid default (Godot's own default Operation is Union).
        /// </summary>
        internal static string ToSchemaString(CsgOperation op)
        {
            switch (op)
            {
                case CsgOperation.Intersection: return "intersection";
                case CsgOperation.Subtraction: return "subtraction";
                case CsgOperation.Union:
                default: return "union";
            }
        }
    }

    /// <summary>
    /// Centralized clamp table for the CSG scalar allow-list (mirrors the P12.3
    /// particles pack's design decision §2). Every scalar the create handlers
    /// accept is clamped here so the table is unit-testable without the editor.
    /// Each <c>Clamp</c> overload returns the clamped value; the handler echoes the
    /// clamped result so an agent can see what landed.
    ///
    /// <para>
    /// The valid ranges mirror Godot's inspector bounds for the CSG primitives:
    /// <list type="bullet">
    /// <item><c>size</c> components (box): float ≥ <see cref="MinPositive"/>. A
    /// zero-size face is degenerate; the engine default box is (1, 1, 1).</item>
    /// <item><c>radius</c> (sphere / cylinder): float ≥ <see cref="MinPositive"/>.
    /// Must be strictly positive — a zero-radius primitive is invisible. Engine
    /// default is 0.5.</item>
    /// <item><c>height</c> (cylinder): float ≥ <see cref="MinPositive"/>. Strictly
    /// positive — a zero-height cylinder is degenerate. Engine default is 2.0.</item>
    /// <item><c>radial_segments</c> (sphere): int in [3, <see cref="MaxSegments"/>].
    /// Godot's editor rejects &lt; 3 (a sphere needs at least three radial
    /// segments); the upper bound caps absurd values that would tank performance
    /// without being restrictive. Engine default is 12.</item>
    /// <item><c>rings</c> (sphere): int in [3, <see cref="MaxSegments"/>]. Same
    /// rationale. Engine default is 6.</item>
    /// <item><c>sides</c> (cylinder): int in [3, <see cref="MaxSegments"/>]. Godot
    /// rejects &lt; 3; a higher value is smoother. Engine default is 8.</item>
    /// <item><c>smooth_faces</c> / <c>cone</c>: bool, no clamping (passed through).
    /// </item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class CsgPropertyClamp
    {
        /// <summary>Strictly-positive floor for size components / radius / height.
        /// Matches the Godot inspector behavior — exactly 0 is rejected by the engine
        /// as invalid (a zero-extent primitive has no volume and renders nothing).
        /// </summary>
        internal const float MinPositive = 0.0001f;

        /// <summary>Generous ceiling for segment / ring / side counts. A typo (e.g.
        /// radial_segments: 100000) would freeze the editor; the engine has no
        /// hard-coded upper bound, so this is a pack-level guardrail. The engine
        /// defaults (12 / 6 / 8) are well below it.</summary>
        internal const int MaxSegments = 1000;

        internal static float ClampPositive(float v) => v < MinPositive ? MinPositive : v;

        internal static int ClampSegmentCount(int v) => v < 3 ? 3 : (v > MaxSegments ? MaxSegments : v);
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_csg_defaults</c> (P12.5). A pure
    /// helper — the handler returns recommended starter scalars for the requested
    /// kind. Only <c>kind</c> is read.
    /// </summary>
    internal sealed class CsgDefaultsBody
    {
        internal CsgKind Kind { get; private set; }

        internal static CsgDefaultsBody Parse(string? body)
        {
            var parsed = new CsgDefaultsBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Kind = CsgKindParser.Parse(JsonScalar.ExtractString(body, "kind"));
            return parsed;
        }

        CsgDefaultsBody() { }
    }

    /// <summary>
    /// Parsed request body shared by the four create tools (<c>godot_open_mcp_csg_box_create</c>
    /// / <c>sphere_create</c> / <c>cylinder_create</c> / <c>combiner_create</c>). Mirrors
    /// <c>node_create</c> for the shape an agent already knows (name / parent_node_path /
    /// position) plus the optional <c>operation</c> (defaults to <c>union</c> in the handler
    /// when absent — Godot's own default Operation is Union).
    ///
    /// <para>
    /// Kind-specific scalars are extracted best-effort from the same body: the box handler
    /// reads <c>size</c> as an "x,y,z" string; the sphere handler reads <c>radius</c> /
    /// <c>radial_segments</c> / <c>rings</c> / <c>smooth_faces</c>; the cylinder handler reads
    /// <c>radius</c> / <c>height</c> / <c>sides</c> / <c>cone</c> / <c>smooth_faces</c>; the
    /// combiner handler reads none beyond the common set. The shared type exists so the four
    /// handlers share one parent-resolution / owner / dirty / serialization path; the per-kind
    /// scalars are read by the handler that owns the relevant primitive class.
    /// </para>
    /// </summary>
    internal sealed class CsgCreateBody
    {
        internal string? Name { get; private set; }
        internal string? ParentNodePath { get; private set; }
        internal string? Position { get; private set; }
        internal CsgOperation Operation { get; private set; }

        // Kind-specific scalars. Each is nullable — null means "use the engine default" (the
        // handler does not write the property when null). The body parser records the raw
        // value; clamping is the handler's job via <see cref="CsgPropertyClamp"/> so the
        // clamped result can be echoed.
        internal string? Size { get; private set; }              // box: "x,y,z"
        internal float? Radius { get; private set; }             // sphere / cylinder
        internal float? Height { get; private set; }             // cylinder
        internal int? RadialSegments { get; private set; }       // sphere
        internal int? Rings { get; private set; }                // sphere
        internal int? Sides { get; private set; }                // cylinder
        internal bool? SmoothFaces { get; private set; }         // sphere / cylinder
        internal bool? Cone { get; private set; }                // cylinder

        /// <summary>True when the agent sent an explicit <c>operation</c> key. The handler
        /// uses this to decide whether to apply the operation at create time (Godot's default
        /// Operation on a fresh shape is Union, so a missing key means "leave the engine
        /// default" rather than "force union").</summary>
        internal bool HasOperation => Operation != CsgOperation.Unknown;

        internal static CsgCreateBody Parse(string? body)
        {
            var parsed = new CsgCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Name = JsonScalar.ExtractString(body, "name");
            parsed.ParentNodePath = JsonScalar.ExtractString(body, "parent_node_path");
            parsed.Position = JsonScalar.ExtractString(body, "position");
            parsed.Operation = CsgOperationParser.Parse(JsonScalar.ExtractString(body, "operation"));
            parsed.Size = JsonScalar.ExtractString(body, "size");
            parsed.Radius = JsonScalar.ExtractFloat(body, "radius");
            parsed.Height = JsonScalar.ExtractFloat(body, "height");
            parsed.RadialSegments = JsonScalar.ExtractIntOrNull(body, "radial_segments");
            parsed.Rings = JsonScalar.ExtractIntOrNull(body, "rings");
            parsed.Sides = JsonScalar.ExtractIntOrNull(body, "sides");
            parsed.SmoothFaces = JsonScalar.ExtractBool(body, "smooth_faces");
            parsed.Cone = JsonScalar.ExtractBool(body, "cone");
            return parsed;
        }

        CsgCreateBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_csg_set_operation</c> (P12.5). Carries
    /// the CSG shape target (node_path) and the <c>operation</c> token. The handler
    /// resolves the node, type-checks against <c>CsgShape3D</c> (so any CSG primitive
    /// or combiner is accepted), and writes the operation via Godot's
    /// <c>OperationEnum</c>.
    /// </summary>
    internal sealed class CsgSetOperationBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal CsgOperation Operation { get; private set; }
        internal bool HasOperation => Operation != CsgOperation.Unknown;

        internal static CsgSetOperationBody Parse(string? body)
        {
            var parsed = new CsgSetOperationBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.Operation = CsgOperationParser.Parse(JsonScalar.ExtractString(body, "operation"));
            return parsed;
        }

        CsgSetOperationBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_csg_get</c> (P12.5). Read-only — carries
    /// only the CSG shape target (node_path). Kind is inferred from the resolved node's
    /// class in the handler.
    /// </summary>
    internal sealed class CsgGetBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal static CsgGetBody Parse(string? body)
        {
            var parsed = new CsgGetBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            return parsed;
        }

        CsgGetBody() { }
    }
}
