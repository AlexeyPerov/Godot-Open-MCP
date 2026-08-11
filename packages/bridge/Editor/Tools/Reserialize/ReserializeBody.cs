#nullable enable
using System;
using System.Collections.Generic;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_reserialize</c> (P17.2). Extracts the
    /// <c>paths</c> JSON string array straight off the raw body using the same hand-rolled
    /// <c>IndexOf</c>-substring scan as <see cref="BridgeRequestBody.ExtractPathsHint"/> — the
    /// bridge deliberately carries no typed JSON DOM dependency on the hot path
    /// (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// Each entry is a <c>res://</c> path that may be either a writable resource file
    /// (<c>.tres</c>/<c>.tscn</c>/<c>.res</c>) or a directory (expanded recursively by the
    /// handler). A missing/malformed array yields an empty list (the handler rejects this with
    /// <c>missing_parameter</c>).
    /// </para>
    ///
    /// <para>
    /// Adapted from Unity Open MCP's <c>reserialize</c> path-list input (adapt fidelity for the
    /// array shape), with the scan inlined here so this parser stays self-contained (the bridge
    /// has no shared JSON-DOM helper — every extractor is a hand-rolled IndexOf scan, per the
    /// <c>ExtractPathsHint</c> convention). Duplicated per the bridge's no-typed-JSON convention:
    /// isolated parsers mean a bug in one cannot regress another.
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is
    /// unit-testable in the binary-less xUnit host.
    /// </para>
    /// </summary>
    internal sealed class ReserializeBody
    {
        /// <summary>
        /// Parsed <c>paths</c> entries. May be empty when the key is absent/malformed (the handler
        /// rejects this with <c>missing_parameter</c>). Order is preserved; duplicates are NOT
        /// removed here (the handler dedups after folder expansion).
        /// </summary>
        internal List<string> Paths { get; private set; } = new List<string>();

        /// <summary>Hard cap on the number of paths in a single request — bounds the fan-out when a
        /// directory expands. Mirrors the execution-plan "remain under hard count/size limits"
        /// guidance.</summary>
        internal const int MaxPaths = 200;

        /// <summary>True when at least one path was parsed.</summary>
        internal bool HasPaths => Paths.Count > 0;

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="ReserializeBody"/>. Never throws — a
        /// missing or malformed field falls back to its default. Empty/null body returns an
        /// all-default instance (the handler rejects this with <c>missing_parameter</c>).
        /// </summary>
        internal static ReserializeBody Parse(string? body)
        {
            var parsed = new ReserializeBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.Paths = ExtractStringArray(body, "paths");
            // Clamp to the hard cap — extra entries are dropped (the handler sees a partial list).
            if (parsed.Paths.Count > MaxPaths)
                parsed.Paths = parsed.Paths.GetRange(0, MaxPaths);

            return parsed;
        }

        ReserializeBody() { }

        // --- hand-rolled JSON string-array extraction --------------------------------
        //
        // Mirrors BridgeRequestBody.ExtractPathsHint: locate `"key"`, walk past the colon, require
        // a `[`, then pull each quoted string element. A non-array value or a non-string element
        // yields an empty list (the handler's missing_parameter guard surfaces the contract
        // violation rather than silently dropping elements — an agent who passed paths:[42] made a
        // mistake worth surfacing).

        static List<string> ExtractStringArray(string body, string key)
        {
            var result = new List<string>();
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return result;

            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return result;

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length || body[start] != '[') return result;

            var i = start + 1;
            while (i < body.Length)
            {
                // Skip whitespace and commas between elements.
                while (i < body.Length && (char.IsWhiteSpace(body[i]) || body[i] == ',')) i++;
                if (i >= body.Length) break;
                if (body[i] == ']') break;
                if (body[i] != '"') return new List<string>(); // non-string element → malformed

                i++;
                var element = new System.Text.StringBuilder();
                while (i < body.Length && body[i] != '"')
                {
                    if (body[i] == '\\' && i + 1 < body.Length)
                    {
                        // Honor the same JSON escape set as SliceQuotedString in ResourceModifyBody
                        // so a path with an escaped quote or backslash round-trips. \uXXXX → char.
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
                // i sits on the closing quote (or end-of-body); advance past it.
                if (i < body.Length && body[i] == '"') i++;
                result.Add(element.ToString());
            }
            return result;
        }
    }
}
