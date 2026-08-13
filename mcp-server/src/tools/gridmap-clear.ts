// `godot_open_mcp_gridmap_clear` tool definition.
//
// Clears every cell on a GridMap while keeping its MeshLibrary assignment.
// Idempotent on an empty grid. The handler lives in the bridge
// (POST /tools/godot_open_mcp_gridmap_clear); this file is the catalog metadata
// only.
//
// Adapted from the tilemap pack's clear surface (adapt fidelity): same
// mutating-tool shape (paths_hint required + gate default enforce). Godot's
// GridMap.Clear empties the cells and preserves the MeshLibrary — the result
// echoes the retained mesh_library_path so an agent can confirm the contract
// held.
//
// This is a `gridmap` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const gridmapClear: Tool = {
  name: "godot_open_mcp_gridmap_clear",
  description:
    "Clear every cell on a Godot 4.3+ GridMap while keeping its MeshLibrary assignment. Idempotent " +
    "on an empty grid (returns cleared:true). The result echoes the retained mesh_library_path so " +
    "an agent can confirm the MeshLibrary was preserved. Use gridmap_erase_cell to remove a single " +
    "cell. The scene is marked unsaved. This is a `gridmap` group tool — activate the group with " +
    "manage_tools first. Mutating: runs the gate cycle by default; paths_hint is the edited scene " +
    "res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target GridMap, relative to the edited scene root. Must " +
          "resolve to a GridMap node (Godot 4.3+).",
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
