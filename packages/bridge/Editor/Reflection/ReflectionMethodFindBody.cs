#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_reflection_method_find</c> (P5.1). Extracts the
    /// scalar fields the discovery handler needs straight off the raw JSON body using the same
    /// hand-rolled <c>IndexOf</c>-substring style as <see cref="NodeFindBody"/> — the bridge carries
    /// no typed JSON DOM dependency on the hot path (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// Adapted (adapt fidelity) from Unity Open MCP's <c>find_members</c> request shape: the same
    /// <c>query</c> / <c>kind</c> / <c>assembly_filter</c> / <c>include_signatures</c> /
    /// <c>max_results</c> surface, with <c>include_godot_editor</c> replacing Unity's
    /// <c>include_unity_editor</c> and an added <c>type_name</c> drill-down for fast single-type
    /// member enumeration. Defaults match Unity's <c>find_members</c> so an agent migrating between
    /// the projects sees the same discovery bound.
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is unit-testable
    /// in the binary-less xUnit host.
    /// </para>
    /// </summary>
    internal sealed class ReflectionMethodFindBody
    {
        /// <summary>
        /// Substring filter matched (case-insensitive, ordinal) against type names, full names, and
        /// member names. Empty when unset — the scan is still bounded by <see cref="MaxResults"/> and
        /// reports <see cref="Truncated"/>. Mirrors Unity's <c>query</c>.
        /// </summary>
        internal string Query { get; private set; } = string.Empty;

        /// <summary>
        /// Member-kind filter: <c>type</c>, <c>method</c>, <c>property</c>, or <c>all</c>. Defaults to
        /// <c>all</c>. An unrecognized value normalizes to <c>all</c> (mirrors Unity's
        /// <c>FindMembersTool</c> fallback).
        /// </summary>
        internal string Kind { get; private set; } = "all";

        /// <summary>
        /// Assembly simple-name contains filter (case-insensitive). Null when unset — all eligible
        /// assemblies are scanned.
        /// </summary>
        internal string? AssemblyFilter { get; private set; }

        /// <summary>
        /// Include Godot editor assemblies (names starting with <c>GodotSharpEditor</c> or containing
        /// <c>Editor</c>). Defaults to <c>true</c>. Replaces Unity's <c>include_unity_editor</c>.
        /// </summary>
        internal bool IncludeGodotEditor { get; private set; } = true;

        /// <summary>
        /// Include the game/scripts assembly and its dependencies. Defaults to <c>true</c>. Mirrors
        /// Unity's <c>include_project</c>.
        /// </summary>
        internal bool IncludeProject { get; private set; } = true;

        /// <summary>
        /// Include the flat <c>signature</c> string and the structured parameter/generic fields on
        /// each member. Defaults to <c>true</c>. Set <c>false</c> for a lighter payload (names only).
        /// Mirrors Unity's <c>include_signatures</c>.
        /// </summary>
        internal bool IncludeSignatures { get; private set; } = true;

        /// <summary>
        /// Hard cap on the number of members returned. Defaults to 50, minimum 1, hard maximum 200.
        /// The remainder count is reported in the result's <c>truncated</c> field. Mirrors Unity's
        /// <c>max_results</c> clamp.
        /// </summary>
        internal int MaxResults { get; private set; } = DefaultMaxResults;

        /// <summary>
        /// Optional: limit the member enumeration to a single declaring type. When set, the
        /// <c>type</c> kind is redundant (the type itself is returned once at most); <c>method</c> /
        /// <c>property</c> enumerate only that type's declared members (faster drill-down after a type
        /// hit). Mirrors Unity's per-type drill-down idiom.
        /// </summary>
        internal string? TypeName { get; private set; }

        /// <summary>Default <see cref="MaxResults"/> when the caller omits it. Matches Unity Open
        /// MCP's <c>find_members</c> default so an agent migrating between the projects sees the same
        /// discovery bound.</summary>
        internal const int DefaultMaxResults = 50;

        /// <summary>Hard ceiling on <see cref="MaxResults"/> regardless of the requested value.</summary>
        internal const int HardMaxResults = 200;

        /// <summary>Valid <see cref="Kind"/> values (an unrecognized kind normalizes to
        /// <c>all</c>).</summary>
        internal static readonly string[] ValidKinds = { "type", "method", "property", "all" };

        internal bool WantsTypes => Kind == "type" || Kind == "all";
        internal bool WantsMethods => Kind == "method" || Kind == "all";
        internal bool WantsProperties => Kind == "property" || Kind == "all";

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="ReflectionMethodFindBody"/>. Never throws —
        /// a missing or malformed field falls back to its default. Empty/null body returns an
        /// all-default instance (broad scan, all kinds, max_results=50).
        /// </summary>
        internal static ReflectionMethodFindBody Parse(string? body)
        {
            var parsed = new ReflectionMethodFindBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.Query = ExtractStringValue(body, "query");
            var kind = ExtractNullableStringValue(body, "kind");
            if (!string.IsNullOrEmpty(kind))
            {
                // Normalize case-insensitively against the valid set; unknown → "all".
                foreach (var valid in ValidKinds)
                {
                    if (string.Equals(kind, valid, StringComparison.OrdinalIgnoreCase))
                    {
                        parsed.Kind = valid;
                        break;
                    }
                }
            }
            parsed.AssemblyFilter = ExtractNullableStringValue(body, "assembly_filter");
            parsed.IncludeGodotEditor = ExtractBoolValue(body, "include_godot_editor", true);
            parsed.IncludeProject = ExtractBoolValue(body, "include_project", true);
            parsed.IncludeSignatures = ExtractBoolValue(body, "include_signatures", true);
            parsed.TypeName = ExtractNullableStringValue(body, "type_name");
            parsed.MaxResults = ClampMaxResults(ExtractIntValue(body, "max_results", DefaultMaxResults));
            return parsed;
        }

        /// <summary>Clamp the caller-supplied cap to [1, <see cref="HardMaxResults"/>]. A non-positive
        /// or malformed value falls back to the default.</summary>
        internal static int ClampMaxResults(int value)
        {
            if (value <= 0) return DefaultMaxResults;
            if (value > HardMaxResults) return HardMaxResults;
            return value;
        }

        ReflectionMethodFindBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Same style as NodeFindBody: locate `"key"`, walk past the colon, read the scalar. Strings
        // are unwrapped from their quotes and unescaped for the standard JSON escape set; integers
        // are parsed invariant-culture; booleans read the leading t/f. A field that is
        // present-but-null is treated as absent.

        static string ExtractStringValue(string body, string key)
        {
            var raw = ExtractRawValue(body, key);
            return raw ?? string.Empty;
        }

        static string? ExtractNullableStringValue(string body, string key)
        {
            return ExtractRawValue(body, key);
        }

        static bool ExtractBoolValue(string body, string key, bool defaultValue)
        {
            var raw = ExtractRawValue(body, key);
            if (raw == null) return defaultValue;
            var trimmed = raw.AsSpan().Trim();
            if (trimmed.Length == 0) return defaultValue;
            if (trimmed[0] == 't') return true;
            if (trimmed[0] == 'f') return false;
            return defaultValue;
        }

        static int ExtractIntValue(string body, string key, int defaultValue)
        {
            var raw = ExtractRawValue(body, key);
            if (raw == null) return defaultValue;
            var trimmed = raw.AsSpan().Trim();
            int end = 0;
            if (trimmed.Length > 0 && (trimmed[0] == '-' || trimmed[0] == '+')) end = 1;
            while (end < trimmed.Length && char.IsDigit(trimmed[end])) end++;
            if (end == 0 || (end == 1 && trimmed.Length > 0 && (trimmed[0] == '-' || trimmed[0] == '+')))
                return defaultValue;
            return int.TryParse(trimmed.Slice(0, end), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
        }

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

            // String value: slice from opening quote to the matching closing quote, honoring escapes.
            if (body[start] == '"')
                return SliceQuotedString(body, start);

            // Number / boolean / etc.: slice to the next comma or closing brace/bracket.
            return SliceBareToken(body, start);
        }

        static string SliceQuotedString(string body, int start)
        {
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
    }
}
