#if TOOLS
#nullable enable
using Godot;
using GodotOpenMcp.Bridge.Runtime.MainThread;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Editor entry point for the Godot Open MCP addon. Referenced by
    /// <c>addons/godot_open_mcp/plugin.cfg</c> (the <c>script</c> field) and loaded
    /// by the Godot Editor when the plugin is enabled.
    ///
    /// Owns bridge startup and shutdown so later phases can register the HTTP
    /// server and <c>/ping</c> (P1.3) and the instance lock (P1.4) on a stable
    /// lifecycle surface. The main-thread dispatcher (P1.2) is installed here and
    /// is the single thread-marshaling path every HTTP handler routes editor API
    /// calls through — no handler calls <c>EditorInterface</c> / scene-tree APIs
    /// directly off the worker thread. No HTTP endpoints or tool dispatch live
    /// here yet.
    ///
    /// Lifecycle mirrors the Unity bridge (<c>BridgeHttpServer</c> static init /
    /// <c>OnBeforeAssemblyReload</c> / <c>OnQuitting</c>) adapted to Godot's
    /// <see cref="EditorPlugin"/> virtuals: <see cref="_EnterTree"/> is enable/init,
    /// <see cref="_ExitTree"/> is disable/teardown. Each is independently
    /// guarded so a partial init failure leaves the editor usable, and the
    /// teardown path is safe to call even if init never completed.
    /// </summary>
    [Tool]
    public partial class GodotOpenMcpPlugin : EditorPlugin
    {
        const string LogPrefix = "Godot Open MCP";

        // Reflects whether the plugin's resources have been armed. Distinct from
        // the Godot-managed in-tree state: this is our own bookkeeping so the
        // disable path knows whether there is anything to tear down. Later phases
        // (HTTP listener, instance lock) will gate their teardown on this same
        // flag; the dispatcher is owned directly below.
        bool _enabled;

        // Pump for off-thread → main-thread work (P1.2). Added as a child of this
        // EditorPlugin Node so it lives in the editor SceneTree and gets _Process
        // ticks for the lifetime of the plugin. Null when the plugin is disabled
        // or the dispatcher failed to install; the disable path frees it under the
        // main-thread guard.
        MainThreadDispatcher? _dispatcher;

        /// <summary>
        /// Enable / init. Runs when the user toggles the plugin on, and on editor
        /// startup if the plugin was left enabled. Performs only scaffolding-level
        /// work for now; bridge subsystems are wired by later phases and will hook
        /// in here.
        /// </summary>
        public override void _EnterTree()
        {
            // Idempotency: a redundant enable is a no-op. Godot does not double-fire
            // _EnterTree under normal conditions, but guarding keeps the disable →
            // re-enable cycle deterministic when later phases add subsystems that
            // are expensive or side-effectful to start.
            if (_enabled)
            {
                LogInfo("plugin enable skipped — already active");
                return;
            }

            try
            {
                // Install the main-thread dispatcher FIRST so every subsequent subsystem
                // (P1.3 HTTP listener, P1.4 instance lock, P2.x tool dispatch) has a
                // ready marshaling path. It is added as a child Node so it ticks for the
                // whole plugin lifetime; AddChild runs synchronously on this (main)
                // thread, so on return any buffered early-boot work is already drained
                // (the dispatcher's _EnterTree flushes the static queue). A failure here
                // is non-fatal to the editor but fatal to the bridge — surface it and
                // leave _enabled false so the disable path is a clean no-op.
                _dispatcher = new MainThreadDispatcher { Name = "GodotOpenMcpMainThreadDispatcher" };
                AddChild(_dispatcher);

                _enabled = true;
                LogInfo($"plugin enabled (v{BridgeSession.BridgeVersion})");
            }
            catch (System.Exception e)
            {
                // Fail safe: never take down the editor from a plugin-load fault. The
                // flag stays false so the teardown path is a clean no-op, and the user
                // sees an actionable error in the editor output.
                LogError($"failed to enable plugin: {e.Message}");
                _enabled = false;
            }
        }

        /// <summary>
        /// Disable / teardown. Runs when the user toggles the plugin off and on
        /// editor quit. Must be safe to call even if <see cref="_EnterTree"/> never
        /// completed (partial-init / faulted-init cases). Each subsystem teardown
        /// added by later phases will be independently guarded.
        /// </summary>
        public override void _ExitTree()
        {
            if (!_enabled)
            {
                // Nothing was armed — this is the normal path after a faulted init or
                // a redundant disable. Quiet rather than noisy: a noisy teardown of
                // never-started resources would spam the log on every editor quit.
                return;
            }

            try
            {
                // P1.4 will release the instance lock here (graceful quit deletes it;
                // domain reload keeps it on disk so the MCP server can detect a stale
                // bridge — the Godot analog of Unity's releaseLock:false on
                // beforeAssemblyReload). P1.3 will stop the HTTP listener. The dispatcher
                // is freed last (among current subsystems) so any teardown work the later
                // subsystems marshal still lands on a live pump.
                FreeDispatcher();

                _enabled = false;
                LogInfo("plugin disabled");
            }
            catch (System.Exception e)
            {
                // Teardown must not throw out of _ExitTree. Suppress and report; the
                // flag is reset below regardless so a later enable starts clean.
                LogError($"error during plugin disable: {e.Message}");
                _enabled = false;
            }
        }

        /// <summary>
        /// Bridge version, sourced from <see cref="BridgeSession"/>. See the remarks
        /// there for the sync contract.
        /// </summary>
        public static string BridgeVersion => BridgeSession.BridgeVersion;

        /// <summary>
        /// Free the dispatcher Node and null the reference. Idempotent — a null or
        /// already-freed instance just clears the reference. The dispatcher's own
        /// <c>_ExitTree</c> runs a bounded drain of any pending queued work before the
        /// Node is freed, so in-flight awaiters are completed rather than dropped and
        /// a re-enqueueing body cannot hang teardown. Must run on the editor main
        /// thread (<see cref="Node.Free"/> is main-thread-only); <c>_ExitTree</c> is a
        /// main-thread callback so that holds.
        /// </summary>
        void FreeDispatcher()
        {
            if (_dispatcher == null)
                return;
            try
            {
                // Skip when Godot has already disposed the dispatcher — nothing left for
                // us to free (defensive against a partial teardown / faulted init).
                if (GodotObject.IsInstanceValid(_dispatcher))
                    _dispatcher.Free();
            }
            catch (System.ObjectDisposedException)
            {
                // Already disposed by Godot — benign.
            }
            catch (System.Exception e)
            {
                LogError($"error freeing dispatcher: {e.Message}");
            }
            _dispatcher = null;
        }

        static void LogInfo(string message) => GD.Print($"[{LogPrefix}] {message}");
        static void LogError(string message) => GD.PushError($"[{LogPrefix}] {message}");
    }
}
#endif
