#if TOOLS
#nullable enable
using System;
using System.IO;
using System.Net;
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Request-body parsing for the tool dispatcher (P2.1). Extracts the scalar fields the
    /// dispatcher needs straight off the raw JSON body — currently just <c>timeout_ms</c> — plus
    /// the timeout clamping bounds. Later phases add <c>gate</c> extraction here (P3.5).
    ///
    /// <para>
    /// Adapted from Unity Open MCP's <c>BridgeRequestBody</c> (copy fidelity for the timeout
    /// extraction): same hand-rolled <c>IndexOf</c> substring parsing rather than a typed JSON
    /// DOM, because it runs on the hot dispatch path and only needs one scalar. Unity also
    /// extracts <c>gate</c> and a <c>paths_hint</c>-from-issue_id helper here; both are deferred
    /// to P3.5 (gate wiring) so this P2.1 port keeps only the timeout.
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
    }
}
#endif
