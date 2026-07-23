// `godot_open_mcp_navigation_region_create` tool definition (P12.2).
//
// Creates a NavigationRegion2D or NavigationRegion3D node in the currently edited
// scene and returns its NodeData (same shape as node_create). The dimension arg
// selects the class. The handler lives in the bridge
// (POST /tools/godot_open_mcp_navigation_region_create); this file is the catalog
// metadata only.
//
// Adapted from Unity Open MCP's navigation-surface-add (adapt fidelity): same
// mutating-tool shape (paths_hint required + gate default enforce), but the Godot
// surface is a node, not a NavMeshSurface component — create makes a node under a
// parent (default: edited scene root), not a component attached to a GameObject.
// The navigation resource (NavigationPolygon / NavigationMesh) is assigned
// separately via navigation_region_set_mesh.
//
// This is a `navigation` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const navigationRegionCreate: Tool = {
  name: "godot_open_mcp_navigation_region_create",
  description:
    "Create a NavigationRegion2D (dimension '2d') or NavigationRegion3D (dimension '3d') node in " +
    "the currently edited scene and return its NodeData. A navigation region defines a navigable " +
    "area; assign its navigation resource (NavigationPolygon in 2D / NavigationMesh in 3D) with " +
    "navigation_region_set_mesh afterwards. The new node's owner is set to the edited scene root " +
    "so it persists in the .tscn on save; the scene is marked unsaved. This is a `navigation` " +
    "group tool — activate the group with manage_tools first. Mutating: runs the gate cycle by " +
    "default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["dimension", "paths_hint"],
    properties: {
      dimension: {
        type: "string",
        enum: ["2d", "3d"],
        description: "Which region class to create. '2d' → NavigationRegion2D; '3d' → NavigationRegion3D.",
      },
      name: {
        type: "string",
        description:
          "Optional name for the new region. When omitted, Godot assigns a default name " +
          "(e.g. 'NavigationRegion3D').",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root. " +
          "Accepts 'Main', 'Main/Player', '/root/Main/Player', or '.' for the root itself " +
          "(same resolver as node_find / node_create). Defaults to the edited scene root.",
      },
      position: {
        type: "string",
        description:
          "Optional position as 'x,y' (2D) or 'x,y,z' (3D). Applied because the region derives " +
          "from Node2D / Node3D; defaults to the origin.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the edited scene res:// path (e.g. res://levels/level_1.tscn). " +
          "Mandatory even when gate is 'off' (handler-level guard).",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → mutate → validate → delta; new " +
          "errors fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the " +
          "cycle (paths_hint is still required).",
      },
    },
    additionalProperties: false,
  },
};
