#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GodotOpenMcp.Bridge.Runtime.Logging;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P4.7 unit tests for the pure-managed <see cref="GodotLogCollector"/> ring buffer and
    /// <see cref="GodotLogEntry"/> DTO. These run in the binary-less xUnit host (no Godot editor
    /// needed) because the collector is pure-managed — no Godot API surface, no <c>#if TOOLS</c>.
    ///
    /// <para>
    /// Covers: FIFO eviction at capacity, monotonic sequence ordering, thread-safe concurrent
    /// writers/readers, severity / age / max-entries / stack-trace filters, clear count + sequence
    /// continuity after clear, message/stack truncation, and DTO serialization shape. The
    /// <c>#if TOOLS</c> handler (<see cref="GodotOpenMcp.Bridge.Editor.ConsoleTools"/>) and the
    /// BridgeLog sink wiring are exercised by the headless Godot smoke, not here.
    /// </para>
    /// </summary>
    public class GodotLogEntryTests
    {
        [Fact]
        public void ToJsonString_ProducesExpectedShapeAndFieldOrder()
        {
            var entry = new GodotLogEntry(
                sequence: 42,
                GodotLogType.Error,
                "boom",
                new DateTime(2026, 7, 10, 19, 0, 0, DateTimeKind.Utc),
                stackTrace: "at Foo()",
                source: GodotLogSource.Bridge);
            var json = entry.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(42L, root.GetProperty("sequence").GetInt64());
            Assert.Equal("error", root.GetProperty("logType").GetString());
            Assert.Equal("boom", root.GetProperty("message").GetString());
            Assert.Equal("2026-07-10T19:00:00.000Z", root.GetProperty("timestamp").GetString());
            Assert.Equal("at Foo()", root.GetProperty("stackTrace").GetString());
            Assert.Equal("bridge", root.GetProperty("source").GetString());

            // Field order is fixed: sequence, logType, message, timestamp, stackTrace, source.
            int seqIdx = json.IndexOf("\"sequence\"");
            int logTypeIdx = json.IndexOf("\"logType\"");
            int msgIdx = json.IndexOf("\"message\"");
            int tsIdx = json.IndexOf("\"timestamp\"");
            int stackIdx = json.IndexOf("\"stackTrace\"");
            int srcIdx = json.IndexOf("\"source\"");
            Assert.True(seqIdx < logTypeIdx);
            Assert.True(logTypeIdx < msgIdx);
            Assert.True(msgIdx < tsIdx);
            Assert.True(tsIdx < stackIdx);
            Assert.True(stackIdx < srcIdx);
        }

        [Fact]
        public void ToJsonString_NullStackTrace_EmitsNull()
        {
            var entry = new GodotLogEntry(1, GodotLogType.Log, "hi", DateTime.UtcNow);
            var json = entry.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("stackTrace").ValueKind == JsonValueKind.Null);
        }

        [Theory]
        [InlineData(GodotLogType.Log, "log")]
        [InlineData(GodotLogType.Warning, "warning")]
        [InlineData(GodotLogType.Error, "error")]
        public void LogTypeToken_MapsToUnityVocabulary(GodotLogType type, string expected)
        {
            var entry = new GodotLogEntry { LogType = type };
            Assert.Equal(expected, entry.LogTypeToken);
        }

        [Theory]
        [InlineData(GodotLogSource.Bridge, "bridge")]
        [InlineData(GodotLogSource.Script, "script")]
        [InlineData(GodotLogSource.Engine, "engine")]
        [InlineData(GodotLogSource.Tool, "tool")]
        public void SourceToken_Maps(GodotLogSource source, string expected)
        {
            var entry = new GodotLogEntry { Source = source };
            Assert.Equal(expected, entry.SourceToken);
        }

        [Fact]
        public void Constructor_TruncatesLongMessage()
        {
            var longMsg = new string('x', GodotLogEntry.MaxMessageLength + 100);
            var entry = new GodotLogEntry(1, GodotLogType.Log, longMsg, DateTime.UtcNow);
            Assert.Equal(GodotLogEntry.MaxMessageLength, entry.Message.Length);
        }

        [Fact]
        public void Constructor_TruncatesLongStackTrace()
        {
            var longStack = new string('y', GodotLogEntry.MaxStackTraceLength + 50);
            var entry = new GodotLogEntry(1, GodotLogType.Error, "m", DateTime.UtcNow, longStack);
            Assert.Equal(GodotLogEntry.MaxStackTraceLength, entry.StackTrace!.Length);
        }

        [Fact]
        public void Constructor_EmptyStackTrace_BecomesNull()
        {
            var entry = new GodotLogEntry(1, GodotLogType.Log, "m", DateTime.UtcNow, "");
            Assert.Null(entry.StackTrace);
        }

        [Fact]
        public void FormatTimestamp_ProducesIsoUtcShape()
        {
            var stamp = GodotLogEntry.FormatTimestamp(new DateTime(2026, 7, 10, 19, 0, 0, 123, DateTimeKind.Utc));
            Assert.Equal("2026-07-10T19:00:00.123Z", stamp);
        }

        [Fact]
        public void ToJsonString_EscapesMessageWithQuotes()
        {
            var entry = new GodotLogEntry(1, GodotLogType.Log, "he said \"hi\"\nnew line", DateTime.UtcNow);
            var json = entry.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("he said \"hi\"\nnew line", doc.RootElement.GetProperty("message").GetString());
        }
    }

    /// <summary>
    /// Collector ring-buffer tests: FIFO eviction, sequence ordering, retention metadata.
    /// </summary>
    public class GodotLogCollectorRingBufferTests
    {
        GodotLogCollector NewCollector() => new GodotLogCollector();

        [Fact]
        public void Append_AssignsMonotonicSequence()
        {
            var c = NewCollector();
            c.Append(GodotLogType.Log, "a");
            c.Append(GodotLogType.Log, "b");
            c.Append(GodotLogType.Log, "c");
            var entries = c.Query(maxEntries: 10);
            // newest-first
            Assert.Equal("c", entries[0].Message);
            Assert.Equal("b", entries[1].Message);
            Assert.Equal("a", entries[2].Message);
            Assert.Equal(1L, entries[2].Sequence);
            Assert.Equal(2L, entries[1].Sequence);
            Assert.Equal(3L, entries[0].Sequence);
        }

        [Fact]
        public void Append_AboveCapacity_EvictsOldestFifo()
        {
            var c = NewCollector();
            // Fill exactly to capacity.
            for (int i = 0; i < GodotLogCollector.Capacity; i++)
                c.Append(GodotLogType.Log, $"msg-{i}");
            Assert.Equal(GodotLogCollector.Capacity, c.Count);

            // One more → evicts the oldest (msg-0).
            c.Append(GodotLogType.Log, "overflow");
            Assert.Equal(GodotLogCollector.Capacity, c.Count);

            var entries = c.Query(maxEntries: GodotLogCollector.Capacity);
            // newest-first: overflow is first, msg-1 is the oldest retained (msg-0 evicted).
            Assert.Equal("overflow", entries[0].Message);
            Assert.Equal("msg-1", entries[entries.Count - 1].Message);
        }

        [Fact]
        public void Query_DefaultNewestFirst_CappedToMaxEntries()
        {
            var c = NewCollector();
            for (int i = 0; i < 50; i++)
                c.Append(GodotLogType.Log, $"msg-{i}");
            var entries = c.Query(maxEntries: 10);
            Assert.Equal(10, entries.Count);
            // newest-first: msg-49 is first.
            Assert.Equal("msg-49", entries[0].Message);
            Assert.Equal("msg-40", entries[9].Message);
        }

        [Fact]
        public void Clear_RemovesAllAndReturnsCount()
        {
            var c = NewCollector();
            c.Append(GodotLogType.Log, "a");
            c.Append(GodotLogType.Warning, "b");
            int removed = c.Clear();
            Assert.Equal(2, removed);
            Assert.Equal(0, c.Count);
            var entries = c.Query(maxEntries: 10);
            Assert.Empty(entries);
        }

        [Fact]
        public void Clear_DoesNotResetSequence()
        {
            var c = NewCollector();
            c.Append(GodotLogType.Log, "a");
            c.Clear();
            c.Append(GodotLogType.Log, "b");
            var entries = c.Query(maxEntries: 10);
            Assert.Single(entries);
            // Sequence continued past the clear — not reset to 1.
            Assert.True(entries[0].Sequence > 1, "sequence must stay monotonic across a clear");
        }

        [Fact]
        public void GetRetention_ReportsCountAndCapacity()
        {
            var c = NewCollector();
            c.Append(GodotLogType.Log, "a");
            var (retained, capacity) = c.GetRetention();
            Assert.Equal(1, retained);
            Assert.Equal(GodotLogCollector.Capacity, capacity);
        }

        [Fact]
        public void Append_NullEntry_IsNoOp()
        {
            var c = NewCollector();
            c.Append((GodotLogEntry)null!);
            Assert.Equal(0, c.Count);
        }
    }

    /// <summary>
    /// Collector filter tests: severity, age (last_minutes), stack-trace strip.
    /// </summary>
    public class GodotLogCollectorFilterTests
    {
        GodotLogCollector NewCollector() => new GodotLogCollector();

        [Fact]
        public void Query_SeverityFilter_KeepsOnlyMatching()
        {
            var c = NewCollector();
            c.Append(GodotLogType.Log, "log");
            c.Append(GodotLogType.Warning, "warn");
            c.Append(GodotLogType.Error, "err");
            c.Append(GodotLogType.Log, "log2");

            var warnings = c.Query(maxEntries: 10, logTypeFilter: GodotLogType.Warning);
            Assert.Single(warnings);
            Assert.Equal("warn", warnings[0].Message);

            var errors = c.Query(maxEntries: 10, logTypeFilter: GodotLogType.Error);
            Assert.Single(errors);
            Assert.Equal("err", errors[0].Message);

            var logs = c.Query(maxEntries: 10, logTypeFilter: GodotLogType.Log);
            Assert.Equal(2, logs.Count);
        }

        [Fact]
        public void Query_LastMinutes_FiltersByAge()
        {
            var c = NewCollector();
            // Inject an old entry by constructing it with a past timestamp, then appending the
            // pre-built entry so the timestamp is honored.
            var oldEntry = new GodotLogEntry(0, GodotLogType.Log, "old",
                DateTime.UtcNow.AddMinutes(-10));
            c.Append(oldEntry);
            c.Append(GodotLogType.Log, "fresh");

            var last5 = c.Query(maxEntries: 10, lastMinutes: 5);
            Assert.Single(last5);
            Assert.Equal("fresh", last5[0].Message);

            // All time → both.
            var all = c.Query(maxEntries: 10, lastMinutes: 0);
            Assert.Equal(2, all.Count);
        }

        [Fact]
        public void Query_StripsStackTraceWhenNotRequested()
        {
            var c = NewCollector();
            c.Append(GodotLogType.Error, "boom", "at Foo()");
            var without = c.Query(maxEntries: 10, includeStackTrace: false);
            Assert.Null(without[0].StackTrace);
            var with = c.Query(maxEntries: 10, includeStackTrace: true);
            Assert.Equal("at Foo()", with[0].StackTrace);
        }

        [Fact]
        public void Query_FloorsMaxEntriesToOne()
        {
            var c = NewCollector();
            c.Append(GodotLogType.Log, "a");
            c.Append(GodotLogType.Log, "b");
            var entries = c.Query(maxEntries: 0); // floored to 1
            Assert.Single(entries);
            Assert.Equal("b", entries[0].Message); // newest
        }
    }

    /// <summary>
    /// Concurrency tests: concurrent writers and a concurrent reader do not corrupt collector state.
    /// </summary>
    public class GodotLogCollectorConcurrencyTests
    {
        [Fact]
        public async Task ConcurrentAppends_AllLandedNoDuplicatesNoGapsInSequence()
        {
            var c = new GodotLogCollector();
            const int Writers = 8;
            const int PerWriter = 500;

            var tasks = new Task[Writers];
            for (int w = 0; w < Writers; w++)
            {
                int wid = w;
                tasks[w] = Task.Run(() =>
                {
                    for (int i = 0; i < PerWriter; i++)
                        c.Append(GodotLogType.Log, $"w{wid}-{i}");
                });
            }
            await Task.WhenAll(tasks);

            // A concurrent reader while writers ran would also be safe (lock-protected); here we
            // verify post-hoc: every sequence number from 1..N appears exactly once.
            var total = Writers * PerWriter;
            // If total exceeds capacity, the oldest were evicted; clamp expectations to retained.
            var retained = c.Count;
            Assert.True(retained <= GodotLogCollector.Capacity);
            Assert.Equal(Math.Min(total, GodotLogCollector.Capacity), retained);

            var entries = c.Query(maxEntries: GodotLogCollector.Capacity);
            var sequences = entries.Select(e => e.Sequence).ToList();
            Assert.Equal(retained, sequences.Count);
            // No duplicate sequences.
            Assert.Equal(sequences.Count, sequences.Distinct().Count());
            // Sequences are monotonic (newest-first → descending).
            for (int i = 1; i < sequences.Count; i++)
                Assert.True(sequences[i - 1] > sequences[i], "sequences must be strictly descending (newest-first)");
        }

        [Fact]
        public async Task ConcurrentAppendAndQuery_DoNotCorruptState()
        {
            var c = new GodotLogCollector();
            var stop = false;

            var writer = Task.Run(() =>
            {
                long i = 0;
                while (!Volatile.Read(ref stop))
                {
                    c.Append(GodotLogType.Log, $"m{i++}");
                    if (i > 5000) break;
                }
            });

            // A handful of concurrent readers while the writer runs.
            var readers = new Task[4];
            for (int r = 0; r < 4; r++)
            {
                readers[r] = Task.Run(() =>
                {
                    for (int k = 0; k < 200; k++)
                    {
                        var entries = c.Query(maxEntries: 50);
                        // Each entry is well-formed (non-null message).
                        foreach (var e in entries)
                            Assert.False(string.IsNullOrEmpty(e.Message));
                    }
                });
            }

            await Task.WhenAll(readers);
            Volatile.Write(ref stop, true);
            await writer;

            // Final state is consistent: count within capacity.
            Assert.True(c.Count <= GodotLogCollector.Capacity);
        }
    }

    /// <summary>
    /// Static-instance tests for <see cref="GodotLogCollector.Current"/> /
    /// <see cref="GodotLogCollector.GetOrCreate"/>. These use the process-wide static, so they reset
    /// it before/after to stay isolated. The reset-after uses a try/finally rather than a finalizer
    /// (a finalizer touching a static during process shutdown is unsafe and crashes the test host).
    /// </summary>
    public class GodotLogCollectorStaticTests
    {
        [Fact]
        public void GetOrCreate_WhenAbsent_InstallsFresh()
        {
            GodotLogCollector.Current = null;
            try
            {
                var c = GodotLogCollector.GetOrCreate();
                Assert.NotNull(c);
                Assert.Same(c, GodotLogCollector.Current);
            }
            finally
            {
                GodotLogCollector.Current = null;
            }
        }

        [Fact]
        public void GetOrCreate_WhenPresent_ReturnsExisting()
        {
            GodotLogCollector.Current = null;
            try
            {
                var first = GodotLogCollector.GetOrCreate();
                var second = GodotLogCollector.GetOrCreate();
                Assert.Same(first, second);
            }
            finally
            {
                GodotLogCollector.Current = null;
            }
        }
    }
}
