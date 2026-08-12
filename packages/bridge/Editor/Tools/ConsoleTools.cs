#if TOOLS
#nullable enable
using System.Globalization;
using System.Text;
using GodotOpenMcp.Bridge.Runtime.Logging;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Console log tool family (P4.7) — the Godot analog of Unity Open MCP's <c>read-console</c>
    /// (read) and <c>console-clear</c> (clear). Two tools:
    /// <list type="bullet">
    /// <item><description><c>godot_open_mcp_console_get_logs</c> (read-only) — query the addon's
    /// bounded log collector, newest-first, with severity / age / max-entries filters and optional
    /// stack traces.</description></item>
    /// <item><description><c>godot_open_mcp_console_clear_logs</c> (gate-free direct) — empty the
    /// addon collector only; never the native editor Output panel.</description></item>
    /// </list>
    ///
    /// <para>
    /// <b>Capture scope is version-gated (P18.3).</b> On the Godot 4.3 baseline the collector is fed
    /// only by the plugin's own <c>BridgeLog</c> path + tool-handler error capture — a faithful record
    /// of the Godot Open MCP plugin's own activity, but not every line in the editor's Output panel.
    /// On Godot 4.5+ the bridge arms the global managed <c>Logger</c> hook (<c>OS.add_logger</c>,
    /// introduced in 4.5 via PR #91006) so the collector ALSO ingests native prints /
    /// <c>push_warning</c> / <c>push_error</c> from the moment the plugin enables. The response
    /// carries explicit capture-capability metadata (<c>capture.mode</c> = <c>addon_only</c> |
    /// <c>native_output</c>) so callers know which mode is active; the arming is reflection-based and
    /// any failure degrades gracefully back to addon-only (never assumed).
    /// </para>
    ///
    /// <para>
    /// <b>Clear is gate-free.</b> It mutates only ephemeral addon-owned collector state, not project
    /// files or Godot editor state. Checkpoint/delta cannot meaningfully cover ephemeral memory, so
    /// the clear is a direct response — never a fake <c>paths_hint</c>. The response always reports
    /// <c>nativeOutputCleared: false</c>.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): the handlers touch the process-wide
    /// <see cref="GodotLogCollector"/>. The pure-managed pieces (<see cref="GodotLogEntry"/>,
    /// <see cref="GodotLogCollector"/>) live in <c>Runtime/Logging/</c> outside the guard and are
    /// unit-tested in the binary-less host.
    /// </summary>
    internal static class ConsoleTools
    {
        /// <summary>The MCP tool name for the console-log read (P4.7).</summary>
        internal const string ConsoleGetLogsToolName = "godot_open_mcp_console_get_logs";

        /// <summary>The MCP tool name for the console-log clear (P4.7).</summary>
        internal const string ConsoleClearLogsToolName = "godot_open_mcp_console_clear_logs";

        /// <summary>Default <c>max_entries</c> when the caller omits it.</summary>
        internal const int DefaultMaxEntries = 100;

        /// <summary>Minimum <c>max_entries</c> (range 1–1000).</summary>
        internal const int MinMaxEntries = 1;

        /// <summary>Maximum <c>max_entries</c> (range 1–1000).</summary>
        internal const int MaxMaxEntries = 1000;

        /// <summary>Hard cap on <c>last_minutes</c> (non-negative). Protects against an unbounded
        /// window that would degenerate the filter to "all".</summary>
        internal const int MaxLastMinutes = 1_000_000;

        // --- registration -----------------------------------------------------------

        /// <summary>
        /// Register the console log tool family (P4.7). Both tools are read-only / gate-free direct
        /// (group <c>editor</c>, default gate <c>off</c>) — get reads the collector, clear mutates
        /// only ephemeral addon state (no project files, no native Output panel). Registered once at
        /// plugin enable; safe to call again on re-enable (the registry is idempotent).
        /// </summary>
        internal static void RegisterConsoleTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ConsoleGetLogsToolName,
                isMutating: false,
                defaultGate: "off",
                group: "editor",
                handler: GetLogs));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ConsoleClearLogsToolName,
                isMutating: false,
                defaultGate: "off",
                group: "editor",
                handler: ClearLogs));
        }

        // --- godot_open_mcp_console_get_logs ---------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_console_get_logs</c>. Read-only / gate-free. Queries the
        /// process-wide <see cref="GodotLogCollector"/> and returns the matching entries newest-first,
        /// with capture-capability metadata so callers do not assume complete native logs.
        ///
        /// <para>
        /// An empty result is a successful response with capture metadata, NOT an error — the
        /// collector may simply have no matching lines.
        /// </para>
        ///
        /// Structured failures: <c>invalid_max_entries</c>, <c>invalid_log_type</c>,
        /// <c>invalid_last_minutes</c>, <c>console_unavailable</c>. Must not throw — exceptions are
        /// caught by the dispatcher and surfaced as <c>execution_error</c>.
        /// </summary>
        internal static ToolDispatchResult GetLogs(string body)
        {
            // Parse the scalar filters off the raw body.
            var maxEntries = ExtractInt(body, "max_entries", DefaultMaxEntries);
            if (maxEntries < MinMaxEntries)
                return ToolDispatchResult.Fail(
                    "invalid_max_entries",
                    $"max_entries must be in [{MinMaxEntries}, {MaxMaxEntries}]; got {maxEntries}.");
            if (maxEntries > MaxMaxEntries)
                maxEntries = MaxMaxEntries;

            var includeStackTrace = ExtractBool(body, "include_stack_trace", defaultValue: false);
            var lastMinutes = ExtractInt(body, "last_minutes", defaultValue: 0);
            if (lastMinutes < 0)
                return ToolDispatchResult.Fail(
                    "invalid_last_minutes",
                    $"last_minutes must be non-negative; got {lastMinutes}.");
            if (lastMinutes > MaxLastMinutes)
                lastMinutes = MaxLastMinutes;

            // Severity filter: an array of "log" | "warning" | "error". The collector's Query takes a
            // single severity; an array with multiple values means "any of these" — we fan out by
            // querying without a filter and then filtering the result set locally when more than one
            // value is present. A single-value array maps to the collector's filter directly.
            var filterTokens = ExtractStringArray(body, "log_type_filter");
            GodotLogType? singleFilter = null;
            bool multiFilter = false;
            var multiFilterSet = new System.Collections.Generic.HashSet<GodotLogType>();
            if (filterTokens != null)
            {
                foreach (var token in filterTokens)
                {
                    var t = token?.Trim();
                    if (string.IsNullOrEmpty(t)) continue;
                    switch (t!.ToLowerInvariant())
                    {
                        case "log":
                            multiFilterSet.Add(GodotLogType.Log);
                            break;
                        case "warning":
                            multiFilterSet.Add(GodotLogType.Warning);
                            break;
                        case "error":
                            multiFilterSet.Add(GodotLogType.Error);
                            break;
                        default:
                            return ToolDispatchResult.Fail(
                                "invalid_log_type",
                                $"log_type_filter value '{t}' is not one of: log, warning, error.");
                    }
                }
                if (multiFilterSet.Count == 1)
                {
                    foreach (var v in multiFilterSet) singleFilter = v;
                }
                else if (multiFilterSet.Count > 1)
                {
                    multiFilter = true;
                }
            }

            var collector = GodotLogCollector.Current;
            if (collector == null)
                return ToolDispatchResult.Fail(
                    "console_unavailable",
                    "Log collector is not initialized.");

            System.Collections.Generic.List<GodotLogEntry> entries;
            if (multiFilter)
            {
                // Query everything matching the window, then filter locally to the multi-set.
                entries = collector.Query(
                    maxEntries: MaxMaxEntries,
                    logTypeFilter: null,
                    includeStackTrace: includeStackTrace,
                    lastMinutes: lastMinutes);
                var filtered = new System.Collections.Generic.List<GodotLogEntry>(entries.Count);
                foreach (var e in entries)
                {
                    if (multiFilterSet.Contains(e.LogType))
                    {
                        filtered.Add(e);
                        if (filtered.Count >= maxEntries) break;
                    }
                }
                entries = filtered;
            }
            else
            {
                entries = collector.Query(
                    maxEntries: maxEntries,
                    logTypeFilter: singleFilter,
                    includeStackTrace: includeStackTrace,
                    lastMinutes: lastMinutes);
            }

            var (retained, capacity) = collector.GetRetention();
            var sb = new StringBuilder(256 + entries.Count * 128);
            sb.Append('{');
            sb.Append("\"entries\":[");
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                entries[i].AppendJsonTo(sb);
            }
            sb.Append(']');
            sb.Append(",\"returned\":").Append(entries.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"retained\":").Append(retained.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"capacity\":").Append(capacity.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"order\":\"newest_first\"");
            sb.Append(",\"capture\":{");
            // P18.3 — capture-capability metadata is now dynamic: reflects whether the global Godot
            // 4.5+ Logger hook is armed (native_output) vs the pre-P18.3 addon-only capture. The
            // renderer is pure-managed (LogCaptureMode) so the metadata shape is unit-tested.
            GodotOpenMcp.Bridge.Runtime.Logging.LogCaptureMode.AppendCaptureJsonTo(sb);
            sb.Append('}');
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- godot_open_mcp_console_clear_logs -------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_console_clear_logs</c>. Gate-free direct (mutates only
        /// ephemeral addon-owned collector state — no project files, no native Output panel). Empties
        /// the process-wide <see cref="GodotLogCollector"/> and returns the number removed plus
        /// <c>nativeOutputCleared: false</c> (Godot's C# API exposes no managed hook to clear the
        /// editor's own Output panel).
        ///
        /// <para>
        /// The collector sequence is NOT reset on clear — a future P5.4 event-stream cursor stays
        /// monotonic across a clear so a consumer can tell "I have seen everything up to N" without
        /// ambiguity.
        /// </para>
        ///
        /// Must not throw — exceptions are caught by the dispatcher and surfaced as
        /// <c>execution_error</c>.
        /// </summary>
        internal static ToolDispatchResult ClearLogs(string body)
        {
            var collector = GodotLogCollector.Current;
            int removed = collector?.Clear() ?? 0;
            var (retained, _) = collector?.GetRetention() ?? (0, GodotLogCollector.Capacity);

            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"cleared\":").Append(removed.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"retained\":").Append(retained.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"nativeOutputCleared\":false");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- hand-rolled JSON scalar extraction (editor-only) -------------------------
        //
        // Same IndexOf style as the other editor tools. Duplicated here (per the bridge's
        // no-typed-JSON convention) so a bug in one parser cannot regress another.

        static int ExtractInt(string body, string key, int defaultValue)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, System.StringComparison.Ordinal);
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
                CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
        }

        static bool ExtractBool(string body, string key, bool defaultValue)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, System.StringComparison.Ordinal);
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

        /// <summary>
        /// Extract a JSON string array (<c>["a","b"]</c>) for <paramref name="key"/>. Returns null
        /// when the key is absent; returns the list (possibly empty) when present. A non-array value
        /// yields null.
        /// </summary>
        static System.Collections.Generic.List<string>? ExtractStringArray(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, System.StringComparison.Ordinal);
            if (idx < 0) return null;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length || body[start] != '[') return null;

            var result = new System.Collections.Generic.List<string>();
            int i = start + 1;
            while (i < body.Length)
            {
                while (i < body.Length && (char.IsWhiteSpace(body[i]) || body[i] == ',')) i++;
                if (i >= body.Length) break;
                if (body[i] == ']') break;
                if (body[i] != '"') return null; // non-string element → malformed
                i++;
                var element = new StringBuilder();
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
                            default: element.Append(nxt); i += 2; continue;
                        }
                    }
                    element.Append(body[i]);
                    i++;
                }
                if (i >= body.Length) return null;
                i++;
                result.Add(element.ToString());
            }
            return result;
        }
    }
}
#endif
