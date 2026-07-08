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
    /// <c>godot_open_mcp_scene_list_opened</c>.
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
    /// <see cref="SceneOpenBody"/>, <see cref="SceneSaveBody"/>) live outside this guard and are
    /// unit-tested.
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
        /// then), and the read-only <c>godot_open_mcp_scene_list_opened</c>. Registered once at
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
