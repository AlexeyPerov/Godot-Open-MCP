#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace GodotOpenMcp.Bridge.Editor
{
    // ===========================================================================
    // P16.1 project-settings pack request bodies + section allowlist.
    //
    // Two tools land in this pack:
    //   - godot_open_mcp_settings_get_project (read-only) — read one project.godot
    //     section (rendering / physics / input / layer_names / autoload /
    //     application / display) or a summary of every section.
    //   - godot_open_mcp_settings_set_project (mutating, gated) — write key/value
    //     pairs within one section via Godot's ProjectSettings API.
    //
    // The body types mirror the hand-rolled IndexOf-substring style already used by
    // the P12.x domain packs (see packages/bridge/AGENTS.md §Transport: the bridge
    // deliberately carries no typed JSON DOM dependency on the hot path). Pure-
    // managed (no Godot API surface, no `#if TOOLS`), so the parsing logic is
    // unit-testable in the binary-less xUnit host.
    //
    // The shared extraction primitives live in the P12.1 `JsonScalar` static class
    // (GodotOpenMcp.Bridge.Editor namespace, sibling file
    // Extensions/Tilemap/TilemapBodies.cs). P16.1 is the first P16 family — it
    // reuses ExtractString. A list-of-objects extractor for the set_project
    // `fields[]` array is added here (SliceArrayEntries) because no existing
    // JsonScalar helper walks a list of objects; the P12 packs only ever extract
    // flat scalars.
    //
    // Section allowlist: only known sections are writable. The allowlist is
    // centralized here so the validation surface is unit-testable without the
    // editor (mirrors the P12.3 particles pack's centralized clamp-table design
    // decision). Unknown sections surface `invalid_parameter` from the handler.
    //
    // Fidelity: adapt — Unity Open MCP's `settings_get_player` / `set_player`
    // (TypedTools/BuildSettingsTools.cs) supplies the section-based read/write
    // shape and the `fields[]` array-of-{key,value} patches contract. Godot
    // section names replace Unity PlayerSettings domains, and writes route through
    // Godot's ProjectSettings.SetSetting + Save (never raw text edits).
    // ===========================================================================

    /// <summary>
    /// Normalized section token extracted from a request body. <see cref="Unknown"/>
    /// covers both "absent" and "not a valid token"; the handler turns that into
    /// <c>invalid_parameter</c>. The set mirrors the P16.1 spec's writable section
    /// list; <see cref="All"/> is the read/get summary switch.
    /// </summary>
    internal enum SettingsSection
    {
        Unknown = 0,
        Rendering = 1,
        Physics = 2,
        Input = 3,
        LayerNames = 4,
        Autoload = 5,
        Application = 6,
        Display = 7,
        All = 8,
    }

    /// <summary>
    /// Map a raw <c>section</c> string to a <see cref="SettingsSection"/>. Returns
    /// <see cref="SettingsSection.Unknown"/> for null / empty / unrecognized tokens
    /// so the handler can surface a single <c>invalid_parameter</c> error.
    /// Case-insensitive to tolerate an agent sending "Rendering" / "PHYSICS".
    /// <see cref="SettingsSection.All"/> is valid only for the read tool (the get
    /// handler accepts it; the set handler rejects it — a section is required to
    /// scope the write).
    /// </summary>
    internal static class SettingsSectionParser
    {
        internal static SettingsSection Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return SettingsSection.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "rendering": return SettingsSection.Rendering;
                case "physics": return SettingsSection.Physics;
                case "input": return SettingsSection.Input;
                case "layer_names": return SettingsSection.LayerNames;
                case "autoload": return SettingsSection.Autoload;
                case "application": return SettingsSection.Application;
                case "display": return SettingsSection.Display;
                case "all": return SettingsSection.All;
                default: return SettingsSection.Unknown;
            }
        }

        /// <summary>
        /// Render a <see cref="SettingsSection"/> back to its MCP schema string. Used
        /// by the read path (<c>get</c>) so the JSON a section key an agent reads
        /// round-trips into <c>set</c> section. Returns <c>"all"</c> for
        /// <see cref="SettingsSection.All"/> and an empty string for
        /// <see cref="SettingsSection.Unknown"/>.
        /// </summary>
        internal static string ToSchemaString(SettingsSection section)
        {
            switch (section)
            {
                case SettingsSection.Rendering: return "rendering";
                case SettingsSection.Physics: return "physics";
                case SettingsSection.Input: return "input";
                case SettingsSection.LayerNames: return "layer_names";
                case SettingsSection.Autoload: return "autoload";
                case SettingsSection.Application: return "application";
                case SettingsSection.Display: return "display";
                case SettingsSection.All: return "all";
                default: return "";
            }
        }
    }

    /// <summary>
    /// Centralized section allowlist + the Godot <c>project.godot</c> property-prefix
    /// each section maps to. The Godot <c>ProjectSettings</c> API stores every setting
    /// under a flat dotted path (e.g. <c>rendering/environment/defaults/default_clear_color</c>);
    /// a section is the first path segment. <see cref="SectionPrefix"/> is that leading
    /// segment, used by the handler to enumerate a section's keys via
    /// <c>ProjectSettings.GetPropertyList()</c> and to scope writes.
    ///
    /// <para>
    /// The allowlist is the writable surface. <see cref="SettingsSection.All"/> is
    /// NOT writable (it is the read summary switch); <see cref="IsWritable"/> returns
    /// false for it. Mirrors the P12.3 particles pack's centralized clamp-table
    /// design decision: pin the validation surface here so it is unit-testable
    /// without the editor.
    /// </para>
    /// </summary>
    internal static class SettingsSectionCatalog
    {
        /// <summary>
        /// The writable sections in catalog order. <see cref="SettingsSection.All"/>
        /// is intentionally absent — it is the read summary switch, not a writable
        /// domain.
        /// </summary>
        internal static readonly SettingsSection[] WritableSections =
        {
            SettingsSection.Rendering,
            SettingsSection.Physics,
            SettingsSection.Input,
            SettingsSection.LayerNames,
            SettingsSection.Autoload,
            SettingsSection.Application,
            SettingsSection.Display,
        };

        /// <summary>True when <paramref name="section"/> is in the writable allowlist
        /// (excludes <see cref="SettingsSection.Unknown"/> and
        /// <see cref="SettingsSection.All"/>).</summary>
        internal static bool IsWritable(SettingsSection section)
        {
            switch (section)
            {
                case SettingsSection.Rendering:
                case SettingsSection.Physics:
                case SettingsSection.Input:
                case SettingsSection.LayerNames:
                case SettingsSection.Autoload:
                case SettingsSection.Application:
                case SettingsSection.Display:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The Godot <c>ProjectSettings</c> property prefix (first path segment) for a
        /// section. <c>autoload</c> is special: Godot stores autoloads under the
        /// literal <c>autoload/</c> prefix in <c>project.godot</c>. Returns an empty
        /// string for <see cref="SettingsSection.Unknown"/> / <see cref="All"/>.
        /// </summary>
        internal static string SectionPrefix(SettingsSection section)
        {
            switch (section)
            {
                case SettingsSection.Rendering: return "rendering/";
                case SettingsSection.Physics: return "physics/";
                case SettingsSection.Input: return "input/";
                case SettingsSection.LayerNames: return "layer_names/";
                case SettingsSection.Autoload: return "autoload/";
                case SettingsSection.Application: return "application/";
                case SettingsSection.Display: return "display/";
                default: return "";
            }
        }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_settings_get_project</c> (P16.1,
    /// read-only). Carries only the <c>section</c> to read; <c>all</c> returns a
    /// summary of every writable section.
    /// </summary>
    internal sealed class SettingsGetProjectBody
    {
        internal SettingsSection Section { get; private set; }

        internal static SettingsGetProjectBody Parse(string? body)
        {
            var parsed = new SettingsGetProjectBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Section = SettingsSectionParser.Parse(JsonScalar.ExtractString(body, "section"));
            return parsed;
        }

        SettingsGetProjectBody() { }
    }

    /// <summary>
    /// One key/value patch in the <c>fields[]</c> array of a
    /// <c>godot_open_mcp_settings_set_project</c> request. <c>Key</c> is the Godot
    /// <c>ProjectSettings</c> property path (the full dotted path, e.g.
    /// <c>application/run/main_scene</c>); the handler prepends the section prefix
    /// only when the key is relative. <c>ValueRaw</c> is the verbatim JSON value
    /// token (string / number / bool / null / object / array) — the handler parses
    /// it via <c>Json.ParseString</c> and writes it through
    /// <c>ProjectSettings.SetSetting</c>.
    ///
    /// <para>
    /// The parser records the raw value token rather than a typed scalar because a
    /// project setting can be any Variant type (color, vector, dictionary). The
    /// handler re-parses the token into a Variant and lets Godot's
    /// <c>SetSetting</c> store it.
    /// </para>
    /// </summary>
    internal sealed class SettingsFieldPatch
    {
        internal string? Key { get; }
        internal string? ValueRaw { get; }
        internal bool HasKey => !string.IsNullOrEmpty(Key);

        internal SettingsFieldPatch(string? key, string? valueRaw)
        {
            Key = key;
            ValueRaw = valueRaw;
        }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_settings_set_project</c> (P16.1,
    /// mutating, gated). Carries the <c>section</c> to scope the write and the
    /// <c>fields[]</c> array of <c>{key, value}</c> patches. The handler validates
    /// that the section is writable (the allowlist excludes <c>all</c> +
    /// <c>unknown</c>), then applies each patch through
    /// <c>ProjectSettings.SetSetting</c> and persists with
    /// <c>ProjectSettings.Save</c>.
    /// </summary>
    internal sealed class SettingsSetProjectBody
    {
        internal SettingsSection Section { get; private set; }
        internal IReadOnlyList<SettingsFieldPatch> Fields { get; private set; } = Array.Empty<SettingsFieldPatch>();

        internal static SettingsSetProjectBody Parse(string? body)
        {
            var parsed = new SettingsSetProjectBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Section = SettingsSectionParser.Parse(JsonScalar.ExtractString(body, "section"));
            parsed.Fields = ParseFieldsArray(body);
            return parsed;
        }

        /// <summary>
        /// Walk the <c>"fields"</c> JSON array and return each top-level element as a
        /// <see cref="SettingsFieldPatch"/>. Mirrors the Unity BuildSettingsTools
        /// pattern (a <c>fields[]</c> array of <c>{key, value}</c> objects). Each
        /// element must be an object with a string <c>key</c> and a <c>value</c> of
        /// any JSON type. A missing <c>value</c> key is treated as JSON null (clear
        /// the setting); an entry with no <c>key</c> is dropped (the handler
        /// surfaces a warning).
        ///
        /// <para>
        /// The <c>value</c> is extracted as the <b>verbatim JSON token</b> (quotes
        /// preserved for strings, braces for objects, brackets for arrays) via
        /// <see cref="ExtractVerbatimValue"/>, NOT via <see cref="JsonScalar"/>.
        /// <c>JsonScalar.ExtractRawValue</c> unwraps string quotes (returns the
        /// inner content), which would strip the type information the handler needs:
        /// a string value <c>"42"</c> would arrive bare as <c>42</c> and re-parse
        /// to an int Variant, silently changing the setting's type. The verbatim
        /// token round-trips through <c>Json.ParseString</c> with full type fidelity.
        /// </para>
        /// </summary>
        static IReadOnlyList<SettingsFieldPatch> ParseFieldsArray(string body)
        {
            var rawArray = JsonScalar.ExtractRawValue(body, "fields");
            if (string.IsNullOrEmpty(rawArray)) return Array.Empty<SettingsFieldPatch>();
            var entries = SliceArrayEntries(rawArray);
            var patches = new List<SettingsFieldPatch>(entries.Count);
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                var key = JsonScalar.ExtractString(entry, "key");
                var valueRaw = ExtractVerbatimValue(entry, "value");
                patches.Add(new SettingsFieldPatch(key, valueRaw));
            }
            return patches;
        }

        /// <summary>
        /// Extract the <b>verbatim</b> JSON token for a key — the exact substring
        /// Godot's <c>Json.ParseString</c> can re-parse. For a string value this
        /// includes the surrounding quotes (so a string stays a string, a number
        /// stays a number); for an object/array the balanced slice; for a scalar
        /// (number/bool/null) the bare token. Returns null when the key is absent or
        /// the value is an explicit JSON null (the handler treats null as "clear the
        /// setting"). Unlike <see cref="JsonScalar.ExtractRawValue"/>, this does NOT
        /// unwrap string quotes — type fidelity is the whole point.
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
        /// starting at <paramref name="start"/>. Tracks brace/bracket depth and skips
        /// over quoted strings (honoring backslash escapes) so a comma/brace inside a
        /// nested string does not prematurely close the slice. Returns the substring
        /// from the opening delimiter through the matching close.
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

        /// <summary>
        /// Walk a JSON array and return each top-level element verbatim (the
        /// substring inside the surrounding brackets, split on top-level commas,
        /// with strings/objects/arrays skipped over so a comma inside one does not
        /// split). Mirrors the Unity BuildSettingsTools.ParseArrayEntries helper —
        /// the existing <see cref="JsonScalar"/> extractors operate on a single
        /// object body, so the array walk is local to this pack.
        /// </summary>
        static List<string> SliceArrayEntries(string rawArray)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(rawArray)) return result;
            var v = rawArray.Trim();
            if (!v.StartsWith("{", StringComparison.Ordinal) && !v.StartsWith("[", StringComparison.Ordinal))
                return result;
            // The ExtractRawValue already returned the balanced slice including the
            // outer brackets for an array value; walk from the first '['.
            var openIdx = v.IndexOf('[');
            if (openIdx < 0) return result;
            int i = openIdx + 1;
            while (i < v.Length)
            {
                while (i < v.Length && char.IsWhiteSpace(v[i])) i++;
                if (i >= v.Length || v[i] == ']') break;

                int start = i;
                if (v[i] == '"')
                {
                    i++;
                    while (i < v.Length)
                    {
                        if (v[i] == '\\' && i + 1 < v.Length) { i += 2; continue; }
                        if (v[i] == '"') { i++; break; }
                        i++;
                    }
                }
                else if (v[i] == '{' || v[i] == '[')
                {
                    var open = v[i];
                    var close = open == '{' ? '}' : ']';
                    int depth = 1;
                    i++;
                    while (i < v.Length && depth > 0)
                    {
                        if (v[i] == '"')
                        {
                            i++;
                            while (i < v.Length)
                            {
                                if (v[i] == '\\' && i + 1 < v.Length) { i += 2; continue; }
                                if (v[i] == '"') { i++; break; }
                                i++;
                            }
                            continue;
                        }
                        if (v[i] == open) depth++;
                        else if (v[i] == close) depth--;
                        i++;
                    }
                }
                else
                {
                    while (i < v.Length && v[i] != ',' && v[i] != ']') i++;
                }
                result.Add(v.Substring(start, i - start).Trim());
                while (i < v.Length && (v[i] == ',' || char.IsWhiteSpace(v[i]))) i++;
            }
            return result;
        }

        SettingsSetProjectBody() { }
    }
}
