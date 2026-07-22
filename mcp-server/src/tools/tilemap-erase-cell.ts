// `godot_open_mcp_tilemap_erase_cell` tool definition (P12.1).
//
// Erases one cell from a TileMapLayer. Erasing an already-empty cell is a no-op
// success. The handler lives in the bridge
// (POST /tools/godot_open_mcp_tilemap_erase_cell); this file is the catalog
// metadata only.
//
// Adapted from Unity Open MCP's tilemap set_tile(null) erase idiom (adapt
// fidelity): same mutating-tool shape (paths_hint required + gate default
// enforce), but Godot exposes a dedicated EraseCell API rather than a
// set-to-null convention. Only node_path + x + y are needed — erase does not
// care which tile occupied the cell.
//
// This is a `tilemap` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const tilemapEraseCell: Tool = {
  name: "godot_open_mcp_tilemap_erase_cell",
  description:
    "Erase one cell from a Godot 4.3+ TileMapLayer. Erasing an already-empty cell is a no-op " +
    "success (returned with erased:true). Only node_path + x + y are needed — erase does not care " +
    "which tile occupied the cell. The scene is marked unsaved. This is a `tilemap` group tool — " +
    "activate the group with manage_tools first. Mutating: runs the gate cycle by default; " +
    "paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "x", "y", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target TileMapLayer, relative to the edited scene root. Must " +
          "resolve to a TileMapLayer node (Godot 4.3+).",
      },
      x: {
        type: "integer",
        description: "Map cell x coordinate to erase.",
      },
      y: {
        type: "integer",
        description: "Map cell y coordinate to erase.",
      },
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
