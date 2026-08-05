#if TOOLS
#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Project-settings pack (P16.1) — two typed tools for reading and writing
    /// <c>project.godot</c> sections via Godot's <c>ProjectSettings</c> API:
    /// <c>godot_open_mcp_settings_get_project</c> (read-only) and
    /// <c>godot_open_mcp_settings_set_project</c> (mutating, gated). The first
    /// Phase 16 typed-editor-breadth family.
    ///
    /// <para>
    /// <b>Fidelity:</b> adapt — Unity Open MCP's <c>settings_get_player</c> /
    /// <c>set_player</c> (<c>TypedTools/BuildSettingsTools.cs</c>) supplies the
    /// section-based read/write shape and the <c>fields[]</c> array-of-{key,value}
    /// patches contract. The deltas from the Unity pattern are:
    /// (1) writes route through Godot's <c>ProjectSettings.SetSetting</c> +
    /// <c>Save</c> (never raw text edits to <c>project.godot</c>) — raw text edits
    /// risk corrupting the file's formatting / escaping;
    /// (2) Godot section names (<c>rendering</c> / <c>physics</c> / <c>input</c> /
    /// <c>layer_names</c> / <c>autoload</c> / <c>application</c> / <c>display</c>)
    /// replace Unity's PlayerSettings domains — Unity-specific sections (PlayerSettings
    /// quality tiers, scripting backend enum, etc.) are intentionally NOT ported;
    /// (3) a section allowlist (<see cref="SettingsSectionCatalog"/>) rejects unknown
    /// sections to prevent typos from inventing a bogus <c>project.godot</c> block;
    /// (4) <c>paths_hint</c> is <c>res://project.godot</c> (the single mutated file),
    /// not an edited scene — this is the only P16 pack whose mutation scope is not a
    /// <c>.tscn</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Read path.</b> <c>get_project</c> enumerates a section's keys via
    /// <c>ProjectSettings.GetPropertyList()</c> (filtering to the section's first
    /// path segment), reads each value with <c>ProjectSettings.GetSetting</c>, and
    /// serializes it to JSON via <see cref="Godot.Json.Stringify(Variant)"/> (Godot's
    /// own serializer — handles every Variant type an agent might encounter, including
    /// Color / Vector / Dictionary). <c>section:"all"</c> returns a per-section key
    /// count summary (no per-key values) so an agent can pick which section to read in
    /// full without dumping the whole file.
    /// </para>
    ///
    /// <para>
    /// <b>Write path.</b> <c>set_project</c> validates the section is writable,
    /// re-parses each patch's raw value token into a <c>Variant</c> via
    /// <c>Json.ParseString</c>, writes it via <c>ProjectSettings.SetSetting</c>, then
    /// persists once with <c>ProjectSettings.Save</c>. A patch whose key is relative
    /// (no <c>/</c>) is prefixed with the section's leading segment; an absolute key
    /// (already starts with the section prefix or is a sibling known path) is written
    /// verbatim after the section-prefix check. Per-key failures are accumulated as
    /// warnings (non-aborting) so a batch's good entries still land; an entirely empty
    /// batch surfaces <c>no_applicable_keys</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Gate contract.</b> <c>set_project</c> registers with
    /// <see cref="BridgeToolEntry.DefaultGate"/> <c>"enforce"</c> and validates
    /// <c>paths_hint</c> itself (mirrors the P4.x resource mutators and the P12.x
    /// domain mutators). The dispatch layer rejects an empty hint when the effective
    /// gate is not <c>off</c>; the handler-level guard ALSO fires when an agent
    /// overrides with <c>gate:"off"</c>, so <c>paths_hint</c> is always required.
    /// The read-only <c>get_project</c> has no gate surface.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): every handler touches
    /// <see cref="ProjectSettings"/> and the editor's <c>GetPropertyList()</c> /
    /// <c>Json</c> surface. The pure-managed pieces (<see cref="SettingsGetProjectBody"/>
    /// / <see cref="SettingsSetProjectBody"/> / <see cref="SettingsFieldPatch"/> /
    /// <see cref="SettingsSection"/> / <see cref="SettingsSectionParser"/> /
    /// <see cref="SettingsSectionCatalog"/>) live outside this guard and are unit-tested.
    /// </summary>
    internal static class SettingsTools
    {
        internal const string SettingsGetProjectToolName = "godot_open_mcp_settings_get_project";
        internal const string SettingsSetProjectToolName = "godot_open_mcp_settings_set_project";

        /// <summary>
        /// The <c>paths_hint</c> scope every <c>set_project</c> call must declare. The
        /// single mutated file is <c>project.godot</c>; surfacing the literal here keeps
        /// the tool's contract self-documenting and lets an agent copy it verbatim.
        /// </summary>
        internal const string ProjectGodotHint = "res://project.godot";

        /// <summary>
        /// Register the settings tool family. The mutator (<c>set_project</c>)
        /// declares <c>defaultGate:"enforce"</c> and <c>isMutating:true</c>; the
        /// read-only <c>get_project</c> is <c>off</c>. Both belong to the
        /// <c>settings</c> group (Phase 16's first group). Registered once at plugin
        /// enable; idempotent (the registry replaces on re-register).
        /// </summary>
        internal static void RegisterSettingsTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: SettingsGetProjectToolName,
                isMutating: false,
                defaultGate: "off",
                group: "settings",
                handler: GetProject));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: SettingsSetProjectToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "settings",
                handler: SetProject));
        }

        // ===========================================================================
        // 1. godot_open_mcp_settings_get_project (read-only)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_settings_get_project</c>. Reads one
        /// <c>project.godot</c> section and returns its keys + values, or a summary of
        /// every section when <c>section:"all"</c>. No scene required; no gate surface.
        /// An unknown/absent section returns <c>invalid_parameter</c>.
        ///
        /// <para>
        /// Structured failures: <c>missing_parameter</c>, <c>invalid_parameter</c>,
        /// <c>execution_error</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult GetProject(string body)
        {
            var request = SettingsGetProjectBody.Parse(body);
            if (request.Section == SettingsSection.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "settings_get_project requires 'section' to be one of: " +
                    "\"rendering\", \"physics\", \"input\", \"layer_names\", \"autoload\", " +
                    "\"application\", \"display\", or \"all\" (summary).");

            try
            {
                if (request.Section == SettingsSection.All)
                    return ToolDispatchResult.Ok(BuildAllSummary());

                return ToolDispatchResult.Ok(BuildSection(request.Section));
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        /// <summary>
        /// Build the per-section summary (<c>section:"all"</c>). Returns one entry per
        /// writable section with its key count — no per-key values, so an agent can pick
        /// which section to read in full without dumping the whole file.
        /// </summary>
        static string BuildAllSummary()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"section\":").Append(BridgeJson.EscapeString("all")).Append(',');
            sb.Append("\"sections\":[");
            var sections = SettingsSectionCatalog.WritableSections;
            for (int i = 0; i < sections.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var section = sections[i];
                var prefix = SettingsSectionCatalog.SectionPrefix(section);
                var keys = CollectSectionKeys(prefix);
                sb.Append('{');
                sb.Append("\"section\":").Append(BridgeJson.EscapeString(
                    SettingsSectionParser.ToSchemaString(section))).Append(',');
                sb.Append("\"keyCount\":").Append(keys.Count);
                sb.Append('}');
            }
            sb.Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Build the full per-section read. Returns every key under the section's
        /// prefix with its serialized value. Values are serialized via
        /// <see cref="Godot.Json.Stringify(Variant)"/> (Godot's own serializer —
        /// handles Color / Vector / Dictionary / Array uniformly).
        /// </summary>
        static string BuildSection(SettingsSection section)
        {
            var prefix = SettingsSectionCatalog.SectionPrefix(section);
            var keys = CollectSectionKeys(prefix);

            var sb = new StringBuilder(128 + keys.Count * 64);
            sb.Append('{');
            sb.Append("\"section\":").Append(
                BridgeJson.EscapeString(SettingsSectionParser.ToSchemaString(section))).Append(',');
            sb.Append("\"keyCount\":").Append(keys.Count).Append(',');
            sb.Append("\"values\":{");
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var key = keys[i];
                sb.Append(BridgeJson.EscapeString(key)).Append(':');
                Variant value;
                try
                {
                    value = ProjectSettings.GetSetting(key);
                }
                catch (System.Exception)
                {
                    // A GetSetting failure is non-fatal — render null and move on so one
                    // unreadable key does not blank the whole section.
                    sb.Append("null");
                    continue;
                }
                sb.Append(SerializeValue(value));
            }
            sb.Append('}');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Serialize a <c>ProjectSettings</c> value to a JSON token. Uses Godot's
        /// <see cref="Godot.Json.Stringify(Variant)"/> so every Variant type an agent
        /// might read (Color, Vector2/3, Dictionary, Array, Packed*) is handled by
        /// Godot's own serializer rather than a hand-rolled branch per type. A Nil
        /// variant renders as <c>null</c>.
        /// </summary>
        static string SerializeValue(Variant value)
        {
            if (value.VariantType == Variant.Type.Nil) return "null";
            return Godot.Json.Stringify(value, "", sortKeys: true, fullPrecision: true);
        }

        /// <summary>
        /// Collect every <c>ProjectSettings</c> property key that starts with
        /// <paramref name="prefix"/> (the section's first path segment + "/"). Walks
        /// <c>ProjectSettings.GetPropertyList()</c> once and filters; the property list
        /// includes a handful of non-setting entries (e.g. <c>resource/local_to_scene</c>,
        /// built-in Object properties) — those never start with a section prefix and are
        /// skipped naturally. Sorted for stable output.
        /// </summary>
        static List<string> CollectSectionKeys(string prefix)
        {
            var keys = new List<string>();
            if (string.IsNullOrEmpty(prefix)) return keys;
            foreach (var propVariant in ProjectSettings.GetPropertyList())
            {
                var propDict = propVariant.As<Godot.Collections.Dictionary>();
                if (propDict == null) continue;
                var nameVariant = propDict["name"];
                if (nameVariant.VariantType != Variant.Type.String) continue;
                var name = nameVariant.AsString();
                if (!name.StartsWith(prefix, System.StringComparison.Ordinal)) continue;
                keys.Add(name);
            }
            keys.Sort(System.StringComparer.Ordinal);
            return keys;
        }

        // ===========================================================================
        // 2. godot_open_mcp_settings_set_project (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_settings_set_project</c>. Writes key/value
        /// pairs within one section via Godot's <c>ProjectSettings.SetSetting</c> +
        /// <c>Save</c>. The section must be writable (the allowlist excludes
        /// <c>all</c> + <c>unknown</c>); <c>paths_hint</c> = <c>res://project.godot</c>
        /// is mandatory. Per-key failures are accumulated as warnings (non-aborting)
        /// so a batch's good entries still land; an entirely empty batch surfaces
        /// <c>no_applicable_keys</c>.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>invalid_parameter</c>, <c>no_applicable_keys</c>, <c>execution_error</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult SetProject(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "settings_set_project is mutating; pass a non-empty paths_hint scoped to " +
                    $"the project file ({ProjectGodotHint}).");

            var request = SettingsSetProjectBody.Parse(body);

            if (!SettingsSectionCatalog.IsWritable(request.Section))
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "settings_set_project requires 'section' to be one of the writable sections: " +
                    "\"rendering\", \"physics\", \"input\", \"layer_names\", \"autoload\", " +
                    "\"application\", \"display\". (\"all\" is read-only.)");

            if (request.Fields.Count == 0)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "settings_set_project requires a non-empty 'fields' array of " +
                    "{key, value} patches. See the tool description for supported keys.");

            try
            {
                var prefix = SettingsSectionCatalog.SectionPrefix(request.Section);
                var applied = new List<string>();
                var warnings = new List<string>();

                foreach (var patch in request.Fields)
                {
                    if (!patch.HasKey)
                    {
                        warnings.Add("Skipped a patch with no 'key'.");
                        continue;
                    }
                    var key = patch.Key!;
                    // Resolve the full property path. A relative key (no '/') is prefixed
                    // with the section's leading segment; an absolute key is written
                    // verbatim after confirming it stays inside the section (a cross-section
                    // write would silently land in a different block — reject it so the
                    // section arg and the key agree).
                    string fullKey;
                    if (!key.Contains('/'))
                        fullKey = prefix + key;
                    else
                        fullKey = key;

                    if (!fullKey.StartsWith(prefix, System.StringComparison.Ordinal))
                    {
                        warnings.Add($"Key '{key}' is outside section '{request.Section}'; skipped.");
                        continue;
                    }

                    Variant value;
                    try
                    {
                        value = ParseValueToken(patch.ValueRaw);
                    }
                    catch (System.Exception e)
                    {
                        warnings.Add($"Could not parse value for key '{key}': {e.Message}");
                        continue;
                    }

                    try
                    {
                        ProjectSettings.SetSetting(fullKey, value);
                        applied.Add(fullKey);
                    }
                    catch (System.Exception e)
                    {
                        warnings.Add($"Could not write key '{fullKey}': {e.Message}");
                    }
                }

                if (applied.Count == 0)
                    return ToolDispatchResult.Fail(
                        "no_applicable_keys",
                        "No project settings keys were applied. " +
                        (warnings.Count > 0 ? string.Join(" ", warnings) : ""));

                var saveErr = ProjectSettings.Save();
                if (saveErr != Error.Ok)
                    return ToolDispatchResult.Fail(
                        "execution_error",
                        $"ProjectSettings.Save failed with error {saveErr}. The in-memory settings " +
                        $"were updated ({applied.Count} key(s)) but were NOT persisted to project.godot.");

                var sb = new StringBuilder(128 + applied.Count * 32 + warnings.Count * 64);
                sb.Append('{');
                sb.Append("\"section\":").Append(
                    BridgeJson.EscapeString(SettingsSectionParser.ToSchemaString(request.Section))).Append(',');
                sb.Append("\"action\":\"set_project\"");
                sb.Append(",\"applied\":[");
                for (int i = 0; i < applied.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BridgeJson.EscapeString(applied[i]));
                }
                sb.Append(']');
                AppendWarnings(sb, warnings);
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        /// <summary>
        /// Re-parse a raw JSON value token into a <c>Variant</c>. A null/absent token
        /// yields a Nil variant (which clears the setting when written via
        /// <c>SetSetting</c>). Uses <c>Json.ParseString</c> so any JSON type (number,
        /// bool, string, object, array) round-trips into the matching Variant type.
        /// A bare token that is not valid JSON on its own (e.g. a color string
        /// "1,0,0,1" with no surrounding quotes) is wrapped in quotes first so a
        /// string setting still lands.
        /// </summary>
        static Variant ParseValueToken(string? valueRaw)
        {
            if (string.IsNullOrEmpty(valueRaw)) return default;

            var parseErr = Godot.Json.ParseString(valueRaw, out var parsed);
            if (parseErr == Error.Ok) return parsed;

            // The raw token was not valid JSON on its own (e.g. a color "r,g,b,a" the
            // agent sent unquoted). Treat it as a string literal so a string setting
            // still lands rather than rejecting the whole patch.
            var quoted = "\"" + valueRaw!.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            var err2 = Godot.Json.ParseString(quoted, out var parsedString);
            if (err2 == Error.Ok) return parsedString;

            // Last resort: store the literal string.
            return Variant.From(valueRaw);
        }

        /// <summary>
        /// Append a <c>"warnings":[...]</c> array to <paramref name="sb"/> when there
        /// are warnings; append nothing when there are none (the ok envelope stays
        /// clean for a fully-successful batch). Mirrors the Unity
        /// BuildSettingsTools.AppendWarnings shape.
        /// </summary>
        static void AppendWarnings(StringBuilder sb, List<string> warnings)
        {
            if (warnings == null || warnings.Count == 0) return;
            sb.Append(",\"warnings\":[");
            for (int i = 0; i < warnings.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(warnings[i]));
            }
            sb.Append(']');
        }
    }
}
#endif
