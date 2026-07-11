// `godot_open_mcp_editor_selection_get` tool definition (P4.6).
//
// Read-only. Returns the Godot editor's current node selection as a flat NodeData list (shallow,
// hierarchyDepth: 0), plus the active (last-selected) node, the count, and the active edited scene
// path. The handler lives in the bridge (POST /tools/godot_open_mcp_editor_selection_get); this file
// is the catalog metadata only — name / description / input schema — advertised to AI clients over
// stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/selection-get.ts (adapt fidelity): Unity's
// selection carries asset GUIDs, instance IDs, and component references. The Godot port is node-only
// — no asset/component/global-object fields. `activeNode` is the last-selected node because Godot has
// no equivalent explicit active object.
//
// Godot-MCP reference (Tool_Editor.Selection.Get): the EditorInterface.GetSelection() +
// GetSelectedNodes() API surface and the last-selected-as-active convention are lifted from there as
// read-only behavior guidance. The structured error contract, the active-scene path field, and the
// result DTO shape are greenfield for this port.
//
// An empty selection (count 0, activeNode null) is a successful response, NOT an error — an agent
// checking "is anything selected?" branches on the count.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const editorSelectionGet: Tool = {
  name: "godot_open_mcp_editor_selection_get",
  description:
    "Get the Godot editor's current node selection as structured data: the selected scene-tree nodes " +
    "(each as shallow NodeData with instanceId/name/path/type/scriptResourcePath/childCount) and the " +
    "active (last-selected) node. Godot's selection is node-only — there is no asset-GUID or component " +
    "selection distinction. The active node is the last-selected node (Godot has no first-class active " +
    "object; this matches how the editor inspector tracks the most-recently-clicked node). Returns an " +
    "empty selection (count 0, activeNode null) when nothing is selected — this is a success, not an " +
    "error. Use editor_selection_set to change the selection. Read-only (gate-free).",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
