#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GodotOpenMcp.Bridge.Editor;
using GodotOpenMcp.Bridge.Runtime.Logging;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P5.4 unit tests for <see cref="BridgeEventSource"/> — the ring buffer + subscriber cursors +
    /// drain math that backs <c>/events</c> (SSE) and <c>/events/poll</c> (JSON). Runs in the
    /// binary-less xUnit host because the source is pure-managed
    /// (<see cref="System.Collections.Concurrent"/> + <see cref="System.Threading.Interlocked"/> only —
    /// no Godot APIs). The SSE/poll HTTP routes are exercised by the integration test.
    ///
    /// <para>
    /// Covers the acceptance criteria: ring eviction (oldest evicted, drain reports
    /// <c>missed</c>), independent subscriber cursors (a slow subscriber never blocks a fast one),
    /// oldest-first drain ordering, JSON render shape, and the collector fan-out sink wiring. Ported
    /// from Unity Open MCP's <c>BridgeEventSource</c> tests (copy fidelity) with the Godot log-source
    /// adaptation (sink from <see cref="GodotLogCollector"/>, not Unity's
    /// <c>Application.logMessageReceived</c>).
    /// </para>
    /// </summary>
    public class BridgeEventSourceTests
    {
        /// <summary>
        /// Each test starts from a clean ring because the source is process-static. ResetForTests is
        /// internal and test-only; the fixture calls it in the constructor so tests are independent
        /// regardless of xUnit collection ordering. (No collection attribute needed because every test
        /// resets.)
        /// </summary>
        public BridgeEventSourceTests()
        {
            BridgeEventSource.ResetForTests();
        }

        // --- Drain ordering + lazy subscribe ---------------------------------------------------

        [Fact]
        public void Drain_LazilySubscribesOnFirstCall()
        {
            // Emit two events BEFORE any subscribe — a fresh subscriber starts at the tail
            // (NextSequence = totalEmitted + 1), so these are NOT replayed.
            BridgeEventSource.EmitForTests("log", "pre-1");
            BridgeEventSource.EmitForTests("log", "pre-2");

            var drain = BridgeEventSource.Drain("client-a", maxEvents: 10);
            Assert.Equal("client-a", drain.SubscriberId);
            Assert.Empty(drain.Events);

            // Events emitted AFTER the (lazy) subscribe are seen on the next drain.
            BridgeEventSource.EmitForTests("log", "post");
            var drain2 = BridgeEventSource.Drain("client-a", maxEvents: 10);
            Assert.Single(drain2.Events);
            Assert.Equal("post", drain2.Events[0].Message);
        }

        [Fact]
        public void Drain_ReturnsEventsOldestFirstWithinABatch()
        {
            BridgeEventSource.EmitForTests("log", "one");
            BridgeEventSource.EmitForTests("log", "two");
            BridgeEventSource.EmitForTests("log", "three");

            // First drain establishes the subscriber at the tail (sees nothing yet), so we subscribe
            // explicitly first, then emit, then drain.
            BridgeEventSource.Subscribe("client");
            BridgeEventSource.EmitForTests("log", "e1");
            BridgeEventSource.EmitForTests("log", "e2");
            BridgeEventSource.EmitForTests("log", "e3");

            var drain = BridgeEventSource.Drain("client", maxEvents: 10);
            Assert.Equal(3, drain.Events.Count);
            Assert.Equal("e1", drain.Events[0].Message);
            Assert.Equal("e2", drain.Events[1].Message);
            Assert.Equal("e3", drain.Events[2].Message);
            // Drain advances the cursor — a second call returns nothing new.
            var drain2 = BridgeEventSource.Drain("client", maxEvents: 10);
            Assert.Empty(drain2.Events);
        }

        [Fact]
        public void Drain_CapsAtMaxEvents()
        {
            BridgeEventSource.Subscribe("client");
            for (int i = 0; i < 5; i++)
                BridgeEventSource.EmitForTests("log", "e" + i);

            var drain = BridgeEventSource.Drain("client", maxEvents: 2);
            Assert.Equal(2, drain.Events.Count);
            Assert.Equal("e0", drain.Events[0].Message);
            Assert.Equal("e1", drain.Events[1].Message);

            // The rest are still drainable on the next call (cursor is at e2).
            var drain2 = BridgeEventSource.Drain("client", maxEvents: 10);
            Assert.Equal(3, drain2.Events.Count);
            Assert.Equal("e2", drain2.Events[0].Message);
        }

        [Fact]
        public void Drain_MaxEventsNonPositiveDefaultsTo100()
        {
            BridgeEventSource.Subscribe("client");
            for (int i = 0; i < 3; i++)
                BridgeEventSource.EmitForTests("log", "e" + i);

            // 0 / negative are clamped to the default cap (100) — the three events all come back.
            var drain = BridgeEventSource.Drain("client", maxEvents: 0);
            Assert.Equal(3, drain.Events.Count);
        }

        // --- Independent subscriber cursors ----------------------------------------------------

        [Fact]
        public void SubscribersDrainIndependently()
        {
            BridgeEventSource.Subscribe("fast");
            BridgeEventSource.Subscribe("slow");

            BridgeEventSource.EmitForTests("log", "shared");

            // Fast drains first and advances its cursor; slow does not drain yet.
            var fastDrain = BridgeEventSource.Drain("fast", maxEvents: 10);
            Assert.Single(fastDrain.Events);

            // Emit another event; fast should see only the new one, slow should see both.
            BridgeEventSource.EmitForTests("log", "second");
            var fastDrain2 = BridgeEventSource.Drain("fast", maxEvents: 10);
            Assert.Single(fastDrain2.Events);
            Assert.Equal("second", fastDrain2.Events[0].Message);

            var slowDrain = BridgeEventSource.Drain("slow", maxEvents: 10);
            Assert.Equal(2, slowDrain.Events.Count);
            Assert.Equal("shared", slowDrain.Events[0].Message);
            Assert.Equal("second", slowDrain.Events[1].Message);
        }

        // --- Ring eviction + missed signaling --------------------------------------------------

        [Fact]
        public void OverflowEvictsOldestAndReportsMissed()
        {
            // We cannot lower BufferCapacity (it's a const), but we can force eviction by emitting
            // more than BufferCapacity events, then checking that a subscriber whose cursor fell behind
            // the evicted tail sees a non-zero missed count.
            BridgeEventSource.Subscribe("laggard");

            // Emit BufferCapacity + 10 events. The ring evicts the oldest 10; the laggard (which has
            // not drained) now has a cursor pointing at events that are gone.
            int overflow = 10;
            int total = BridgeEventSource.BufferCapacity + overflow;
            for (int i = 0; i < total; i++)
                BridgeEventSource.EmitForTests("log", "e" + i);

            var drain = BridgeEventSource.Drain("laggard", maxEvents: total);
            // The drain returns at most BufferCapacity events (the ring size), and missed reports the
            // 10 evicted events the subscriber never saw.
            Assert.True(drain.Events.Count <= BridgeEventSource.BufferCapacity);
            Assert.Equal((long)overflow, drain.Missed);
            // The first returned event is the oldest still in the ring.
            Assert.Equal("e" + overflow, drain.Events[0].Message);
        }

        [Fact]
        public void NoMissedWhenSubscriberKeepsUp()
        {
            BridgeEventSource.Subscribe("steady");
            for (int i = 0; i < 5; i++)
                BridgeEventSource.EmitForTests("log", "e" + i);

            var drain = BridgeEventSource.Drain("steady", maxEvents: 10);
            Assert.Equal(0, drain.Missed);
            Assert.Equal(5, drain.Events.Count);
        }

        // --- JSON render ------------------------------------------------------------------------

        [Fact]
        public void RenderEvent_LogShapeMatchesContract()
        {
            BridgeEventSource.Subscribe("client");
            BridgeEventSource.EmitForTests("log", "hello");
            var drain = BridgeEventSource.Drain("client", maxEvents: 1);
            Assert.Single(drain.Events);
            var json = BridgeEventSource.RenderEvent(drain.Events[0]);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("seq").GetInt64() > 0);
            Assert.Equal("log", root.GetProperty("type").GetString());
            Assert.Equal("log", root.GetProperty("logType").GetString());
            Assert.Equal("hello", root.GetProperty("message").GetString());
            Assert.True(root.GetProperty("ts").GetString()!.EndsWith("Z"));
        }

        [Fact]
        public void RenderEvent_EditorStateShapeMatchesContract()
        {
            BridgeEventSource.NotifyEditorState(BridgeInstanceLock.StateCompiling,
                isCompiling: true, isPlaying: false);
            // Subscribe first so the cursor is at the tail; the NotifyEditorState above happened
            // before subscribe, so re-emit after subscribe to read it.
            BridgeEventSource.Subscribe("client");
            BridgeEventSource.NotifyEditorState(BridgeInstanceLock.StatePlaying,
                isCompiling: false, isPlaying: true);

            var drain = BridgeEventSource.Drain("client", maxEvents: 1);
            Assert.Single(drain.Events);
            var evt = drain.Events[0];
            Assert.Equal("editor_state", evt.Type);
            Assert.Equal(BridgeInstanceLock.StatePlaying, evt.State);
            Assert.True(evt.IsPlaying);
            Assert.False(evt.IsCompiling);

            var json = BridgeEventSource.RenderEvent(evt);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal("editor_state", root.GetProperty("type").GetString());
            Assert.Equal("playing", root.GetProperty("state").GetString());
            Assert.True(root.GetProperty("isPlaying").GetBoolean());
            Assert.False(root.GetProperty("isCompiling").GetBoolean());
        }

        [Fact]
        public void RenderDrain_EnvelopeShapeMatchesContract()
        {
            BridgeEventSource.Subscribe("client");
            BridgeEventSource.EmitForTests("log", "one");

            var drain = BridgeEventSource.Drain("client", maxEvents: 10);
            var json = BridgeEventSource.RenderDrain(drain);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal("client", root.GetProperty("subscriberId").GetString());
            Assert.Equal(1, root.GetProperty("count").GetInt32());
            Assert.Equal(0, root.GetProperty("missed").GetInt64());
            Assert.True(root.GetProperty("totalEmitted").GetInt64() > 0);
            Assert.Equal(1, root.GetProperty("events").GetArrayLength());
        }

        [Fact]
        public void RenderEvent_EscapesMessageWithNewlines()
        {
            BridgeEventSource.Subscribe("client");
            BridgeEventSource.EmitForTests("log", "line1\nline2");
            var drain = BridgeEventSource.Drain("client", maxEvents: 1);
            var json = BridgeEventSource.RenderEvent(drain.Events[0]);
            // The rendered JSON must be valid (parses) and carry the escaped newline.
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("line1\nline2", doc.RootElement.GetProperty("message").GetString());
        }

        // --- Collector fan-out sink -------------------------------------------------------------

        /// <summary>
        /// The P5.4 event source fans log events in from the P4.7 collector via the
        /// <see cref="GodotLogCollector.EntryAppended"/> sink. Assert that appending to the collector
        /// produces a corresponding <c>log</c> event in the ring, with the shared sequence number.
        /// This is the single fan-in — no second raw Godot logger.
        /// </summary>
        [Fact]
        public void CollectorFanOut_EmitsLogEventWithSharedSequence()
        {
            // Reset the process-static collector so this test is independent. The event source's
            // Initialize() arms the sink against GetOrCreate().
            GodotLogCollector.Current = null;
            try
            {
                BridgeEventSource.ResetForTests();
                BridgeEventSource.Initialize();
                BridgeEventSource.Subscribe("client");

                var collector = GodotLogCollector.GetOrCreate();
                collector.Append(GodotLogType.Warning, "from-collector");

                var drain = BridgeEventSource.Drain("client", maxEvents: 1);
                Assert.Single(drain.Events);
                var evt = drain.Events[0];
                Assert.Equal("log", evt.Type);
                Assert.Equal("warning", evt.LogType);
                Assert.Equal("from-collector", evt.Message);
                // The event sequence matches the collector's sequence (shared cursor vocabulary).
                Assert.Equal(1, evt.Sequence);

                BridgeEventSource.Stop();
            }
            finally
            {
                GodotLogCollector.Current = null;
            }
        }

        /// <summary>
        /// Initialize is idempotent and Stop disarms the sink. After Stop, collector appends no longer
        /// produce events.
        /// </summary>
        [Fact]
        public void Initialize_IsIdempotent_AndStopDisarmsSink()
        {
            GodotLogCollector.Current = null;
            try
            {
                BridgeEventSource.ResetForTests();
                BridgeEventSource.Initialize();
                BridgeEventSource.Initialize(); // idempotent — no double-arm
                BridgeEventSource.Subscribe("client");

                var collector = GodotLogCollector.GetOrCreate();
                BridgeEventSource.Stop();

                collector.Append(GodotLogType.Log, "after-stop");
                var drain = BridgeEventSource.Drain("client", maxEvents: 10);
                Assert.Empty(drain.Events);
            }
            finally
            {
                GodotLogCollector.Current = null;
            }
        }

        // --- Concurrency ------------------------------------------------------------------------

        /// <summary>
        /// Concurrent producers + drainers must not corrupt the ring or lose events beyond the eviction
        /// policy. The ring uses ConcurrentQueue + Interlocked counters so this is safe by
        /// construction; the test pins it.
        /// </summary>
        [Fact]
        public async Task ConcurrentProducersAndDrainers_AreSafe()
        {
            BridgeEventSource.Subscribe("drainer");
            int producerCount = 4;
            int perProducer = 200;

            var produceTasks = new List<Task>();
            for (int p = 0; p < producerCount; p++)
            {
                int pid = p;
                produceTasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < perProducer; i++)
                        BridgeEventSource.EmitForTests("log", $"p{pid}-e{i}");
                }));
            }

            int drained = 0;
            var drainTask = Task.Run(() =>
            {
                while (drained < producerCount * perProducer)
                {
                    var d = BridgeEventSource.Drain("drainer", maxEvents: 50);
                    Interlocked.Add(ref drained, d.Events.Count);
                    if (d.Events.Count == 0) Thread.Sleep(1);
                }
            });

            await Task.WhenAll(produceTasks);
            await drainTask;

            // Capacity is 1024 and we emit 800 events — none are evicted, so the drain count must
            // match exactly.
            Assert.Equal(producerCount * perProducer, drained);
        }
    }
}
