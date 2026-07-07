#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_node_find</c> (P2.2). Extracts the scalar fields
    /// the handler needs straight off the raw JSON body using the same hand-rolled
    /// <c>IndexOf</c>-substring style as <see cref="BridgeRequestBody.ExtractTimeoutMs"/> — the
    /// bridge deliberately carries no typed JSON DOM dependency on the hot path
    /// (<c>packages/bridge/AGENTS.md</c> §Transport), and the few fields this tool needs do not
    /// justify constructing one.
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is
    /// unit-testable in the binary-less xUnit host. The editor-only <see cref="NodeTools"/>
    /// constructs this from the raw body string and then branches on the parsed fields.
    /// </para>
    /// </summary>
    internal sealed class NodeFindBody
    {
        /// <summary>
        /// Targeted-mode resolver priority 1: scene-tree path (<c>Main/Player</c>,
        /// <c>/root/Main/Player</c>, or <c>.</c> for the edited root). Empty when unset.
        /// </summary>
        internal string NodePath { get; private set; } = string.Empty;

        /// <summary>
        /// Targeted-mode resolver priority 2: node name (first match in the edited scene). Empty
        /// when unset.
        /// </summary>
        internal string Name { get; private set; } = string.Empty;

        /// <summary>
        /// List-mode filter: substring matched (case-insensitive, ordinal) against node names. Null
        /// when unset (no filtering).
        /// </summary>
        internal string? NameContains { get; private set; }

        /// <summary>
        /// List-mode filter: Godot class name (e.g. <c>Node3D</c>, <c>Sprite2D</c>) matched
        /// case-sensitively against each node's <c>GetClass()</c>. Null when unset.
        /// </summary>
        internal string? Type { get; private set; }

        /// <summary>
        /// Hierarchy depth for <see cref="NodeData.Children"/> population. 0 = the matched node(s)
        /// only, no children; 1 = direct children; etc. Clamped to &gt;= 0.
        /// </summary>
        internal int HierarchyDepth { get; private set; } = 0;

        /// <summary>
        /// Hard cap on the number of nodes returned in list mode. Defaults to 50, minimum 1. A list
        /// scan that exceeds the cap is truncated and the remainder count is reported in the
        /// result's <c>truncated</c> field.
        /// </summary>
        internal int MaxResults { get; private set; } = DefaultMaxResults;

        /// <summary>Default <see cref="MaxResults"/> when the caller omits it. Matches Unity Open
        /// MCP's <c>gameobject_find</c> default so an agent migrating between the projects sees the
        /// same list bound.</summary>
        internal const int DefaultMaxResults = 50;

        /// <summary>True when any targeted-mode resolver (<see cref="NodePath"/> or
        /// <see cref="Name"/>) is set — the handler runs targeted lookup instead of list
        /// enumeration.</summary>
        internal bool IsTargeted => !string.IsNullOrEmpty(NodePath) || !string.IsNullOrEmpty(Name);

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="NodeFindBody"/>. Never throws — a
        /// missing or malformed field falls back to its default. Empty/null body returns an
        /// all-default instance (list mode, no filters, max_results=50).
        /// </summary>
        internal static NodeFindBody Parse(string? body)
        {
            var parsed = new NodeFindBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.NodePath = ExtractStringValue(body, "node_path");
            // Fall back to the alternate "path" key some clients send — same resolver, less surprise.
            if (string.IsNullOrEmpty(parsed.NodePath))
                parsed.NodePath = ExtractStringValue(body, "path");
            parsed.Name = ExtractStringValue(body, "name");
            parsed.NameContains = ExtractNullableStringValue(body, "name_contains");
            parsed.Type = ExtractNullableStringValue(body, "type");
            parsed.HierarchyDepth = Math.Max(0, ExtractIntValue(body, "hierarchy_depth", 0));
            parsed.MaxResults = Math.Max(1, ExtractIntValue(body, "max_results", DefaultMaxResults));
            return parsed;
        }

        NodeFindBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Mirrors BridgeRequestBody.ExtractTimeoutMs: locate `"key"`, walk past the colon, read the
        // scalar. Strings are unwrapped from their quotes and unescaped for the small set of JSON
        // string escapes (\" \\ \/ \n \r \t \uXXXX); integers are parsed invariant-culture. A field
        // that is present-but-null (e.g. `"name":null`) is treated as absent.

        static string ExtractStringValue(string body, string key)
        {
            var raw = ExtractRawValue(body, key);
            return Unquote(raw) ?? string.Empty;
        }

        static string? ExtractNullableStringValue(string body, string key)
        {
            var raw = ExtractRawValue(body, key);
            return Unquote(raw);
        }

        static int ExtractIntValue(string body, string key, int defaultValue)
        {
            var raw = ExtractRawValue(body, key);
            if (raw == null) return defaultValue;
            // Strip a trailing comma / whitespace / closing brace the raw slicer may have left.
            var trimmed = raw.AsSpan().Trim();
            // Stop at the first non-digit/sign char (commas, whitespace, braces).
            int end = 0;
            if (trimmed.Length > 0 && (trimmed[0] == '-' || trimmed[0] == '+')) end = 1;
            while (end < trimmed.Length && char.IsDigit(trimmed[end])) end++;
            if (end == 0 || (end == 1 && trimmed.Length > 0 && (trimmed[0] == '-' || trimmed[0] == '+')))
                return defaultValue;
            return int.TryParse(trimmed.Slice(0, end), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
        }

        /// <summary>
        /// Slice the raw token(s) following <c>"key":</c> up to the next top-level comma or closing
        /// brace. Returns null when the key is absent.
        /// </summary>
        static string? ExtractRawValue(string body, string key)
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
            if (start + 4 <= body.Length && body.Substring(start, 4) == "null")
                return null;

            // String value: slice from opening quote to the matching closing quote, honoring
            // backslash escapes so an embedded quote doesn't end the value early.
            if (body[start] == '"')
                return SliceQuotedString(body, start);

            // Number / boolean / etc.: slice to the next comma or closing brace.
            return SliceBareToken(body, start);
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

        static string SliceBareToken(string body, int start)
        {
            int end = start;
            while (end < body.Length && body[end] != ',' && body[end] != '}' && body[end] != ']')
                end++;
            return body.Substring(start, end - start).Trim();
        }

        /// <summary>
        /// Unquote a raw value into a non-empty string, or null when the raw is null/empty/already
        /// the literal null marker. <see cref="ExtractRawValue"/> already unwraps quotes, so this
        /// mostly filters absent values.
        /// </summary>
        static string? Unquote(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            return raw;
        }
    }
}
