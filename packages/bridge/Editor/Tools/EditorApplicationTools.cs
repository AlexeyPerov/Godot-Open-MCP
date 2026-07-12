#if TOOLS
#nullable enable
using System.Globalization;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Editor application-state tool family (P4.5) — the Godot analog of Unity Open MCP's
    /// <c>editor_status</c> (read) and <c>editor_set_state</c> (write). Two tools:
    /// <list type="bullet">
    /// <item><description><c>godot_open_mcp_editor_application_get_state</c> (read-only) — observed
    /// play-process state (<c>isPlaying</c> + <c>playingScene</c> + <c>editorVersion</c> +
    /// <c>observedAt</c>).</description></item>
    /// <item><description><c>godot_open_mcp_editor_application_set_state</c> (mutating, default gate
    /// <c>enforce</c>) — start the main/current/custom scene or stop the play process, with a bounded
    /// observation window that never claims an unobserved state.</description></item>
    /// </list>
    ///
    /// <para>
    /// <b>Intentional deltas from Unity</b> (per the P4.5 plan + <c>packages/bridge/AGENTS.md</c>
    /// §Unity-first): Unity toggles an in-editor playmode flag and exposes <c>isPaused</c> /
    /// <c>isCompiling</c> / domain-reload concepts. Godot launches the game as a SEPARATE OS process;
    /// there is no editor-side pause/compile equivalent. The state DTO therefore carries only what is
    /// truthful for Godot: <c>isPlaying</c>, <c>playingScene</c>, <c>editorVersion</c>,
    /// <c>observedAt</c>. The set-state action set is start/stop only — no pause, no step, no
    /// debugger control (Unity's <c>editor_set_state</c> exposes play/pause/stop; Godot's play model
    /// has no pause).
    /// </para>
    ///
    /// <para>
    /// <b>Behavior reference</b> (Godot-MCP <c>Tool_Editor.GetState</c> / <c>SetState</c>): the
    /// <c>EditorInterface</c> play API surface (<c>PlayMainScene</c> / <c>PlayCurrentScene</c> /
    /// <c>PlayCustomScene</c> / <c>StopPlayingScene</c> / <c>IsPlayingScene</c> /
    /// <c>GetPlayingScene</c>) and the selector resolution (<c>main</c> / <c>current</c> /
    /// <c>res://</c>) are lifted from there as read-only behavior guidance. The structured error
    /// contract, the bounded settle wait, the gate integration, and the requested-vs-observed DTO are
    /// greenfield for this port.
    /// </para>
    ///
    /// <para>
    /// <b>Gate.</b> Set-state is mutating (it changes editor/project runtime state — launches or stops
    /// a process) and registered with default gate <c>enforce</c>. It writes no files, so the gate's
    /// verify delta will be clean in the common case; the gate scope (<c>paths_hint</c>) carries the
    /// explicit scene/project scope per the P4.5 plan.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): the handlers touch <see cref="EditorInterface"/> and
    /// <see cref="Engine"/>. The pure-managed pieces (<see cref="EditorApplicationStateData"/>,
    /// <see cref="EditorApplicationSetStateBody"/>, <see cref="EditorStateSettleWait"/>) live outside
    /// this guard and are unit-tested.
    /// </summary>
    internal static class EditorApplicationTools
    {
        /// <summary>The MCP tool name for the editor-application state read (P4.5).</summary>
        internal const string GetStateToolName = "godot_open_mcp_editor_application_get_state";

        /// <summary>The MCP tool name for the editor-application state mutation (P4.5).</summary>
        internal const string SetStateToolName = "godot_open_mcp_editor_application_set_state";

        // --- registration -----------------------------------------------------------

        /// <summary>
        /// Register the editor application-state tool family (P4.5). One read-only tool
        /// (<c>godot_open_mcp_editor_application_get_state</c>, group <c>editor</c>, default gate
        /// <c>off</c>) and one gated mutator (<c>godot_open_mcp_editor_application_set_state</c>,
        /// group <c>editor</c>, default gate <c>enforce</c>). Registered once at plugin enable; safe
        /// to call again on re-enable (the registry is idempotent).
        /// </summary>
        internal static void RegisterEditorApplicationTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: GetStateToolName,
                isMutating: false,
                defaultGate: "off",
                group: "editor",
                handler: GetState));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: SetStateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "editor",
                handler: SetState));
        }

        // --- godot_open_mcp_editor_application_get_state ----------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_editor_application_get_state</c>. Read-only. Returns a
        /// truthful snapshot of the editor's play-process state: <c>isPlaying</c>, the
        /// <c>res://</c> path of the scene the play process is running (when available), the Godot
        /// editor version, and the UTC observation timestamp.
        ///
        /// <para>
        /// Adapted from Unity Open MCP's <c>editor_status</c> (adapt fidelity — same read shape, Godot
        /// fields) and Godot-MCP's <c>Tool_Editor.GetState</c> (behavior reference —
        /// <c>EditorInterface.IsPlayingScene()</c> / <c>GetPlayingScene()</c> / <c>Engine.GetVersionInfo</c>).
        /// Does NOT expose <c>isPaused</c> or <c>isCompiling</c> — Godot has no reliable editor-side
        /// equivalent contract.
        /// </para>
        ///
        /// Must not throw — exceptions are caught by the dispatcher and surfaced as
        /// <c>execution_error</c>.
        /// </summary>
        internal static ToolDispatchResult GetState(string body)
        {
            var state = CaptureState();
            var sb = new StringBuilder(128);
            state.AppendJsonTo(sb);
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- godot_open_mcp_editor_application_set_state ----------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_editor_application_set_state</c>. Mutating (default gate
        /// <c>enforce</c>). Starts the main/current/custom scene or stops the play process. The
        /// transition is observed with a bounded deadline (<c>timeout_ms</c>); the tool never claims a
        /// state it did not observe.
        ///
        /// <para>
        /// <b>Start.</b> Resolve the selector (<c>main</c> default / <c>current</c> / explicit
        /// <c>res://...tscn</c>), validate it, refuse a conflicting start (already playing a different
        /// scene) unless the requested scene is observably the same, invoke the matching
        /// <c>EditorInterface.Play*</c> API, then observe <c>IsPlayingScene()</c> until true or the
        /// deadline. Returns the normalized requested action + the observed state + <c>settled</c> +
        /// <c>elapsedMs</c>.
        /// </para>
        ///
        /// <para>
        /// <b>Stop.</b> If already stopped, return an idempotent success. Otherwise invoke
        /// <c>EditorInterface.StopPlayingScene()</c> and observe until stopped or the deadline.
        /// </para>
        ///
        /// <para>
        /// <b>paths_hint.</b> Mandatory (handler-level guard that fires even under <c>gate:"off"</c>).
        /// For a start it carries the explicit scene scope (the <c>res://</c> path for a custom scene,
        /// the edited scene path for <c>current</c>, or <c>res://project.godot</c> for <c>main</c> — the
        /// documented project-scope sentinel). For a stop it carries the previously-playing scene path
        /// when known, or <c>res://project.godot</c>.
        /// </para>
        ///
        /// <para>
        /// <b>No automatic save.</b> A play start does not save the edited scene first — unsaved
        /// current-scene behavior is explicit and documented. A <c>current</c> start requires a saved
        /// edited scene (a path); a freshly-created unsaved scene yields
        /// <c>current_scene_unavailable</c>.
        /// </para>
        ///
        /// Structured failures: <c>paths_hint_required</c>, <c>invalid_scene_selector</c>,
        /// <c>scene_not_found</c>, <c>current_scene_unavailable</c>, <c>already_playing</c>,
        /// <c>play_start_failed</c>, <c>play_stop_failed</c>, <c>state_transition_timeout</c>. A timeout
        /// carries the last observed state so a caller can safely follow up with get-state. Must not
        /// throw.
        /// </summary>
        internal static ToolDispatchResult SetState(string body)
        {
            var request = EditorApplicationSetStateBody.Parse(body);

            // Handler-level paths_hint guard — fires even under gate:"off".
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "editor_application_set_state requires 'paths_hint': the explicit scene path " +
                    "(or res://project.godot for the main-scene/project scope).");

            if (!request.IsPlaying)
                return Stop(pathsHint, request.TimeoutMs);

            return Start(request, pathsHint);
        }

        // --- start ------------------------------------------------------------------

        static ToolDispatchResult Start(EditorApplicationSetStateBody request, string[] pathsHint)
        {
            // Resolve + validate the selector.
            var selector = ResolveSelector(request);
            if (selector.Kind == SelectorKind.Invalid)
                return ToolDispatchResult.Fail(
                    "invalid_scene_selector",
                    selector.ErrorMessage ?? "Invalid 'scene' selector.");

            // Capture the current state for the conflict check and the result's `before`.
            var before = CaptureState();

            // Conflict check: starting while already playing must not silently restart another process.
            // Allowed only when the requested scene is observably the same as the playing scene.
            if (before.IsPlaying)
            {
                if (selector.Kind == SelectorKind.Custom)
                {
                    // Already playing the exact requested path → idempotent.
                    if (before.PlayingScene == selector.ResolvedPath)
                        return BuildSetStateResult(
                            action: "start_noop",
                            selector: selector,
                            before: before,
                            after: before,
                            settled: true,
                            elapsedMs: 0,
                            timeoutMs: request.TimeoutMs);
                }
                // Already playing a different scene (or main/current where we cannot prove sameness) →
                // surface the conflict with the observed state so the caller can stop first.
                return ToolDispatchResult.FailWithOutput(
                    "already_playing",
                    $"A play process is already running scene '{before.PlayingScene ?? "(unknown)"}'. " +
                    "Call editor_application_set_state with is_playing:false (stop) first, or target the " +
                    "same scene for an idempotent start.",
                    BuildConflictPayload(selector, before));
            }

            // Validate selector-specific preconditions.
            if (selector.Kind == SelectorKind.Custom)
            {
                if (!ResourceLoader.Exists(selector.ResolvedPath))
                    return ToolDispatchResult.Fail(
                        "scene_not_found",
                        $"No scene resource exists at '{selector.ResolvedPath}'.");
            }
            else if (selector.Kind == SelectorKind.Current)
            {
                var editedRoot = EditorInterface.Singleton.GetEditedSceneRoot();
                if (editedRoot == null)
                    return ToolDispatchResult.Fail(
                        "current_scene_unavailable",
                        "No scene is currently being edited; cannot play the current scene. " +
                        "Open a scene first or use scene:'main' / an explicit res:// path.");
                var editedPath = editedRoot.GetSceneFilePath();
                if (string.IsNullOrEmpty(editedPath))
                    return ToolDispatchResult.Fail(
                        "current_scene_unavailable",
                        "The edited scene has never been saved; cannot play it. Save the scene first " +
                        "(scene_save) or use scene:'main' / an explicit res:// path.");
                selector = selector.WithResolvedPath(editedPath);
            }

            // Invoke the matching play API.
            try
            {
                switch (selector.Kind)
                {
                    case SelectorKind.Main:
                        EditorInterface.Singleton.PlayMainScene();
                        break;
                    case SelectorKind.Current:
                        EditorInterface.Singleton.PlayCurrentScene();
                        break;
                    case SelectorKind.Custom:
                        EditorInterface.Singleton.PlayCustomScene(selector.ResolvedPath!);
                        break;
                }
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.FailWithOutput(
                    "play_start_failed",
                    $"EditorInterface.Play* threw: {e.Message}",
                    BuildObservedPayload(selector, before, CaptureState(), settled: false, elapsedMs: 0,
                        timeoutMs: request.TimeoutMs, reason: "play_start_threw"));
            }

            // Observe the transition with a bounded deadline.
            var (settled, elapsedMs) = EditorStateSettleWait.Wait(
                () => EditorInterface.Singleton.IsPlayingScene(),
                request.TimeoutMs,
                sleep: ms => OS.DelayMsec(ms));

            var after = CaptureState();
            if (!settled || !after.IsPlaying)
            {
                // The play process did not start within the deadline, or started but is not reported by
                // IsPlayingScene. Surface the last observed state so the caller can follow up.
                return ToolDispatchResult.FailWithOutput(
                    "state_transition_timeout",
                    $"Play start for '{selector.Label}' was not observed within {request.TimeoutMs}ms " +
                    "(EditorInterface.IsPlayingScene() did not become true).",
                    BuildObservedPayload(selector, before, after, settled, elapsedMs,
                        timeoutMs: request.TimeoutMs, reason: "start_not_observed"));
            }

            // P5.4 — emit the play-state transition into the event stream. The event is emitted only
            // after the settle wait confirms the transition (authoritative), so a subscriber never sees
            // a `playing` event for a play that did not start. This is the bridge-driven path; a future
            // phase adds a main-thread observer for user-clicked plays.
            BridgeEventSource.NotifyEditorState(BridgeInstanceLock.StatePlaying, isCompiling: false, isPlaying: true);

            return BuildSetStateResult(
                action: "start",
                selector: selector,
                before: before,
                after: after,
                settled: true,
                elapsedMs: elapsedMs,
                timeoutMs: request.TimeoutMs);
        }

        // --- stop -------------------------------------------------------------------

        static ToolDispatchResult Stop(string[] pathsHint, int timeoutMs)
        {
            var before = CaptureState();

            // Idempotent: stop-while-stopped is a success.
            if (!before.IsPlaying)
            {
                return BuildSetStateResult(
                    action: "stop_noop",
                    selector: new SceneSelector(SelectorKind.Main, null, "stop"),
                    before: before,
                    after: before,
                    settled: true,
                    elapsedMs: 0,
                    timeoutMs: timeoutMs);
            }

            try
            {
                EditorInterface.Singleton.StopPlayingScene();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.FailWithOutput(
                    "play_stop_failed",
                    $"EditorInterface.StopPlayingScene threw: {e.Message}",
                    BuildObservedPayload(new SceneSelector(SelectorKind.Main, null, "stop"),
                        before, CaptureState(), settled: false, elapsedMs: 0,
                        timeoutMs: timeoutMs, reason: "play_stop_threw"));
            }

            var (settled, elapsedMs) = EditorStateSettleWait.Wait(
                () => !EditorInterface.Singleton.IsPlayingScene(),
                timeoutMs,
                sleep: ms => OS.DelayMsec(ms));

            var after = CaptureState();
            if (!settled || after.IsPlaying)
            {
                return ToolDispatchResult.FailWithOutput(
                    "state_transition_timeout",
                    $"Play stop was not observed within {timeoutMs}ms " +
                    "(EditorInterface.IsPlayingScene() did not become false).",
                    BuildObservedPayload(new SceneSelector(SelectorKind.Main, null, "stop"),
                        before, after, settled, elapsedMs,
                        timeoutMs: timeoutMs, reason: "stop_not_observed"));
            }

            // P5.4 — emit the idle transition into the event stream after settle confirms the stop.
            BridgeEventSource.NotifyEditorState(BridgeInstanceLock.StateIdle, isCompiling: false, isPlaying: false);

            return BuildSetStateResult(
                action: "stop",
                selector: new SceneSelector(SelectorKind.Main, null, "stop"),
                before: before,
                after: after,
                settled: true,
                elapsedMs: elapsedMs,
                timeoutMs: timeoutMs);
        }

        // --- state capture ----------------------------------------------------------

        /// <summary>Capture a truthful <see cref="EditorApplicationStateData"/> snapshot from
        /// <c>EditorInterface</c> / <c>Engine</c>. Main-thread only.</summary>
        static EditorApplicationStateData CaptureState()
        {
            var ei = EditorInterface.Singleton;
            bool isPlaying;
            string? playingScene;
            try
            {
                isPlaying = ei.IsPlayingScene();
                playingScene = isPlaying ? ei.GetPlayingScene() : null;
            }
            catch (System.Exception)
            {
                // EditorInterface not ready — report stopped. This is a defensive guard for very early
                // enable; the smoke covers the normal path.
                isPlaying = false;
                playingScene = null;
            }
            return new EditorApplicationStateData
            {
                IsPlaying = isPlaying,
                PlayingScene = isPlaying && !string.IsNullOrEmpty(playingScene) ? playingScene : null,
                EditorVersion = ResolveEditorVersion(),
                ObservedAt = EditorApplicationStateData.NowObservedAt(),
            };
        }

        static string ResolveEditorVersion()
        {
            // Prefer the cached session value (resolved once at enable); fall back to a live read so a
            // pre-enable call still gets a version.
            var cached = BridgeSession.GodotVersion;
            if (!string.IsNullOrEmpty(cached)) return cached!;
            try
            {
                var info = Engine.GetVersionInfo();
                if (info != null && info.TryGetValue("string", out var s) && s.VariantType == Variant.Type.String)
                    return s.AsString();
            }
            catch
            {
                // Engine not ready — fall through to empty string.
            }
            return string.Empty;
        }

        // --- selector resolution ----------------------------------------------------

        internal enum SelectorKind { Main, Current, Custom, Invalid }

        /// <summary>Resolved scene selector for a start request.</summary>
        internal sealed class SceneSelector
        {
            public SelectorKind Kind { get; }
            public string? ResolvedPath { get; }
            public string Label { get; }       // human-readable label for messages
            public string? ErrorMessage { get; }

            public SceneSelector(SelectorKind kind, string? resolvedPath, string label,
                string? errorMessage = null)
            {
                Kind = kind;
                ResolvedPath = resolvedPath;
                Label = label;
                ErrorMessage = errorMessage;
            }

            public SceneSelector WithResolvedPath(string path) =>
                new SceneSelector(Kind, path, Label, ErrorMessage);
        }

        /// <summary>Resolve the scene selector from the parsed body. <c>main</c> (default when
        /// absent), <c>current</c>, or an explicit <c>res://...tscn</c>/<c>.scn</c> path.</summary>
        static SceneSelector ResolveSelector(EditorApplicationSetStateBody request)
        {
            var raw = request.HasScene ? request.Scene!.Trim() : "main";

            if (raw.Length == 0)
            {
                return new SceneSelector(SelectorKind.Main, null, "main");
            }

            // main / current are explicit keywords.
            if (raw.Equals("main", System.StringComparison.OrdinalIgnoreCase))
                return new SceneSelector(SelectorKind.Main, null, "main");
            if (raw.Equals("current", System.StringComparison.OrdinalIgnoreCase))
                return new SceneSelector(SelectorKind.Current, null, "current");

            // Otherwise must be a res:// scene path.
            if (!raw.StartsWith("res://", System.StringComparison.Ordinal))
            {
                return new SceneSelector(SelectorKind.Invalid, null, raw,
                    $"'scene' must be 'main', 'current', or a res:// path; got '{raw}'.");
            }
            if (raw.EndsWith("/", System.StringComparison.Ordinal))
            {
                return new SceneSelector(SelectorKind.Invalid, null, raw,
                    $"'scene' res:// path must be a file, not a directory; got '{raw}'.");
            }
            if (!EndsWithSceneExt(raw))
            {
                return new SceneSelector(SelectorKind.Invalid, null, raw,
                    $"'scene' res:// path must end with '.tscn' or '.scn'; got '{raw}'.");
            }

            return new SceneSelector(SelectorKind.Custom, raw, raw);
        }

        static bool EndsWithSceneExt(string path)
            => path.EndsWith(".tscn", System.StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".scn", System.StringComparison.OrdinalIgnoreCase);

        // --- result builders --------------------------------------------------------

        /// <summary>Build the success result envelope: requested selector + before/after observed
        /// state + settle metadata.</summary>
        static ToolDispatchResult BuildSetStateResult(
            string action,
            SceneSelector selector,
            EditorApplicationStateData before,
            EditorApplicationStateData after,
            bool settled,
            int elapsedMs,
            int timeoutMs)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"requested\":");
            AppendRequested(sb, action, selector);
            sb.Append(",\"before\":");
            before.AppendJsonTo(sb);
            sb.Append(",\"after\":");
            after.AppendJsonTo(sb);
            sb.Append(",\"state\":");
            after.AppendJsonTo(sb);
            AppendSettle(sb, settled, elapsedMs, timeoutMs, reason: null);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        /// <summary>Build an observed-state payload for a failure (timeout / play_start_failed /
        /// conflict). Carries the last observed state so the caller can recover without a second
        /// round-trip.</summary>
        static string BuildObservedPayload(
            SceneSelector selector,
            EditorApplicationStateData before,
            EditorApplicationStateData after,
            bool settled,
            int elapsedMs,
            int timeoutMs,
            string reason)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"requested\":");
            AppendRequested(sb, "start", selector);
            sb.Append(",\"before\":");
            before.AppendJsonTo(sb);
            sb.Append(",\"after\":");
            after.AppendJsonTo(sb);
            sb.Append(",\"state\":");
            after.AppendJsonTo(sb);
            AppendSettle(sb, settled, elapsedMs, timeoutMs, reason);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Build the conflict (already_playing) payload — observed state only, no
        /// before/after transition.</summary>
        static string BuildConflictPayload(SceneSelector selector, EditorApplicationStateData observed)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"requested\":");
            AppendRequested(sb, "start", selector);
            sb.Append(",\"state\":");
            observed.AppendJsonTo(sb);
            sb.Append('}');
            return sb.ToString();
        }

        static void AppendRequested(StringBuilder sb, string action, SceneSelector selector)
        {
            sb.Append('{');
            sb.Append("\"action\":").Append(BridgeJson.EscapeString(action)).Append(',');
            sb.Append("\"scene\":").Append(BridgeJson.EscapeString(selector.Label));
            if (selector.Kind == SelectorKind.Custom && selector.ResolvedPath != null)
            {
                sb.Append(",\"resolvedPath\":").Append(BridgeJson.EscapeString(selector.ResolvedPath));
            }
            sb.Append('}');
        }

        static void AppendSettle(StringBuilder sb, bool settled, int elapsedMs, int timeoutMs, string? reason)
        {
            sb.Append(",\"settled\":").Append(settled ? "true" : "false").Append(',');
            sb.Append("\"elapsedMs\":").Append(elapsedMs.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"timeoutMs\":").Append(timeoutMs.ToString(CultureInfo.InvariantCulture));
            if (reason != null)
                sb.Append(",\"reason\":").Append(BridgeJson.EscapeString(reason));
        }
    }
}
#endif
