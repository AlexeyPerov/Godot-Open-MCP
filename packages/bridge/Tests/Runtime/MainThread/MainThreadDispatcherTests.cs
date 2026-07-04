#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GodotOpenMcp.Bridge.Runtime.MainThread;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// Pins the lifecycle contract of <see cref="MainThreadDispatcher"/>: the bridge's single
    /// thread-marshaling path. Every HTTP handler (P1.3 <c>/ping</c>, P2.x tool dispatch,
    /// P3.x gate flow) routes editor API access through here, so the buffer/drain/teardown
    /// guarantees are load-bearing for the whole bridge.
    ///
    /// <para>
    /// Three acceptance criteria from the P1.2 plan, mapped to test groups below:
    /// <list type="bullet">
    ///   <item><b>Off-thread calls are marshaled to main thread reliably</b> —
    ///   <see cref="Enqueue_RunsOnceOnDrain"/>, <see cref="Enqueue_PreservesFifoOrdering"/>,
    ///   <see cref="EnqueueOffThread_BeforeInstance_Buffers_ThenRunsOnDrain"/>,
    ///   <see cref="EnqueueAsync_ReturnsResultFromMainDrain"/>.</item>
    ///   <item><b>Dispatcher shutdown does not deadlock or drop in-flight operations silently</b> —
    ///   <see cref="TeardownDrain_IsBounded_TerminatesWhenDrainedBodyReEnqueues"/>,
    ///   <see cref="TeardownViaExit_DrainsPendingOnce_ThenFailsFast"/>,
    ///   <see cref="EnqueueAsync_PendingAtTeardown_IsCompletedNotDropped"/>.</item>
    ///   <item><b>Bridge uses one consistent dispatch path</b> — both fire-and-forget
    ///   (<see cref="Enqueue(Action)"/>) and request/response (<see cref="MainThreadDispatcher.EnqueueAsync{T}"/>
    ///   share the same queue and drain, asserted by <see cref="EnqueueAndEnqueueAsync_ShareOneQueue"/>.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// These tests are <b>pure-managed</b> and never construct a Godot <c>Node</c>: a real
    /// <see cref="MainThreadDispatcher"/> is a Godot Node whose construction P/Invokes into
    /// <c>godotsharp_*</c> and faults the binary-less xUnit host. The buffer/drain lifecycle lives
    /// entirely in the dispatcher's static members, so the boot/teardown edges are modelled via the
    /// type's internal <c>*ForTests</c> seams. The live Node lifecycle (<c>_EnterTree</c>/
    /// <c>_Process</c>/<c>_ExitTree</c> wiring into <c>GodotOpenMcpPlugin</c>) is covered by the
    /// headless Godot smoke planned for P1.9, not here.
    /// </para>
    ///
    /// <para>
    /// <see cref="MainThreadDispatcher"/>'s lifecycle state is <c>static</c> and outlives a single
    /// test, so each test resets it via the constructor (<see cref="ResetState"/>) and
    /// <see cref="Dispose"/>. The <c>[Collection]</c> attribute serializes these tests against each
    /// other (and disables cross-class parallelism with any future test that touches the same
    /// statics) so a concurrent test never observes mid-mutation state.
    /// </para>
    /// </summary>
    [Collection(nameof(MainThreadDispatcherTests))]
    [CollectionDefinition(nameof(MainThreadDispatcherTests), DisableParallelization = true)]
    public class MainThreadDispatcherTests : IDisposable
    {
        public MainThreadDispatcherTests() => ResetState();

        public void Dispose() => ResetState();

        static void ResetState() => MainThreadDispatcher.ResetForTests();

        // -----------------------------------------------------------------------------------------------------
        // Reliable marshaling — fire-and-forget.
        // -----------------------------------------------------------------------------------------------------

        [Fact]
        public void Enqueue_RunsOnceOnDrain()
        {
            MainThreadDispatcher.SimulateInstanceEnteredForTests(); // instance present

            var runCount = 0;
            MainThreadDispatcher.Enqueue(() => runCount++);

            // Queued for the next tick, not run inline (matches the per-_Process-tick drain contract).
            Assert.Equal(0, runCount);
            Assert.Equal(1, MainThreadDispatcher.PendingActionCountForTests);

            MainThreadDispatcher.DrainForTests();

            Assert.Equal(1, runCount);
            Assert.Equal(0, MainThreadDispatcher.PendingActionCountForTests);
        }

        [Fact]
        public void Enqueue_PreservesFifoOrdering()
        {
            MainThreadDispatcher.SimulateInstanceEnteredForTests();

            var order = new List<int>();
            for (var i = 0; i < 5; i++)
            {
                var captured = i;
                MainThreadDispatcher.Enqueue(() => order.Add(captured));
            }

            Assert.Empty(order); // still buffered

            MainThreadDispatcher.DrainForTests();

            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, order);
        }

        [Fact]
        public void Enqueue_DoesNotDoubleRun_AcrossDrains()
        {
            MainThreadDispatcher.SimulateInstanceEnteredForTests();

            var runCount = 0;
            MainThreadDispatcher.Enqueue(() => runCount++);

            MainThreadDispatcher.DrainForTests();
            Assert.Equal(1, runCount);

            // A subsequent drain (e.g. the next _Process tick) must NOT replay it.
            MainThreadDispatcher.DrainForTests();
            Assert.Equal(1, runCount);
        }

        [Fact]
        public void Enqueue_ActionThatThrows_DoesNotAbortTheDrain()
        {
            // A faulting action must not kill the pump loop — the next item still runs. The
            // error is surfaced via GD.PushError in production; here we only assert the drain
            // continues. (The action wrapper built by EnqueueAsync routes its own exceptions
            // back to the TCS; Enqueue callers are fire-and-forget and have nowhere to route.)
            MainThreadDispatcher.SimulateInstanceEnteredForTests();

            var ranAfter = false;
            MainThreadDispatcher.Enqueue(() => throw new InvalidOperationException("boom"));
            MainThreadDispatcher.Enqueue(() => ranAfter = true);

            MainThreadDispatcher.DrainForTests();

            Assert.True(ranAfter, "drain must continue past a faulting action");
            Assert.Equal(0, MainThreadDispatcher.PendingActionCountForTests);
        }

        // -----------------------------------------------------------------------------------------------------
        // Reliable marshaling — request/response (EnqueueAsync<T>).
        // Mirrors Unity MainThreadDispatcher.EnqueueAsync: worker awaits a TCS the main drain resolves.
        // -----------------------------------------------------------------------------------------------------

        [Fact]
        public async Task EnqueueAsync_ReturnsResultFromMainDrain()
        {
            MainThreadDispatcher.SimulateInstanceEnteredForTests();

            // Dispatch from a background thread so IsMainThread is false and it takes the
            // queue path (the main-thread captured by SimulateInstanceEntered is THIS test thread).
            Task<int>? task = null;
            var bg = new Thread(() => { task = MainThreadDispatcher.EnqueueAsync(() => 42, 5_000); });
            bg.Start();
            bg.Join();

            Assert.NotNull(task);
            Assert.False(task!.IsCompleted); // pending — nothing has drained it yet

            MainThreadDispatcher.DrainForTests(); // main-thread drain resolves the TCS

            Assert.True(task.IsCompletedSuccessfully);
            Assert.Equal(42, await task);
        }

        [Fact]
        public async Task EnqueueAsync_PropagatesActionExceptionToAwaiter()
        {
            MainThreadDispatcher.SimulateInstanceEnteredForTests();

            Task<int>? task = null;
            var bg = new Thread(() => { task = MainThreadDispatcher.EnqueueAsync<int>(
                () => throw new InvalidOperationException("tool-failed"), 5_000); });
            bg.Start();
            bg.Join();

            MainThreadDispatcher.DrainForTests();

            Assert.True(task!.IsFaulted);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
            Assert.Equal("tool-failed", ex.Message);
        }

        [Fact]
        public async Task EnqueueAsync_TimeoutWhenWorkNeverDrains_ThrowsMainThreadBlocked()
        {
            // The Unity-faithful contract: when the timeout fires AND the action never started
            // draining (StartedDrainAtUtc is null), the main thread was blocked the entire window
            // — surface MainThreadBlockedException so the HTTP handler can build a
            // main_thread_blocked envelope. We do NOT drain, so the action never starts.
            MainThreadDispatcher.SimulateInstanceEnteredForTests();

            Task<int>? task = null;
            var bg = new Thread(() => { task = MainThreadDispatcher.EnqueueAsync(() => 1, 50); });
            bg.Start();
            bg.Join();

            // No drain — the action sits in the queue past the timeout.
            var ex = await Assert.ThrowsAsync<MainThreadBlockedException>(async () => await task!);
            Assert.Equal(50, ex.TimeoutMs);
        }

        [Fact]
        public async Task EnqueueAsync_TimeoutWhenWorkStartedButRanLong_ThrowsTimeout()
        {
            // The other half of the Unity contract: if the work STARTED draining (StartedDrainAtUtc
            // is set) but did not finish within the timeout, the tool itself is slow — keep the
            // legacy TimeoutException so existing handlers match. We model "started but ran long"
            // by draining an action that blocks on a gate we hold open past the timeout.
            MainThreadDispatcher.SimulateInstanceEnteredForTests();

            var started = new ManualResetEventSlim(false);
            var allowFinish = new ManualResetEventSlim(false);
            Task<int>? task = null;
            var bg = new Thread(() =>
            {
                task = MainThreadDispatcher.EnqueueAsync(() =>
                {
                    started.Set();
                    // Block until the test releases us — past the 50ms timeout, simulating a slow tool.
                    allowFinish.Wait(TimeSpan.FromSeconds(30));
                    return 1;
                }, 50);
            });
            bg.Start();
            bg.Join();

            // Drain on a separate background thread so the blocking action does not deadlock the
            // test thread. The drain marks StartedDrainAtUtc, then blocks on allowFinish — so the
            // 50ms timeout fires mid-execution, exercising the "started but ran long" branch.
            var drainer = new Thread(() => MainThreadDispatcher.DrainForTests()) { IsBackground = true };
            drainer.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "drained action never started");

            var ex = await Assert.ThrowsAsync<TimeoutException>(async () => await task!);
            Assert.NotNull(ex);

            // Release the blocked drainer so it exits cleanly instead of outliving the test host.
            // (The action then TrySetResult on an already-faulted TCS — a no-op — and the drainer
            // thread joins on its own.)
            allowFinish.Set();
        }

        // -----------------------------------------------------------------------------------------------------
        // One consistent dispatch path — fire-and-forget and request/response share one queue/drain.
        // -----------------------------------------------------------------------------------------------------

        [Fact]
        public async Task EnqueueAndEnqueueAsync_ShareOneQueue()
        {
            // Both Enqueue and EnqueueAsync must land in the SAME queue and drain in FIFO order on
            // the SAME pump tick. This is the "one consistent dispatch path" acceptance criterion —
            // a tool handler that mixes fire-and-forget logging with a request/response call sees
            // them in enqueue order, never reordered across two queues.
            MainThreadDispatcher.SimulateInstanceEnteredForTests();

            var order = new List<string>();
            MainThreadDispatcher.Enqueue(() => order.Add("fire-and-forget"));

            Task<int>? task = null;
            var bg = new Thread(() => { task = MainThreadDispatcher.EnqueueAsync(() =>
            {
                order.Add("request-response");
                return 7;
            }, 5_000); });
            bg.Start();
            bg.Join();

            Assert.Equal(2, MainThreadDispatcher.PendingActionCountForTests);

            MainThreadDispatcher.DrainForTests(); // single drain runs both, FIFO

            Assert.Equal(new[] { "fire-and-forget", "request-response" }, order);
            Assert.Equal(7, await task!);
        }

        // -----------------------------------------------------------------------------------------------------
        // Early-boot buffering — calls before the dispatcher arrives do NOT throw; they buffer and
        // drain (FIFO) when the first dispatcher enters the tree.
        // -----------------------------------------------------------------------------------------------------

        [Fact]
        public void Enqueue_BeforeAnyInstance_DoesNotThrow_AndBuffers()
        {
            var ex = Record.Exception(() => MainThreadDispatcher.Enqueue(() => { }));

            Assert.Null(ex);
            Assert.Equal(1, MainThreadDispatcher.PendingActionCountForTests);
            Assert.False(MainThreadDispatcher.HasEverEnteredForTests);
        }

        [Fact]
        public void Enqueue_BeforeInstance_RunsOnce_WhenInstanceArrives()
        {
            var runCount = 0;
            MainThreadDispatcher.Enqueue(() => runCount++);

            Assert.Equal(0, runCount); // not run yet — nothing is in the tree to drain it

            MainThreadDispatcher.SimulateInstanceEnteredForTests(); // first dispatcher arrives

            Assert.Equal(1, runCount);
            Assert.Equal(0, MainThreadDispatcher.PendingActionCountForTests);
        }

        [Fact]
        public void Enqueue_BeforeInstance_PreservesFifoOrdering_OnDrain()
        {
            var order = new List<int>();
            for (var i = 0; i < 5; i++)
            {
                var captured = i;
                MainThreadDispatcher.Enqueue(() => order.Add(captured));
            }

            Assert.Empty(order); // still buffered

            MainThreadDispatcher.SimulateInstanceEnteredForTests();

            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, order);
        }

        [Fact]
        public void EnqueueOffThread_BeforeInstance_Buffers_ThenRunsOnDrain()
        {
            // Enqueue is contractually thread-safe; callers marshal FROM background threads. Prove
            // an off-main-thread enqueue before boot buffers without throwing, then drains when the
            // instance lands.
            var ran = false;
            Exception? offThreadEx = null;

            var t = new Thread(() =>
            {
                try { MainThreadDispatcher.Enqueue(() => ran = true); }
                catch (Exception e) { offThreadEx = e; }
            });
            t.Start();
            t.Join();

            Assert.Null(offThreadEx);
            Assert.Equal(1, MainThreadDispatcher.PendingActionCountForTests);
            Assert.False(ran);

            MainThreadDispatcher.SimulateInstanceEnteredForTests();
            Assert.True(ran);
        }

        // -----------------------------------------------------------------------------------------------------
        // Shutdown safety — bounded teardown drain, no deadlock, no silent drop.
        // -----------------------------------------------------------------------------------------------------

        [Fact]
        public void TeardownDrain_IsBounded_TerminatesWhenDrainedBodyReEnqueues()
        {
            // Boot, then queue an action whose body re-enqueues a fresh action every time it runs.
            // The bounded teardown drain snapshots the current count first, so it runs only the items
            // present at entry and the re-enqueued item is left in the queue — the drain terminates
            // (no hang / no overflow). The unbounded DrainQueue used on the normal _Process tick
            // would loop forever here.
            MainThreadDispatcher.SimulateInstanceEnteredForTests();

            var runs = 0;
            void ReEnqueueingBody()
            {
                runs++;
                if (runs < 1_000_000) // would loop a million times under the unbounded drain
                    MainThreadDispatcher.Enqueue(ReEnqueueingBody);
            }
            MainThreadDispatcher.Enqueue(ReEnqueueingBody);
            Assert.Equal(1, MainThreadDispatcher.PendingActionCountForTests);

            MainThreadDispatcher.DrainBoundedForTests();

            Assert.Equal(1, runs); // ran exactly the one snapshotted item, not the re-enqueued follow-on
            Assert.Equal(1, MainThreadDispatcher.PendingActionCountForTests); // re-enqueue left queued
        }

        [Fact]
        public void TeardownViaExit_DrainsPendingOnce_ThenFailsFast()
        {
            // The bounded drain through the real teardown seam (SimulateInstanceExited →
            // DrainQueueBounded). Two non-re-enqueueing bodies are pending at teardown; the bounded
            // drain runs BOTH (the snapshot is 2) and terminates, and once torn down a later Enqueue
            // fails fast so a pending awaiter cannot hang on a queue nothing will drain.
            MainThreadDispatcher.SimulateInstanceEnteredForTests();

            var runs = 0;
            MainThreadDispatcher.Enqueue(() => runs++);
            MainThreadDispatcher.Enqueue(() => runs++);
            Assert.Equal(2, MainThreadDispatcher.PendingActionCountForTests);

            MainThreadDispatcher.SimulateInstanceExitedForTests(); // bounded teardown drain — must terminate

            Assert.Equal(2, runs);
            Assert.Equal(0, MainThreadDispatcher.PendingActionCountForTests);
            Assert.Throws<InvalidOperationException>(() => MainThreadDispatcher.Enqueue(() => { }));
        }

        [Fact]
        public async Task EnqueueAsync_PendingAtTeardown_IsCompletedNotDropped()
        {
            // A pending request/response call at teardown must NOT be silently dropped: the teardown
            // drain runs its body (completing the TCS), and if for some reason it does not, the
            // post-teardown Enqueue path faults the TCS rather than letting it hang. This test
            // asserts the happy path — the body runs on the teardown drain and the awaiter unblocks.
            MainThreadDispatcher.SimulateInstanceEnteredForTests();

            Task<int>? task = null;
            var bg = new Thread(() => { task = MainThreadDispatcher.EnqueueAsync(() => 99, 5_000); });
            bg.Start();
            bg.Join();
            Assert.False(task!.IsCompleted);

            MainThreadDispatcher.SimulateInstanceExitedForTests(); // teardown drain runs the body

            Assert.True(task.IsCompletedSuccessfully);
            Assert.Equal(99, await task);
        }

        [Fact]
        public async Task EnqueueAsync_PostTeardown_FaultsImmediatelyWithoutHanging()
        {
            // After teardown, EnqueueAsync cannot queue (nothing would drain it). Rather than letting
            // the awaiter hang until the per-call timeout, the TCS is faulted immediately with a
            // structured InvalidOperationException so the HTTP worker returns a clean error.
            MainThreadDispatcher.SimulateInstanceEnteredForTests();
            MainThreadDispatcher.SimulateInstanceExitedForTests();

            Task<int>? task = null;
            var bg = new Thread(() => { task = MainThreadDispatcher.EnqueueAsync(() => 1, 5_000); });
            bg.Start();
            bg.Join();

            Assert.True(task!.IsFaulted);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
            Assert.Contains(nameof(MainThreadDispatcher), ex.Message);
        }

        // -----------------------------------------------------------------------------------------------------
        // Null-argument guard.
        // -----------------------------------------------------------------------------------------------------

        [Fact]
        public void Enqueue_NullAction_ThrowsArgumentNull_RegardlessOfLifecycle()
        {
            Assert.Throws<ArgumentNullException>(() => MainThreadDispatcher.Enqueue(null!));

            MainThreadDispatcher.SimulateInstanceEnteredForTests();
            Assert.Throws<ArgumentNullException>(() => MainThreadDispatcher.Enqueue(null!));
        }

        [Fact]
        public async Task EnqueueAsync_NullAction_ThrowsArgumentNull()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await MainThreadDispatcher.EnqueueAsync<int>(null!, 1_000));
        }

        // -----------------------------------------------------------------------------------------------------
        // Continuations don't run inline on the pump/drain thread.
        // -----------------------------------------------------------------------------------------------------

        [Fact]
        public async Task EnqueueAsync_ContinuationDoesNotRunInlineOnDrainThread()
        {
            // The TCS is built with RunContinuationsAsynchronously, so an awaiter's continuation is
            // posted to the thread pool — NOT executed inline on whatever thread completes the TCS.
            // The completing thread here is the drain thread (DrainForTests below = the _Process /
            // _ExitTree pump in production). Without this option a continuation (e.g. more main-thread
            // work that touches the SceneTree) would execute synchronously on the pump thread
            // mid-drain — a re-entrancy / SceneTree-touch hazard during teardown.
            MainThreadDispatcher.SimulateInstanceEnteredForTests();

            Task<int>? task = null;
            var bg = new Thread(() => { task = MainThreadDispatcher.EnqueueAsync(() => 7, 5_000); });
            bg.Start();
            bg.Join();
            Assert.NotNull(task);
            Assert.False(task!.IsCompleted);

            // Attach a continuation that records WHICH thread it runs on. ExecuteSynchronously asks
            // the runtime to run it inline on the completing thread IF the TCS allows synchronous
            // continuations — which is exactly what RunContinuationsAsynchronously forbids. So with
            // the fix this continuation can never observe the drain thread.
            var continuationThreadId = 0;
            var continuationRan = new ManualResetEventSlim(false);
            var cont = task.ContinueWith(_ =>
                {
                    continuationThreadId = Thread.CurrentThread.ManagedThreadId;
                    continuationRan.Set();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            var drainThreadId = Thread.CurrentThread.ManagedThreadId;
            MainThreadDispatcher.DrainForTests();

            Assert.True(task.IsCompletedSuccessfully);
            Assert.False(continuationRan.IsSet); // not run inline on the drain thread

            // It does still run — just asynchronously, on a thread that is NOT the drain thread.
            Assert.True(continuationRan.Wait(TimeSpan.FromSeconds(5)), "continuation never ran asynchronously");
            Assert.NotEqual(drainThreadId, continuationThreadId);
            Assert.Equal(7, await task);
            await cont;
        }
    }
}
