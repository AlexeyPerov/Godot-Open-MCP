// `godot_open_mcp_navigation_region_set_mesh` tool definition (P12.2).
//
// Assigns an existing navigation resource to a region. The resource type must
// match the region dimension: NavigationPolygon for a 2D region, NavigationMesh
// for a 3D region. The handler lives in the bridge
// (POST /tools/godot_open_mcp_navigation_region_set_mesh); this file is the
// catalog metadata only.
//
// Adapted from Unity Open MCP's navigation-set-bake-settings surface (adapt
// fidelity): same mutating-tool shape (paths_hint required + gate default
// enforce), but Godot addresses a navigation resource as a res:// resource path
// and assigns it to the region's NavigationPolygon / NavigationMesh property
// directly. No baking in v1 — agents supply a pre-authored resource (use the
// editor's bake UI or a resource_create workflow to author one).
//
// This is a `navigation` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const navigationRegionSetMesh: Tool = {
  name: "godot_open_mcp_navigation_region_set_mesh",
  description:
    "Assign an existing navigation resource to a NavigationRegion2D or NavigationRegion3D. The " +
    "mesh_path must be a res:// path to an existing NavigationPolygon (for a 2D region) or " +
    "NavigationMesh (for a 3D region) — a type mismatch returns resource_load_failed. No baking " +
    "is bundled with this tool; supply a pre-authored resource (use the editor's bake UI or a " +
    "resource_create workflow to build one first). The scene is marked unsaved. This is a " +
    "`navigation` group tool — activate the group with manage_tools first. Mutating: runs the " +
    "gate cycle by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "mesh_path", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target NavigationRegion2D or NavigationRegion3D, relative to " +
          "the edited scene root ('Main/NavRegion', '/root/Main/NavRegion', or '.' for the root). " +
          "A node of any other type returns wrong_node_type.",
      },
      mesh_path: {
        type: "string",
        description:
          "res:// (or uid://) path to an existing navigation resource. Must be a NavigationPolygon " +
          "for a 2D region or a NavigationMesh for a 3D region; a mismatch returns " +
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
