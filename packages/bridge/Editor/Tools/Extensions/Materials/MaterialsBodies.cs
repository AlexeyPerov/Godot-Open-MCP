#nullable enable
using System;
using System.Collections.Generic;

namespace GodotOpenMcp.Bridge.Editor
{
    // ===========================================================================
    // P16.2 materials/shaders pack request bodies + material-kind catalog.
    //
    // Five tools land in this pack:
    //   - godot_open_mcp_material_create (mutating, gated) — instantiate a
    //     StandardMaterial3D / ORMMaterial3D / ShaderMaterial and save as .tres.
    //   - godot_open_mcp_material_get_properties (read-only) — list a material's
    //     properties + current values.
    //   - godot_open_mcp_material_set_property (mutating, gated) — set a single
    //     property (color/scalar/texture/enum) on a saved .tres material.
    //   - godot_open_mcp_material_set_shader (mutating, gated) — assign a .gdshader
    //     to a ShaderMaterial and save.
    //   - godot_open_mcp_shader_get_data (read-only) — read a .gdshader's uniforms
    //     + types.
    //
    // The body types mirror the hand-rolled IndexOf-substring style already used by
    // the P12.x domain packs and the P16.1 settings pack (see packages/bridge/
    // AGENTS.md §Transport: the bridge deliberately carries no typed JSON DOM
    // dependency on the hot path). Pure-managed (no Godot API surface, no `#if
    // TOOLS`), so the parsing logic is unit-testable in the binary-less xUnit host.
    //
    // The shared extraction primitives live in the P12.1 `JsonScalar` static class
    // (GodotOpenMcp.Bridge.Editor namespace, sibling file
    // Extensions/Tilemap/TilemapBodies.cs). P16.2 reuses ExtractString /
    // ExtractBool. The set_property `value` field needs the verbatim JSON token
    // (quotes preserved for strings, so a string "42" stays a string on re-parse
    // into a Variant) — the same contract the P16.1 settings pack introduced
    // (SettingsSetProjectBody.ExtractVerbatimValue). That helper is private to the
    // settings pack; P16.2 duplicates it here rather than widening the settings
    // type's surface for one helper (the P12 packs duplicate TryParseVector3 the
    // same way).
    //
    // Material-kind catalog: only the three first-class material families the plan
    // names are creatable — StandardMaterial3D (standard), ORMMaterial3D (orm),
    // ShaderMaterial (shader). The catalog maps the kind token to the Godot class
    // name the editor-only handler instantiates via ClassDB. Centralized here so
    // the vocabulary is unit-testable without the editor (mirrors the P16.1
    // settings pack's section allowlist design decision).
    //
    // Fidelity: adapt — Unity Open MCP's MaterialTools (create/get-properties/
    // set-property/set-shader shape) supplies the tool roster. The deltas are:
    // (1) Godot material classes replace Unity Material + Shader (Godot's
    // StandardMaterial3D / ORMMaterial3D / ShaderMaterial vs Unity's single
    // Material + Shader.Find); (2) Godot's .gdshader uniform system replaces
    // Unity's shader property reflection (Shader.GetPropertyCount / type / name);
    // (3) Unity render-queue / SRP-batcher keyword APIs are intentionally NOT
    // ported (the plan's skip fidelity tag).
    // ===========================================================================

    /// <summary>
    /// Normalized material-kind token extracted from a <c>material_create</c>
    /// request body. <see cref="Unknown"/> covers both "absent" and "not a valid
    /// token"; the handler turns that into <c>invalid_parameter</c>. The three
    /// values map to the three first-class Godot material families the plan names.
    /// </summary>
    internal enum MaterialKind
    {
        Unknown = 0,
        Standard = 1,   // StandardMaterial3D
        Orm = 2,        // ORMMaterial3D
        Shader = 3,     // ShaderMaterial
    }

    /// <summary>
    /// Map a raw <c>kind</c> string ("standard" | "orm" | "shader") to a
    /// <see cref="MaterialKind"/>. Returns <see cref="MaterialKind.Unknown"/> for
    /// null / empty / unrecognized tokens so the handler can surface a single
    /// <c>invalid_parameter</c> error. Case-insensitive to tolerate an agent
    /// sending "Standard" / "SHADER".
    /// </summary>
    internal static class MaterialKindParser
    {
        internal static MaterialKind Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return MaterialKind.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "standard": return MaterialKind.Standard;
                case "orm": return MaterialKind.Orm;
                case "shader": return MaterialKind.Shader;
                default: return MaterialKind.Unknown;
            }
        }

        /// <summary>
        /// Render a <see cref="MaterialKind"/> back to its MCP schema string. Used
        /// by the create handler so the JSON key an agent reads round-trips into
        /// a subsequent <c>material_get_properties</c> call. Returns an empty
        /// string for <see cref="MaterialKind.Unknown"/>.
        /// </summary>
        internal static string ToSchemaString(MaterialKind kind)
        {
            switch (kind)
            {
                case MaterialKind.Standard: return "standard";
                case MaterialKind.Orm: return "orm";
                case MaterialKind.Shader: return "shader";
                default: return "";
            }
        }

        /// <summary>
        /// The Godot class name the editor-only handler instantiates for a kind.
        /// The handler validates the class exists + is instantiable via ClassDB
        /// before calling <c>ClassDB.Instantiate</c>. Returns an empty string for
        /// <see cref="MaterialKind.Unknown"/>.
        /// </summary>
        internal static string ToClassName(MaterialKind kind)
        {
            switch (kind)
            {
                case MaterialKind.Standard: return "StandardMaterial3D";
                case MaterialKind.Orm: return "ORMMaterial3D";
                case MaterialKind.Shader: return "ShaderMaterial";
                default: return "";
            }
        }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_material_create</c> (P16.2,
    /// mutating, gated). Carries the destination <c>resource_path</c>, the
    /// material <c>kind</c> (standard / orm / shader), an optional
    /// <c>shader_path</c> (res:// .gdshader, only meaningful for the shader
    /// kind), and an optional <c>overwrite</c> flag (default false — a material
    /// already at the destination surfaces <c>material_exists</c> rather than
    /// silently re-GUIDing it, matching the Unity MaterialTools.Create pattern).
    /// </summary>
    internal sealed class MaterialCreateBody
    {
        internal string? ResourcePath { get; private set; }
        internal bool HasResourcePath => !string.IsNullOrEmpty(ResourcePath);

        internal MaterialKind Kind { get; private set; }

        internal string? ShaderPath { get; private set; }
        internal bool HasShaderPath => !string.IsNullOrEmpty(ShaderPath);

        internal bool? Overwrite { get; private set; }

        internal static MaterialCreateBody Parse(string? body)
        {
            var parsed = new MaterialCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.ResourcePath = JsonScalar.ExtractString(body, "resource_path");
            parsed.Kind = MaterialKindParser.Parse(JsonScalar.ExtractString(body, "kind"));
            parsed.ShaderPath = JsonScalar.ExtractString(body, "shader_path");
            parsed.Overwrite = JsonScalar.ExtractBool(body, "overwrite");
            return parsed;
        }

        MaterialCreateBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_material_get_properties</c>
    /// (P16.2, read-only). Carries only the <c>resource_path</c> (res:// or
    /// uid://) of the material to inspect. The handler loads the resource,
    /// type-checks it against <c>Material</c>, and enumerates its property list.
    /// </summary>
    internal sealed class MaterialGetPropertiesBody
    {
        internal string? ResourcePath { get; private set; }
        internal bool HasResourcePath => !string.IsNullOrEmpty(ResourcePath);

        internal static MaterialGetPropertiesBody Parse(string? body)
        {
            var parsed = new MaterialGetPropertiesBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.ResourcePath = JsonScalar.ExtractString(body, "resource_path");
            return parsed;
        }

        MaterialGetPropertiesBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_material_set_property</c>
    /// (P16.2, mutating, gated). Carries the material's <c>resource_path</c>,
    /// the <c>property</c> name to set, and the verbatim JSON <c>value</c> token.
    /// The handler re-parses the token into a <c>Variant</c> via
    /// <c>Json.ParseString</c> and writes it via <c>material.Set(property,
    /// value)</c>, so any property type Godot supports (Color / Vector / scalar /
    /// texture / enum) round-trips. The token is extracted verbatim (quotes
    /// preserved for strings) to keep type fidelity — the same contract the P16.1
    /// settings pack uses for its <c>fields[].value</c>.
    /// </summary>
    internal sealed class MaterialSetPropertyBody
    {
        internal string? ResourcePath { get; private set; }
        internal bool HasResourcePath => !string.IsNullOrEmpty(ResourcePath);

        internal string? Property { get; private set; }
        internal bool HasProperty => !string.IsNullOrEmpty(Property);

        /// <summary>
        /// The verbatim JSON value token (quotes preserved for strings, braces
        /// for objects, brackets for arrays). The handler re-parses it into a
        /// Variant. Null when the <c>value</c> key is absent or an explicit JSON
        /// null (the handler treats null as "clear the property").
        /// </summary>
        internal string? ValueRaw { get; private set; }

        internal static MaterialSetPropertyBody Parse(string? body)
        {
            var parsed = new MaterialSetPropertyBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.ResourcePath = JsonScalar.ExtractString(body, "resource_path");
            parsed.Property = JsonScalar.ExtractString(body, "property");
            parsed.ValueRaw = ExtractVerbatimValue(body, "value");
            return parsed;
        }

        /// <summary>
        /// Extract the <b>verbatim</b> JSON token for a key — the exact substring
        /// Godot's <c>Json.ParseString</c> can re-parse. For a string value this
        /// includes the surrounding quotes (so a string stays a string, a number
        /// stays a number); for an object/array the balanced slice; for a scalar
        /// (number/bool/null) the bare token. Returns null when the key is absent
        /// or the value is an explicit JSON null. Unlike
        /// <see cref="JsonScalar.ExtractRawValue"/>, this does NOT unwrap string
        /// quotes — type fidelity is the whole point. Duplicated from
        /// <c>SettingsSetProjectBody</c> (the P16.1 settings pack) rather than
        /// widening that type's surface; the P12 packs duplicate TryParseVector3
        /// the same way.
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

            // Explicit JSON null → null (the handler treats null as "clear").
            if (start + 4 <= body.Length && body.Substring(start, 4) == "null")
                return null;

            // String value: return the quoted token verbatim (including the quotes),
            // honoring backslash escapes so an embedded quote does not end it early.
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

            // Object / array: return the balanced slice verbatim (with the outer
            // braces/brackets).
            if (body[start] == '{' || body[start] == '[')
                return SliceBalancedVerbatim(body, start);

            // Number / boolean / etc.: slice to the next comma or closing brace.
            var tokenEnd = start;
            while (tokenEnd < body.Length && body[tokenEnd] != ',' && body[tokenEnd] != '}' && body[tokenEnd] != ']')
                tokenEnd++;
            return body.Substring(start, tokenEnd - start).Trim();
        }

        /// <summary>
        /// Slice a balanced JSON object (<c>{…}</c>) or array (<c>[…]</c>) verbatim
        /// starting at <paramref name="start"/>. Tracks brace/bracket depth and
        /// skips over quoted strings (honoring backslash escapes) so a comma/brace
        /// inside a nested string does not prematurely close the slice. Returns the
        /// substring from the opening delimiter through the matching close.
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
            // Unterminated — return the rest so the handler can reject it.
            return body.Substring(start);
        }

        MaterialSetPropertyBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_material_set_shader</c>
    /// (P16.2, mutating, gated). Carries the <c>ShaderMaterial</c>'s
    /// <c>resource_path</c> (the .tres to mutate) and the <c>shader_path</c>
    /// (res:// .gdshader to assign). The handler loads the material, type-checks
    /// it against <c>ShaderMaterial</c>, loads the shader, assigns it via
    /// <c>material.Shader = shader</c>, and saves — after which the shader's
    /// uniforms become settable properties visible to
    /// <c>material_get_properties</c>.
    /// </summary>
    internal sealed class MaterialSetShaderBody
    {
        internal string? ResourcePath { get; private set; }
        internal bool HasResourcePath => !string.IsNullOrEmpty(ResourcePath);

        internal string? ShaderPath { get; private set; }
        internal bool HasShaderPath => !string.IsNullOrEmpty(ShaderPath);

        internal static MaterialSetShaderBody Parse(string? body)
        {
            var parsed = new MaterialSetShaderBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.ResourcePath = JsonScalar.ExtractString(body, "resource_path");
            parsed.ShaderPath = JsonScalar.ExtractString(body, "shader_path");
            return parsed;
        }

        MaterialSetShaderBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_shader_get_data</c> (P16.2,
    /// read-only). Carries only the <c>shader_path</c> (res:// .gdshader) to
    /// inspect. The handler loads the Shader resource, creates a temporary
    /// ShaderMaterial to enumerate the uniforms via Godot's own parameter list
    /// (the Shader class's uniform enumeration surface varies across Godot
    /// versions; the ShaderMaterial parameter list is the stable, always-available
    /// surface), and returns the uniform names + types.
    /// </summary>
    internal sealed class ShaderGetDataBody
    {
        internal string? ShaderPath { get; private set; }
        internal bool HasShaderPath => !string.IsNullOrEmpty(ShaderPath);

        internal static ShaderGetDataBody Parse(string? body)
        {
            var parsed = new ShaderGetDataBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.ShaderPath = JsonScalar.ExtractString(body, "shader_path");
            return parsed;
        }

        ShaderGetDataBody() { }
    }
}
