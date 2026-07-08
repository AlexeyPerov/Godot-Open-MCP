#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_scene_save</c> (P2.6). Extracts the optional
    /// save-as path and the <c>save_all</c> flag straight off the raw JSON body using the same
    /// hand-rolled <c>IndexOf</c>-substring style as <see cref="SceneOpenBody"/> /
    /// <see cref="NodeCreateBody"/> — the bridge deliberately carries no typed JSON DOM dependency
    /// on the hot path (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// Three save modes, resolved by the handler from the parsed fields:
    /// <list type="bullet">
    /// <item><description><see cref="SaveAll"/> true — iterate every open scene tab and save each.</description></item>
    /// <item><description><see cref="Path"/> set (and <see cref="SaveAll"/> false) — save-as the edited scene to the given <c>res://</c> path.</description></item>
    /// <item><description>Neither — save the edited scene back to its existing file.</description></item>
    /// </list>
    /// When <see cref="SaveAll"/> is true, <see cref="Path"/> is ignored.
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is
    /// unit-testable in the binary-less xUnit host.
    /// </para>
    /// </summary>
    internal sealed class SceneSaveBody
    {
        /// <summary>
        /// Optional <c>res://</c> destination path (ending in <c>.tscn</c> / <c>.scn</c>) for a
        /// save-as. When null and <see cref="SaveAll"/> is false, the edited scene is saved back to
        /// its existing file. Ignored when <see cref="SaveAll"/> is true.
        /// </summary>
        internal string? Path { get; private set; }

        /// <summary>
        /// When true, save every open scene tab (not just the edited one). Default false.
        /// </summary>
        internal bool SaveAll { get; private set; } = false;

        /// <summary>True when <see cref="SaveAll"/> is set — the save-all branch wins over save-as.</summary>
        internal bool IsSaveAll => SaveAll;

        /// <summary>True when <see cref="Path"/> is set and <see cref="SaveAll"/> is false — the save-as branch.</summary>
        internal bool IsSaveAs => !SaveAll && !string.IsNullOrEmpty(Path);

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="SceneSaveBody"/>. Never throws — a missing
        /// or malformed field falls back to its default (null path / false save_all). Empty/null body
        /// returns an all-default instance (save current scene to its file).
        /// </summary>
        internal static SceneSaveBody Parse(string? body)
        {
            var parsed = new SceneSaveBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.Path = ExtractNullableStringValue(body, "path");
            parsed.SaveAll = ExtractBoolValue(body, "save_all", defaultValue: false);
            return parsed;
        }

        SceneSaveBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Mirrors SceneOpenBody.Extract* / NodeCreateBody.Extract*: locate `"key"`, walk past the
        // colon, read the scalar. Strings unwrapped + unescaped for \" \\ \/ \n \r \t \uXXXX; a
        // present-but-null field is treated as absent. Duplicated per the convention noted in
        // NodeCreateBody (tiny field set, independent parsers).

        static string? ExtractNullableStringValue(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return null;
            if (start + 4 <= body.Length && body.Substring(start, 4) == "null") return null;
            if (body[start] == '"')
                return SliceQuotedString(body, start);
            return null;
        }

        static bool ExtractBoolValue(string body, string key, bool defaultValue)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return defaultValue;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return defaultValue;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return defaultValue;
            if (start + 4 <= body.Length && body.Substring(start, 4) == "true") return true;
            if (start + 5 <= body.Length && body.Substring(start, 5) == "false") return false;
            return defaultValue;
        }

        static string SliceQuotedString(string body, int start)
        {
            // start points at the opening quote.
            var sb = new System.Text.StringBuilder(body.Length - start);
            int i = start + 1;
            while (i < body.Length)
            {
                var c = body[i];
                if (c == '\\' && i + 1 < body.Length)
                {
                    var next = body[i + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); i += 2; continue;
                        case '\\': sb.Append('\\'); i += 2; continue;
                        case '/': sb.Append('/'); i += 2; continue;
                        case 'n': sb.Append('\n'); i += 2; continue;
                        case 'r': sb.Append('\r'); i += 2; continue;
                        case 't': sb.Append('\t'); i += 2; continue;
                        case 'b': sb.Append('\b'); i += 2; continue;
                        case 'f': sb.Append('\f'); i += 2; continue;
                        case 'u' when i + 5 < body.Length:
                            if (int.TryParse(body.Substring(i + 2, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var code))
                                sb.Append((char)code);
                            i += 6;
                            continue;
                        default:
                            sb.Append(next); i += 2; continue;
                    }
                }
                if (c == '"') return sb.ToString();
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}
