#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_filesystem_list</c> (P4.4). Extracts the scalar
    /// fields the list handler needs straight off the raw JSON body using the same hand-rolled
    /// <c>IndexOf</c>-substring style as <see cref="ResourceFindBody"/> /
    /// <see cref="ResourceDeleteBody"/> — the bridge deliberately carries no typed JSON DOM
    /// dependency on the hot path (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// The handler accepts an optional <c>res://</c> directory (default the project root), a
    /// bounded <c>page_size</c> (default 100, hard cap 500), an opaque continuation
    /// <c>cursor</c>, and an <c>include_hidden</c> flag (default false). The handler normalizes the
    /// directory (trailing-slash form) and rejects non-<c>res://</c>/traversal/file inputs.
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is
    /// unit-testable in the binary-less xUnit host.
    /// </para>
    /// </summary>
    internal sealed class FileSystemListBody
    {
        /// <summary><c>res://</c> directory to list. Null/empty defaults to the project root
        /// (<c>res://</c>) — the handler resolves the default.</summary>
        internal string? Path { get; private set; }

        /// <summary>True when <see cref="Path"/> is present (non-whitespace).</summary>
        internal bool HasPath => !string.IsNullOrWhiteSpace(Path);

        /// <summary>Bounded positive page size. Defaults to <see cref="DefaultPageSize"/>; clamped
        /// to [&lt;1, <see cref="MaxPageSize"/>].</summary>
        internal int PageSize { get; private set; } = DefaultPageSize;

        /// <summary>Opaque continuation cursor for paging. Null when unset (first page).</summary>
        internal string? Cursor { get; private set; }

        /// <summary>When true, include hidden entries the editor index exposes. Defaults to
        /// false.</summary>
        internal bool IncludeHidden { get; private set; }

        /// <summary>Default page size when the caller omits it.</summary>
        internal const int DefaultPageSize = 100;

        /// <summary>Hard cap on <see cref="PageSize"/>.</summary>
        internal const int MaxPageSize = 500;

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="FileSystemListBody"/>. Never throws — a
        /// missing or malformed field falls back to its default. Empty/null body returns an
        /// all-default instance (the handler lists the project root with default paging).
        /// </summary>
        internal static FileSystemListBody Parse(string? body)
        {
            var parsed = new FileSystemListBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.Path = ExtractNullableStringValue(body, "path");
            parsed.Cursor = ExtractNullableStringValue(body, "cursor");

            var raw = ExtractIntValue(body, "page_size", int.MinValue);
            if (raw == int.MinValue)
            {
                // snake_case key absent — try the camelCase alias.
                raw = ExtractIntValue(body, "pageSize", DefaultPageSize);
            }
            parsed.PageSize = Math.Max(1, Math.Min(MaxPageSize, raw));

            parsed.IncludeHidden = ExtractBoolValue(body, "include_hidden", false);
            if (!parsed.IncludeHidden)
                parsed.IncludeHidden = ExtractBoolValue(body, "includeHidden", false);

            return parsed;
        }

        FileSystemListBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Mirrors ResourceFindBody.Extract*: locate `"key"`, walk past the colon, read the scalar.
        // Strings are unwrapped from quotes and unescaped for the JSON escape set; a field
        // present-but-null is treated as absent. The extraction helpers are duplicated per the
        // bridge's no-typed-JSON convention (isolated parsers mean a bug in one cannot regress
        // another).

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
