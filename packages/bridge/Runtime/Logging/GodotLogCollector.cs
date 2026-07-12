#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace GodotOpenMcp.Bridge.Runtime.Logging
{
    /// <summary>
    /// In-memory, bounded ring-buffer of captured log lines (P4.7) — the Godot analog of Unity Open
    /// MCP's <c>UnityLogCollector</c>. Unity subscribes to
    /// <c>Application.logMessageReceivedThreaded</c> to capture EVERY engine log line; Godot's C# API
    /// exposes NO such global managed log hook (it is a long-standing engine gap — there is no managed
    /// <c>OS.add_logger</c> / log-received signal in 4.x at the baseline). So this collector is fed
    /// explicitly by the plugin's own logging path (<see cref="BridgeLog"/>) and by tool-handler error
    /// capture, giving the <c>console_get_logs</c> tool a faithful, queryable record of the Godot Open
    /// MCP plugin's own editor activity even though the broader editor Output panel cannot be tapped
    /// from managed code at the 4.3 baseline.
    ///
    /// <para>
    /// <b>Thread-safe.</b> A single lock guards the buffer: <see cref="Append"/> may be called from
    /// any thread the plugin logs on (the HTTP listener worker, or the editor main thread via the
    /// BridgeLog sink), while <see cref="Query(int, GodotLogType?, bool, int)"/> reads from the tool
    /// dispatch thread. Bounded by <see cref="Capacity"/> — once full, the oldest line is dropped
    /// (FIFO), so memory never grows without bound during a long editor session.
    /// </para>
    ///
    /// <para>
    /// <b>No recursive logging.</b> The write path (<see cref="Append"/>) must not call
    /// <c>BridgeLog</c> recursively — doing so would re-enter the collector under the same lock and
    /// deadlock. The collector writes to the ring only.
    /// </para>
    ///
    /// <para>
    /// Adapted from Unity Open MCP's <c>UnityLogCollector</c> (adapt fidelity — same bounded ring +
    /// Query semantics) and Godot-MCP's <c>GodotLogCollector</c> (behavior reference — same ring +
    /// FIFO + thread-safe pattern, implemented first-party). The monotonic sequence number, the
    /// source tag, the capture-capability metadata, and the structured JSON envelope are greenfield
    /// for this port.
    /// </para>
    ///
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so it is unit-testable in the
    /// binary-less xUnit host — including concurrency and eviction coverage.
    /// </summary>
    public sealed class GodotLogCollector
    {
        /// <summary>Maximum number of retained log lines before the oldest is evicted (FIFO). Matches
        /// Unity Open MCP's collector ceiling.</summary>
        public const int Capacity = 1000;

        readonly object _gate = new();
        readonly LinkedList<GodotLogEntry> _entries = new();

        /// <summary>
        /// Optional sink invoked AFTER an entry is appended (P5.4 event-source fan-out). The sink
        /// receives a copy of the appended entry (with its assigned sequence) so an event-stream
        /// subscriber sees the same sequence number as a <c>console_get_logs</c> caller — the two
        /// surfaces share a cursor vocabulary. Captured as a plain field (Volatile read on invoke)
        /// so <see cref="Append"/> never blocks on the sink. The sink is invoked OUTSIDE the lock
        /// (after the entry is committed) so a slow sink cannot stall the collector, and a sink that
        /// re-enters Append would simply append a second entry (no deadlock under the collector
        /// lock). Null when no event source is armed (the common case in tests).
        /// </summary>
        Action<GodotLogEntry>? _entryAppended;

        /// <summary>
        /// The P5.4 event-source fan-out sink. Set by <c>BridgeEventSource.ArmCollectorSink</c> on
        /// plugin enable; cleared on disable. The delegate is invoked on the thread that called
        /// <see cref="Append"/> (the HTTP listener worker or the editor main thread), so a sink must
        /// be thread-safe. The event source's sink is (it enqueues onto a lock-free
        /// ConcurrentQueue).
        /// </summary>
        public Action<GodotLogEntry>? EntryAppended
        {
            get => Volatile.Read(ref _entryAppended);
            set => Volatile.Write(ref _entryAppended, value);
        }

        /// <summary>Backing field for <see cref="Current"/>; accessed only through Volatile
        /// read/write.</summary>
        static GodotLogCollector? _current;

        long _nextSequence = 1;

        /// <summary>
        /// Process-wide collector used by the plugin's logging path and read by the
        /// <c>console_*</c> tools. Assigned at editor boot (each <c>_EnterTree</c> installs a fresh
        /// buffer) and read by the BridgeLog sink, which runs on ARBITRARY threads. The reference swap
        /// therefore goes through <see cref="Volatile.Write{T}(ref T, T)"/> and reads go through
        /// <see cref="Volatile.Read{T}(ref T)"/> so the transition is published without torn reads and
        /// is immediately visible to the background readers — a plain auto-property would expose the
        /// reader to a stale/torn reference on a weak memory model.
        ///
        /// <para>
        /// Deliberately NOT nulled on teardown: nulling here would wipe the buffer exactly when the
        /// teardown / reload-window diagnostics matter most. Instead the buffer stays readable until
        /// the next <c>_EnterTree</c> installs a new one (last-writer-wins), so
        /// <c>console_get_logs</c> can still surface the most recent session's lines after a plugin
        /// disable / hot-reload.
        /// </para>
        /// </summary>
        public static GodotLogCollector? Current
        {
            get => Volatile.Read(ref _current);
            set => Volatile.Write(ref _current, value);
        }

        /// <summary>
        /// Return <see cref="Current"/> when set, otherwise atomically install a fresh empty collector
        /// and return it. Uses <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> so two
        /// concurrent callers cannot each publish a different buffer (the loser discards its candidate
        /// and adopts the winner's), keeping a single process-wide collector even under a race.
        /// </summary>
        public static GodotLogCollector GetOrCreate()
        {
            var existing = Volatile.Read(ref _current);
            if (existing != null)
                return existing;

            var candidate = new GodotLogCollector();
            return Interlocked.CompareExchange(ref _current, candidate, null) ?? candidate;
        }

        /// <summary>
        /// Append a captured line, evicting the oldest when at <see cref="Capacity"/>. Assigns the
        /// next monotonic sequence number. Thread-safe. MUST NOT call BridgeLog (recursive logging
        /// would re-enter under the same lock and deadlock).
        /// </summary>
        public void Append(GodotLogEntry entry)
        {
            if (entry == null) return;

            GodotLogEntry? committed = null;
            lock (_gate)
            {
                if (_entries.Count >= Capacity)
                    _entries.RemoveFirst();
                entry.Sequence = _nextSequence++;
                _entries.AddLast(entry);
                // Snapshot the committed entry (with its assigned sequence) so the P5.4 fan-out sink
                // sees the same sequence as a console_get_logs caller. The copy is made under the
                // lock so the sequence is stable; the sink fires OUTSIDE the lock below so a slow
                // sink cannot stall concurrent Appenders and a sink that re-enters Append cannot
                // deadlock under the collector lock.
                committed = new GodotLogEntry(entry.Sequence, entry.LogType, entry.Message,
                    entry.TimestampUtc, entry.StackTrace, entry.Source);
            }

            var sink = EntryAppended;
            if (sink != null && committed != null)
            {
                try { sink(committed); }
                catch
                {
                    // The fan-out sink must never break the caller's append path. Swallow — the
                    // entry is already committed to the ring.
                }
            }
        }

        /// <summary>Convenience overload: capture a line from its parts (sequence assigned by
        /// <see cref="Append"/>, timestamp defaults to now-UTC).</summary>
        public void Append(GodotLogType logType, string message, string? stackTrace = null,
            GodotLogSource source = GodotLogSource.Bridge)
        {
            Append(new GodotLogEntry(sequence: 0, logType, message, DateTime.UtcNow, stackTrace, source));
        }

        /// <summary>Drop all retained log lines. Returns the number removed. Thread-safe. The
        /// P5.4 fan-out sink (<see cref="EntryAppended"/>) is NOT cleared — only the buffer is.
        /// Sequence is NOT reset (monotonic across a clear).</summary>
        public int Clear()
        {
            lock (_gate)
            {
                int removed = _entries.Count;
                _entries.Clear();
                // Sequence is NOT reset — the P5.4 event-stream cursor stays monotonic across a
                // clear so a consumer can tell "I have seen everything up to N" without ambiguity.
                return removed;
            }
        }

        /// <summary>Current number of retained log lines. Thread-safe.</summary>
        public int Count
        {
            get { lock (_gate) { return _entries.Count; } }
        }

        /// <summary>
        /// Query the retained lines, newest-first, mirroring Unity Open MCP's collector
        /// <c>Query</c> semantics: optional severity filter, optional last-N-minutes window,
        /// stack-trace strip, and a <paramref name="maxEntries"/> cap applied AFTER ordering (so the
        /// cap keeps the most recent lines). Returns a fresh list of copies so the caller can
        /// serialize off the lock.
        /// </summary>
        public List<GodotLogEntry> Query(
            int maxEntries = 100,
            GodotLogType? logTypeFilter = null,
            bool includeStackTrace = false,
            int lastMinutes = 0)
        {
            if (maxEntries < 1)
                maxEntries = 1;

            DateTime? cutoff = lastMinutes > 0 ? DateTime.UtcNow.AddMinutes(-lastMinutes) : null;

            // Snapshot the buffer under the lock, then filter/order off the lock so the collector is
            // not held while the caller serializes.
            List<GodotLogEntry> snapshot;
            lock (_gate)
            {
                snapshot = new List<GodotLogEntry>(_entries.Count);
                foreach (var e in _entries)
                    snapshot.Add(e);
            }

            var result = new List<GodotLogEntry>(Math.Min(maxEntries, snapshot.Count));
            // Newest-first: iterate the snapshot (which is oldest-first, FIFO) in reverse.
            for (int i = snapshot.Count - 1; i >= 0 && result.Count < maxEntries; i--)
            {
                var e = snapshot[i];
                if (logTypeFilter.HasValue && e.LogType != logTypeFilter.Value)
                    continue;
                if (cutoff.HasValue && e.TimestampUtc < cutoff.Value)
                    continue;
                // Return a copy with the stack trace stripped when not requested.
                result.Add(includeStackTrace
                    ? new GodotLogEntry(e.Sequence, e.LogType, e.Message, e.TimestampUtc, e.StackTrace, e.Source)
                    : new GodotLogEntry(e.Sequence, e.LogType, e.Message, e.TimestampUtc, null, e.Source));
            }
            return result;
        }

        /// <summary>Current number of retained log lines and the capacity (thread-safe snapshot for
        /// the response metadata).</summary>
        public (int retained, int capacity) GetRetention()
        {
            lock (_gate) { return (_entries.Count, Capacity); }
        }
    }
}
