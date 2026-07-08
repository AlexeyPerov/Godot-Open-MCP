// `godot_open_mcp_node_set_parent` tool definition (P2.5).
//
// Reparents a Node under a new parent in the currently edited Godot scene and
// returns the reparented Node's NodeData. The handler lives in the bridge
// (POST /tools/godot_open_mcp_node_set_parent); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/gameobject-set-parent.ts
// (adapt fidelity): same mutating-tool shape (paths_hint + gate forward-compat
// fields, cycle-safe), but the resolver vocabulary is swapped for Godot:
//   - `node_path` + `parent_node_path` replace Unity's instance_id/path and
//     parent_instance_id/parent_path pairs. Godot scenes are single-rooted
//     under the edited scene root.
//   - `keep_global_transform` (default true) replaces Unity's
//     `world_position_stays`. Same semantics, Godot-native spelling — maps to
//     Node.Reparent(parent, keepGlobalTransform).
//   - Unity's local_space is not relevant; Godot's Node.Reparent is a single
//     primitive that preserves or resets transform in one call.
//   - `paths_hint` and `gate` are schema no-ops until the gate flow lands
//     (P3.5), same forward-compat shape as the other P2 mutators.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const nodeSetParent: Tool = {
  name: "godot_open_mcp_node_set_parent",
  description:
    "Reparent a Node under a new parent in the currently edited Godot scene, preserving its global " +
    "transform by default (Godot's Node.Reparent). Cycle-safe: refuses to reparent the edited scene " +
    "root, a Node under itself, or a Node under one of its own descendants. The reparented sub-tree's " +
    "Owner is reset to the edited scene root so it persists in the .tscn on save. The scene is marked " +
    "unsaved. Returns the reparented Node's NodeData (instanceId, name, path, type, scriptResourcePath, " +
    "childCount) so an agent can chain immediately. NOTE: this is a mutating tool, but the gate safety " +
    "layer is not wired yet — no checkpoint/validate/delta cycle and no editor Undo until the gate " +
    "lands. Verify the result with node_find.",
  inputSchema: {
    type: "object",
    properties: {
      node_path: {
        type: "string",
        description:
          "Required. Scene-tree path of the Node to reparent (same resolver as node_find: 'Main/Player', " +
          "'/root/Main/Player', or '.' for the edited scene root — though reparenting the root is refused).",
      },
      parent_node_path: {
        type: "string",
        description:
          "Required. Scene-tree path of the new parent Node. Same resolver as node_path. The new parent " +
          "must already exist in the edited scene.",
      },
      keep_global_transform: {
        type: "boolean",
        default: true,
        description:
          "When true (default), preserve the Node's global transform across the reparent. When false, " +
          "keep its local transform. Maps to Node.Reparent(parent, keepGlobalTransform). Replaces " +
          "Unity's world_position_stays.",
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
