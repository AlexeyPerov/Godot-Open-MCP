#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_filesystem_reimport</c> (P4.4). Extracts the
    /// <c>files</c> string array (exact reimport) and a bounded <c>timeout_ms</c> straight off the
    /// raw JSON body using the same hand-rolled <c>IndexOf</c>-substring style as
    /// <see cref="ResourceFindBody"/> / <see cref="ResourceDeleteBody"/> — the bridge deliberately
    /// carries no typed JSON DOM dependency on the hot path
    /// (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// Two modes (mutually exclusive, selected by whether <see cref="Files"/> is non-empty):
    /// <list type="bullet">
    /// <item><description><b>files</b> — reimport exactly those <c>res://</c> files via
    /// <c>EditorFileSystem.ReimportFiles</c>. The entire list is validated before any file is
    /// touched (no partial effect).</description></item>
    /// <item><description><b>full scan</b> — trigger <c>EditorFileSystem.Scan</c> to pick up
    /// added/removed/changed files.</description></item>
    /// </list>
    /// The handler reads <c>paths_hint</c>/<c>gate</c> via <see cref="BridgeRequestBody"/> (the
    /// dispatcher-level scalars); this parser owns only the reimport-specific fields.
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is
    /// unit-testable in the binary-less xUnit host.
    /// </para>
    /// </summary>
    internal sealed class FileSystemReimportBody
    {
        /// <summary>List of <c>res://</c> file paths to reimport. Null when the field is absent;
        /// empty when present-but-empty. Non-empty selects exact-file mode; null/empty selects
        /// full-scan mode.</summary>
        internal string[]? Files { get; private set; }

        /// <summary>True when the <c>files</c> field was present in the body (even if empty). Used
        /// to distinguish "omitted → full scan" from "present-but-empty → also full scan" (both
        /// behave the same, but the distinction is useful for diagnostics).</summary>
        internal bool HasFilesField { get; private set; }

        /// <summary>True when exact-file mode is selected (a non-empty <see cref="Files"/>
        /// list).</summary>
        internal bool IsExactFilesMode => Files != null && Files.Length > 0;

        /// <summary>Bounded settle timeout in milliseconds. Defaults to
        /// <see cref="DefaultTimeoutMs"/>; clamped to
        /// [&lt;<see cref="MinTimeoutMs"/>, <see cref="MaxTimeoutMs"/>].</summary>
        internal int TimeoutMs { get; private set; } = DefaultTimeoutMs;

        /// <summary>Default settle timeout when the caller omits <c>timeout_ms</c>.</summary>
        internal const int DefaultTimeoutMs = 5_000;

        /// <summary>Minimum clamped timeout. A sub-second value is clamped up so the scan is given
        /// a genuine chance to start and drain.</summary>
        internal const int MinTimeoutMs = 1_000;

        /// <summary>Maximum clamped timeout. Caps a runaway caller value so a wedged import
        /// pipeline surfaces as <c>settled:false</c> within a bounded wait rather than hanging the
        /// dispatch indefinitely.</summary>
        internal const int MaxTimeoutMs = 60_000;

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="FileSystemReimportBody"/>. Never throws
        /// — a missing or malformed field falls back to its default. Empty/null body returns an
        /// all-default instance (full-scan mode, default timeout).
        /// </summary>
        internal static FileSystemReimportBody Parse(string? body)
        {
            var parsed = new FileSystemReimportBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.Files = ExtractStringArray(body, "files", out var fieldPresent);
            parsed.HasFilesField = fieldPresent;

            var raw = ExtractIntValue(body, "timeout_ms", int.MinValue);
            if (raw == int.MinValue)
                raw = ExtractIntValue(body, "timeoutMs", DefaultTimeoutMs);
            parsed.TimeoutMs = Math.Clamp(raw, MinTimeoutMs, MaxTimeoutMs);

            return parsed;
        }

        FileSystemReimportBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Mirrors BridgeRequestBody.ExtractPathsHint's array scan: walk the JSON array, pulling
        // each quoted string element. A non-string element yields a null array (malformed) so the
        // handler rejects it rather than silently dropping elements. A field present-but-null is
        // treated as absent (full-scan mode).

        static string[]? ExtractStringArray(string body, string key, out bool fieldPresent)
        {
            fieldPresent = false;
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;

            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return null;

            // present-but-null → absent (full-scan mode).
            if (start + 4 <= body.Length && body.Substring(start, 4) == "null") return null;
            // Must be a JSON array; anything else is a contract violation → null.
            if (body[start] != '[') return null;

            fieldPresent = true;
            var result = new System.Collections.Generic.List<string>();
            var i = start + 1;
            while (i < body.Length)
            {
                while (i < body.Length && (char.IsWhiteSpace(body[i]) || body[i] == ',')) i++;
                if (i >= body.Length) break;
                if (body[i] == ']') break;
                if (body[i] != '"') return null; // non-string element → malformed

                i++;
                var element = new System.Text.StringBuilder();
                while (i < body.Length && body[i] != '"')
                {
                    if (body[i] == '\\' && i + 1 < body.Length)
                    {
                        var nxt = body[i + 1];
                        switch (nxt)
                        {
                            case '"': element.Append('"'); i += 2; continue;
                            case '\\': element.Append('\\'); i += 2; continue;
                            case '/': element.Append('/'); i += 2; continue;
                            case 'n': element.Append('\n'); i += 2; continue;
                            case 'r': element.Append('\r'); i += 2; continue;
                            case 't': element.Append('\t'); i += 2; continue;
                            case 'b': element.Append('\b'); i += 2; continue;
                            case 'f': element.Append('\f'); i += 2; continue;
                            case 'u' when i + 5 < body.Length:
                                if (int.TryParse(body.Substring(i + 2, 4),
                                        System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture, out var code))
                                    element.Append((char)code);
                                i += 6;
                                continue;
                            default:
                                element.Append(nxt); i += 2; continue;
                        }
                    }
                    element.Append(body[i]);
                    i++;
                }
                if (i >= body.Length) return null; // unterminated string
                i++; // consume closing quote
                result.Add(element.ToString());
            }

            return result.ToArray();
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
    }
}
