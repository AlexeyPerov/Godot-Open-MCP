#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_scene_open</c> (P2.6). Extracts the scalar fields the
    /// open handler needs straight off the raw JSON body using the same hand-rolled
    /// <c>IndexOf</c>-substring style as <see cref="NodeCreateBody"/> and <see cref="NodeFindBody"/> —
    /// the bridge deliberately carries no typed JSON DOM dependency on the hot path
    /// (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is unit-testable
    /// in the binary-less xUnit host. The editor-only <see cref="SceneTools"/> handler constructs this
    /// from the raw body string and then branches on the parsed fields.
    /// </para>
    /// </summary>
    internal sealed class SceneOpenBody
    {
        /// <summary>
        /// <c>res://</c> path of the scene file to open (e.g. <c>res://levels/level_1.tscn</c>).
        /// Required by the handler — an empty/missing path fails with <c>missing_parameter</c>.
        /// </summary>
        internal string? Path { get; private set; }

        /// <summary>
        /// When true, the open proceeds even if the current edited scene has unsaved changes. Default
        /// false: the bridge refuses a dirty open with <c>scene_dirty</c> so an agent does not lose
        /// edits by switching scenes (Godot's native flow would pop a save modal; the bridge avoids
        /// modal dialogs entirely).
        /// </summary>
        internal bool IgnoreDirty { get; private set; } = false;

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="SceneOpenBody"/>. Never throws — a missing
        /// or malformed field falls back to its default (null path / false ignore_dirty). Empty/null
        /// body returns an all-default instance.
        /// </summary>
        internal static SceneOpenBody Parse(string? body)
        {
            var parsed = new SceneOpenBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.Path = ExtractNullableStringValue(body, "path");
            parsed.IgnoreDirty = ExtractBoolValue(body, "ignore_dirty", defaultValue: false);
            return parsed;
        }

        SceneOpenBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Mirrors NodeCreateBody.Extract*: locate `"key"`, walk past the colon, read the scalar.
        // Strings are unwrapped from their quotes and unescaped for the small set of JSON string
        // escapes (\" \\ \/ \n \r \t \uXXXX); a field that is present-but-null is treated as absent.
        // The extraction logic is duplicated from NodeCreateBody rather than factored into a shared
        // helper (see NodeCreateBody's note on why: each tool's field set is tiny, independent
        // parsers mean a bug in one cannot regress the other).

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
            // null literal → absent.
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
