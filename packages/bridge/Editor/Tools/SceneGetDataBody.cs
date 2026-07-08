#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_scene_get_data</c> (P2.7). Extracts the scalar fields
    /// the get-data handler needs straight off the raw JSON body using the same hand-rolled
    /// <c>IndexOf</c>-substring style as <see cref="NodeFindBody"/> and <see cref="SceneOpenBody"/> —
    /// the bridge deliberately carries no typed JSON DOM dependency on the hot path
    /// (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is unit-testable
    /// in the binary-less xUnit host. The editor-only <see cref="SceneTools"/> handler constructs this
    /// from the raw body string and then branches on the parsed fields.
    /// </para>
    /// </summary>
    internal sealed class SceneGetDataBody
    {
        /// <summary>
        /// <c>res://</c> path of the scene to read (e.g. <c>res://levels/level_1.tscn</c>). When null,
        /// the handler reads the currently-edited scene. When set, the handler validates it matches the
        /// edited scene's path (get-data is a pure read in P2 — it does NOT switch the active scene; the
        /// agent must call <c>scene_open</c> first to read a different scene). Offline read of an
        /// arbitrary <c>.tscn</c> on disk lands in P7.2.
        /// </summary>
        internal string? Path { get; private set; }

        /// <summary>
        /// Depth of the node tree to include in the result. 0 = the root node only (no children); 1 =
        /// root + direct children (default); N = N layers of children. Negative values (<c>-1</c>) walk
        /// the entire tree. Positive values are capped at <see cref="MaxHierarchyDepth"/> to bound the
        /// token budget (deep trees blow the response size). See the risks row in
        /// <c>specs/execution/P2/P2.7.md</c>.
        /// </summary>
        internal int HierarchyDepth { get; private set; } = DefaultHierarchyDepth;

        /// <summary>Default <see cref="HierarchyDepth"/> when the caller omits it. 1 = root + direct
        /// children — a cheap, useful default that lets an agent see the scene's top-level structure
        /// without pulling the whole tree.</summary>
        internal const int DefaultHierarchyDepth = 1;

        /// <summary>Hard cap on a positive <see cref="HierarchyDepth"/>. Deep trees blow the response
        /// token budget; the schema documents this cap so an agent does not request 50 levels and get a
        /// megabyte of JSON. <c>-1</c> bypasses the cap (walks the whole tree).</summary>
        internal const int MaxHierarchyDepth = 5;

        /// <summary>The effective depth to pass to <see cref="NodeTools.ToNodeData"/>. Negative is
        /// translated to a very large sentinel so the depth-counted walker visits the whole tree (same
        /// trick Godot-MCP's <c>Tool_Scene.GetData</c> uses).</summary>
        internal int EffectiveDepth => HierarchyDepth < 0 ? int.MaxValue : HierarchyDepth;

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="SceneGetDataBody"/>. Never throws — a missing
        /// or malformed field falls back to its default (null path / depth 1). Empty/null body returns
        /// an all-default instance (edited scene, depth 1).
        /// </summary>
        internal static SceneGetDataBody Parse(string? body)
        {
            var parsed = new SceneGetDataBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.Path = ExtractNullableStringValue(body, "path");
            var raw = ExtractIntValue(body, "hierarchy_depth", DefaultHierarchyDepth);
            // -1 (or any negative) = unlimited; positive capped at MaxHierarchyDepth; 0 stays 0.
            parsed.HierarchyDepth = raw < 0 ? raw : Math.Min(raw, MaxHierarchyDepth);
            return parsed;
        }

        SceneGetDataBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Mirrors NodeFindBody.Extract* / SceneOpenBody.Extract*: locate `"key"`, walk past the colon,
        // read the scalar. Strings are unwrapped from their quotes and unescaped for the small set of
        // JSON string escapes (\" \\ \/ \n \r \t \uXXXX); a field that is present-but-null is treated
        // as absent. The extraction logic is duplicated per the bridge's no-typed-JSON convention
        // (each tool's field set is tiny, independent parsers mean a bug in one cannot regress the
        // other).

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

        static int ExtractIntValue(string body, string key, int defaultValue)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return defaultValue;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return defaultValue;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return defaultValue;
            // Slice the bare token up to the next comma / brace / bracket.
            int end = start;
            if (end < body.Length && (body[end] == '-' || body[end] == '+')) end++;
            while (end < body.Length && char.IsDigit(body[end])) end++;
            var token = body.AsSpan(start, end - start).Trim();
            if (token.Length == 0) return defaultValue;
            return int.TryParse(token, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
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
