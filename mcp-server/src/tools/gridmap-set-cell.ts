// `godot_open_mcp_gridmap_set_cell` tool definition.
//
// Paints one cell on a GridMap using Godot's 3D grid addressing (x, y, z) plus
// a MeshLibrary item id and an orientation (0–23 orthonormal rotations). The
// handler lives in the bridge (POST /tools/godot_open_mcp_gridmap_set_cell);
// this file is the catalog metadata only.
//
// Adapted from the tilemap pack's set_cell surface (adapt fidelity): same
// mutating-tool shape (paths_hint required + gate default enforce), but the
// addressing is Godot-native:
//   - x / y / z are 3D map cell coordinates (Vector3I) — GridMap is 3D, the
//     tilemap's 2D plane + atlas quadruple does not apply.
//   - `item` is a MeshLibrary item id (must exist in the GridMap's
//     MeshLibrary).
//   - `orientation` is a Godot orthonormal-rotation ordinal (0–23, default 0).
//
// This is a `gridmap` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const gridmapSetCell: Tool = {
  name: "godot_open_mcp_gridmap_set_cell",
  description:
    "Paint one cell on a Godot 4.3+ GridMap using the 3D grid coordinate (x, y, z) plus a " +
    "MeshLibrary item id and an orientation. The GridMap must already have a MeshLibrary assigned " +
    "(call gridmap_set_mesh_library first) — painting on a GridMap without a MeshLibrary returns " +
    "mesh_library_required, and an item id not present in the MeshLibrary returns " +
    "invalid_parameter. The orientation (0–23 Godot orthonormal rotations) defaults to 0. The " +
    "scene is marked unsaved. This is a `gridmap` group tool — activate the group with manage_tools " +
    "first. Mutating: runs the gate cycle by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "x", "y", "z", "item", "paths_hint"],
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
      item: {
        type: "integer",
        description:
          "MeshLibrary item id to place. Must be a valid item id in the GridMap's MeshLibrary " +
          "(read gridmap_get_used_cells / inspect the library to find the ids).",
      },
      orientation: {
        type: "integer",
        default: 0,
        description:
          "Godot orthonormal-rotation ordinal (0–23, default 0). 24 rotations covering the " +
          "axis-aligned orientations of the placed mesh.",
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
