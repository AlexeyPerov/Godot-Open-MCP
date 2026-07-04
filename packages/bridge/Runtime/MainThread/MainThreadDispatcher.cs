#if TOOLS
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace GodotOpenMcp.Bridge.Runtime.MainThread
{
    /// <summary>
    /// Pumps <see cref="Enqueue(Action)"/> / <see cref="EnqueueAsync{T}"/> work onto the
    /// Godot editor main thread, once per <see cref="Node._Process"/> tick. This is the
    /// Godot analog of Unity Open MCP's <c>MainThreadDispatcher</c>
    /// (<c>EditorApplication.update</c>-pumped) and the bridge's single thread-marshaling
    /// path: every HTTP handler that needs <c>EditorInterface</c> / scene-tree access
    /// routes through here (P1.3 <c>/ping</c>, P2.x tool dispatch, P3.x gate flow).
    ///
    /// Godot has no static <c>EditorApplication.update</c> hook, so the pump is a long-lived
    /// editor <see cref="Node"/> added to the <see cref="SceneTree"/> by
    /// <c>GodotOpenMcpPlugin._EnterTree</c> as a child of the plugin. It ticks for the whole
    /// plugin lifetime, draining the queue on every frame. Off-thread callers (the HTTP
    /// listener worker in P1.3) enqueue from any thread; the action runs on the next
    /// <see cref="_Process"/> tick on the main thread.
    ///
    /// Two flows:
    /// <list type="bullet">
    ///   <item><see cref="Enqueue"/> — fire-and-forget. Mirrors Unity's <c>Enqueue(Action)</c>;
    ///   used by background telemetry / log forwarding where no return value is needed.</item>
    ///   <item><see cref="EnqueueAsync{T}"/> — request/response with a per-call timeout. Mirrors
    ///   Unity's <c>EnqueueAsync&lt;T&gt;</c>: the worker thread awaits a
    ///   <see cref="TaskCompletionSource{T}"/> that the main-thread drain resolves with the
    ///   delegate's result (or faults with its exception). The timeout distinguishes
    ///   "the work never started draining" (<see cref="MainThreadBlockedException"/> — almost
    ///   certainly a Godot modal blocking the main thread) from "the work started but ran past
    ///   the timeout" (<see cref="TimeoutException"/> — the tool itself is slow).</item>
    /// </list>
    ///
    /// Lifecycle guarantees (the acceptance criteria for this task):
    /// <list type="bullet">
    ///   <item><b>Reliable marshaling.</b> Off-thread calls land on the main thread on the next
    ///   <see cref="_Process"/> tick once the dispatcher Node is in the tree.</item>
    ///   <item><b>Early-boot buffering.</b> <see cref="Enqueue"/>/<see cref="EnqueueAsync{T}"/>
    ///   issued before the dispatcher Node has entered the tree do NOT throw; they buffer in
    ///   the static queue and drain (FIFO) the moment the first dispatcher arrives — same
    ///   contract as Unity's <c>EditorApplication.update</c> registration firing on the next
    ///   editor frame.</item>
    ///   <item><b>Shutdown safety.</b> <see cref="_ExitTree"/> drains anything already queued
    ///   via a <b>bounded</b> pass (a snapshot of the current count), so a body that re-enqueues
    ///   cannot make teardown hang. A pending awaiter is completed (not silently dropped) and a
    ///   post-teardown <see cref="Enqueue"/> fails fast with <see cref="InvalidOperationException"/>
    ///   so a later caller cannot hang on a queue nothing will drain.</item>
    /// </list>
    /// </summary>
    public partial class MainThreadDispatcher : Node
    {
        /// <summary>
        /// Queue-wait time (seconds) after which <see cref="DrainQueue"/> also logs a diagnostic.
        /// The call may still complete; this only surfaces "a modal or heavy editor stall held
        /// the main thread" in the console / log without failing the call. Kept separate from the
        /// per-call timeout (which is the hard fail). Mirrors Unity's <c>QueueStallWarnSeconds</c>.
        /// </summary>
        const double QueueStallWarnSeconds = 5.0;

        /// <summary>
        /// The managed thread id captured when this dispatcher entered the tree. Godot calls
        /// <see cref="_EnterTree"/> on the engine main thread, so this is the main-thread id.
        /// Defaults to a sentinel (<c>-1</c>, which no real <see cref="Thread.ManagedThreadId"/>
        /// takes) until <see cref="_EnterTree"/> runs, so a pre-boot caller is correctly treated
        /// as off-main-thread rather than capturing a wrong id from whatever thread first touches
        /// this type.
        /// </summary>
        public static int MainThreadId { get; private set; } = -1;

        /// <summary>True when the calling thread is the captured Godot main thread.</summary>
        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        static readonly ConcurrentQueue<QueuedAction> _queue = new ConcurrentQueue<QueuedAction>();

        /// <summary>
        /// The currently-installed dispatcher, or <c>null</c> when none is in the tree.
        /// <b>Marked <c>volatile</c></b> so its store/load is ordered relative to the
        /// <see cref="_hasEverEntered"/> volatile publish: <see cref="_EnterTree"/> writes
        /// <c>_instance = this</c> BEFORE <see cref="_hasEverEntered = true"/>, and a background-
        /// thread <see cref="Enqueue"/> reads <see cref="_hasEverEntered"/> then <see cref="_instance"/>.
        /// Without the acquire/release semantics a reader could observe a fresh
        /// <see cref="_hasEverEntered"/><c>==true</c> against a stale <c>_instance==null</c> at the
        /// trailing boot edge and throw even though a live dispatcher is already in the tree. The
        /// two volatile accesses keep the "is there an instance / has one ever entered" decision
        /// reading a coherent pair.
        /// </summary>
        static volatile MainThreadDispatcher? _instance;

        /// <summary>
        /// Test-only override of "a dispatcher is currently in the tree." Production NEVER sets
        /// this — it is always <c>false</c> there and <see cref="InstancePresent"/> falls through
        /// to the real <see cref="_instance"/>. The unit tests cannot construct a Godot
        /// <see cref="Node"/> (it faults the binary-less host), so they flip this flag via the
        /// simulate-* seams to model boot/teardown edges while keeping the throw-vs-buffer
        /// decision in <see cref="Enqueue"/> byte-for-byte identical to prod.
        /// </summary>
        static bool _instancePresentForTests;

        /// <summary>
        /// True when a dispatcher is currently installed and able to drain the queue. In
        /// production this is just "<see cref="_instance"/> is non-null"; the test-only
        /// <see cref="_instancePresentForTests"/> lets the Node-less unit tests model the same
        /// condition without a live instance.
        /// </summary>
        static bool InstancePresent => _instance != null || _instancePresentForTests;

        /// <summary>
        /// True once ANY dispatcher instance has entered the tree at least once. Combined with
        /// <see cref="_instance"/> being <c>null</c>, this disambiguates the two reasons no
        /// dispatcher is currently installed: <c>false</c> = "never booted yet" (the early-boot
        /// window — buffer and drain when one arrives), <c>true</c> = "booted then torn down"
        /// (plugin unloaded / between editor reloads — fail fast so a pending awaiter does not
        /// hang on a queue nothing will drain). Written only on the editor main thread
        /// (<see cref="_EnterTree"/>); read in the otherwise-thread-safe <see cref="Enqueue"/>.
        /// Both this flag and <see cref="_instance"/> are <c>volatile</c>, so the
        /// <see cref="_EnterTree"/> publish order (write <c>_instance</c> first, then this flag)
        /// is preserved for a concurrent reader: a fresh <see cref="_hasEverEntered"/><c>==true</c>
        /// can never be paired with a stale <c>_instance==null</c>, so a live, just-booted
        /// dispatcher is never mistaken for a torn-down one (which would wrongly throw).
        /// </summary>
        static volatile bool _hasEverEntered;

        /// <summary>The currently-installed dispatcher instance, or <c>null</c> when none is in the tree.</summary>
        public static MainThreadDispatcher? Instance => _instance;

        /// <summary>
        /// Queue an action to run on the next main-thread <see cref="_Process"/> tick.
        /// Thread-safe. Mirrors Unity's <c>MainThreadDispatcher.Enqueue(Action)</c>.
        /// <para>
        /// <b>Early-boot buffering.</b> The dispatcher Node is added to the tree via
        /// <c>AddChild</c> in <c>GodotOpenMcpPlugin._EnterTree</c>. A caller that marshals to the
        /// main thread in the window before that lands — or before the plugin has been enabled at
        /// all — must NOT throw: the action is buffered in the static queue and drained the moment
        /// the first dispatcher enters the tree (<see cref="_EnterTree"/>), with FIFO ordering
        /// preserved. This turns the install race into a deferred run.
        /// </para>
        /// <para>
        /// <b>Post-teardown fail-fast.</b> Once a dispatcher HAS booted and then been removed
        /// (plugin disabled / editor reload), nothing remains to drain the queue, so a pending
        /// awaiter would hang forever. In that state (<see cref="_instance"/> is <c>null</c> but
        /// <see cref="_hasEverEntered"/> is <c>true</c>) <see cref="Enqueue"/> throws
        /// <see cref="InvalidOperationException"/> immediately.
        /// </para>
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            // No dispatcher currently in the tree: buffer if one has never booted yet (it will
            // arrive and drain — the early-boot window), but fail fast if one booted and was torn
            // down (nothing left to drain → the awaiter would hang). With a live instance, this
            // is the normal enqueue path.
            if (!InstancePresent && _hasEverEntered)
                throw new InvalidOperationException(
                    $"No {nameof(MainThreadDispatcher)} is in the tree; cannot enqueue main-thread work. " +
                    "The dispatcher booted and was removed (plugin disabled / editor reload); nothing would " +
                    "drain the queue. It is added by GodotOpenMcpPlugin._EnterTree and removed on _ExitTree.");

            _queue.Enqueue(new QueuedAction { Action = action, EnqueuedAtUtc = DateTime.UtcNow });
        }

        /// <summary>
        /// Enqueue a request/response call and return a <see cref="Task{TResult}"/> the worker
        /// thread awaits. Mirrors Unity's <c>MainThreadDispatcher.EnqueueAsync&lt;T&gt;</c>:
        /// the <paramref name="action"/> runs on the main thread (next <see cref="_Process"/>
        /// tick) and the returned task completes with its result, or faults with its exception.
        /// <para>
        /// The <paramref name="timeoutMs"/> per-call timeout distinguishes two failure modes
        /// (mirrors Unity's <c>MainThreadBlockedException</c> split — adapted to Godot):
        /// <list type="bullet">
        ///   <item>If the action NEVER started draining within the timeout
        ///   (<see cref="QueuedAction.StartedDrainAtUtc"/> is still null), the main thread was
        ///   blocked the entire window — almost certainly a Godot modal (unsaved-changes dialog,
        ///   export dialog, a third-party editor window). The task faults with
        ///   <see cref="MainThreadBlockedException"/> so the HTTP handler (P1.3+) can build a
        ///   <c>main_thread_blocked</c> error envelope pointing the agent at the recovery hints
        ///   (dismiss the dialog, <c>scene_save</c>, restart).</item>
        ///   <item>If the action started but did not finish within the timeout, the tool itself
        ///   ran long. The task faults with <see cref="TimeoutException"/> so existing handlers
        ///   keep their semantics.</item>
        /// </list>
        /// </para>
        /// </summary>
        public static Task<T> EnqueueAsync<T>(Func<T> action, int timeoutMs)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Wrap the typed Func in an Action that resolves the TCS, then track drain timing via
            // the shared QueuedAction envelope. The timeout callback inspects
            // StartedDrainAtUtc to distinguish "main thread blocked" (never drained) from
            // "tool ran long".
            //
            // The TCS uses RunContinuationsAsynchronously so the awaiter's continuation is posted
            // to the thread pool instead of running INLINE on whatever thread completes the TCS.
            // The completing thread here is the main-thread drain (this class's _Process /
            // _ExitTree). Without this option a continuation (e.g. more main-thread work that
            // touches the SceneTree) would execute synchronously on the pump thread mid-drain, a
            // re-entrancy / SceneTree-touch hazard; it would also let a re-enqueueing continuation
            // extend the drain inline. Posting continuations off-thread keeps the pump tick (and
            // the bounded teardown drain) free of caller-supplied continuation bodies.
            var queued = new QueuedAction
            {
                EnqueuedAtUtc = DateTime.UtcNow,
                Action = () =>
                {
                    try
                    {
                        tcs.TrySetResult(action());
                    }
                    catch (Exception e)
                    {
                        tcs.TrySetException(e);
                    }
                },
            };

            // Same buffer-vs-throw edge as Enqueue: pre-boot buffers (the early-boot window),
            // post-teardown throws (nothing would drain → the awaiter would hang).
            if (InstancePresent || !_hasEverEntered)
            {
                _queue.Enqueue(queued);
            }
            else
            {
                // Post-teardown: nothing will drain this. Fault the TCS immediately so the awaiter
                // unblocks with a structured error instead of hanging on the timeout.
                tcs.TrySetException(new InvalidOperationException(
                    $"No {nameof(MainThreadDispatcher)} is in the tree; cannot enqueue main-thread work. " +
                    "The dispatcher booted and was removed (plugin disabled / editor reload); nothing would " +
                    "drain the queue."));
            }

            // When the timeout fires, distinguish "the work never started draining" (the main
            // thread was blocked the whole window — almost certainly a Godot modal) from "the
            // work started but ran past the timeout" (the tool itself is slow). The former
            // surfaces a structured MainThreadBlockedException so the caller can build a
            // main_thread_blocked / modal_likely_open error; the latter keeps the legacy
            // TimeoutException so existing handlers still match.
            var timer = new System.Threading.Timer(_ =>
            {
                if (!queued.StartedDrainAtUtc.HasValue)
                {
                    tcs.TrySetException(new MainThreadBlockedException(timeoutMs));
                }
                else
                {
                    tcs.TrySetException(new TimeoutException());
                }
            }, null, timeoutMs, Timeout.Infinite);
            tcs.Task.ContinueWith(_ => timer.Dispose());

            return tcs.Task;
        }

        public override void _EnterTree()
        {
            MainThreadId = Thread.CurrentThread.ManagedThreadId;
            _instance = this;
            _hasEverEntered = true;

            // Drain anything buffered before this first dispatcher arrived (early-boot window:
            // callers that marshalled to the main thread before AddChild landed). Running it here
            // — on the engine main thread, which is where _EnterTree fires — flushes the early-
            // boot backlog as soon as the dispatcher enters the tree rather than waiting for the
            // first _Process tick, and preserves FIFO order. (An action enqueued AFTER this loop
            // observes the queue empty is not lost — it is picked up on the next _Process tick,
            // which runs exactly once per frame.)
            DrainQueue();
        }

        public override void _ExitTree()
        {
            if (ReferenceEquals(_instance, this))
                _instance = null;

            // Drain anything left so pending awaiters do not hang forever, but BOUND the drain to
            // the items already queued at this moment (see DrainQueueBounded). The unbounded
            // DrainQueue used on the normal _Process tick keeps draining until the queue empties;
            // at teardown that is a liveness hazard, because an action body (or, without
            // RunContinuationsAsynchronously, an awaiter continuation) that re-enqueues could make
            // the loop run forever while the editor is mid-teardown. Snapshotting the count first
            // means a body that re-enqueues lands its work in the queue but does NOT extend this
            // drain — the teardown terminates deterministically.
            DrainQueueBounded();
        }

        public override void _Process(double delta)
        {
            DrainQueue();
        }

        static void DrainQueue()
        {
            while (_queue.TryDequeue(out var queued))
            {
                InvokeDrained(queued);
            }
        }

        /// <summary>
        /// Drain only the actions already queued at the moment this is called — at most a snapshot
        /// of the current <see cref="ConcurrentQueue{T}.Count"/> — so a drained body that re-enqueues
        /// cannot make the loop run unboundedly. Used at <see cref="_ExitTree"/> teardown, where the
        /// unbounded <see cref="DrainQueue"/> would be a liveness hazard against re-enqueueing work
        /// while the editor is tearing the dispatcher down. Re-enqueued items are simply left in the
        /// queue (no live dispatcher will drain them afterward; <see cref="Enqueue"/> then fails fast
        /// for any later caller because <see cref="_hasEverEntered"/> is set). FIFO order of the
        /// snapshot is preserved.
        /// </summary>
        static void DrainQueueBounded()
        {
            // Snapshot the bound BEFORE dequeuing. ConcurrentQueue.Count is a point-in-time read;
            // capturing it first caps the iteration at the items present now, so any item a drained
            // body re-enqueues is not pulled into this same drain pass.
            var budget = _queue.Count;
            for (var i = 0; i < budget && _queue.TryDequeue(out var queued); i++)
            {
                InvokeDrained(queued);
            }
        }

        /// <summary>
        /// Invoke one drained action, swallowing and surfacing any throw without killing the pump
        /// loop. The action wrapper (built by <see cref="EnqueueAsync{T}"/> or supplied to
        /// <see cref="Enqueue"/>) is responsible for routing its own exceptions back to its
        /// awaiter (the TCS try/catch). A throw here would only mean a bug in the action wrapper
        /// itself.
        /// </summary>
        static void InvokeDrained(QueuedAction queued)
        {
            var waitMs = (DateTime.UtcNow - queued.EnqueuedAtUtc).TotalMilliseconds;
            // Stamp the drain start so EnqueueAsync's timeout can tell "never started" (main
            // thread blocked) from "ran long".
            queued.StartedDrainAtUtc = DateTime.UtcNow;
            if (waitMs > QueueStallWarnSeconds * 1000)
            {
                // The action sat in the queue for seconds before the main thread picked it up — a
                // strong signal a modal or heavy editor stall held the thread. Log once so it
                // surfaces in the console / Editor.log without failing the call (the work may
                // still succeed).
                GD.PushWarning(
                    $"[Godot Open MCP] main-thread queue stalled for {waitMs:F0}ms before " +
                    "processing a tool dispatch — a Godot modal dialog or a long editor " +
                    "operation may be blocking the main thread.");
            }
            try
            {
                queued.Action();
            }
            catch (Exception e)
            {
                GD.PushError($"[Godot Open MCP] main-thread dispatcher action threw: {e}");
            }
        }

        // ---------------------------------------------------------------------------------------------------
        // Pure-managed test seams. The buffer/drain lifecycle lives entirely in the static members
        // above (_queue / _instance / _hasEverEntered / Enqueue / DrainQueue); none of it needs a
        // live Godot Node. But _EnterTree / _ExitTree — the only members that mutate _instance — are
        // Node lifecycle callbacks, and constructing a MainThreadDispatcher (a Godot Node) faults
        // the binary-less test host. These seams let the unit tests model the boot/teardown edges
        // and drain the queue WITHOUT instantiating a Node, so the lifecycle is CI-unit-testable.
        // They are internal (same-assembly only) and never referenced by production code.

        /// <summary>Test-only: number of actions currently buffered (not yet drained).</summary>
        internal static int PendingActionCountForTests => _queue.Count;

        /// <summary>Test-only: true once any dispatcher has entered the tree (the fail-fast vs buffer edge).</summary>
        internal static bool HasEverEnteredForTests => _hasEverEntered;

        /// <summary>
        /// Test-only: model the first dispatcher entering the tree WITHOUT constructing a Godot Node.
        /// Mirrors <see cref="_EnterTree"/>'s pure-managed effects (capture the main-thread id, mark
        /// an instance present and ever-entered, drain the early-boot backlog) but never touches
        /// native Godot.
        /// </summary>
        internal static void SimulateInstanceEnteredForTests()
        {
            // Presence is faked via _instancePresentForTests — a real instance is a Node we cannot
            // construct in the binary-less host. _instance stays null here; production sets it from
            // the real _EnterTree.
            MainThreadId = Thread.CurrentThread.ManagedThreadId;
            _hasEverEntered = true;
            _instancePresentForTests = true;
            DrainQueue();
        }

        /// <summary>
        /// Test-only: model the dispatcher leaving the tree (teardown). Mirrors
        /// <see cref="_ExitTree"/>'s pure-managed effects — mark no instance present and drain
        /// anything still pending via the SAME bounded drain teardown uses — so that the unit
        /// tests exercise the real termination guarantee and a subsequent <see cref="Enqueue"/>
        /// takes the post-teardown fail-fast branch.
        /// </summary>
        internal static void SimulateInstanceExitedForTests()
        {
            _instancePresentForTests = false;
            DrainQueueBounded();
        }

        /// <summary>Test-only: drain the buffered actions on the calling thread (no Node, no tick).</summary>
        internal static void DrainForTests() => DrainQueue();

        /// <summary>
        /// Test-only: model the bounded teardown drain (<see cref="_ExitTree"/>) directly, WITHOUT
        /// also flipping the instance-present flag. Lets a test assert the snapshot-and-bound
        /// termination guarantee against a re-enqueueing body in isolation from the fail-fast
        /// lifecycle transition.
        /// </summary>
        internal static void DrainBoundedForTests() => DrainQueueBounded();

        /// <summary>
        /// Test-only: reset ALL static lifecycle state to its pre-boot defaults. Static state
        /// outlives a single test, so a test exercising the boot/teardown edges MUST call this
        /// first to start from a clean "never booted, nothing buffered" baseline.
        /// </summary>
        internal static void ResetForTests()
        {
            while (_queue.TryDequeue(out _)) { }
            _instance = null;
            _instancePresentForTests = false;
            _hasEverEntered = false;
            MainThreadId = -1;
        }

        /// <summary>
        /// Holds the queue-wait timing for the stall diagnostic. <see cref="DrainQueue"/> stamps
        /// <see cref="StartedDrainAtUtc"/> when it begins running the Action; the
        /// <see cref="EnqueueAsync{T}"/> timeout callback reads it to distinguish "main thread
        /// blocked the whole window" (null → <see cref="MainThreadBlockedException"/>) from "the
        /// work started but ran past the timeout" (set → <see cref="TimeoutException"/>).
        /// </summary>
        sealed class QueuedAction
        {
            public Action Action = delegate { };
            public DateTime EnqueuedAtUtc;

            /// <summary>
            /// Null until <see cref="DrainQueue"/> starts draining this action. Set on the main
            /// thread; read by the <see cref="EnqueueAsync{T}"/> timeout callback on a Timer
            /// thread. Volatile would be the strictly-correct guard, but the worst case of a stale
            /// read is surfacing the generic <see cref="TimeoutException"/> instead of
            /// <see cref="MainThreadBlockedException"/> — both are failures, and the diagnostic
            /// intent (point at a likely modal) only fires when this is null, so a false negative
            /// degrades gracefully. Kept as a plain field for simplicity.
            /// </summary>
            public DateTime? StartedDrainAtUtc;
        }
    }

    /// <summary>
    /// Raised by <see cref="MainThreadDispatcher.EnqueueAsync{T}"/> when the per-call timeout
    /// elapses AND the queued action never started draining — i.e. the main thread was blocked
    /// for the entire window. Callers (the P1.3+ HTTP handlers) catch this and build a
    /// <c>main_thread_blocked</c> / <c>modal_likely_open</c> error envelope pointing the agent
    /// at the dismiss loop, <c>scene_save</c>, or a restart. Distinct from
    /// <see cref="TimeoutException"/> (which means the work started but ran long) so existing
    /// handlers keep their semantics. Adapted from Unity's
    /// <c>UnityOpenMcpBridge.MainThreadBlockedException</c>.
    /// </summary>
    public sealed class MainThreadBlockedException : Exception
    {
        /// <summary>The per-call timeout that elapsed (ms), surfaced for the error envelope.</summary>
        public int TimeoutMs { get; }

        public MainThreadBlockedException(int timeoutMs)
            : base(
                "The Godot main thread did not process the tool dispatch within the timeout — " +
                "a Godot modal dialog (unsaved changes, export, a third-party editor window) " +
                "or a long editor operation is almost certainly blocking it. " +
                "Dismiss any open dialog, scene_save before retrying, " +
                "or restart the editor if a popup is wedged.")
        {
            TimeoutMs = timeoutMs;
        }
    }
}
#endif
