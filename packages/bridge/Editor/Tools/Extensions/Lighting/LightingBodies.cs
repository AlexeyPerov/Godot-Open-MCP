#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    // ===========================================================================
    // P16.3 lighting pack request bodies + light-kind / environment catalogs.
    //
    // Four tools land in this pack:
    //   - godot_open_mcp_light_create (mutating, gated) — create a directional /
    //     omni / spot Light3D or a DirectionalLight2D / PointLight2D in the edited
    //     scene, with starter scalars applied at create time.
    //   - godot_open_mcp_light_set (mutating, gated) — patch one light scalar
    //     (color / energy / range / attenuation / shadows / spot angle) on an
    //     existing light node, with central clamping + validation.
    //   - godot_open_mcp_light_modify (mutating, gated) — bulk patch multiple
    //     light scalars in one call (an alias of light_set for the multi-field
    //     case; same validation + clamping).
    //   - godot_open_mcp_environment_set (mutating, gated) — create or replace the
    //     WorldEnvironment node's Environment resource (sky / fog / tonemap /
    //     ambient), addressed by a res:// Environment .tres path.
    //
    // The body types mirror the hand-rolled IndexOf-substring style already used by
    // the P12.x domain packs and the P16.1 / P16.2 packs (see packages/bridge/
    // AGENTS.md §Transport: the bridge deliberately carries no typed JSON DOM
    // dependency on the hot path). Pure-managed (no Godot API surface, no `#if
    // TOOLS`), so the parsing logic is unit-testable in the binary-less xUnit host.
    //
    // The shared extraction primitives live in the P12.1 `JsonScalar` static class
    // (GodotOpenMcp.Bridge.Editor namespace, sibling file
    // Extensions/Tilemap/TilemapBodies.cs). P16.3 reuses ExtractString /
    // ExtractFloat / ExtractBool.
    //
    // Light-kind catalog: the six light node families the plan names are
    // creatable — DirectionalLight3D (directional3d), OmniLight3D (omni3d),
    // SpotLight3D (spot3d), DirectionalLight2D (directional2d), PointLight2D
    // (point2d). The catalog maps the kind token to the Godot class name the
    // editor-only handler instantiates via `new T()`. Centralized here so the
    // vocabulary is unit-testable without the editor (mirrors the P16.2
    // materials pack's kind-catalog design decision).
    //
    // Fidelity: adapt — Unity Open MCP's LightingTools (light_add / light_set /
    // light_modify shape) supplies the tool roster. The deltas are:
    // (1) Godot lights are Node3D / Node2D subclasses, not Unity Light components
    // attached to a GameObject — create makes a node, not a component add;
    // (2) Godot's `Light3D` enum (Param.spot_angle / Param.range / Param.energy /
    // Param.attenuation) replaces Unity's flat Light.range / intensity / spotAngle
    // fields — clamping uses Godot's documented bounds;
    // (3) Unity render_mode / cullingMask are NOT ported (Godot lights have no
    // per-light render-mode or layer-culling analog in the same form —
    // light_cull_mask is a separate property on Light3D an agent can set via
    // node_modify);
    // (4) WorldEnvironment is a Godot-specific resource (no Unity equivalent —
    // Unity's RenderSettings.skybox / ambient maps to Godot's Environment.sky /
    // Environment.ambient_light, but the resource model is Godot's own);
    // (5) Unity baked-lighting / lightmap / ReflectionProbe APIs are intentionally
    // NOT ported (the plan's skip fidelity tag — Godot lighting model differs).
    // ===========================================================================

    /// <summary>
    /// Normalized light-kind token extracted from a <c>light_create</c> request
    /// body. <see cref="Unknown"/> covers both "absent" and "not a valid token";
    /// the handler turns that into <c>invalid_parameter</c>. The five values map
    /// to the five light node families the plan names.
    /// </summary>
    internal enum LightKind
    {
        Unknown = 0,
        Directional3D = 1,   // DirectionalLight3D
        Omni3D = 2,          // OmniLight3D
        Spot3D = 3,          // SpotLight3D
        Directional2D = 4,   // DirectionalLight2D
        Point2D = 5,         // PointLight2D
    }

    /// <summary>
    /// Map a raw <c>kind</c> string ("directional3d" | "omni3d" | "spot3d" |
    /// "directional2d" | "point2d") to a <see cref="LightKind"/>. Returns
    /// <see cref="LightKind.Unknown"/> for null / empty / unrecognized tokens so
    /// the handler can surface a single <c>invalid_parameter</c> error.
    /// Case-insensitive to tolerate an agent sending "Directional3D" / "OMNI3D".
    /// </summary>
    internal static class LightKindParser
    {
        internal static LightKind Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return LightKind.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "directional3d": return LightKind.Directional3D;
                case "omni3d": return LightKind.Omni3D;
                case "spot3d": return LightKind.Spot3D;
                case "directional2d": return LightKind.Directional2D;
                case "point2d": return LightKind.Point2D;
                default: return LightKind.Unknown;
            }
        }

        /// <summary>
        /// Render a <see cref="LightKind"/> back to its MCP schema string. Used by
        /// the create handler so the JSON key an agent reads round-trips into a
        /// subsequent <c>light_set</c> call. Returns an empty string for
        /// <see cref="LightKind.Unknown"/>.
        /// </summary>
        internal static string ToSchemaString(LightKind kind)
        {
            switch (kind)
            {
                case LightKind.Directional3D: return "directional3d";
                case LightKind.Omni3D: return "omni3d";
                case LightKind.Spot3D: return "spot3d";
                case LightKind.Directional2D: return "directional2d";
                case LightKind.Point2D: return "point2d";
                default: return "";
            }
        }

        /// <summary>
        /// The Godot class name the editor-only handler instantiates for a kind.
        /// The handler instantiates via <c>new T()</c> (these are concrete engine
        /// nodes). Returns an empty string for <see cref="LightKind.Unknown"/>.
        /// </summary>
        internal static string ToClassName(LightKind kind)
        {
            switch (kind)
            {
                case LightKind.Directional3D: return "DirectionalLight3D";
                case LightKind.Omni3D: return "OmniLight3D";
                case LightKind.Spot3D: return "SpotLight3D";
                case LightKind.Directional2D: return "DirectionalLight2D";
                case LightKind.Point2D: return "PointLight2D";
                default: return "";
            }
        }

        /// <summary>
        /// True for the three 3D light kinds (DirectionalLight3D / OmniLight3D /
        /// SpotLight3D). The handler uses this to decide which scalar surface
        /// applies — 3D lights expose energy / range / spot_angle / shadow_enabled;
        /// 2D lights expose a smaller scalar surface (no range / spot angle).
        /// </summary>
        internal static bool Is3D(LightKind kind)
        {
            return kind == LightKind.Directional3D
                || kind == LightKind.Omni3D
                || kind == LightKind.Spot3D;
        }
    }

    /// <summary>
    /// Centralized clamp table for the lighting scalar allow-list (mirrors the
    /// P12.3 particles pack's + P12.5 CSG pack's design decision §2). Every
    /// scalar the create / set / modify handlers accept is clamped here so the
    /// table is unit-testable without the editor. Each <c>Clamp</c> overload
    /// returns the clamped value; the handler echoes the clamped result so an
    /// agent can see what landed.
    ///
    /// <para>
    /// The valid ranges mirror Godot's documented bounds for the Light3D /
    /// Light2D properties:
    /// <list type="bullet">
    /// <item><c>energy</c> / <c>intensity</c>: float ≥ 0. Godot's
    /// <c>Light3D.LightEnergy</c> and <c>Light2D.Energy</c> clamp nothing
    /// themselves, but a negative energy is physically meaningless; a very large
    /// value blooms the whole screen. No hard upper bound — pass through after
    /// the non-negative floor.</item>
    /// <item><c>range</c> (Omni/Spot 3D / PointLight2D): float ≥
    /// <see cref="MinRange"/>. Godot's <c>Light3D.LightSize</c> (Omni/Spot range)
    /// rejects 0 / negative in the inspector (a zero-range light illuminates
    /// nothing); the engine default is 5.0 for 3D and 256 for 2D texture height.
    /// <see cref="MinRange"/> is the same strictly-positive floor the CSG pack
    /// uses.</item>
    /// <item><c>spot_angle</c> (SpotLight3D): float in [0.01, 180]. Godot's
    /// <c>Light3D.Param.SpotAngle</c> is in degrees; 0 is degenerate, 180 turns
    /// the spot into a hemisphere. The engine default is 45.</item>
    /// <item><c>attenuation</c> / <c>shadow_enabled</c>: passed through (float ≥
    /// 0 for attenuation; bool for shadow_enabled — no clamping).</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class LightPropertyClamp
    {
        /// <summary>Strictly-positive floor for range. A zero-range light
        /// illuminates nothing; the engine rejects it in the inspector. Matches
        /// the CSG pack's <c>MinPositive</c>.</summary>
        internal const float MinRange = 0.0001f;

        /// <summary>Lower bound for spot angle (degrees). Godot rejects exactly 0;
        /// 0.01 keeps the spot non-degenerate. The upper bound is 180 (a
        /// hemisphere).</summary>
        internal const float MinSpotAngle = 0.01f;
        internal const float MaxSpotAngle = 180f;

        /// <summary>Clamp energy / intensity to non-negative. A negative energy is
        /// physically meaningless (a light subtracts light); no hard upper bound
        /// so an agent can intentionally drive a bloom.</summary>
        internal static float ClampEnergy(float v) => v < 0f ? 0f : v;

        /// <summary>Clamp range to strictly-positive (Omni/Spot 3D + PointLight2D
        /// texture height). Mirrors the CSG pack's ClampPositive.</summary>
        internal static float ClampRange(float v) => v < MinRange ? MinRange : v;

        /// <summary>Clamp spot angle (SpotLight3D) to [0.01, 180] degrees.</summary>
        internal static float ClampSpotAngle(float v)
        {
            if (v < MinSpotAngle) return MinSpotAngle;
            if (v > MaxSpotAngle) return MaxSpotAngle;
            return v;
        }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_light_create</c> (P16.3,
    /// mutating, gated). Carries the <c>kind</c> (which light node family to
    /// create), the standard node-creation fields (<c>name</c> /
    /// <c>parent_node_path</c> / <c>position</c>), and the optional starter
    /// scalars (<c>color</c> / <c>energy</c> / <c>range</c> / <c>spot_angle</c> /
    /// <c>attenuation</c> / <c>shadow_enabled</c>) applied at create time. A null
    /// scalar means "leave the engine default" — the handler does not write the
    /// property.
    /// </summary>
    internal sealed class LightCreateBody
    {
        internal LightKind Kind { get; private set; }

        internal string? Name { get; private set; }
        internal string? ParentNodePath { get; private set; }
        internal string? Position { get; private set; }

        // Optional starter scalars. Each is nullable — null means "use the engine
        // default" (the handler does not write the property when null). The body
        // parser records the raw value; clamping is the handler's job via
        // <see cref="LightPropertyClamp"/> so the clamped result can be echoed.
        internal string? Color { get; private set; }             // "r,g,b[,a]" 0-1
        internal float? Energy { get; private set; }             // all lights
        internal float? Range { get; private set; }              // omni/spot3d, point2d
        internal float? SpotAngle { get; private set; }          // spot3d only
        internal float? Attenuation { get; private set; }        // omni/spot3d
        internal bool? ShadowEnabled { get; private set; }       // all lights

        internal static LightCreateBody Parse(string? body)
        {
            var parsed = new LightCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Kind = LightKindParser.Parse(JsonScalar.ExtractString(body, "kind"));
            parsed.Name = JsonScalar.ExtractString(body, "name");
            parsed.ParentNodePath = JsonScalar.ExtractString(body, "parent_node_path");
            parsed.Position = JsonScalar.ExtractString(body, "position");
            parsed.Color = JsonScalar.ExtractString(body, "color");
            parsed.Energy = JsonScalar.ExtractFloat(body, "energy");
            parsed.Range = JsonScalar.ExtractFloat(body, "range");
            parsed.SpotAngle = JsonScalar.ExtractFloat(body, "spot_angle");
            parsed.Attenuation = JsonScalar.ExtractFloat(body, "attenuation");
            parsed.ShadowEnabled = JsonScalar.ExtractBool(body, "shadow_enabled");
            return parsed;
        }

        LightCreateBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_light_set</c> (P16.3, mutating,
    /// gated). Carries the light target (<c>node_path</c>) and ONE scalar field
    /// to patch. The handler resolves the node, type-checks it against
    /// <c>Light3D</c> / <c>Light2D</c>, validates the field is one of the
    /// allow-listed light scalars, clamps it, and writes it. <c>light_modify</c>
    /// reuses this body but accepts multiple fields via a separate field-set
    /// extraction.
    /// </summary>
    internal sealed class LightSetBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        /// <summary>The single scalar field to patch (color / energy / range /
        /// spot_angle / attenuation / shadow_enabled). Must be one of the
        /// allow-listed light scalars the handler validates.</summary>
        internal string? Field { get; private set; }
        internal bool HasField => !string.IsNullOrEmpty(Field);

        /// <summary>The verbatim JSON value token (quotes preserved for strings,
        /// braces for objects, brackets for arrays). The handler re-parses it
        /// into the right Godot type per field. Null when the <c>value</c> key
        /// is absent.</summary>
        internal string? ValueRaw { get; private set; }

        internal static LightSetBody Parse(string? body)
        {
            var parsed = new LightSetBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.Field = JsonScalar.ExtractString(body, "field");
            parsed.ValueRaw = ExtractVerbatimValue(body, "value");
            return parsed;
        }

        /// <summary>
        /// Extract the <b>verbatim</b> JSON token for a key — the exact substring
        /// Godot's <c>Json.ParseString</c> can re-parse. Duplicated from the
        /// P16.2 materials pack's <c>MaterialSetPropertyBody.ExtractVerbatimValue</c>
        /// rather than widening that type's surface for one helper; the P12 packs
        /// duplicate TryParseVector3 the same way.
        /// </summary>
        static string? ExtractVerbatimValue(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;

            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return null;

            // Explicit JSON null → null (the handler treats null as "clear"/"no-op").
            if (start + 4 <= body.Length && body.Substring(start, 4) == "null")
                return null;

            // String value: return the quoted token verbatim.
            if (body[start] == '"')
            {
                var end = start + 1;
                while (end < body.Length)
                {
                    if (body[end] == '\\' && end + 1 < body.Length) { end += 2; continue; }
                    if (body[end] == '"') { end++; break; }
                    end++;
                }
                return body.Substring(start, end - start);
            }

            // Object / array: return the balanced slice verbatim.
            if (body[start] == '{' || body[start] == '[')
                return SliceBalancedVerbatim(body, start);

            // Number / boolean / etc.: slice to the next comma or closing brace.
            var tokenEnd = start;
            while (tokenEnd < body.Length && body[tokenEnd] != ',' && body[tokenEnd] != '}' && body[tokenEnd] != ']')
                tokenEnd++;
            return body.Substring(start, tokenEnd - start).Trim();
        }

        /// <summary>
        /// Slice a balanced JSON object or array verbatim starting at
        /// <paramref name="start"/>. Verbatim from the P16.2 materials pack.
        /// </summary>
        static string SliceBalancedVerbatim(string body, int start)
        {
            char open = body[start];
            char close = open == '{' ? '}' : ']';
            int depth = 0;
            int i = start;
            bool inString = false;
            while (i < body.Length)
            {
                char c = body[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < body.Length) { i += 2; continue; }
                    if (c == '"') inString = false;
                    i++;
                    continue;
                }
                if (c == '"') { inString = true; i++; continue; }
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return body.Substring(start, i - start + 1);
                }
                i++;
            }
            return body.Substring(start);
        }

        LightSetBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_light_modify</c> (P16.3,
    /// mutating, gated). Carries the light target (<c>node_path</c>) and a
    /// <c>fields</c> map of {field → value} entries. The handler resolves the
    /// node, type-checks it, and applies each field through the same validation +
    /// clamping path <c>light_set</c> uses, accumulating per-field results
    /// (applied + errors) so a single bad entry does not abort the batch.
    ///
    /// <para>
    /// The <c>fields</c> map is extracted as the raw JSON object slice — the
    /// handler walks its top-level keys (each key is a light scalar field name,
    /// each value the verbatim token) so the same field allow-list +
    /// <see cref="LightPropertyClamp"/> validation covers both
    /// <c>light_set</c> (one field) and <c>light_modify</c> (many).
    /// </para>
    /// </summary>
    internal sealed class LightModifyBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        /// <summary>The raw JSON object slice for the <c>fields</c> map (without
        /// the surrounding braces), or null when absent. The handler walks it
        /// top-level to extract each {field → verbatim value} pair. Null/empty
        /// surfaces <c>missing_parameter</c>.</summary>
        internal string? FieldsRaw { get; private set; }
        internal bool HasFields => !string.IsNullOrEmpty(FieldsRaw);

        internal static LightModifyBody Parse(string? body)
        {
            var parsed = new LightModifyBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.FieldsRaw = ExtractObjectSlice(body, "fields");
            return parsed;
        }

        /// <summary>
        /// Extract the raw JSON object slice (the text between the outer braces,
        /// exclusive) for a key. Returns null when the key is absent or the value
        /// is not a JSON object. The handler walks the slice top-level to
        /// enumerate {field → value} pairs. Mirrors the verbatim slicers in the
        /// P16.2 pack but returns the inner slice (between the braces) rather
        /// than the whole object.
        /// </summary>
        static string? ExtractObjectSlice(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;

            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length || body[start] != '{') return null;

            // Find the matching close brace, tracking depth + skipping strings.
            int depth = 0;
            int i = start;
            bool inString = false;
            while (i < body.Length)
            {
                char c = body[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < body.Length) { i += 2; continue; }
                    if (c == '"') inString = false;
                    i++;
                    continue;
                }
                if (c == '"') { inString = true; i++; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return body.Substring(start + 1, i - start - 1);
                }
                i++;
            }
            return null;
        }

        LightModifyBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_environment_set</c> (P16.3,
    /// mutating, gated). Carries the edited scene's <c>WorldEnvironment</c> node
    /// target (<c>node_path</c>, optional — when omitted the handler finds the
    /// first WorldEnvironment in the edited scene, or creates one under the root)
    /// and the <c>environment_path</c> (a res:// Environment .tres to assign).
    /// The handler loads the Environment resource, assigns it to the
    /// WorldEnvironment node's <c>Environment</c> property, and marks the scene
    /// unsaved.
    /// </summary>
    internal sealed class EnvironmentSetBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal string? EnvironmentPath { get; private set; }
        internal bool HasEnvironmentPath => !string.IsNullOrEmpty(EnvironmentPath);

        /// <summary>When true and no WorldEnvironment node exists in the edited
        /// scene, the handler creates one under the root. Default false — a
        /// missing WorldEnvironment surfaces <c>environment_node_not_found</c>
        /// so the agent decides whether to create one.</summary>
        internal bool? CreateIfMissing { get; private set; }

        internal static EnvironmentSetBody Parse(string? body)
        {
            var parsed = new EnvironmentSetBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.EnvironmentPath = JsonScalar.ExtractString(body, "environment_path");
            parsed.CreateIfMissing = JsonScalar.ExtractBool(body, "create_if_missing");
            return parsed;
        }

        EnvironmentSetBody() { }
    }
}
