// `godot_open_mcp_node_duplicate` tool definition (P2.5).
//
// Duplicates a Node (and its whole sub-tree) in the currently edited Godot
// scene and returns the duplicate's NodeData. The handler lives in the bridge
// (POST /tools/godot_open_mcp_node_duplicate); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/gameobject-duplicate.ts
// (adapt fidelity): same mutating-tool shape (paths_hint + gate forward-compat
// fields), but the resolver + placement vocabulary is swapped for Godot:
//   - `node_path` replaces Unity's instance_id/path resolvers.
//   - `new_name` (optional) renames the duplicate; when omitted Godot assigns a
//     unique sibling name. Unity's gameobject-duplicate has no rename arg — the
//     Godot handler surfaces one because Godot's Duplicate() does not auto-suffix
//     reliably across types.
//   - `parent_node_path` (optional) places the duplicate under a different
//     parent than the source (default: same parent). Unity's duplicate is
//     same-parent only; Godot adds cross-parent placement as a convenience.
//   - `paths_hint` and `gate` are schema no-ops until the gate flow lands
//     (P3.5), same forward-compat shape as the other P2 mutators.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const nodeDuplicate: Tool = {
  name: "godot_open_mcp_node_duplicate",
  description:
    "Duplicate a Node (and its whole sub-tree) in the currently edited Godot scene via Godot's " +
    "Node.Duplicate, adding the copy as a sibling under the same parent by default. The duplicate's " +
    "Owner (and the Owner of its owner-less sub-tree) is set to the edited scene root so the copy " +
    "persists in the .tscn on save. The scene is marked unsaved and the duplicate is selected. Returns " +
    "the duplicate's NodeData (instanceId, name, path, type, scriptResourcePath, childCount) so an " +
    "agent can chain immediately. NOTE: this is a mutating tool, but the gate safety layer is not wired " +
    "yet — no checkpoint/validate/delta cycle and no editor Undo until the gate lands.",
  inputSchema: {
    type: "object",
    properties: {
      node_path: {
        type: "string",
        description:
          "Required. Scene-tree path of the Node to duplicate (same resolver as node_find: 'Main/Player', " +
          "'/root/Main/Player'). Duplicating the edited scene root is refused — pick a child node.",
      },
      new_name: {
        type: "string",
        description:
          "Optional name for the duplicate. When omitted, Godot assigns a default name for the type. " +
          "There is no collision guard — passing an existing sibling name leaves two siblings with the " +
          "same name (Godot permits this but node_find's first-match-by-name becomes ambiguous).",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the destination parent. Defaults to the source's parent (sibling " +
          "placement). When provided, the duplicate is added under that parent instead. The parent must " +
          "already exist in the edited scene.",
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
