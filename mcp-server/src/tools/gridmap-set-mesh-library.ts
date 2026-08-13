// `godot_open_mcp_gridmap_set_mesh_library` tool definition.
//
// Assigns an existing Godot MeshLibrary resource to a GridMap. No MeshLibrary
// authoring — agents point this at an existing .tres/.res MeshLibrary (use
// resource_create to build one if needed). The handler lives in the bridge
// (POST /tools/godot_open_mcp_gridmap_set_mesh_library); this file is the
// catalog metadata only.
//
// Adapted from the tilemap pack's set_tileset surface (adapt fidelity): same
// mutating-tool shape (paths_hint required + gate default enforce), but Godot
// addresses a MeshLibrary as a res:// resource path and assigns it to the
// GridMap's MeshLibrary property directly.
//
// This is a `gridmap` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const gridmapSetMeshLibrary: Tool = {
  name: "godot_open_mcp_gridmap_set_mesh_library",
  description:
    "Assign an existing Godot MeshLibrary resource to a GridMap. The mesh_library_path must be a " +
    "res:// path to an existing .tres/.res MeshLibrary (use resource_create to build one first if " +
    "needed — no MeshLibrary authoring is bundled with this tool). After assignment the GridMap can " +
    "be painted with gridmap_set_cell. The scene is marked unsaved. This is a `gridmap` group tool " +
    "— activate the group with manage_tools first. Mutating: runs the gate cycle by default; " +
    "paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "mesh_library_path", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target GridMap, relative to the edited scene root " +
          "('Main/Grid', '/root/Main/Grid', or '.' for the root). Must resolve to a GridMap node " +
          "(Godot 4.3+).",
      },
      mesh_library_path: {
        type: "string",
        description:
          "res:// path (or uid://) to an existing MeshLibrary resource (.tres/.res). Must exist " +
          "and be a MeshLibrary — a non-MeshLibrary resource at the path returns " +
          "resource_load_failed.",
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
