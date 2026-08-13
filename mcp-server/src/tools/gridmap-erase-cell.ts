// `godot_open_mcp_gridmap_erase_cell` tool definition.
//
// Erases one cell from a GridMap (sets the item to INVALID_CELL_ITEM, -1). The
// handler lives in the bridge (POST /tools/godot_open_mcp_gridmap_erase_cell);
// this file is the catalog metadata only.
//
// Adapted from the tilemap pack's erase_cell surface (adapt fidelity): same
// mutating-tool shape (paths_hint required + gate default enforce). Godot's
// GridMap has no dedicated erase method — setting the item to the
// INVALID_CELL_ITEM sentinel (-1) clears the cell (the same value GetCellItem
// returns for an empty cell).
//
// This is a `gridmap` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const gridmapEraseCell: Tool = {
  name: "godot_open_mcp_gridmap_erase_cell",
  description:
    "Erase one cell from a Godot 4.3+ GridMap at the 3D grid coordinate (x, y, z). Erasing an " +
    "already-empty cell is a no-op success (returns erased:true). Godot's GridMap has no dedicated " +
    "erase method — erasing sets the cell item to the INVALID_CELL_ITEM sentinel (-1). The scene is " +
    "marked unsaved. This is a `gridmap` group tool — activate the group with manage_tools first. " +
    "Mutating: runs the gate cycle by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "x", "y", "z", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target GridMap, relative to the edited scene root. Must " +
          "resolve to a GridMap node (Godot 4.3+).",
      },
      x: { type: "integer", description: "Map cell x coordinate." },
      y: { type: "integer", description: "Map cell y coordinate." },
      z: { type: "integer", description: "Map cell z coordinate." },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the edited scene res:// path. Mandatory even when gate is 'off'.",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description: "Gate mode. 'enforce' (default), 'warn', or 'off' (paths_hint still required).",
      },
    },
    additionalProperties: false,
  },
};
