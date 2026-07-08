// `godot_open_mcp_node_delete` tool definition (P2.5).
//
// Deletes one or more Nodes (and their sub-trees) from the currently edited
// Godot scene and returns the list of deleted paths. The handler lives in the
// bridge (POST /tools/godot_open_mcp_node_delete); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/gameobject-destroy.ts
// (adapt fidelity): same mutating-tool shape (paths_hint + gate forward-compat
// fields, single + batch targets), but the resolver + semantics are swapped for
// Godot:
//   - `node_path` (single) + `node_paths` (array) replace Unity's instance_id/
//     path resolvers. Batch targets share one call.
//   - `fail_if_has_children` (optional, default false) refuses non-leaf nodes
//     when true — a guard Unity's destroy does not expose. Useful for agents
//     that want to delete only leaves without pruning whole sub-trees by
//     accident.
//   - Godot uses synchronous Node.Free in editor mode (QueueFree defers to the
//     next idle frame, which never ticks deterministically under a tool-driven
//     flow). The deleted node is gone immediately after the call returns.
//   - `paths_hint` and `gate` are schema no-ops until the gate flow lands
//     (P3.5), same forward-compat shape as the other P2 mutators.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const nodeDelete: Tool = {
  name: "godot_open_mcp_node_delete",
  description:
    "Delete one or more Nodes (and all of their children) from the currently edited Godot scene. The " +
    "Node(s) are removed from their parent and freed synchronously (Node.Free — required for " +
    "editor-mode edits; QueueFree would defer past the tool call). Returns the list of deleted scene " +
    "paths and a count. Refuses to delete the edited scene root — close or replace the scene instead. " +
    "Per-target resolution misses are returned as warnings (the rest of the batch still deletes). The " +
    "scene is marked unsaved when any deletion landed. NOTE: this is a mutating tool and deletes are " +
    "IRREVERSIBLE until the gate flow (with checkpoint/validate/delta + editor Undo) lands in P3.5 — " +
    "verify targets with node_find before deleting.",
  inputSchema: {
    type: "object",
    properties: {
      node_path: {
        type: "string",
        description:
          "Single-target scene-tree path (same resolver as node_find). When both node_path and " +
          "node_paths are set, the union is deleted (de-duplicated).",
      },
      node_paths: {
        type: "array",
        items: { type: "string" },
        description:
          "Batch-target scene-tree paths. Each resolved target is deleted; misses become warnings. " +
          "Empty entries are dropped.",
      },
      fail_if_has_children: {
        type: "boolean",
        default: false,
        description:
          "When true, refuse to delete any target that has children (the target is skipped with a " +
          "has_children warning; the rest of the batch still proceeds). Default false deletes the whole " +
          "sub-tree. A guard Unity's destroy does not expose — useful for leaf-only cleanup.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the edited scene res:// path. Forward-compat no-op until the gate flow " +
          "(P3.5) lands; the mutating handler runs without checkpoint/validate/delta until then.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "off",
        description:
          "Gate mode. Forward-compat no-op until P3.5; default 'off' because the gate is not wired yet.",
      },
    },
    additionalProperties: false,
  },
};
