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
    /// Owns bridge startup and shutdown: P1.2 installs the main-thread dispatcher
    /// and P1.3 starts the HTTP listener serving <c>GET /ping</c>; P1.4 will add
    /// the instance lock on the same lifecycle surface. The main-thread dispatcher
    /// is the single thread-marshaling path every HTTP handler routes editor API
    /// calls through — no handler calls <c>EditorInterface</c> / scene-tree APIs
    /// directly off the worker thread.
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

                // Cache the session's static state (project path, Godot version) BEFORE the
                // HTTP listener starts so /ping has a deterministic payload from the first
                // probe. Runs on the main thread (here) so it may freely touch engine APIs.
                BridgeSession.InitializeForEnable();

                // Register the P2.1 smoke stub tool so POST /tools/godot_open_mcp_echo
                // round-trips end-to-end before any real tool family lands. Real tool families
                // (P2.2+) register themselves the same way; the registry is idempotent so a
                // re-enable after a domain reload refreshes entries without duplicate-key noise.
                BridgeToolRegistry.RegisterEchoStub();
                // P2.2 — first real read-only tool: godot_open_mcp_node_find. Registers alongside
                // the echo stub on the same enable surface; idempotent re-register refreshes the
                // handler reference after a domain reload.
                NodeTools.RegisterNodeTools();
                // P2.6 — scene lifecycle tools: godot_open_mcp_scene_open / scene_save /
                // scene_list_opened. Registered alongside the node family on the same enable
                // surface; idempotent re-register refreshes the handler references after a domain
                // reload.
                SceneTools.RegisterSceneTools();
                // P3.6 — gate meta-tools: godot_open_mcp_validate_edit / checkpoint_create /
                // delta. Read-only (bypass the gate), group core. Registered alongside the node/scene
                // families so an agent can run the explicit checkpoint → mutate → delta workflow in
                // one session. Idempotent re-register refreshes the handler references after a
                // domain reload.
                GateTools.RegisterGateTools();
                // P4.1 — resource read tools: godot_open_mcp_resource_find (exact path/UID lookup +
                // indexed type search) and godot_open_mcp_resource_get_data (bounded property
                // inspection). Read-only, group resource. Registered alongside the other families so
                // an agent can discover and inspect standalone .tres/.res resources. Idempotent
                // re-register refreshes the handler references after a domain reload.
                ResourceTools.RegisterResourceTools();
                // P4.4 — filesystem tools: godot_open_mcp_filesystem_list (indexed directory
                // listing, read-only) and godot_open_mcp_filesystem_reimport (exact-file
                // reimport or full scan, mutating). Registered alongside the resource family so an
                // agent can browse the res:// tree and ask the editor to notice changed files.
                // Idempotent re-register refreshes the handler references after a domain reload.
                FileSystemTools.RegisterFilesystemTools();
                // P4.5 — editor application-state tools: godot_open_mcp_editor_application_get_state
                // (read-only play-process snapshot) and godot_open_mcp_editor_application_set_state
                // (start main/current/custom scene or stop play, gated). Registered alongside the other
                // families so an agent can drive the editor's play lifecycle. Idempotent re-register
                // refreshes the handler references after a domain reload.
                EditorApplicationTools.RegisterEditorApplicationTools();
                // P4.6 — editor selection tools: godot_open_mcp_editor_selection_get (read-only node
                // selection snapshot) and godot_open_mcp_editor_selection_set (replace/clear the
                // selection, gated). Registered alongside the other editor families so an agent can
                // inspect and drive the editor's node selection. Idempotent re-register refreshes the
                // handler references after a domain reload.
                EditorSelectionTools.RegisterEditorSelectionTools();

                // P4.7 — console log tools. Install the process-wide log collector and wire BridgeLog
                // to forward into it (no recursive logging — the collector write path never logs back).
                // Then register godot_open_mcp_console_get_logs (read-only) and
                // godot_open_mcp_console_clear_logs (gate-free direct — mutates only ephemeral addon
                // state, not project files or the native Output panel). Idempotent re-register refreshes
                // the handler references after a domain reload.
                GodotOpenMcp.Bridge.Runtime.Logging.GodotLogCollector.GetOrCreate();
                BridgeLog.InstallCollectorSink();
                ConsoleTools.RegisterConsoleTools();

                // P5.4 — event stream. Initialize the ring buffer + collector fan-out sink AFTER the
                // collector is installed (the sink resolves a live collector at arm time) and BEFORE the
                // HTTP listener starts so /events and /events/poll are serving against an armed source
                // from the first request. The sink is the single fan-in — no second raw Godot logger
                // (dual-ingest risk). Editor-state transitions are emitted from the authoritative
                // observed-transition points (EditorApplicationTools start/stop); a background observer
                // for user-clicked play arrives in a later phase.
                BridgeEventSource.Initialize();

                // P4.8 — screenshot tools: screenshot_viewport (active editor 2D/3D viewport),
                // screenshot_camera (off-screen capture from a Camera2D/Camera3D), and
                // screenshot_isolated (render a Node3D in an isolated world from six views). All three
                // are read-only (group editor, gate off) — they create transient editor render nodes
                // but free them on every path and write no project files. Idempotent re-register
                // refreshes the handler references after a domain reload.
                ScreenshotTools.RegisterScreenshotTools();

                // P5.1 — reflection tools: godot_open_mcp_reflection_method_find (read-only member
                // discovery across loaded assemblies) and godot_open_mcp_reflection_method_call (gated
                // method invoke with node_path instance targeting). Registered alongside the other
                // families so an agent can discover + invoke C# members against the actually-installed
                // Godot/.NET assemblies. Idempotent re-register refreshes the handler references after a
                // domain reload.
                ReflectionTools.RegisterReflectionTools();
                // P12.1 — tilemap domain pack: six TileMapLayer tools (create / set_tileset /
                // set_cell / erase_cell / get_used_cells / clear), group tilemap. The first Phase 12
                // domain pack and the reference implementation for P12.2–P12.5. The five mutators
                // register defaultGate:"enforce" and validate paths_hint themselves; the read-only
                // get_used_cells is gate-free. Idempotent re-register refreshes the handler references
                // after a domain reload.
                TilemapTools.RegisterTilemapTools();
                // P12.2 — navigation domain pack: seven navigation tools (defaults / region_create /
                // region_set_mesh / agent_create / agent_configure / link_create / get), group
                // navigation. The second Phase 12 domain pack. The five mutators register
                // defaultGate:"enforce" and validate paths_hint themselves; the read-only defaults
                // and get are gate-free. Idempotent re-register refreshes the handler references
                // after a domain reload.
                NavigationTools.RegisterNavigationTools();
                // P12.3 — particles domain pack: five GpuParticles tools (defaults / create /
                // configure / set_emitting / get), group particles. The third Phase 12 domain pack.
                // The three mutators register defaultGate:"enforce" and validate paths_hint
                // themselves; the read-only defaults and get are gate-free. Idempotent re-register
                // refreshes the handler references after a domain reload.
                ParticlesTools.RegisterParticlesTools();
                // P12.4 — animation domain pack: seven AnimationPlayer tools (defaults /
                // player_create / library_add / animation_create / add_track / insert_key /
                // get), group animation. The fourth Phase 12 domain pack. The five mutators
                // register defaultGate:"enforce" and validate paths_hint themselves; the
                // read-only defaults and get are gate-free. Idempotent re-register refreshes
                // the handler references after a domain reload.
                AnimationTools.RegisterAnimationTools();
                // P12.5 — CSG domain pack: seven CSG primitive tools (defaults / box_create /
                // sphere_create / cylinder_create / combiner_create / set_operation / get),
                // group csg. The fifth and final Phase 12 domain pack. The five mutators
                // register defaultGate:"enforce" and validate paths_hint themselves; the
                // read-only defaults and get are gate-free. Idempotent re-register refreshes
                // the handler references after a domain reload.
                CsgTools.RegisterCsgTools();

                // Start the HTTP listener serving /ping and POST /tools/{name}. Stays down
                // (and connected:false) if the bind fails — the editor remains usable, the
                // bridge is just unreachable. The listener thread is the only off-thread path;
                // it reads only cached session statics for /ping and marshals tool handlers to
                // the main thread via the dispatcher, never touching EditorInterface directly.
                BridgeHttpServer.Start();

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
                // Stop the HTTP listener FIRST so no new /ping probes arrive mid-teardown
                // (a probe during dispatcher teardown would see connected:false via the
                // listener's own SetConnected(false), which is fine — but closing the
                // listener cleanly before the rest is the deterministic order).
                //
                // The instance lock is deliberately NOT released here. `_ExitTree` also runs on a
                // C# assembly reload, and the lock must survive that: a retained lock whose
                // heartbeat has stopped advancing while the editor PID is still alive is the only
                // out-of-band signal the MCP server has for "the bridge assembly failed to
                // recompile and will not come back" (see packages/bridge/AGENTS.md §Multi-instance,
                // "Lock retention on domain reload"). Releasing on every _ExitTree would erase that
                // signal. A lock left behind by a graceful quit is reaped by the PID-liveness sweep
                // in BridgeInstanceLock.Acquire; stopping the heartbeat here is enough to mark this
                // bridge as no longer serving.
                BridgeHttpServer.Stop();
                // P5.4 — stop the event source and detach the collector fan-out sink. The ring buffer
                // stays drainable (no ResetForTests here) so a reconnecting MCP subscriber can read the
                // tail of the previous session's events across a domain reload. Must run AFTER the HTTP
                // listener stops so an in-flight /events drain completes against a live source.
                BridgeEventSource.Stop();
                // P4.7 — detach the BridgeLog → collector forward sink. The collector itself stays
                // readable (see GodotLogCollector.Current remarks) so console_get_logs can still
                // surface the most recent session's lines after a disable.
                BridgeLog.RemoveCollectorSink();
                BridgeSession.ResetForDisable();

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
