#nullable enable
using System;
using System.Globalization;
using System.Text;

namespace GodotOpenMcp.Bridge.Runtime.Logging
{
    /// <summary>
    /// Severity of a captured log line (P4.7) — the Godot analog of Unity's
    /// <c>UnityEngine.LogType</c>. Godot's logging surface (<c>GD.Print</c> / <c>GD.PushWarning</c> /
    /// <c>GD.PushError</c>) collapses to three levels, so this enum mirrors them rather than Unity's
    /// five (Unity's Assert/Exception have no distinct Godot counterpart).
    /// </summary>
    public enum GodotLogType
    {
        /// <summary>Informational message (GD.Print).</summary>
        Log = 0,

        /// <summary>Warning message (GD.PushWarning).</summary>
        Warning = 1,

        /// <summary>Error message (GD.PushError).</summary>
        Error = 2,
    }

    /// <summary>
    /// Origin of a captured log line (P4.7). Identifies which path ingested the line so an agent
    /// reading the logs can gauge capture scope — bridge/plugin logs are always captured; routed
    /// game/script/engine output is captured only when a supported hook is active.
    /// </summary>
    public enum GodotLogSource
    {
        /// <summary>Emitted by the Godot Open MCP bridge/plugin itself (BridgeLog path).</summary>
        Bridge = 0,

        /// <summary>Captured from game/script output via a supported hook (version-gated).</summary>
        Script = 1,

        /// <summary>Captured from engine/script errors via a supported hook (version-gated).</summary>
        Engine = 2,

        /// <summary>Captured from a tool handler error during dispatch.</summary>
        Tool = 3,
    }

    /// <summary>
    /// One captured log line (P4.7) — the Godot analog of Unity Open MCP's <c>LogEntry</c>. Holds the
    /// severity, message text, a UTC timestamp, an optional stack trace, a monotonic sequence number,
    /// and a source tag. Produced by <see cref="GodotLogCollector"/> and returned by the
    /// <c>console_get_logs</c> tool.
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so it is unit-testable in the
    /// binary-less xUnit host. Self-serializing via <see cref="AppendJsonTo"/> using
    /// <see cref="BridgeJson"/> for string escaping — the bridge carries no System.TextJson /
    /// Newtonsoft dependency per <c>packages/bridge/AGENTS.md</c> §Transport.
    /// </para>
    /// </summary>
    public sealed class GodotLogEntry
    {
        /// <summary>Monotonic sequence number assigned by the collector at ingestion. Deterministic
        /// ordering and the basis for a future P5.4 event-stream cursor.</summary>
        public long Sequence { get; set; }

        /// <summary>Severity of the log line (Log / Warning / Error).</summary>
        public GodotLogType LogType { get; set; } = GodotLogType.Log;

        /// <summary>The log message text. Truncated to <see cref="MaxMessageLength"/> at ingestion to
        /// bound memory/response size.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>UTC timestamp when the line was captured (ISO-8601 with milliseconds).</summary>
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Optional stack trace associated with the line; null when none was captured. Stored
        /// only when available; omitted from output unless requested. Truncated to
        /// <see cref="MaxStackTraceLength"/> at ingestion.</summary>
        public string? StackTrace { get; set; }

        /// <summary>Origin of the line (bridge / script / engine / tool). Helps callers gauge capture
        /// scope.</summary>
        public GodotLogSource Source { get; set; } = GodotLogSource.Bridge;

        /// <summary>Hard cap on the message length captured per line. Protects against a single
        /// pathological log line consuming the whole buffer.</summary>
        public const int MaxMessageLength = 4096;

        /// <summary>Hard cap on the stack-trace length captured per line.</summary>
        public const int MaxStackTraceLength = 8192;

        public GodotLogEntry() { }

        public GodotLogEntry(
            long sequence,
            GodotLogType logType,
            string message,
            DateTime timestampUtc,
            string? stackTrace = null,
            GodotLogSource source = GodotLogSource.Bridge)
        {
            Sequence = sequence;
            LogType = logType;
            Message = Truncate(message ?? string.Empty, MaxMessageLength);
            TimestampUtc = timestampUtc;
            StackTrace = string.IsNullOrEmpty(stackTrace) ? null : Truncate(stackTrace!, MaxStackTraceLength);
            Source = source;
        }

        /// <summary>The log type token emitted in the JSON envelope (<c>"log"</c> / <c>"warning"</c>
        /// / <c>"error"</c>). Matches the Unity Open MCP <c>logType</c> vocabulary.</summary>
        public string LogTypeToken => LogType switch
        {
            GodotLogType.Log => "log",
            GodotLogType.Warning => "warning",
            GodotLogType.Error => "error",
            _ => "log",
        };

        /// <summary>The source token emitted in the JSON envelope (<c>"bridge"</c> / <c>"script"</c>
        /// / <c>"engine"</c> / <c>"tool"</c>).</summary>
        public string SourceToken => Source switch
        {
            GodotLogSource.Bridge => "bridge",
            GodotLogSource.Script => "script",
            GodotLogSource.Engine => "engine",
            GodotLogSource.Tool => "tool",
            _ => "bridge",
        };

        /// <summary>
        /// Append this entry as a JSON object. Field order is fixed (sequence, logType, message,
        /// timestamp, stackTrace, source) so diffing clients don't flap on reordering. The
        /// <c>stackTrace</c> field is always present (null when absent or stripped) so the shape is
        /// stable regardless of the <c>include_stack_trace</c> query flag.
        /// </summary>
        public void AppendJsonTo(StringBuilder sb)
        {
            sb.Append('{');
            sb.Append("\"sequence\":").Append(Sequence.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"logType\":").Append(EscapeString(LogTypeToken)).Append(',');
            sb.Append("\"message\":").Append(EscapeString(Message)).Append(',');
            sb.Append("\"timestamp\":").Append(EscapeString(FormatTimestamp(TimestampUtc))).Append(',');
            sb.Append("\"stackTrace\":").Append(EscapeString(StackTrace)).Append(',');
            sb.Append("\"source\":").Append(EscapeString(SourceToken));
            sb.Append('}');
        }

        /// <summary>
        /// Wrap <paramref name="s"/> in quotes after escaping it; <c>null</c> → <c>"null"</c>.
        /// Inlined (rather than reaching into the editor-only <c>BridgeJson</c>) so this runtime-safe
        /// DTO compiles without <c>#if TOOLS</c> and stays unit-testable in the binary-less host.
        /// Same escape set as <c>BridgeJson.EscapeString</c>.
        /// </summary>
        static string EscapeString(string? s)
        {
            if (s == null) return "null";
            var inner = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': inner.Append("\\\""); break;
                    case '\\': inner.Append("\\\\"); break;
                    case '\n': inner.Append("\\n"); break;
                    case '\r': inner.Append("\\r"); break;
                    case '\t': inner.Append("\\t"); break;
                    default:
                        if (c < 32)
                            inner.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        else
                            inner.Append(c);
                        break;
                }
            }
            return "\"" + inner + "\"";
        }

        /// <summary>Serialize to a JSON string (convenience wrapper over
        /// <see cref="AppendJsonTo"/>).</summary>
        public string ToJsonString()
        {
            var sb = new StringBuilder(128);
            AppendJsonTo(sb);
            return sb.ToString();
        }

        /// <summary>Format a UTC <see cref="DateTime"/> as ISO-8601 with milliseconds and a trailing
        /// <c>Z</c> (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>). Matches the Unity Open MCP timestamp
        /// vocabulary.</summary>
        public static string FormatTimestamp(DateTime timestampUtc)
            => timestampUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        static string Truncate(string value, int max)
            => value.Length <= max ? value : value.Substring(0, max);
    }
}
