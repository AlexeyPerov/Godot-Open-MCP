// `godot_open_mcp_tilemap_clear` tool definition (P12.1).
//
// Clears every cell on a TileMapLayer while keeping its TileSet assignment.
// Idempotent on an empty layer. The handler lives in the bridge
// (POST /tools/godot_open_mcp_tilemap_clear); this file is the catalog metadata
// only.
//
// Adapted from Unity Open MCP's tilemap clear surface (adapt fidelity): same
// mutating-tool shape (paths_hint required + gate default enforce). Godot's
// TileMapLayer.Clear empties the cells and preserves the TileSet — the result
// echoes the retained tileset_path so an agent can confirm the contract held.
//
// This is a `tilemap` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const tilemapClear: Tool = {
  name: "godot_open_mcp_tilemap_clear",
  description:
    "Clear every cell on a Godot 4.3+ TileMapLayer while keeping its TileSet assignment. " +
    "Idempotent on an empty layer (returns cleared:true). The result echoes the retained " +
    "tileset_path so an agent can confirm the TileSet was preserved. Use tilemap_erase_cell to " +
    "remove a single cell. The scene is marked unsaved. This is a `tilemap` group tool — activate " +
    "the group with manage_tools first. Mutating: runs the gate cycle by default; paths_hint is " +
    "the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target TileMapLayer, relative to the edited scene root. Must " +
          "resolve to a TileMapLayer node (Godot 4.3+).",
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
