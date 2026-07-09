#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Request-body parsing for the tool dispatcher (P2.1). Extracts the scalar fields the dispatcher
    /// needs straight off the raw JSON body — <c>timeout_ms</c> (P2.1), <c>gate</c> and
    /// <c>paths_hint</c> (P3.5) — plus the timeout clamping bounds.
    ///
    /// <para>
    /// Adapted from Unity Open MCP's <c>BridgeRequestBody</c> (copy fidelity for the timeout and gate
    /// extraction): same hand-rolled <c>IndexOf</c> substring parsing rather than a typed JSON DOM,
    /// because it runs on the hot dispatch path and only needs a handful of scalars. Unity also has a
    /// <c>paths_hint</c>-from-issue_id helper (used by <c>apply_fix</c>); that arrives with P3.7 when
    /// the first fix tool lands.
    /// </para>
    /// </summary>
    internal static class BridgeRequestBody
    {
        /// <summary>
        /// Default per-tool dispatch timeout (ms) when the caller omits <c>timeout_ms</c> or passes
        /// an unparseable value. Matches Unity's <c>DefaultTimeoutMs</c> so an agent migrating
        /// between the two projects sees the same wait before the bridge gives up.
        /// </summary>
        internal const int DefaultTimeoutMs = 30_000;

        /// <summary>
        /// Minimum clamped timeout. A caller passing a sub-second value gets clamped up to this
        /// rather than failing fast — editor main-thread hops rarely complete under 1s, and a
        /// sub-second timeout would surface <c>main_thread_blocked</c> for work that simply hadn't
        /// been scheduled yet.
        /// </summary>
        internal const int MinTimeoutMs = 1_000;

        /// <summary>
        /// Maximum clamped timeout. Caps a runaway caller value so a misbehaving agent cannot hold
        /// a bridge worker thread indefinitely. Matches Unity's ceiling.
        /// </summary>
        internal const int MaxTimeoutMs = 600_000;

        /// <summary>
        /// Read the raw request body as a UTF-8 string. The dispatcher parses it with the
        /// <c>Extract*</c> helpers below; a typed JSON DOM is not constructed because only one or
        /// two scalars are needed on the hot path. Mirrors Unity's <c>ReadRequestBody</c>.
        /// </summary>
        internal static string ReadRequestBody(HttpListenerRequest request)
        {
            using var stream = request.InputStream;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// Extract <c>timeout_ms</c> from the raw JSON body, clamped to
        /// [<see cref="MinTimeoutMs"/>, <see cref="MaxTimeoutMs"/>]. Returns
        /// <see cref="DefaultTimeoutMs"/> when the field is absent or unparseable — never throws,
        /// never returns a value outside the clamp range. Adapted from Unity's
        /// <c>ExtractTimeoutMs</c>.
        /// </summary>
        internal static int ExtractTimeoutMs(string body)
        {
            if (string.IsNullOrEmpty(body)) return DefaultTimeoutMs;

            const string key = "\"timeout_ms\"";
            var idx = body.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return DefaultTimeoutMs;

            var colonIdx = body.IndexOf(':', idx + key.Length);
            if (colonIdx < 0) return DefaultTimeoutMs;

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;

            // Allow an optional leading sign so negative values parse and then get clamped to the
            // minimum, rather than falling through to the default (which would silently ignore the
            // caller's explicit value).
            var signEnd = start;
            if (signEnd < body.Length && (body[signEnd] == '-' || body[signEnd] == '+'))
                signEnd++;

            var end = signEnd;
            while (end < body.Length && char.IsDigit(body[end])) end++;

            if (end == signEnd || !int.TryParse(body.Substring(start, end - start), out var ms))
                return DefaultTimeoutMs;

            return Math.Clamp(ms, MinTimeoutMs, MaxTimeoutMs);
        }

        /// <summary>
        /// Resolve the effective gate mode for a dispatch, applying the precedence chain
        /// (<c>packages/bridge/AGENTS.md</c> §Gate policy):
        /// <list type="number">
        ///   <item>Request body <c>"gate"</c> string — the agent's per-call opt-in.</item>
        ///   <item>Project default (<see cref="GateDefaultPolicy.GetProjectDefault"/>).</item>
        ///   <item><paramref name="toolDefault"/> — the tool's registered default.</item>
        /// </list>
        /// Returns the first non-null, valid value. An invalid request value (typo, unknown mode) is
        /// ignored and the chain falls through — a malformed opt-in never silently disables the gate,
        /// because it falls to the project/tool default rather than to "off". Never throws.
        ///
        /// <para>
        /// Adapted from Unity Open MCP's <c>ExtractGateMode</c> (copy fidelity for the hand-rolled
        /// IndexOf parser). Unity resolves request → project only and treats the tool attribute as
        /// catalog metadata; this port threads the tool default through so the dispatcher can pass
        /// <see cref="BridgeToolEntry.DefaultGate"/> without a second lookup.
        /// </para>
        /// </summary>
        internal static string ExtractGateMode(string body, string toolDefault)
        {
            // (2) Project default is the fallback between request and tool default. v1 always returns
            // null (no settings reader), so the effective chain is request → toolDefault.
            var projectDefault = GateDefaultPolicy.GetProjectDefault();

            // (1) Request body. Parse the quoted string the same way as ExtractTimeoutMs's scalar scan.
            if (!string.IsNullOrEmpty(body))
            {
                const string key = "\"gate\"";
                var idx = body.IndexOf(key, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var colonIdx = body.IndexOf(':', idx + key.Length);
                    if (colonIdx >= 0)
                    {
                        var start = colonIdx + 1;
                        while (start < body.Length && char.IsWhiteSpace(body[start])) start++;

                        if (start < body.Length && body[start] == '"')
                        {
                            start++;
                            var end = start;
                            while (end < body.Length && body[end] != '"') end++;
                            if (end > start)
                            {
                                var value = body.Substring(start, end - start);
                                if (GateDefaultPolicy.IsValid(value))
                                    return value;
                            }
                        }
                    }
                }
            }

            // Fall through the chain: project default (if set), then the tool default.
            return GateDefaultPolicy.IsValid(projectDefault) ? projectDefault! : toolDefault;
        }

        /// <summary>
        /// Extract the <c>"paths_hint"</c> JSON string array from the body. Returns null when the field
        /// is absent; returns the array (possibly empty) when present. Never throws — a malformed array
        /// yields null so the dispatcher's emptiness guard fires as <c>paths_hint_required</c> rather
        /// than a parse fault. The mutation scope (<c>res://</c> paths) the gate checkpoints/validates
        /// against; there is no whole-project fallback when the gate is active.
        ///
        /// <para>
        /// Adapted from Unity Open MCP's <c>JsonBody.GetStringArray</c> (copy fidelity for the array
        /// scan), inlined here so the dispatcher's body-parsing stays in one place (the bridge has no
        /// shared JSON-DOM helper — every extractor is a hand-rolled IndexOf scan, per the
        /// <c>ExtractTimeoutMs</c> convention).
        /// </para>
        /// </summary>
        internal static string[]? ExtractPathsHint(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;

            const string key = "\"paths_hint\"";
            var idx = body.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;

            var colonIdx = body.IndexOf(':', idx + key.Length);
            if (colonIdx < 0) return null;

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return null;

            // Must be a JSON array. A non-array value (e.g. a stray string) yields null so the caller's
            // emptiness guard handles it — the contract is "absent or a string array", nothing else.
            if (body[start] != '[') return null;

            // Walk the array, pulling each quoted string element. Skip whitespace, commas, and any
            // non-string element (a non-string element is a contract violation; treat the whole array as
            // absent rather than silently dropping elements — an agent who passed paths_hint:[42] made a
            // mistake worth surfacing as paths_hint_required).
            var result = new List<string>();
            var i = start + 1;
            while (i < body.Length)
            {
                // Skip whitespace and commas between elements.
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
                        // Honor the same JSON escape set as SliceBodyQuotedString in NodeTools so a path
                        // with an escaped quote or backslash round-trips. \uXXXX is decoded to its char.
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
    }
}
#endif
