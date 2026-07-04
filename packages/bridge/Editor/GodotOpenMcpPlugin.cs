#if TOOLS
#nullable enable
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Editor entry point for the Godot Open MCP addon. Referenced by
    /// <c>addons/godot_open_mcp/plugin.cfg</c> (the <c>script</c> field) and loaded
    /// by the Godot Editor when the plugin is enabled.
    ///
    /// This is the P1.1 scaffold: plugin metadata + editor bootstrap wiring only.
    /// It owns bridge startup and shutdown so later phases can register the
    /// main-thread dispatcher (P1.2), the HTTP server and <c>/ping</c> (P1.3),
    /// and the instance lock (P1.4) on a stable lifecycle surface. No HTTP
    /// endpoints or tool dispatch live here yet.
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
        // (dispatcher, HTTP listener, instance lock) will gate their teardown on
        // this same flag.
        bool _enabled;

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
                // P1.2 will install the main-thread dispatcher here (added as a child
                // Node so it receives _Process ticks for the lifetime of the plugin).
                // P1.3 will start the HTTP listener; P1.4 will acquire the instance
                // lock. Each subsystem owns its own try/catch so a failure in one does
                // not abort the others — but for the scaffold there is nothing yet to
                // start, so the armed flag is the only state.

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
                // beforeAssemblyReload). P1.3 will stop the HTTP listener; P1.2 will
                // free the dispatcher Node.

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

        static void LogInfo(string message) => GD.Print($"[{LogPrefix}] {message}");
        static void LogError(string message) => GD.PushError($"[{LogPrefix}] {message}");
    }
}
#endif
