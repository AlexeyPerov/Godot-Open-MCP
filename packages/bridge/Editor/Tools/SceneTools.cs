#if TOOLS
#nullable enable
using System.Collections.Generic;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Scene tool family — the Godot analog of Unity Open MCP's
    /// <c>TypedTools/ScenesTools.cs</c>. P2.6 implements the three scene lifecycle tools:
    /// <c>godot_open_mcp_scene_open</c>, <c>godot_open_mcp_scene_save</c>, and the read-only
    /// <c>godot_open_mcp_scene_list_opened</c>. P2.7 adds the live hierarchy read
    /// <c>godot_open_mcp_scene_get_data</c> and the scene-file creator
    /// <c>godot_open_mcp_scene_create</c>.
    ///
    /// <para>
    /// Godot ↔ Unity mapping: a Godot scene is a <see cref="PackedScene"/> on disk
    /// (<c>res://*.tscn</c>) instanced as the editor's edited root <see cref="Node"/>.
    /// <c>EditorInterface.Singleton.OpenSceneFromPath</c> ↔ Unity's
    /// <c>EditorSceneManager.OpenScene</c>; <c>SaveScene</c> / <c>SaveSceneAs</c> ↔
    /// <c>SaveScene</c> / save-as. Godot 4.3 exposes the open-scene set as a flat list of
    /// <c>res://</c> paths (<c>GetOpenScenes</c>) plus the single edited root
    /// (<c>GetEditedSceneRoot</c>) — no Unity additive/single mode and no build index.
    /// </para>
    ///
    /// <para>
    /// Editor-only (<c>#if TOOLS</c>): the handlers touch <see cref="EditorInterface"/> and live
    /// <see cref="Node"/> objects. The pure-managed pieces (<see cref="SceneSummary"/>,
    /// <see cref="SceneOpenBody"/>, <see cref="SceneSaveBody"/>, <see cref="SceneGetDataBody"/>,
    /// <see cref="SceneCreateBody"/>) live outside this guard and are unit-tested.
    /// </para>
    /// </summary>
    internal static class SceneTools
    {
        /// <summary>The MCP tool name for the scene opener (P2.6).</summary>
        internal const string SceneOpenToolName = "godot_open_mcp_scene_open";

        /// <summary>The MCP tool name for the scene saver (P2.6).</summary>
        internal const string SceneSaveToolName = "godot_open_mcp_scene_save";

        /// <summary>The MCP tool name for the opened-scene lister (P2.6).</summary>
        internal const string SceneListOpenedToolName = "godot_open_mcp_scene_list_opened";

        /// <summary>The MCP tool name for the scene hierarchy read (P2.7).</summary>
        internal const string SceneGetDataToolName = "godot_open_mcp_scene_get_data";

        /// <summary>The MCP tool name for the scene file creator (P2.7).</summary>
        internal const string SceneCreateToolName = "godot_open_mcp_scene_create";

        // --- bridge-tracked dirty state ---------------------------------------------
        //
        // Godot 4.3 has NO public EditorInterface API to query a scene's dirty (unsaved) state —
        // the editor tracks it internally via EditorUndoRedoManager, which is not bound to C#. The
        // bridge maintains its own best-effort flag instead: MarkEditedSceneDirty() is the single
        // setter the node-tool mutators (NodeTools.Create / Modify / SetParent / Duplicate / Delete)
        // call alongside EditorInterface.MarkSceneAsUnsaved(), and it is cleared on save / open.
        //
        // This catches the agent-driven danger case (an agent mutates a scene then opens another
        // without saving) — the exact risk the spec's "Default scene_dirty refusal" targets. It does
        // NOT reflect edits a human makes directly in the editor, because the bridge is not wired
        // into the editor's own change signals. That blind spot is an intentional delta from Unity
        // (which has a clean isDirty query) and is documented on SceneSummary.IsDirty.
        //
        // The flag is keyed by the edited scene's res:// path so a save/open of one scene does not
        // spuriously clear the flag for a different scene. A freshly-created unsaved scene (empty
        // path) is tracked under a sentinel key.

        static readonly Dictionary<string, bool> _dirtyScenes = new(System.StringComparer.Ordinal);
        const string UnsavedSceneKey = "<unsaved>";

        /// <summary>
        /// Mark the currently-edited scene as dirty (has unsaved changes). Called by the node-tool
        /// mutators (and internally on a failed save-as) alongside
        /// <c>EditorInterface.MarkSceneAsUnsaved()</c>. Main-thread only.
        /// </summary>
        internal static void MarkEditedSceneDirty()
        {
            var key = EditedSceneDirtyKey();
            if (key == null) return;
            _dirtyScenes[key] = true;
        }

        /// <summary>Dirty-state key for the currently-edited scene (its res:// path, or the
        /// <c>&lt;unsaved&gt;</c> sentinel for a never-saved scene). Null when no scene is edited.
        /// Main-thread only.</summary>
        static string? EditedSceneDirtyKey()
        {
            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            return root == null ? null : DirtyKeyFor(root);
        }

        static string DirtyKeyFor(Node root)
        {
            var p = root.GetSceneFilePath();
            return string.IsNullOrEmpty(p) ? UnsavedSceneKey : p;
        }

        static bool IsEditedSceneDirty()
        {
            var key = EditedSceneDirtyKey();
            return key != null && _dirtyScenes.TryGetValue(key, out var dirty) && dirty;
        }

        static void ClearDirty(string key)
        {
            _dirtyScenes.Remove(key);
        }

        // --- registration -----------------------------------------------------------

        /// <summary>
        /// Register the scene tool family. P2.6 adds three tools — the mutating
        /// <c>godot_open_mcp_scene_open</c> and <c>godot_open_mcp_scene_save</c> (group
        /// <c>scene</c>, default gate <c>off</c> — the gate flow lands in P3.5 and is a no-op until
        /// then), and the read-only <c>godot_open_mcp_scene_list_opened</c>. P2.7 adds two more —
        /// the read-only <c>godot_open_mcp_scene_get_data</c> (live hierarchy snapshot) and the
        /// mutating <c>godot_open_mcp_scene_create</c> (new <c>.tscn</c> file). Registered once at
        /// plugin enable; safe to call again on re-enable (the registry is idempotent).
        /// </summary>
        internal static void RegisterSceneTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: SceneOpenToolName,
                isMutating: true,
                defaultGate: "off",
                group: "scene",
                handler: Open));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: SceneSaveToolName,
                isMutating: true,
                defaultGate: "off",
                group: "scene",
                handler: Save));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: SceneListOpenedToolName,
                isMutating: false,
                defaultGate: "off",
                group: "scene",
                handler: ListOpened));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: SceneGetDataToolName,
                isMutating: false,
                defaultGate: "off",
                group: "scene",
                handler: GetData));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: SceneCreateToolName,
                isMutating: true,
                defaultGate: "off",
                group: "scene",
                handler: Create));
        }

        // --- godot_open_mcp_scene_open ----------------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_scene_open</c>. Opens a Godot scene asset
        /// (<c>res://*.tscn</c>) in the editor and makes it the active/edited scene. Adapted from
        /// Unity Open MCP's <c>ScenesTools.OpenScene</c> (adapt fidelity) and Godot-MCP's
        /// <c>Tool_Scene.Open</c> (behavior reference): Unity supports additive/single modes; Godot
        /// opens scenes in tabs (no additive mode in P2.6), and the bridge refuses a dirty open by
        /// default rather than popping Godot's native save modal.
        ///
        /// <para>
        /// Structured failures: <c>missing_parameter</c>, <c>invalid_path</c>,
        /// <c>scene_not_found</c>, <c>scene_dirty</c>. Must not throw — exceptions are caught by the
        /// dispatcher and surfaced as <c>execution_error</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Open(string body)
        {
            var request = SceneOpenBody.Parse(body);

            if (string.IsNullOrEmpty(request.Path))
            {
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "scene_open requires 'path' (a res:// path to a .tscn/.scn scene).");
            }

            var rawPath = request.Path!;
            if (!IsResPath(rawPath))
            {
                return ToolDispatchResult.Fail(
                    "invalid_path",
                    $"path must be a 'res://' path; got '{rawPath}'.");
            }

            // Dirty guard BEFORE opening — Godot would otherwise pop a native save modal that hangs
            // the worker thread. An agent opts out with ignore_dirty=true.
            if (!request.IgnoreDirty && IsEditedSceneDirty())
            {
                var root = EditorInterface.Singleton.GetEditedSceneRoot();
                var curPath = root != null ? root.GetSceneFilePath() : null;
                return ToolDispatchResult.Fail(
                    "scene_dirty",
                    $"The current scene '{curPath ?? "<unsaved>"}' has unsaved changes. " +
                    "Re-pass with ignore_dirty=true to discard them, or call scene_save first.");
            }

            if (!ResourceLoader.Exists(rawPath))
            {
                return ToolDispatchResult.Fail(
                    "scene_not_found",
                    $"No scene resource exists at '{rawPath}'.");
            }

            // Snapshot the previous edited scene summary BEFORE opening (for the result's `previous`
            // field) — OpenSceneFromPath re-points the edited root.
            SceneSummary? previous = null;
            {
                var prevRoot = EditorInterface.Singleton.GetEditedSceneRoot();
                if (prevRoot != null)
                    previous = ToSceneSummary(prevRoot, isActive: true);
            }

            // Opening a fresh scene clears the dirty flag for the NEWLY edited scene (it was just
            // loaded from disk) — the previous scene's flag is left as-is so a later re-open still
            // reports its dirty state correctly.
            EditorInterface.Singleton.OpenSceneFromPath(rawPath);

            var editedRoot = EditorInterface.Singleton.GetEditedSceneRoot();
            if (editedRoot == null)
            {
                return ToolDispatchResult.Fail(
                    "scene_not_found",
                    $"Opened '{rawPath}' but the editor has no edited scene root afterwards.");
            }

            // The opened scene came from disk, so it is not dirty from the bridge's perspective.
            ClearDirty(DirtyKeyFor(editedRoot));

            var opened = ToSceneSummary(editedRoot, isActive: true);

            var sb = new StringBuilder(160);
            sb.Append("{\"opened\":");
            opened.AppendJsonTo(sb);
            sb.Append(",\"previous\":");
            if (previous == null) sb.Append("null"); else previous.AppendJsonTo(sb);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- godot_open_mcp_scene_save ----------------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_scene_save</c>. Three save modes:
        /// <list type="bullet">
        /// <item><description><c>save_all</c> true — save every open scene tab.</description></item>
        /// <item><description><c>path</c> set (save-as) — save the edited scene to a new <c>res://</c> path.</description></item>
        /// <item><description>Neither — save the edited scene back to its existing file.</description></item>
        /// </list>
        /// Adapted from Unity Open MCP's <c>ScenesTools.SaveScene</c> (adapt fidelity) and
        /// Godot-MCP's <c>Tool_Scene.Save</c> (behavior reference): Unity's <c>save_all</c> iterates
        /// <c>LoadedSceneCount</c>; Godot's iterates <c>GetOpenScenes()</c> and re-opens each as the
        /// edited scene to save it (Godot 4.3 has no save-non-edited-scene API).
        ///
        /// <para>
        /// <b>Save-as verification.</b> <c>SaveSceneAs</c> returns void (no <c>Error</c>), so a silent
        /// failure (e.g. an unwritable target dir) would otherwise be reported as success. The
        /// handler re-reads the edited scene's file path and confirms it matches the requested path,
        /// mirroring Godot-MCP's <c>Tool_Scene.Save</c> verification pattern.
        /// </para>
        ///
        /// <para>
        /// <b>No gate yet.</b> Same forward-compat <c>gate</c>/<c>paths_hint</c> no-op as the other
        /// P2 mutators; no editor Undo yet.
        /// </para>
        ///
        /// Structured failures: <c>no_edited_scene</c>, <c>invalid_path</c>, <c>save_failed</c>.
        /// Must not throw.
        /// </summary>
        internal static ToolDispatchResult Save(string body)
        {
            var request = SceneSaveBody.Parse(body);

            if (request.IsSaveAll)
            {
                return SaveAll();
            }

            var editedRoot = EditorInterface.Singleton.GetEditedSceneRoot();
            if (editedRoot == null)
            {
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; nothing to save.");
            }

            if (request.IsSaveAs)
            {
                return SaveAs(editedRoot, request.Path!);
            }

            return SaveCurrent(editedRoot);
        }

        static ToolDispatchResult SaveCurrent(Node editedRoot)
        {
            var existingPath = editedRoot.GetSceneFilePath();
            if (string.IsNullOrEmpty(existingPath))
            {
                return ToolDispatchResult.Fail(
                    "save_failed",
                    "The edited scene has never been saved; provide a 'path' (save-as) to save it for the first time.");
            }

            var err = EditorInterface.Singleton.SaveScene();
            if (err != Error.Ok)
            {
                return ToolDispatchResult.Fail(
                    "save_failed",
                    $"Failed to save scene '{existingPath}': {err}.");
            }

            ClearDirty(DirtyKeyFor(editedRoot));
            var summary = ToSceneSummary(editedRoot, isActive: true);
            var sb = new StringBuilder(128);
            sb.Append("{\"saved\":[");
            sb.Append(BridgeJson.EscapeString(summary.Path));
            sb.Append("],\"count\":1}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        static ToolDispatchResult SaveAs(Node editedRoot, string path)
        {
            if (!IsResPath(path))
            {
                return ToolDispatchResult.Fail(
                    "invalid_path",
                    $"path must be a 'res://' path; got '{path}'.");
            }
            if (!EndsWithSceneExt(path))
            {
                return ToolDispatchResult.Fail(
                    "invalid_path",
                    $"path must end with '.tscn' or '.scn'; got '{path}'.");
            }

            // SaveSceneAs returns void — verify by re-reading the edited scene's file path.
            EditorInterface.Singleton.SaveSceneAs(path);

            var rootAfter = EditorInterface.Singleton.GetEditedSceneRoot();
            var savedPath = rootAfter?.GetSceneFilePath();
            if (savedPath != path)
            {
                // The save-as did not land. Keep the dirty flag set (the scene still has unsaved
                // changes) and surface the failure.
                return ToolDispatchResult.Fail(
                    "save_failed",
                    $"Save-as to '{path}' did not take effect (edited scene path is now '{savedPath ?? "<none>"}').");
            }

            // The old dirty key (previous path or <unsaved>) is now stale; the scene is saved under
            // the new path, which is clean.
            ClearDirty(DirtyKeyFor(editedRoot));
            var summary = ToSceneSummary(rootAfter ?? editedRoot, isActive: true);
            var sb = new StringBuilder(128);
            sb.Append("{\"saved\":[");
            sb.Append(BridgeJson.EscapeString(summary.Path));
            sb.Append("],\"count\":1}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        static ToolDispatchResult SaveAll()
        {
            // Godot 4.3 has no API to save a non-edited open scene directly. Snapshot the open-scene
            // paths first (OpenSceneFromPath can mutate the open-scene set), then for each path open
            // it as the edited scene and SaveScene(). Finally restore the originally-edited scene.
            // This mirrors the behavior of the editor's "Scene → Save All Scenes" without relying on
            // private APIs.
            var openScenes = EditorInterface.Singleton.GetOpenScenes();
            var paths = new List<string>();
            foreach (var sceneVariant in openScenes)
            {
                var p = sceneVariant.AsString();
                if (!string.IsNullOrEmpty(p))
                    paths.Add(p);
            }

            var savedPaths = new List<string>();
            var failed = new List<string>();

            // Remember the currently edited scene so it can be restored as the active tab at the end.
            var originalRoot = EditorInterface.Singleton.GetEditedSceneRoot();
            var originalPath = originalRoot?.GetSceneFilePath() ?? string.Empty;

            foreach (var scenePath in paths)
            {
                EditorInterface.Singleton.OpenSceneFromPath(scenePath);
                var root = EditorInterface.Singleton.GetEditedSceneRoot();
                if (root == null)
                {
                    failed.Add(scenePath);
                    continue;
                }
                var err = EditorInterface.Singleton.SaveScene();
                if (err == Error.Ok)
                {
                    savedPaths.Add(scenePath);
                    ClearDirty(scenePath);
                }
                else
                {
                    failed.Add(scenePath);
                }
            }

            // Restore the originally-edited scene as the active tab (unless it was unsaved, in which
            // case there is no path to re-open — leave whatever is currently active).
            if (!string.IsNullOrEmpty(originalPath))
            {
                EditorInterface.Singleton.OpenSceneFromPath(originalPath);
            }

            if (failed.Count > 0 && savedPaths.Count == 0)
            {
                return ToolDispatchResult.Fail(
                    "save_failed",
                    $"Failed to save scene(s): {string.Join(", ", failed)}.");
            }

            var sb = new StringBuilder(128);
            sb.Append("{\"saved\":[");
            for (int i = 0; i < savedPaths.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(savedPaths[i]));
            }
            sb.Append("],\"count\":").Append(savedPaths.Count.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            if (failed.Count > 0)
            {
                sb.Append(",\"failed\":[");
                for (int i = 0; i < failed.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BridgeJson.EscapeString(failed[i]));
                }
                sb.Append(']');
            }
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- godot_open_mcp_scene_list_opened ---------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_scene_list_opened</c>. Read-only. Enumerates every scene
        /// currently open in the editor as a shallow snapshot, flagging which one is the
        /// active/edited scene. Adapted from Unity Open MCP's <c>ScenesTools.ListOpened</c> (adapt
        /// fidelity — Unity returns build index + isLoaded; Godot has neither for editor scenes) and
        /// Godot-MCP's <c>Tool_Scene.ListOpened</c> (behavior reference — Godot 4.3 exposes the
        /// open-scene set as a flat list of <c>res://</c> paths plus the single edited root).
        ///
        /// <para>
        /// A freshly-created unsaved active scene has no path and may not appear in
        /// <c>GetOpenScenes()</c>; it is surfaced explicitly so the active scene is never missing
        /// from the result (mirrors Godot-MCP's <c>Tool_Scene.ListOpened</c>).
        /// </para>
        ///
        /// <para>
        /// <c>isDirty</c> reflects the bridge-tracked flag (see the dirty-state note on
        /// <see cref="SceneTools"/>), not Godot's internal editor dirty state.
        /// </para>
        ///
        /// Must not throw.
        /// </summary>
        internal static ToolDispatchResult ListOpened(string body)
        {
            var editedRoot = EditorInterface.Singleton.GetEditedSceneRoot();
            var activePath = editedRoot?.GetSceneFilePath() ?? string.Empty;

            var scenes = new List<SceneSummary>();
            var openPaths = EditorInterface.Singleton.GetOpenScenes();
            var sawActive = false;

            foreach (var pathVariant in openPaths)
            {
                var path = pathVariant.AsString();
                if (string.IsNullOrEmpty(path)) continue;
                var isActive = !string.IsNullOrEmpty(activePath) && path == activePath;
                if (isActive && editedRoot != null)
                {
                    scenes.Add(ToSceneSummary(editedRoot, isActive: true));
                    sawActive = true;
                }
                else
                {
                    // Godot 4.3 exposes no root accessor for a non-active open scene — report the
                    // path + file stem as the name. isDirty reflects bridge-tracked state.
                    scenes.Add(PathOnlySummary(path));
                }
            }

            // A freshly-created-but-unsaved active scene has no path, so GetOpenScenes() may not
            // list it; surface it explicitly so the active scene is never missing.
            if (!sawActive && editedRoot != null)
            {
                scenes.Add(ToSceneSummary(editedRoot, isActive: true));
            }

            var sb = new StringBuilder(256);
            sb.Append("{\"scenes\":[");
            for (int i = 0; i < scenes.Count; i++)
            {
                if (i > 0) sb.Append(',');
                scenes[i].AppendJsonTo(sb);
            }
            sb.Append("],\"count\":").Append(scenes.Count.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"editedPath\":").Append(BridgeJson.EscapeString(
                string.IsNullOrEmpty(activePath) ? null : activePath));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- godot_open_mcp_scene_get_data (P2.7) -----------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_scene_get_data</c>. Read-only. Returns a live snapshot of the
        /// edited scene's hierarchy as a <see cref="NodeData"/> tree (the same DTO
        /// <c>node_find</c> returns), driven by a <c>hierarchy_depth</c> bound. Adapted from Unity
        /// Open MCP's <c>ScenesTools.GetData</c> (adapt fidelity — Unity walks GameObject roots;
        /// Godot walks the single edited scene root) and Godot-MCP's <c>Tool_Scene.GetData</c>
        /// (behavior reference — the <c>hierarchyDepth</c>-to-<c>int.MaxValue</c> sentinel trick for
        /// "whole tree" is lifted from there).
        ///
        /// <para>
        /// P2.7 scope is live-only: get-data reads the editor's edited scene. Offline read of an
        /// arbitrary <c>.tscn</c> on disk (without opening it) lands in P7.2. When <c>path</c> is
        /// provided and does NOT match the edited scene, the handler returns
        /// <c>scene_not_edited</c> and points the agent at <c>scene_open</c> — switching the active
        /// scene is a mutating op that belongs to <c>scene_open</c>, not this read.
        /// </para>
        ///
        /// <para>
        /// <c>hierarchy_depth</c> semantics: 0 = root node only (no children); 1 (default) = root +
        /// direct children; N = N layers; <c>-1</c> = the whole tree (positive values capped at 5 to
        /// bound the token budget). The result carries the scene's path/name/isDirty plus a
        /// <c>root</c> NodeData with its children populated per the depth.
        /// </para>
        ///
        /// Structured failures: <c>no_edited_scene</c>, <c>scene_not_edited</c>. Must not throw —
        /// exceptions are caught by the dispatcher and surfaced as <c>execution_error</c>.
        /// </summary>
        internal static ToolDispatchResult GetData(string body)
        {
            var request = SceneGetDataBody.Parse(body);

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
            {
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling scene_get_data.");
            }

            // P2.7 reads the edited scene only. A path that does not match is a soft mismatch — the
            // agent likely wants a different scene, which requires scene_open (a mutating op). Pointing
            // them there keeps get-data read-only and avoids silently switching the active scene.
            if (request.Path != null)
            {
                var editedPath = root.GetSceneFilePath();
                if (request.Path != editedPath)
                {
                    return ToolDispatchResult.Fail(
                        "scene_not_edited",
                        $"scene_get_data is a live read of the edited scene only (P2.7). " +
                        $"Requested '{request.Path}' but the edited scene is '{editedPath ?? "<unsaved>"}'. " +
                        "Call scene_open first to switch scenes; offline read of an arbitrary .tscn lands in P7.2.");
                }
            }

            // Reuse the node tool family's tree serializer. It is depth-counted and walks non-internal
            // children, matching Godot-MCP's Tool_Scene.GetData behavior. EffectiveDepth translates -1
            // (unlimited) into int.MaxValue so ToNodeData's depth decrement visits the whole tree.
            var rootData = NodeTools.ToNodeData(root, request.EffectiveDepth);

            var path = root.GetSceneFilePath();
            var summary = ToSceneSummary(root, isActive: true);

            var sb = new StringBuilder(256);
            sb.Append("{\"path\":").Append(BridgeJson.EscapeString(summary.Path));
            sb.Append(",\"name\":").Append(BridgeJson.EscapeString(summary.Name));
            sb.Append(",\"isDirty\":").Append(IsEditedSceneDirty() ? "true" : "false");
            sb.Append(",\"rootType\":").Append(BridgeJson.EscapeString(summary.RootType));
            sb.Append(",\"hierarchyDepth\":").Append(request.HierarchyDepth.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"root\":");
            rootData.AppendJsonTo(sb);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- godot_open_mcp_scene_create (P2.7) ------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_scene_create</c>. Creates a new <c>.tscn</c> scene asset at a
        /// <c>res://</c> path and (by default) opens it as the active scene. Adapted from Unity Open
        /// MCP's <c>ScenesTools.Create</c> (adapt fidelity — Unity uses
        /// <c>EditorSceneManager.NewScene</c>; Godot builds a <see cref="PackedScene"/> from a root
        /// Node and saves via <see cref="ResourceSaver"/>) and Godot-MCP's <c>Tool_Scene.Create</c>
        /// (behavior reference — the Pack → Save → UpdateFile → OpenSceneFromPath sequence is lifted
        /// from there).
        ///
        /// <para>
        /// Root node creation mirrors <c>node_create</c>'s typed path: <c>ClassDB.ClassExists</c> +
        /// <c>CanInstantiate</c> validation, then <c>ClassDB.Instantiate</c>. The created node is
        /// packed into a <see cref="PackedScene"/>, saved to disk, the editor's resource filesystem
        /// refreshed, and (unless <c>open: false</c>) opened as the active scene. The root's name
        /// defaults to a PascalCased derivation of the filename stem when not supplied.
        /// </para>
        ///
        /// <para>
        /// <b>No gate yet.</b> Same forward-compat <c>gate</c>/<c>paths_hint</c> no-op as the other P2
        /// mutators; no editor Undo yet.
        /// </para>
        ///
        /// Structured failures: <c>missing_parameter</c>, <c>invalid_path</c>, <c>path_exists</c>,
        /// <c>invalid_root_type</c>, <c>create_failed</c>. Must not throw.
        /// </summary>
        internal static ToolDispatchResult Create(string body)
        {
            var request = SceneCreateBody.Parse(body);

            if (string.IsNullOrEmpty(request.Path))
            {
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "scene_create requires 'path' (a res:// path ending in .tscn or .scn).");
            }

            var path = request.Path!;
            if (!IsResPath(path))
            {
                return ToolDispatchResult.Fail(
                    "invalid_path",
                    $"path must be a 'res://' path; got '{path}'.");
            }
            if (!EndsWithSceneExt(path))
            {
                return ToolDispatchResult.Fail(
                    "invalid_path",
                    $"path must end with '.tscn' or '.scn'; got '{path}'.");
            }
            if (!request.Overwrite && ResourceLoader.Exists(path))
            {
                return ToolDispatchResult.Fail(
                    "path_exists",
                    $"A resource already exists at '{path}'. Pass overwrite=true to replace it.");
            }

            var className = request.EffectiveRootType;
            if (!ClassDB.ClassExists(className))
            {
                return ToolDispatchResult.Fail(
                    "invalid_root_type",
                    $"Unknown Godot class '{className}'.");
            }
            if (!ClassDB.CanInstantiate(className))
            {
                return ToolDispatchResult.Fail(
                    "invalid_root_type",
                    $"Class '{className}' exists but cannot be instantiated (abstract or singleton).");
            }

            Node? root;
            try
            {
                var variant = ClassDB.Instantiate(className);
                root = variant.As<Node>();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate root class '{className}': {e.Message}");
            }
            if (root == null)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Instantiated '{className}' but the result was not a Node.");
            }

            // Name: explicit > filename-stem derivation. The editor's new-scene naming is PascalCased
            // from the file stem, so an agent creating res://levels/level_2.tscn gets a root named
            // Level2 without having to pass root_name.
            if (!string.IsNullOrEmpty(request.RootName))
            {
                root.Name = request.RootName!;
            }
            else
            {
                var derived = DeriveRootName(path);
                if (!string.IsNullOrEmpty(derived))
                    root.Name = derived;
            }

            // Pack the root into a PackedScene, then save. ResourceSaver.Save does NOT create missing
            // parent directories — make them first so a nested target (res://levels/x.tscn) saves
            // instead of failing with CantOpen. Mirrors Godot-MCP's Tool_Scene.Create and Unity's
            // MaterialTools.EnsureFolderRecursive.
            Error saveErr;
            try
            {
                var packed = new PackedScene();
                var packErr = packed.Pack(root);
                if (packErr != Error.Ok)
                {
                    root.Free();
                    return ToolDispatchResult.Fail("create_failed",
                        $"Failed to pack root node into PackedScene: {packErr}.");
                }

                EnsureParentDir(path);
                saveErr = ResourceSaver.Save(packed, path);
            }
            catch (System.Exception e)
            {
                root.Free();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to save scene to '{path}': {e.Message}");
            }
            finally
            {
                // The in-memory root was only needed to pack the PackedScene; free it so it does not
                // leak. The editor re-instances its own root when the saved scene is opened below.
                if (Godot.GodotObject.IsInstanceValid(root))
                    root.Free();
            }

            if (saveErr != Error.Ok)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"ResourceSaver.Save returned {saveErr} for '{path}'.");
            }

            // Make the new asset visible to the editor's resource filesystem.
            EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(path);

            if (request.Open)
            {
                // OpenSceneFromPath re-points the edited root. The freshly-saved scene is clean from the
                // bridge's perspective (just loaded from disk) — clear any stale dirty flag for the path.
                EditorInterface.Singleton.OpenSceneFromPath(path);
                var editedRoot = EditorInterface.Singleton.GetEditedSceneRoot();
                if (editedRoot == null)
                {
                    return ToolDispatchResult.Fail("create_failed",
                        $"Created '{path}' but the editor has no edited scene root after opening it.");
                }
                ClearDirty(DirtyKeyFor(editedRoot));

                var summary = ToSceneSummary(editedRoot, isActive: true);
                var sb = new StringBuilder(160);
                sb.Append("{\"created\":true,\"opened\":true,\"path\":").Append(BridgeJson.EscapeString(summary.Path));
                sb.Append(",\"name\":").Append(BridgeJson.EscapeString(summary.Name));
                sb.Append(",\"rootType\":").Append(BridgeJson.EscapeString(summary.RootType));
                sb.Append(",\"root\":");
                NodeTools.ToNodeData(editedRoot).AppendJsonTo(sb);
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }

            // Created without opening — return a path-only summary (no edited root to snapshot).
            var sb2 = new StringBuilder(96);
            sb2.Append("{\"created\":true,\"opened\":false,\"path\":").Append(BridgeJson.EscapeString(path));
            sb2.Append(",\"name\":").Append(BridgeJson.EscapeString(SceneFileStem(path)));
            sb2.Append(",\"rootType\":").Append(BridgeJson.EscapeString(className));
            sb2.Append('}');
            return ToolDispatchResult.Ok(sb2.ToString());
        }

        /// <summary>
        /// Create the parent directory for a <c>res://</c> path so <c>ResourceSaver.Save</c> does not
        /// fail with <c>CantOpen</c> on a nested target. Uses <c>DirAccess.MakeDirRecursiveAbsolute</c>
        /// on the <c>res://</c>-relative parent. Main-thread only. Adapted from Godot-MCP's
        /// <c>Tool_Scene.Create</c> / Unity's <c>MaterialTools.EnsureFolderRecursive</c>.
        /// </summary>
        static void EnsureParentDir(string resPath)
        {
            var lastSlash = resPath.LastIndexOf('/');
            if (lastSlash <= "res://".Length - 1) return; // no parent dir beyond res:// itself
            var parentDir = resPath.Substring(0, lastSlash);
            var da = DirAccess.Open("res://");
            if (da == null) return;
            try
            {
                // MakeDirRecursiveAbsolute takes a path relative to the opened dir (res://).
                var rel = parentDir.Substring("res://".Length);
                if (!string.IsNullOrEmpty(rel))
                    da.MakeDirRecursiveAbsolute(rel);
            }
            catch { /* best-effort; a failure surfaces as a save error downstream */ }
            finally { da.Dispose(); }
        }

        /// <summary>
        /// Derive a PascalCased root node name from a <c>res://</c> scene path's filename stem.
        /// <c>res://levels/level_2.tscn</c> → <c>Level2</c>; <c>res://main.tscn</c> → <c>Main</c>.
        /// Matches the Godot editor's own new-scene naming convention so a created scene looks native.
        /// </summary>
        static string DeriveRootName(string resPath)
        {
            var stem = SceneFileStem(resPath);
            if (string.IsNullOrEmpty(stem)) return string.Empty;
            var sb = new System.Text.StringBuilder(stem.Length);
            bool capitalizeNext = true;
            foreach (var c in stem)
            {
                if (c == '_' || c == '-' || c == ' ' || c == '.')
                {
                    capitalizeNext = true;
                    continue;
                }
                sb.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
                capitalizeNext = false;
            }
            return sb.ToString();
        }

        // --- shared helpers ---------------------------------------------------------

        /// <summary>
        /// Build a <see cref="SceneSummary"/> for a live edited root (the active scene). Populates
        /// path, name (root node name), rootType, isActive, and isDirty (bridge-tracked). Main-thread
        /// only.
        /// </summary>
        static SceneSummary ToSceneSummary(Node editedRoot, bool isActive)
        {
            var path = editedRoot.GetSceneFilePath();
            var key = DirtyKeyFor(editedRoot);
            var isDirty = key != null && _dirtyScenes.TryGetValue(key, out var d) && d;
            return new SceneSummary
            {
                Path = string.IsNullOrEmpty(path) ? null : path,
                Name = editedRoot.Name.ToString(),
                RootType = editedRoot.GetClass(),
                IsActive = isActive,
                IsDirty = isDirty,
            };
        }

        /// <summary>
        /// Build a path-only <see cref="SceneSummary"/> for an open scene that is NOT the active one
        /// (Godot 4.3 exposes no root accessor for non-active open scenes). Name is the file stem;
        /// rootType is null; isActive false; isDirty reflects bridge-tracked state for the path.
        /// </summary>
        static SceneSummary PathOnlySummary(string resourcePath)
        {
            var isDirty = _dirtyScenes.TryGetValue(resourcePath, out var d) && d;
            return new SceneSummary
            {
                Path = resourcePath,
                Name = SceneFileStem(resourcePath),
                RootType = null,
                IsActive = false,
                IsDirty = isDirty,
            };
        }

        static bool IsResPath(string path)
            => path.StartsWith("res://", System.StringComparison.Ordinal);

        static bool EndsWithSceneExt(string path)
            => path.EndsWith(".tscn", System.StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".scn", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>File stem of a <c>res://</c> path (last path segment without extension). Used as
        /// the scene name for a non-active open scene where no root is available.</summary>
        static string? SceneFileStem(string resourcePath)
        {
            if (string.IsNullOrEmpty(resourcePath)) return null;
            var lastSlash = resourcePath.LastIndexOf('/');
            var file = lastSlash >= 0 ? resourcePath.Substring(lastSlash + 1) : resourcePath;
            var dot = file.LastIndexOf('.');
            return dot > 0 ? file.Substring(0, dot) : file;
        }
    }
}
#endif
