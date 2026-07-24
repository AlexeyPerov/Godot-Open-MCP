#nullable enable
using System.Globalization;

namespace GodotOpenMcp.Bridge.Editor
{
    // ===========================================================================
    // P12.3 particles pack request bodies.
    //
    // Each tool that needs structured extraction gets a tiny sealed body type. They
    // mirror the hand-rolled `IndexOf`-substring style already used by the P12.1
    // tilemap bodies and the P12.2 navigation bodies (see
    // packages/bridge/AGENTS.md §Transport: the bridge deliberately carries no
    // typed JSON DOM dependency on the hot path). Pure-managed (no Godot API
    // surface, no `#if TOOLS`), so the parsing logic is unit-testable in the
    // binary-less xUnit host.
    //
    // The shared extraction primitives live in the P12.1 `JsonScalar` static class
    // (GodotOpenMcp.Bridge.Editor namespace, sibling file). P12.3 is the "fourth
    // family" — it reuses ExtractString / ExtractFloat / ExtractBool and the
    // P12.3-added ExtractIntOrNull for the particles pack's nullable int scalars.
    //
    // Dimension handling: every create / defaults tool accepts a `dimension` string
    // ("2d" | "3d"). The body parser records the raw value; the handler normalizes
    // via <see cref="ParticlesDimensionParser"/> — Unknown when absent or not one
    // of the two valid tokens, surfaced as `invalid_parameter` by the handler.
    //
    // Clamping: the particles pack's scalar allow-list is fixed (amount / lifetime /
    // one_shot / preprocess / speed_scale / explosiveness / randomness / fixed_fps /
    // interpolate / fract_delta / local_coords). The body parsers record raw values;
    // clamping is centralized in <see cref="ParticlesPropertyClamp"/> so the clamp
    // table is unit-testable without the editor (the design decision §2 in the P12.3
    // plan). The handler echoes the clamped result.
    // ===========================================================================

    /// <summary>
    /// Normalized dimension token extracted from a request body. <see cref="Unknown"/>
    /// covers both "absent" and "not a valid token"; the handler turns that into
    /// <c>invalid_parameter</c> so an agent cannot create a half-resolved emitter.
    /// </summary>
    internal enum ParticlesDimension
    {
        Unknown = 0,
        TwoD = 2,
        ThreeD = 3,
    }

    /// <summary>
    /// Map a raw <c>dimension</c> string ("2d" | "3d") to a <see cref="ParticlesDimension"/>.
    /// Returns <see cref="ParticlesDimension.Unknown"/> for null / empty / unrecognized tokens so
    /// the handler can surface a single <c>invalid_parameter</c> error. Case-insensitive to
    /// tolerate an agent sending "2D" / "3D".
    /// </summary>
    internal static class ParticlesDimensionParser
    {
        internal static ParticlesDimension Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return ParticlesDimension.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "2d": return ParticlesDimension.TwoD;
                case "3d": return ParticlesDimension.ThreeD;
                default: return ParticlesDimension.Unknown;
            }
        }
    }

    /// <summary>
    /// Centralized clamp table for the particles scalar allow-list (P12.3 design decision §2).
    /// Every scalar the configure handler accepts is clamped here so the table is unit-testable
    /// without the editor. Each <c>Apply</c> overload returns the clamped value; the handler
    /// echoes the clamped result so an agent can see what landed.
    ///
    /// <para>
    /// The valid ranges mirror Godot's inspector bounds for GpuParticles2D/3D:
    /// <list type="bullet">
    /// <item><c>amount</c>: int in [1, <see cref="MaxAmount"/>]. Godot enforces ≥ 1; the upper
    /// bound caps absurd values that would tank performance (the engine default is 8 / 16).</item>
    /// <item><c>lifetime</c>: float in [<see cref="MinPositive"/>, +inf). Must be strictly
    /// positive — a zero/negative lifetime is meaningless (particles would never live).</item>
    /// <item><c>preprocess</c>: float ≥ 0. Preprocess is a duration; zero means "start fresh".</item>
    /// <item><c>speed_scale</c>: float ≥ 0. A negative speed scale would reverse time — clamped to 0.</item>
    /// <item><c>explosiveness</c> / <c>randomness</c>: float in [0, 1]. Godot's editor clamps these
    /// to the unit range; they are ratios.</item>
    /// <item><c>fixed_fps</c>: int ≥ 0. 0 means "use the render frame rate".</item>
    /// <item><c>one_shot</c> / <c>interpolate</c> / <c>fract_delta</c> / <c>local_coords</c>:
    /// bool, no clamping (passed through).</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class ParticlesPropertyClamp
    {
        /// <summary>Godot's per-node particle cap for the amount property. A generous ceiling that
        /// keeps a typo (e.g. amount: 1000000) from freezing the editor without being restrictive
        /// for legitimate dense effects. The engine has no hard-coded upper bound; this is a
        /// pack-level guardrail.</summary>
        internal const int MaxAmount = 100000;

        /// <summary>Strictly-positive floor for lifetime. Matches the Godot inspector behavior —
        /// a lifetime of exactly 0 is rejected by the engine as invalid.</summary>
        internal const float MinPositive = 0.0001f;

        internal static int ClampAmount(int v) => v < 1 ? 1 : (v > MaxAmount ? MaxAmount : v);

        internal static float ClampLifetime(float v) => v < MinPositive ? MinPositive : v;

        internal static float ClampNonNegative(float v) => v < 0f ? 0f : v;

        internal static float ClampUnit(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }

        internal static int ClampFixedFps(int v) => v < 0 ? 0 : v;
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_particles_defaults</c> (P12.3). A pure helper —
    /// the handler returns recommended starter scalars for the requested dimension. Only
    /// <c>dimension</c> is read.
    /// </summary>
    internal sealed class ParticlesDefaultsBody
    {
        internal ParticlesDimension Dimension { get; private set; }

        internal static ParticlesDefaultsBody Parse(string? body)
        {
            var parsed = new ParticlesDefaultsBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Dimension = ParticlesDimensionParser.Parse(JsonScalar.ExtractString(body, "dimension"));
            return parsed;
        }

        ParticlesDefaultsBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_particles_create</c> (P12.3). Mirrors
    /// <c>node_create</c> for the shape an agent already knows (name / parent_node_path /
    /// position), plus the <c>dimension</c> that selects GpuParticles2D vs GpuParticles3D, an
    /// optional <c>process_material_path</c> (res:// to a ParticleProcessMaterial), and the
    /// initial scalar <c>properties</c> object the handler applies post-creation (same allow-list
    /// as configure — the body parser does not recurse into the object; the handler re-extracts
    /// each scalar from the raw body string with the same JsonScalar extractors).
    /// </summary>
    internal sealed class ParticlesCreateBody
    {
        internal ParticlesDimension Dimension { get; private set; }
        internal string? Name { get; private set; }
        internal string? ParentNodePath { get; private set; }
        internal string? Position { get; private set; }
        internal string? ProcessMaterialPath { get; private set; }

        internal bool HasProcessMaterialPath => !string.IsNullOrEmpty(ProcessMaterialPath);

        internal static ParticlesCreateBody Parse(string? body)
        {
            var parsed = new ParticlesCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Dimension = ParticlesDimensionParser.Parse(JsonScalar.ExtractString(body, "dimension"));
            parsed.Name = JsonScalar.ExtractString(body, "name");
            parsed.ParentNodePath = JsonScalar.ExtractString(body, "parent_node_path");
            parsed.Position = JsonScalar.ExtractString(body, "position");
            parsed.ProcessMaterialPath = JsonScalar.ExtractString(body, "process_material_path");
            // NOTE: the initial `properties` object is intentionally NOT parsed here. The handler
            // re-extracts each scalar from the raw body via JsonScalar.ExtractIntOrNull /
            // ExtractFloat / ExtractBool, which honor nested-key lookup (the shared
            // ExtractRawValue scans for `"key"` anywhere in the body string, so a key inside the
            // properties object resolves). This avoids a recursive object parse for one optional
            // field while keeping the configure/create scalar allow-list identical.
            return parsed;
        }

        ParticlesCreateBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_particles_configure</c> (P12.3). Carries the
    /// emitter target (node_path) plus the clamped scalar properties the handler applies. The
    /// known scalar allow-list is fixed (amount / lifetime / one_shot / preprocess / speed_scale /
    /// explosiveness / randomness / fixed_fps / interpolate / fract_delta / local_coords); the
    /// schema declares <c>additionalProperties:false</c> so a conforming client never sends an
    /// unknown key. Each scalar is extracted individually rather than via a free-form map so the
    /// handler can clamp each one to its valid range.
    ///
    /// <para>
    /// Nullable extractors (<see cref="JsonScalar.ExtractIntOrNull"/> / ExtractFloat /
    /// ExtractBool) return null when the key is absent — the handler treats null as "leave
    /// unchanged" and applies only the keys the agent sent. A present-but-invalid value
    /// (non-numeric) also returns null and is silently skipped; the handler does NOT error on a
    /// single bad scalar, matching <c>node_modify</c>'s non-aborting contract.
    /// </para>
    /// </summary>
    internal sealed class ParticlesConfigureBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        // Int scalars (nullable — null = leave unchanged).
        internal int? Amount { get; private set; }
        internal int? FixedFps { get; private set; }

        // Float scalars (nullable — null = leave unchanged).
        internal float? Lifetime { get; private set; }
        internal float? Preprocess { get; private set; }
        internal float? SpeedScale { get; private set; }
        internal float? Explosiveness { get; private set; }
        internal float? Randomness { get; private set; }

        // Bool scalars (nullable — null = leave unchanged).
        internal bool? OneShot { get; private set; }
        internal bool? Interpolate { get; private set; }
        internal bool? FractDelta { get; private set; }
        internal bool? LocalCoords { get; private set; }

        internal static ParticlesConfigureBody Parse(string? body)
        {
            var parsed = new ParticlesConfigureBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.Amount = JsonScalar.ExtractIntOrNull(body, "amount");
            parsed.FixedFps = JsonScalar.ExtractIntOrNull(body, "fixed_fps");
            parsed.Lifetime = JsonScalar.ExtractFloat(body, "lifetime");
            parsed.Preprocess = JsonScalar.ExtractFloat(body, "preprocess");
            parsed.SpeedScale = JsonScalar.ExtractFloat(body, "speed_scale");
            parsed.Explosiveness = JsonScalar.ExtractFloat(body, "explosiveness");
            parsed.Randomness = JsonScalar.ExtractFloat(body, "randomness");
            parsed.OneShot = JsonScalar.ExtractBool(body, "one_shot");
            parsed.Interpolate = JsonScalar.ExtractBool(body, "interpolate");
            parsed.FractDelta = JsonScalar.ExtractBool(body, "fract_delta");
            parsed.LocalCoords = JsonScalar.ExtractBool(body, "local_coords");
            return parsed;
        }

        ParticlesConfigureBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_particles_set_emitting</c> (P12.3). Carries the
    /// emitter target (node_path), the <c>emitting</c> bool, and the optional <c>restart</c> flag
    /// (when true the handler calls Godot's <c>Restart()</c> before flipping emitting, clearing
    /// existing particles and restarting the emission cycle).
    /// </summary>
    internal sealed class ParticlesSetEmittingBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal bool? Emitting { get; private set; }
        internal bool Restart { get; private set; }

        internal static ParticlesSetEmittingBody Parse(string? body)
        {
            var parsed = new ParticlesSetEmittingBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.Emitting = JsonScalar.ExtractBool(body, "emitting");
            // restart defaults to false when absent — unlike the nullable scalars, this is a plain
            // flag, so a missing key means "no restart". ExtractBool returns null for absent; coerce.
            var restart = JsonScalar.ExtractBool(body, "restart");
            parsed.Restart = restart ?? false;
            return parsed;
        }

        ParticlesSetEmittingBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_particles_get</c> (P12.3). Read-only — carries
    /// only the emitter target (node_path). Dimension is inferred from the resolved node's class
    /// in the handler.
    /// </summary>
    internal sealed class ParticlesGetBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal static ParticlesGetBody Parse(string? body)
        {
            var parsed = new ParticlesGetBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            return parsed;
        }

        ParticlesGetBody() { }
    }
}
