// `godot_open_mcp_tilemap_get_used_cells` tool definition (P12.1).
//
// Lists the used cells on a TileMapLayer, bounded by max_results. Read-only
// (gate-free). The handler lives in the bridge
// (POST /tools/godot_open_mcp_tilemap_get_used_cells); this file is the catalog
// metadata only.
//
// The read-only analog in Unity Open MCP is the implicit get-tiles surface
// (adapt fidelity): same bounded-list contract (max_results + truncated) as
// node_find / reflection_method_find, but Godot's TileMapLayer.GetUsedCells
// returns the map coords and the handler reads back the full addressing
// quadruple (source_id + atlas coords + alternative_tile) per cell so an agent
// can echo it back into tilemap_set_cell.
//
// This is a `tilemap` group tool — activate the group with manage_tools first.
// Read-only — no paths_hint, no gate.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const tilemapGetUsedCells: Tool = {
  name: "godot_open_mcp_tilemap_get_used_cells",
  description:
    "List the used cells on a Godot 4.3+ TileMapLayer, bounded by max_results. Each cell carries " +
    "the full addressing quadruple (x, y, sourceId, atlasX, atlasY, alternativeTile) so an agent " +
    "can echo it back into tilemap_set_cell. The remainder beyond max_results is reported in " +
    "truncated. Read-only (gate-free). This is a `tilemap` group tool — activate the group with " +
    "manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["node_path"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target TileMapLayer, relative to the edited scene root. Must " +
          "resolve to a TileMapLayer node (Godot 4.3+).",
      },
      max_results: {
        type: "integer",
        default: 256,
        minimum: 1,
        description:
          "Max cells returned (default 256, hard cap 2000). The remainder is reported in " +
          "'truncated' so an agent knows whether to page. A non-positive value falls back to the " +
          "default.",
      },
    },
    additionalProperties: false,
  },
};
