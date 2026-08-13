// `godot_open_mcp_gridmap_get_used_cells` tool definition.
//
// Lists the used cells on a GridMap, bounded by max_results. Read-only
// (gate-free). The handler lives in the bridge
// (POST /tools/godot_open_mcp_gridmap_get_used_cells); this file is the catalog
// metadata only.
//
// Adapted from the tilemap pack's get_used_cells surface (adapt fidelity): same
// bounded-list contract (max_results + truncated), but Godot's
// GridMap.GetUsedCells returns 3D coords (Vector3I) and the handler reads back
// the item id + orientation per cell so an agent can echo it back into
// gridmap_set_cell.
//
// This is a `gridmap` group tool — activate the group with manage_tools first.
// Read-only — no paths_hint, no gate.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const gridmapGetUsedCells: Tool = {
  name: "godot_open_mcp_gridmap_get_used_cells",
  description:
    "List the used cells on a Godot 4.3+ GridMap, bounded by max_results. Each cell carries the " +
    "3D coordinate (x, y, z) plus its MeshLibrary item id and orientation so an agent can echo it " +
    "back into gridmap_set_cell. The remainder beyond max_results is reported in truncated. " +
    "Read-only (gate-free). This is a `gridmap` group tool — activate the group with manage_tools " +
    "first.",
  inputSchema: {
    type: "object",
    required: ["node_path"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target GridMap, relative to the edited scene root. Must " +
          "resolve to a GridMap node (Godot 4.3+).",
      },
      max_results: {
        type: "integer",
        default: 256,
        maximum: 2000,
        description:
          "Max cells returned (default 256, hard cap 2000). The remainder is reported in " +
          "'truncated' so an agent knows whether to page. A non-positive value falls back to the " +
          "default (matches the bridge's EffectiveMaxResults fallback).",
      },
    },
    additionalProperties: false,
  },
};
