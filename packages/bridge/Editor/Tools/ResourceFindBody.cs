#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_resource_find</c> (P4.1). Extracts the scalar fields
    /// the find handler needs straight off the raw JSON body using the same hand-rolled
    /// <c>IndexOf</c>-substring style as <see cref="NodeFindBody"/> and <see cref="SceneGetDataBody"/>
    /// — the bridge deliberately carries no typed JSON DOM dependency on the hot path
    /// (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// Selector precedence (matches the MCP schema description): <see cref="Uid"/> &gt;
    /// <see cref="ResourcePath"/> &gt; <see cref="TypeFilter"/>. When a direct selector (uid or path)
    /// is set, the handler resolves a single resource and ignores <see cref="TypeFilter"/> /
    /// <see cref="Directory"/>. When only <see cref="TypeFilter"/> is set, the handler runs an indexed
    /// recursive scan over <c>EditorFileSystem</c>.
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is
    /// unit-testable in the binary-less xUnit host.
    /// </para>
    /// </summary>
    internal sealed class ResourceFindBody
    {
        /// <summary>Exact <c>res://</c> path (or <c>uid://</c>) to resolve to a single resource.
        /// Null when unset. Priority 2 (below uid, above type-filter scan).</summary>
        internal string? ResourcePath { get; private set; }

        /// <summary><c>uid://</c> identifier to resolve to a single resource. Null when unset.
        /// Priority 1 — takes precedence over <see cref="ResourcePath"/>.</summary>
        internal string? Uid { get; private set; }

        /// <summary>Godot class/type name for the indexed recursive scan. Null when unset. Only
        /// applies when no direct selector (uid/path) is set.</summary>
        internal string? TypeFilter { get; private set; }

        /// <summary><c>res://</c> directory scope for the type-filtered scan. Null/empty defaults to
        /// <c>res://</c> (whole project). Only applies to the scan.</summary>
        internal string? Directory { get; private set; }

        /// <summary>Bounded positive page size for scan results. Defaults to
        /// <see cref="DefaultPageSize"/>; clamped to [&lt;1, <see cref="MaxPageSize"/>].</summary>
        internal int PageSize { get; private set; } = DefaultPageSize;

        /// <summary>Opaque continuation cursor for paging scan results. Null when unset (first
        /// page).</summary>
        internal string? Cursor { get; private set; }

        /// <summary>Default page size when the caller omits it.</summary>
        internal const int DefaultPageSize = 50;

        /// <summary>Hard cap on <see cref="PageSize"/>.</summary>
        internal const int MaxPageSize = 200;

        /// <summary>True when a direct lookup selector (uid or path) is set.</summary>
        internal bool IsDirectLookup
            => !string.IsNullOrWhiteSpace(Uid) || !string.IsNullOrWhiteSpace(ResourcePath);

        /// <summary>True when at least one selector is present.</summary>
        internal bool HasAnySelector
            => !string.IsNullOrWhiteSpace(Uid)
               || !string.IsNullOrWhiteSpace(ResourcePath)
               || !string.IsNullOrWhiteSpace(TypeFilter);

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="ResourceFindBody"/>. Never throws — a
        /// missing or malformed field falls back to its default. Empty/null body returns an
        /// all-default instance (no selectors — the handler rejects this with
        /// <c>invalid_request</c>).
        /// </summary>
        internal static ResourceFindBody Parse(string? body)
        {
            var parsed = new ResourceFindBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.Uid = ExtractNullableStringValue(body, "uid");
            parsed.ResourcePath = ExtractNullableStringValue(body, "resource_path");
            // Accept the canonical "resourcePath" alias too (camelCase, matching the result DTO).
            if (string.IsNullOrWhiteSpace(parsed.ResourcePath))
                parsed.ResourcePath = ExtractNullableStringValue(body, "resourcePath");
            parsed.TypeFilter = ExtractNullableStringValue(body, "type_filter");
            if (string.IsNullOrWhiteSpace(parsed.TypeFilter))
                parsed.TypeFilter = ExtractNullableStringValue(body, "typeFilter");
            parsed.Directory = ExtractNullableStringValue(body, "directory");
            parsed.Cursor = ExtractNullableStringValue(body, "cursor");

            var raw = ExtractIntValue(body, "page_size", DefaultPageSize);
            if (string.IsNullOrWhiteSpace(parsed.ResourcePath) && raw == DefaultPageSize)
            {
                // Accept the camelCase alias only when the snake_case key was absent AND the default
                // was applied (so an explicit 0 via camelCase is still honored below).
                raw = ExtractIntValue(body, "pageSize", DefaultPageSize);
            }
            parsed.PageSize = Math.Max(1, Math.Min(MaxPageSize, raw));

            return parsed;
        }

        ResourceFindBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Mirrors NodeFindBody.Extract* / SceneGetDataBody.Extract*: locate `"key"`, walk past the
        // colon, read the scalar. Strings are unwrapped from quotes and unescaped for the JSON
        // escape set (\" \\ \/ \n \r \t \uXXXX); a field present-but-null is treated as absent. The
        // extraction helpers are duplicated per the bridge's no-typed-JSON convention (each tool's
        // field set is tiny, isolated parsers mean a bug in one cannot regress another).

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
