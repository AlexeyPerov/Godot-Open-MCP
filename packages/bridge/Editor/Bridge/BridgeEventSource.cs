#if TOOLS
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using GodotOpenMcp.Bridge.Runtime.Logging;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Streaming notifications source (P5.4) — the Godot analog of Unity Open MCP's
    /// <c>BridgeEventSource</c>.
    ///
    /// <para>
    /// The bridge is otherwise pure request/response. Long ops (reimports, play launches,
    /// compiles) force an agent to poll. This module is the producer side of a notification
    /// channel: it captures console log entries (fanned in from the P4.7
    /// <see cref="GodotLogCollector"/>) and editor state transitions into a ring buffer that the
    /// <c>/events</c> SSE endpoint and <c>/events/poll</c> JSON endpoint drain. The MCP server
    /// keeps a process-local <c>BridgeEventStream</c> SSE subscription and surfaces it through the
    /// <c>godot_open_mcp_pull_events</c> tool (stdio cannot push bridge SSE to the model natively).
    /// </para>
    ///
    /// <para>
    /// Subscribers are identified by an opaque string id. Each subscriber tracks its own cursor over
    /// the ring buffer so a slow client never blocks a fast one — it just sees fewer events. The ring
    /// evicts the oldest event when full; a subscriber whose cursor falls behind the evicted tail gets
    /// a <c>missed</c> count on its next drain so the loss is never silent.
    /// </para>
    ///
    /// <para>
    /// <b>Threading.</b> Event production (collector sink, state-transition observer) happens on the
    /// editor main thread or the HTTP listener worker; the ring uses lock-free
    /// <see cref="ConcurrentQueue{T}"/> / <see cref="ConcurrentDictionary{TKey,TValue}"/> +
    /// <see cref="Interlocked"/> counters so produce/drain are safe from any thread. Drain (the SSE /
    /// poll response path) is pure memory — it never touches Godot APIs (packages/bridge/AGENTS.md
    /// §Transport).
    /// </para>
    ///
    /// <para>
    /// <b>Lifecycle.</b> Unity uses <c>[InitializeOnLoadMethod]</c>; Godot has no equivalent, so
    /// <see cref="GodotOpenMcpPlugin"/> calls <see cref="Initialize"/> on enable and <see cref="Stop"/>
    /// on disable. The collector fan-out sink is armed/disarmed there so log events start/stop with the
    /// addon. The event source itself is editor-only (<c>#if TOOLS</c>); the pure-managed ring math is
    /// unit-tested via the binary-less xUnit host (the test csproj defines TOOLS and links this file).
    /// </para>
    ///
    /// <para>
    /// <b>Fidelity.</b> Copy of Unity's ring + subscriber cursors + drain + render semantics; the log
    /// fan-in is adapted (Godot collector sink, not <c>Application.logMessageReceived</c>) and the
    /// state-transition hooks are adapted (Godot play/compile observation instead of Unity
    /// <c>EditorApplication</c> events). No <c>unity_senses_*</c> alias (ADR-003: single
    /// <c>godot_open_mcp_*</c> prefix).
    /// </para>
    /// </summary>
    internal static class BridgeEventSource
    {
        /// <summary>
        /// Cap the in-memory ring. Each event is a few hundred bytes; 1024 keeps several minutes of
        /// typical console chatter without unbounded growth. Matches Unity's <c>BufferCapacity</c>.
        /// </summary>
        public const int BufferCapacity = 1024;

        static readonly ConcurrentQueue<BridgeEvent> _buffer = new();
        static int _bufferCount;
        static long _totalEmitted;

        /// <summary>
        /// Per-subscriber cursor map. Keyed by the opaque subscriber id assigned on
        /// <see cref="Subscribe"/>; <see cref="SubscriberState.NextSequence"/> is the next sequence
        /// the subscriber wants to see. ConcurrentDictionary so drain (worker thread) and Subscribe
        /// (worker thread, minting an id for a fresh client) do not contend on a single lock.
        /// </summary>
        static readonly ConcurrentDictionary<string, SubscriberState> _subscribers = new();

        static bool _initialized;
        static bool _stopped;

        // --- Lifecycle -------------------------------------------------------------------------

        /// <summary>
        /// Arm the event source for production. Idempotent: a redundant call is a no-op. Wires the
        /// <see cref="GodotLogCollector"/> fan-out sink so every collected log line is emitted as a
        /// <c>log</c> event (single fan-in — no second raw Godot logger that would duplicate the
        /// collector, per the P5.4 risk table). Called from
        /// <see cref="GodotOpenMcpPlugin._EnterTree"/> AFTER the collector is installed so the sink
        /// resolves a live collector.
        /// </summary>
        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            _stopped = false;
            ArmCollectorSink();
        }

        /// <summary>
        /// Stop producing events and detach the collector sink. Idempotent. Buffered events remain
        /// drainable until <see cref="ResetForTests"/> (a reconnecting MCP subscriber can still read
        /// the tail of the previous session's ring across a domain reload). Called from
        /// <see cref="GodotOpenMcpPlugin._ExitTree"/> BEFORE the HTTP listener stops so an in-flight
        /// drain completes against a live ring.
        /// </summary>
        internal static void Stop()
        {
            if (!_initialized || _stopped) return;
            _stopped = true;
            DisarmCollectorSink();
        }

        // --- Collector fan-out sink ------------------------------------------------------------

        /// <summary>
        /// The collector fan-out sink. Set by <see cref="ArmCollectorSink"/>, cleared by
        /// <see cref="DisarmCollectorSink"/>. Captured locally so a hot-reload that installs a fresh
        /// collector instance is picked up by resolving <see cref="GodotLogCollector.GetOrCreate"/>
        /// at call time (mirrors <see cref="BridgeLog.InstallCollectorSink"/>).
        /// </summary>
        static Action<GodotLogEntry>? _collectorSinkRegistration;

        static void ArmCollectorSink()
        {
            // Capture the delegate into a single local so _collectorSinkRegistration and the
            // collector's EntryAppended hold the SAME instance — DisarmCollectorSink detaches by
            // ReferenceEquals, and local-function-to-delegate conversions can otherwise mint distinct
            // instances per use site. Re-entrancy is already guarded by GodotLogCollector.Append.
            Action<GodotLogEntry> sink = entry => EmitLog(entry);
            _collectorSinkRegistration = sink;
            GodotLogCollector.GetOrCreate().EntryAppended = sink;
        }

        static void DisarmCollectorSink()
        {
            // Only clear the sink if it is still ours (a later plugin enable may have armed a fresh
            // one after a hot reload). Compare by reference to the captured delegate instance.
            var collector = GodotLogCollector.Current;
            if (collector != null && ReferenceEquals(collector.EntryAppended, _collectorSinkRegistration))
                collector.EntryAppended = null;
            _collectorSinkRegistration = null;
        }

        // --- Log fan-in ------------------------------------------------------------------------

        /// <summary>
        /// Emit a <c>log</c> event from a collected <see cref="GodotLogEntry"/>. The entry's
        /// <see cref="GodotLogEntry.Sequence"/> (assigned by the collector) becomes the event
        /// sequence so the event stream and the <c>console_get_logs</c> tool share a cursor
        /// vocabulary. Called from the collector sink; safe from any thread.
        /// </summary>
        internal static void EmitLog(GodotLogEntry entry)
        {
            if (entry == null) return;
            Emit(new BridgeEvent
            {
                Type = "log",
                LogType = entry.LogTypeToken,
                Message = entry.Message ?? "",
                Stack = entry.StackTrace,
            }, entry.Sequence);
        }

        // --- Editor-state fan-in ---------------------------------------------------------------

        /// <summary>
        /// Emit an <c>editor_state</c> event. The <paramref name="state"/> string reuses
        /// <see cref="BridgeInstanceLock"/>'s state vocabulary (<c>idle</c>, <c>compiling</c>,
        /// <c>playing</c>, …) so the SSE stream and the on-disk lock stay consistent. The lock remains
        /// the source of truth for classifiers; events are notifications. Safe from any thread.
        /// </summary>
        internal static void NotifyEditorState(string state, bool isCompiling, bool isPlaying)
        {
            Emit(new BridgeEvent
            {
                Type = "editor_state",
                State = state ?? BridgeInstanceLock.StateIdle,
                IsCompiling = isCompiling,
                IsPlaying = isPlaying,
            });
        }

        // --- Ring produce ----------------------------------------------------------------------

        static void Emit(BridgeEvent evt) => Emit(evt, sequence: 0);

        /// <summary>
        /// Enqueue an event with a caller-supplied sequence (the log path reuses the collector's
        /// monotonic sequence so the two surfaces share a cursor vocabulary). When
        /// <paramref name="sequence"/> is 0, the next internal monotonic is minted via
        /// <see cref="Interlocked.Increment(ref long)"/>. Eviction drains the oldest entries one at a
        /// time until <c>_bufferCount &lt;= BufferCapacity</c> (ConcurrentQueue has no TryDequeue-N).
        /// </summary>
        static void Emit(BridgeEvent evt, long sequence)
        {
            // The log path reuses the collector's sequence; the state path (and tests) mint a fresh
            // monotonic. Either way, _totalEmitted tracks the high-water mark for Subscribe's
            // tail-start cursor and is also used by tests to assert eviction.
            if (sequence <= 0)
                sequence = Interlocked.Increment(ref _totalEmitted);
            else if (sequence > _totalEmitted)
                Interlocked.Exchange(ref _totalEmitted, sequence);

            evt.Sequence = sequence;
            evt.Timestamp = DateTime.UtcNow;

            _buffer.Enqueue(evt);
            var countAfter = Interlocked.Increment(ref _bufferCount);

            while (countAfter > BufferCapacity && _buffer.TryDequeue(out _))
                countAfter = Interlocked.Decrement(ref _bufferCount);
        }

        // --- Subscriber cursors ----------------------------------------------------------------

        /// <summary>
        /// Register a subscriber. Returns the id (caller-supplied or minted) so the HTTP layer can
        /// keep cursors across multiple polls / SSE reconnects. Idempotent — re-subscribing with an
        /// existing id resets its cursor to "now" (the tail of the current buffer) so a fresh
        /// subscriber only sees events emitted AFTER <c>Subscribe()</c>.
        /// </summary>
        internal static string Subscribe(string? id)
        {
            if (string.IsNullOrEmpty(id))
                id = Guid.NewGuid().ToString("N");

            _subscribers[id] = new SubscriberState
            {
                Id = id,
                // Start at the tail of the current buffer so a fresh subscriber only sees events
                // emitted after Subscribe().
                NextSequence = Interlocked.Read(ref _totalEmitted) + 1,
            };
            return id;
        }

        internal static void Unsubscribe(string? id)
        {
            if (string.IsNullOrEmpty(id)) return;
            _subscribers.TryRemove(id, out _);
        }

        /// <summary>
        /// Drain all events for <paramref name="id"/> since its last drain. Returns up to
        /// <paramref name="maxEvents"/> events plus a <c>missed</c> count if any events were evicted
        /// from the ring before the subscriber could read them. The drain call advances the cursor;
        /// calling again immediately returns an empty list unless new events arrived.
        ///
        /// <para>
        /// Lazy-subscribes on first drain (convenient for HTTP clients that skip a separate
        /// subscribe round-trip). Iterates the ring linearly, skips events with
        /// <c>Sequence &lt; NextSequence</c>, and computes <c>missed</c> =
        /// <c>oldestInRing - NextSequence</c> when the oldest still-in-ring sequence is greater than
        /// the subscriber's cursor (i.e. evicted before they read them).
        /// </para>
        /// </summary>
        internal static DrainResult Drain(string id, int maxEvents)
        {
            if (maxEvents <= 0) maxEvents = 100;

            if (!_subscribers.TryGetValue(id, out var state))
            {
                // Lazily subscribe on first drain.
                Subscribe(id);
                _subscribers.TryGetValue(id, out state);
            }
            if (state == null) return new DrainResult { SubscriberId = id };

            var events = new List<BridgeEvent>();
            long highestSeen = state.NextSequence - 1;

            foreach (var evt in _buffer)
            {
                if (evt.Sequence < state.NextSequence) continue;
                if (events.Count >= maxEvents) break;
                events.Add(evt);
                if (evt.Sequence > highestSeen) highestSeen = evt.Sequence;
            }

            // Did the ring evict events this subscriber never saw? Peek the oldest sequence still in
            // the ring; if it is past the subscriber's cursor, the gap is the lost count.
            long missed = 0;
            long oldestInRing = long.MaxValue;
            foreach (var e in _buffer) { oldestInRing = e.Sequence; break; }
            if (oldestInRing != long.MaxValue && oldestInRing > state.NextSequence)
                missed = oldestInRing - state.NextSequence;

            state.NextSequence = highestSeen + 1;

            return new DrainResult
            {
                SubscriberId = id,
                Events = events,
                Missed = missed,
                TotalEmitted = Interlocked.Read(ref _totalEmitted),
            };
        }

        // --- Public read-only state ------------------------------------------------------------

        /// <summary>Current number of events retained in the ring (best-effort snapshot).</summary>
        internal static int BufferCount => Volatile.Read(ref _bufferCount);

        /// <summary>High-water mark of every event ever emitted.</summary>
        internal static long TotalEmitted => Interlocked.Read(ref _totalEmitted);

        // --- JSON render -----------------------------------------------------------------------

        /// <summary>
        /// Render one event as a compact JSON object. Used by both <c>/events</c> (SSE) and
        /// <c>/events/poll</c> (plain JSON). Field order is fixed so a client parser can rely on it.
        /// </summary>
        internal static string RenderEvent(BridgeEvent evt)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"seq\":").Append(evt.Sequence).Append(',');
            sb.Append("\"ts\":\"").Append(IsoUtc(evt.Timestamp)).Append("\",");
            sb.Append("\"type\":\"").Append(Escape(evt.Type)).Append('"');
            if (evt.Type == "log")
            {
                sb.Append(",\"logType\":\"").Append(Escape(evt.LogType)).Append('"');
                sb.Append(",\"message\":\"").Append(Escape(evt.Message)).Append('"');
                if (!string.IsNullOrEmpty(evt.Stack))
                    sb.Append(",\"stack\":\"").Append(Escape(evt.Stack)).Append('"');
            }
            else
            {
                sb.Append(",\"state\":\"").Append(Escape(evt.State)).Append('"');
                sb.Append(",\"isCompiling\":").Append(evt.IsCompiling ? "true" : "false");
                sb.Append(",\"isPlaying\":").Append(evt.IsPlaying ? "true" : "false");
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Render a <see cref="DrainResult"/> as the JSON envelope returned by <c>/events/poll</c>.
        /// </summary>
        internal static string RenderDrain(DrainResult result)
        {
            var sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append("\"subscriberId\":\"").Append(Escape(result.SubscriberId)).Append('"');
            sb.Append(",\"events\":[");
            if (result.Events != null)
            {
                for (int i = 0; i < result.Events.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(RenderEvent(result.Events[i]));
                }
            }
            sb.Append(']');
            sb.Append(",\"count\":").Append(result.Events?.Count ?? 0);
            sb.Append(",\"missed\":").Append(result.Missed);
            sb.Append(",\"totalEmitted\":").Append(result.TotalEmitted);
            sb.Append('}');
            return sb.ToString();
        }

        // --- Types -----------------------------------------------------------------------------

        internal struct BridgeEvent
        {
            public long Sequence;
            public DateTime Timestamp;
            public string Type;        // "log" | "editor_state"
            // log fields
            public string LogType;     // "log" | "warning" | "error"
            public string Message;
            public string? Stack;
            // editor_state fields
            public string State;       // BridgeInstanceLock.State*
            public bool IsCompiling;
            public bool IsPlaying;
        }

        internal sealed class SubscriberState
        {
            internal string Id = "";
            internal long NextSequence;
        }

        internal sealed class DrainResult
        {
            internal string SubscriberId = "";
            internal List<BridgeEvent> Events = new();
            internal long Missed;
            internal long TotalEmitted;
        }

        // --- Test surface ----------------------------------------------------------------------

        /// <summary>
        /// Clear the buffer and subscriber state. Only used by unit tests to get a deterministic
        /// starting point; never call from production (it would erase a reconnecting subscriber's
        /// cursor).
        /// </summary>
        internal static void ResetForTests()
        {
            while (_buffer.TryDequeue(out _)) { }
            _bufferCount = 0;
            _totalEmitted = 0;
            _subscribers.Clear();
            _initialized = false;
            _stopped = false;
            _collectorSinkRegistration = null;
        }

        /// <summary>
        /// Emit a synthetic event so a test can assert drain behavior without waiting on collector
        /// delivery. <paramref name="type"/> <c>"log"</c> emits a log event; anything else emits an
        /// editor_state event with <paramref name="message"/> as the state string.
        /// </summary>
        internal static void EmitForTests(string type, string message)
        {
            if (type == "log")
                Emit(new BridgeEvent { Type = "log", LogType = "log", Message = message ?? "" });
            else
                Emit(new BridgeEvent
                {
                    Type = "editor_state",
                    State = message ?? BridgeInstanceLock.StateIdle,
                    IsCompiling = false,
                    IsPlaying = false,
                });
        }

        // --- Helpers ---------------------------------------------------------------------------

        static string IsoUtc(DateTime dt) =>
            dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffZ");

        static string Escape(string? s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
#endif
