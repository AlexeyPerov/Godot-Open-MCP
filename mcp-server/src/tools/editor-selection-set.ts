// `godot_open_mcp_editor_selection_set` tool definition (P4.6).
//
// Mutating (default gate "enforce"). Replaces the whole editor selection with the provided node
// references, or clears it with an empty list. The handler lives in the bridge
// (POST /tools/godot_open_mcp_editor_selection_set); this file is the catalog metadata only — name /
// description / input schema — advertised to AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/selection-set.ts (adapt fidelity): Unity's
// selection-set carries asset GUIDs, instance IDs, and component refs with targets[]/clear semantics.
// The Godot port is node-only: `select` is a list of node refs (instance_id preferred, else
// node_path). An empty list clears.
//
// Godot-MCP reference (Tool_Editor.Selection.Set): the EditorInterface.GetSelection().Clear() +
// AddNode() surface and the resolve-all-before-clear (all-or-nothing) algorithm are lifted from
// there as read-only behavior guidance. The active-scene membership guard, the duplicate-node
// rejection, the hard selection count limit, the gate integration, and the observed-post-state result
// are greenfield for this port.
//
// All refs are resolved completely BEFORE the current selection is cleared, so a single bad ref
// leaves the existing selection intact. Resolution precedence: instance_id (priority 1) then
// node_path (priority 2). Each resolved node must belong to the active edited scene; a foreign-scene
// node is rejected. Duplicates (after resolution) are rejected. The result carries the observed
// post-change selection (same shape as editor_selection_get) plus a `cleared` boolean.
//
// paths_hint is mandatory: the active edited scene path for a non-empty/clear operation (or
// res://project.godot for a clear). This is both the gate scope and a handler-level guard that fires
// even when an agent overrides with gate:"off".

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const editorSelectionSet: Tool = {
  name: "godot_open_mcp_editor_selection_set",
  description:
    "Set the Godot editor's node selection to the provided nodes (replacing any current selection). " +
    "Mutating — runs the full gate cycle (checkpoint → clear+add → validate → delta) by default, even " +
    "though the selection write changes no files (it changes editor selection state). The 'select' " +
    "array is a list of node references, each identified by instance_id (preferred, stable identity) " +
    "or node_path (scene-tree path, same forms as node_find: 'Main/Player', '/root/Main/Player', or " +
    "'.' for the edited root). When both are set, instance_id wins. All refs are resolved completely " +
    "BEFORE the current selection is cleared — a single bad ref leaves the existing selection intact " +
    "(all-or-nothing). Each resolved node must belong to the active edited scene; duplicates (after " +
    "resolution) and foreign-scene nodes are rejected. Pass an empty list to clear the selection. " +
    "Returns the observed post-change selection (same shape as editor_selection_get) plus a " +
    "'cleared' boolean. paths_hint is mandatory: the active edited scene path (or " +
    "res://project.godot for a clear).",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      select: {
        type: "array",
        description:
          "Nodes to select, each identified by instance_id (preferred, priority 1) or node_path " +
          "(priority 2). An empty list (or omitted array) clears the selection. Order is preserved in " +
          "the request; the observed post-state may reorder (Godot's EditorSelection does not " +
          "guarantee order-stable reads). Each ref must resolve to a live node in the active edited " +
          "scene; duplicates and foreign-scene nodes are rejected.",
        items: {
          type: "object",
          properties: {
            instance_id: {
              type: "integer",
              description:
                "Godot instance id of the Node (GodotObject.GetInstanceId()). Priority 1 when non-zero. " +
                "A stable identity within the session.",
            },
            node_path: {
              type: "string",
              description:
                "Scene-tree path of the Node (e.g. 'Main/Player', '/root/Main/Player', or '.' for the " +
                "edited root). Priority 2. Same path vocabulary as node_find.",
            },
          },
          additionalProperties: false,
        },
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the active edited scene path for a non-empty/clear operation, or " +
          "'res://project.godot' for a clear. Mandatory even when gate is 'off' (handler-level guard). " +
          "There is no implicit whole-project gate fallback.",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → clear+add → validate → delta; new errors " +
          "fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the cycle " +
          "(paths_hint is still required). The selection write changes no files, so the verify delta " +
          "is clean in the common case — the gate still runs because the tool changes editor selection " +
          "state.",
      },
    },
    additionalProperties: false,
  },
};
