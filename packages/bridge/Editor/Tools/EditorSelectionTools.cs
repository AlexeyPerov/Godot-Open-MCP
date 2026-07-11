#if TOOLS
#nullable enable
using System.Collections.Generic;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Editor selection tool family (P4.6) — the Godot analog of Unity Open MCP's
    /// <c>selection-get</c> (read) and <c>selection-set</c> (write). Two tools:
    /// <list type="bullet">
    /// <item><description><c>godot_open_mcp_editor_selection_get</c> (read-only) — the currently
    /// selected nodes as a flat <see cref="NodeData"/> list, plus the active (last-selected) node and
    /// the active edited scene path.</description></item>
    /// <item><description><c>godot_open_mcp_editor_selection_set</c> (mutating, default gate
    /// <c>enforce</c>) — replace the whole selection with a list of node references, or clear it with
    /// an empty list. References are resolved completely BEFORE the current selection is cleared, so a
    /// bad ref leaves the existing selection intact (all-or-nothing).</description></item>
    /// </list>
    ///
    /// <para>
    /// Godot's <see cref="EditorSelection"/> (obtained from <c>EditorInterface.GetSelection()</c>)
    /// selects scene-tree <see cref="Node"/>s only — there is no Unity-style asset-GUID / Transform /
    /// Component selection distinction, and no first-class "active object". So the result is a flat
    /// node list plus the LAST selected node as the "active" one (matching how the editor inspector
    /// tracks the most-recently-clicked node).
    /// </para>
    ///
    /// <para>
    /// <b>Behavior reference</b> (Godot-MCP <c>Tool_Editor.Selection</c>): the
    /// <c>EditorInterface.GetSelection().GetSelectedNodes()</c> / <c>Clear()</c> /
    /// <c>AddNode(Node)</c> API surface is lifted from there as read-only behavior guidance. The
    /// structured error contract, the all-or-nothing resolve-before-mutate algorithm, the
    /// active-scene membership guard, the duplicate-node rejection, the hard selection count limit,
    /// the gate integration, and the result DTO are greenfield for this port.
    /// </para>
    ///
    /// <para>
    /// <b>Intentional deltas from Unity</b> (per the P4.6 plan + <c>packages/bridge/AGENTS.md</c>
    /// §Unity-first): Unity selection carries asset GUIDs, instance IDs, and component references. The
    /// Godot port is node-only. <c>activeNode</c> is the last selected node because Godot has no
    /// equivalent explicit active object. Set replaces the whole selection; additive/toggle modes are
    /// deferred.
    /// </para>
    ///
    /// <para>
    /// <b>Gate.</b> Set is mutating (it changes editor selection state — not project files) and
    /// registered with default gate <c>enforce</c>. It writes no files, so the gate's verify delta
    /// will be clean in the common case; the gate scope (<c>paths_hint</c>) carries the explicit
    /// edited-scene scope per the P4.6 plan.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): the handlers touch <see cref="EditorInterface"/> and live
    /// <see cref="Node"/> objects. The pure-managed pieces (<see cref="EditorSelectionData"/>,
    /// <see cref="EditorSelectionSetBody"/>) live outside this guard and are unit-tested.
    /// </summary>
    internal static class EditorSelectionTools
    {
        /// <summary>The MCP tool name for the editor-selection read (P4.6).</summary>
        internal const string GetSelectionToolName = "godot_open_mcp_editor_selection_get";

        /// <summary>The MCP tool name for the editor-selection mutation (P4.6).</summary>
        internal const string SetSelectionToolName = "godot_open_mcp_editor_selection_set";

        /// <summary>
        /// Hard maximum on the number of nodes a single set-selection call can select. Protects against
        /// a runaway request flooding the editor selection. Matches the spirit of Unity's bounded
        /// selection surface.
        /// </summary>
        internal const int MaxSelectionCount = 256;

        // --- registration -----------------------------------------------------------

        /// <summary>
        /// Register the editor selection tool family (P4.6). One read-only tool
        /// (<c>godot_open_mcp_editor_selection_get</c>, group <c>editor</c>, default gate <c>off</c>)
        /// and one gated mutator (<c>godot_open_mcp_editor_selection_set</c>, group <c>editor</c>,
        /// default gate <c>enforce</c>). Registered once at plugin enable; safe to call again on
        /// re-enable (the registry is idempotent).
        /// </summary>
        internal static void RegisterEditorSelectionTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: GetSelectionToolName,
                isMutating: false,
                defaultGate: "off",
                group: "editor",
                handler: GetSelection));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: SetSelectionToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "editor",
                handler: SetSelection));
        }

        // --- godot_open_mcp_editor_selection_get ------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_editor_selection_get</c>. Read-only. Returns the currently
        /// selected nodes as a flat <see cref="NodeData"/> list (shallow, <c>hierarchyDepth: 0</c>),
        /// plus the active (last-selected) node, the count, and the active edited scene path.
        ///
        /// <para>
        /// Adapted from Unity Open MCP's <c>selection-get</c> (adapt fidelity — same read shape, Godot
        /// node-only fields) and Godot-MCP's <c>Tool_Editor.Selection.Get</c> (behavior reference —
        /// <c>EditorInterface.GetSelection().GetSelectedNodes()</c>).
        /// </para>
        ///
        /// <para>
        /// An empty selection (count 0, activeNode null) is a successful response, NOT an error — an
        /// agent checking "is anything selected?" branches on the count, not on ok:false. When no scene
        /// is being edited, the result is still successful with an empty selection and
        /// <c>scenePath: null</c>.
        /// </para>
        ///
        /// Must not throw — exceptions are caught by the dispatcher and surfaced as
        /// <c>execution_error</c>.
        /// </summary>
        internal static ToolDispatchResult GetSelection(string body)
        {
            var data = CaptureSelection(cleared: null);
            return ToolDispatchResult.Ok(data.ToJsonString());
        }

        // --- godot_open_mcp_editor_selection_set ------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_editor_selection_set</c>. Mutating (default gate
        /// <c>enforce</c>). Replaces the whole editor selection with the provided node references, or
        /// clears it with an empty list. References are resolved completely BEFORE the current selection
        /// is cleared, so a single bad ref leaves the existing selection intact (all-or-nothing).
        ///
        /// <para>
        /// <b>Algorithm.</b> (1) Parse the <c>select</c> array. (2) Enforce the hard count limit. (3)
        /// Obtain the active edited scene root. (4) Resolve every ref using the Phase 2 node resolver
        /// (<c>node_path</c>) or instance-id lookup. (5) Verify each node belongs to the active edited
        /// scene. (6) Reject duplicates after resolution. (7) Clear the current selection. (8) Add the
        /// resolved nodes in request order. (9) Read the selection back and return the observed state.
        /// </para>
        ///
        /// <para>
        /// <b>Empty list.</b> Steps 3–6 are vacuous — the operation just clears the selection. The
        /// plan allows clearing even when no scene is edited (the <c>EditorSelection</c> is still
        /// available via <c>EditorInterface</c>); a clear always returns the post-clear observed state.
        /// </para>
        ///
        /// <para>
        /// <b>paths_hint.</b> Mandatory (handler-level guard that fires even under <c>gate:"off"</c>).
        /// Carries the active edited scene path for a non-empty/clear operation. When no scene is
        /// edited and the list is non-empty, the call fails with <c>edited_scene_unavailable</c>; when
        /// the list is empty (clear), the clear proceeds and <c>paths_hint</c> may be
        /// <c>res://project.godot</c>.
        /// </para>
        ///
        /// <para>
        /// <b>Selection write.</b> Godot's <c>EditorSelection</c> does not guarantee order-stable
        /// reads after <c>AddNode</c>, so the observed post-state may reorder. The handler verifies the
        /// resolved SET matches the observed SET (not the order) — a mismatch surfaces
        /// <c>selection_update_failed</c> with the observed state.
        /// </para>
        ///
        /// Structured failures: <c>paths_hint_required</c>, <c>selection_limit_exceeded</c>,
        /// <c>edited_scene_unavailable</c>, <c>node_not_found</c>, <c>node_not_in_edited_scene</c>,
        /// <c>duplicate_node</c>, <c>selection_update_failed</c>, <c>selection_unavailable</c>. Must
        /// not throw — exceptions are caught by the dispatcher and surfaced as
        /// <c>execution_error</c>.
        /// </summary>
        internal static ToolDispatchResult SetSelection(string body)
        {
            // Handler-level paths_hint guard — fires even under gate:"off".
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "editor_selection_set requires 'paths_hint': the active edited scene path " +
                    "(or res://project.godot for a clear).");

            var request = EditorSelectionSetBody.Parse(body);

            // (2) Hard count limit before any resolution work.
            if (request.Select.Count > MaxSelectionCount)
                return ToolDispatchResult.Fail(
                    "selection_limit_exceeded",
                    $"Selection request of {request.Select.Count} nodes exceeds the hard maximum of " +
                    $"{MaxSelectionCount}. Select fewer nodes per call.");

            // (3) Active edited scene root + scene path. A non-empty selection requires an edited scene.
            Node? editedRoot = EditorInterface.Singleton.GetEditedSceneRoot();
            string? scenePath = editedRoot?.GetSceneFilePath();
            if (scenePath == null) scenePath = editedRoot?.GetPath().ToString();

            if (request.Select.Count > 0 && editedRoot == null)
            {
                return ToolDispatchResult.Fail(
                    "edited_scene_unavailable",
                    "Non-empty selection requested but no scene is currently being edited; open a " +
                    ".tscn before setting a selection, or pass an empty list to clear.");
            }

            // (4–6) Resolve + validate every ref BEFORE touching the live selection (all-or-nothing).
            var resolved = new List<Node>();
            for (int i = 0; i < request.Select.Count; i++)
            {
                var r = request.Select[i];
                if (r.IsEmpty)
                {
                    return ToolDispatchResult.Fail(
                        "node_not_found",
                        $"select[{i}] has neither 'instance_id' nor 'node_path'; cannot resolve.");
                }

                Node? node = ResolveRef(editedRoot!, r);
                if (node == null)
                {
                    return ToolDispatchResult.Fail(
                        "node_not_found",
                        $"select[{i}] ({r}) could not be resolved in the edited scene.");
                }

                // (5) Active-scene membership: the resolved node must be the edited root or a
                // descendant of it (walk the parent chain).
                if (!BelongsToEditedScene(node, editedRoot!))
                {
                    return ToolDispatchResult.Fail(
                        "node_not_in_edited_scene",
                        $"select[{i}] resolved to '{node.GetPath()}' which does not belong to the " +
                        "active edited scene.");
                }

                // (6) Duplicate detection after resolution.
                if (resolved.Contains(node))
                {
                    return ToolDispatchResult.Fail(
                        "duplicate_node",
                        $"select[{i}] resolves to the same node as an earlier entry " +
                        $"('{node.GetPath()}'); remove the duplicate.");
                }

                resolved.Add(node);
            }

            // (7–8) Clear + add. Now that every ref resolved, mutate the live selection.
            var selection = EditorInterface.Singleton.GetSelection();
            if (selection == null)
                return ToolDispatchResult.Fail(
                    "selection_unavailable",
                    "EditorInterface.GetSelection() returned null; editor selection service unavailable.");

            selection.Clear();
            foreach (var node in resolved)
                selection.AddNode(node);

            // (9) Read selection back and return observed state.
            var observed = CaptureSelection(cleared: true);

            // Verify the observed set matches the resolved set (order-independent).
            if (!SelectionMatches(resolved, observed.Nodes))
            {
                return ToolDispatchResult.FailWithOutput(
                    "selection_update_failed",
                    "Observed selection does not match the requested set after the write.",
                    observed.ToJsonString());
            }

            return ToolDispatchResult.Ok(observed.ToJsonString());
        }

        // --- helpers -----------------------------------------------------------------

        /// <summary>
        /// Build an <see cref="EditorSelectionData"/> snapshot from the editor's current node
        /// selection. Main-thread only. The active node is reported as the LAST selected node (Godot
        /// has no first-class active object). <paramref name="cleared"/> is null for the get handler
        /// (field omitted); true for the set handler (replace operation always clears first).
        /// </summary>
        static EditorSelectionData CaptureSelection(bool? cleared)
        {
            var data = new EditorSelectionData { Cleared = cleared };

            Node? editedRoot = null;
            try { editedRoot = EditorInterface.Singleton.GetEditedSceneRoot(); }
            catch (System.Exception) { /* EditorInterface not ready — treat as no edited scene. */ }
            data.ScenePath = editedRoot?.GetSceneFilePath();
            if (data.ScenePath == null) data.ScenePath = editedRoot?.GetPath().ToString();

            try
            {
                var selection = EditorInterface.Singleton.GetSelection();
                if (selection != null)
                {
                    var nodes = selection.GetSelectedNodes();
                    foreach (var node in nodes)
                    {
                        if (node == null) continue;
                        var n = node as Node;
                        if (n == null) continue;
                        data.Nodes.Add(NodeTools.ToNodeData(n, hierarchyDepth: 0));
                    }
                }
            }
            catch (System.Exception)
            {
                // EditorInterface not ready — report an empty selection rather than faulting.
            }

            data.Count = data.Nodes.Count;
            data.ActiveNode = data.Nodes.Count > 0 ? data.Nodes[data.Nodes.Count - 1] : null;
            return data;
        }

        /// <summary>
        /// Resolve a single <see cref="EditorSelectionSetBody.NodeRef"/> to a live node in the edited
        /// scene. Resolution precedence: <c>instance_id</c> (priority 1, when non-zero) then
        /// <c>node_path</c> (priority 2). Returns null when neither resolves. Main-thread only.
        ///
        /// <para>
        /// <c>instance_id</c> uses <c>GodotObject.InstanceFromId</c> — a live scene-tree lookup. The
        /// caller verifies active-scene membership separately. <c>node_path</c> uses the Phase 2
        /// <see cref="NodePathNormalizer"/> so the same path forms as <c>node_find</c> are accepted.
        /// </para>
        /// </summary>
        static Node? ResolveRef(Node editedRoot, EditorSelectionSetBody.NodeRef r)
        {
            // instance_id (priority 1).
            if (r.HasInstanceId)
            {
                var obj = GodotObject.InstanceFromId(r.InstanceId);
                return obj as Node;
            }

            // node_path (priority 2).
            if (r.HasNodePath)
            {
                var path = NodePathNormalizer.Normalize(r.NodePath, editedRoot.Name.ToString());
                if (string.IsNullOrEmpty(path) || path == ".")
                    return editedRoot;
                return editedRoot.GetNodeOrNull(path);
            }

            return null;
        }

        /// <summary>
        /// True when <paramref name="node"/> is the edited root or a descendant of it. Walks the parent
        /// chain. Main-thread only.
        /// </summary>
        static bool BelongsToEditedScene(Node node, Node editedRoot)
        {
            if (node == editedRoot) return true;
            var cursor = node.GetParent();
            while (cursor != null)
            {
                if (cursor == editedRoot) return true;
                cursor = cursor.GetParent();
            }
            return false;
        }

        /// <summary>
        /// True when the resolved node set matches the observed <c>NodeData</c> set by instance id
        /// (order-independent). Used to detect a selection write that did not land.
        /// </summary>
        static bool SelectionMatches(List<Node> resolved, List<NodeData> observed)
        {
            if (resolved.Count != observed.Count) return false;
            var resolvedIds = new HashSet<ulong>();
            foreach (var n in resolved) resolvedIds.Add(n.GetInstanceId());
            foreach (var o in observed)
            {
                if (!resolvedIds.Remove(o.InstanceId)) return false;
            }
            return resolvedIds.Count == 0;
        }
    }
}
#endif
