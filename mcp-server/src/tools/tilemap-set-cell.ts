// `godot_open_mcp_tilemap_set_cell` tool definition (P12.1).
//
// Paints one cell on a TileMapLayer using Godot's atlas addressing quadruple
// (map coords + source_id + atlas coords + alternative_tile). The handler lives
// in the bridge (POST /tools/godot_open_mcp_tilemap_set_cell); this file is the
// catalog metadata only.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/tilemap-set-tile.ts (adapt
// fidelity): same mutating-tool shape (paths_hint required + gate default
// enforce), but the addressing is Godot-native:
//   - Godot addresses a tile by the atlas quadruple (source_id + atlas coords +
//     alternative_tile) inside the layer's TileSet, NOT by a Unity TileBase
//     asset path.
//   - x / y are map cell coordinates (Vector2I); there is no z plane (Godot 2D
//     tilemaps are single-plane — Unity's z is dropped).
//   - The atlas quadruple defaults to (0, 0, 0, 0) so a single-source
//     single-tile TileSet can omit every optional field.
//
// This is a `tilemap` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const tilemapSetCell: Tool = {
  name: "godot_open_mcp_tilemap_set_cell",
  description:
    "Paint one cell on a Godot 4.3+ TileMapLayer using the atlas addressing quadruple (map " +
    "coords + source_id + atlas coords + alternative_tile). The layer must already have a TileSet " +
    "assigned (call tilemap_set_tileset first) — painting on a layer without a TileSet returns " +
    "tileset_required, and a source_id not present in the TileSet returns invalid_parameter. The " +
    "atlas quadruple (source_id, atlas_x, atlas_y, alternative_tile) defaults to (0, 0, 0, 0) so " +
    "a single-source single-tile TileSet can omit every optional field. The scene is marked " +
    "unsaved. This is a `tilemap` group tool — activate the group with manage_tools first. " +
    "Mutating: runs the gate cycle by default; paths_hint is the edited scene res:// path.",
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
        description: "Map cell x coordinate.",
      },
      y: {
        type: "integer",
        description: "Map cell y coordinate.",
      },
      source_id: {
        type: "integer",
        default: 0,
        description:
          "TileSet source id (default 0 — the first/only atlas source in a simple TileSet). " +
          "Must be a valid source id in the layer's TileSet.",
      },
      atlas_x: {
        type: "integer",
        default: 0,
        description: "Atlas x coordinate inside the source (default 0).",
      },
      atlas_y: {
        type: "integer",
        default: 0,
        description: "Atlas y coordinate inside the source (default 0).",
      },
      alternative_tile: {
        type: "integer",
        default: 0,
        description: "Alternative tile id inside the source (default 0 — the base alternative).",
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
