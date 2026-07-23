// `godot_open_mcp_navigation_link_create` tool definition (P12.2).
//
// Creates a NavigationLink2D or NavigationLink3D node — an off-mesh connection
// between two points (a jump pad, a ladder, a teleport). The dimension arg
// selects the class. The handler lives in the bridge
// (POST /tools/godot_open_mcp_navigation_link_create); this file is the catalog
// metadata only.
//
// Adapted from Unity Open MCP's navigation-link-add (adapt fidelity): same
// mutating-tool shape (paths_hint required + gate default enforce), but the Godot
// link is a node with local-space start/end positions, not an off-mesh-link
// component with world-space endpoints. Set start_position / end_position (local
// to the link node) and the optional bidirectional flag.
//
// This is a `navigation` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const navigationLinkCreate: Tool = {
  name: "godot_open_mcp_navigation_link_create",
  description:
    "Create a NavigationLink2D (dimension '2d') or NavigationLink3D (dimension '3d') node — an " +
    "off-mesh connection between two points (a jump pad, a ladder, a teleport). start_position " +
    "and end_position are LOCAL to the link node (Godot exposes them in local space). Set " +
    "bidirectional true to allow traversal both ways. The new node's owner is set to the edited " +
    "scene root so it persists in the .tscn on save; the scene is marked unsaved. This is a " +
    "`navigation` group tool — activate the group with manage_tools first. Mutating: runs the " +
    "gate cycle by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["dimension", "start_position", "end_position", "paths_hint"],
    properties: {
      dimension: {
        type: "string",
        enum: ["2d", "3d"],
        description: "Which link class to create. '2d' → NavigationLink2D; '3d' → NavigationLink3D.",
      },
      name: {
        type: "string",
        description:
          "Optional name for the new link. When omitted, Godot assigns a default name " +
          "(e.g. 'NavigationLink3D').",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root " +
          "(same resolver as node_find / node_create). Defaults to the edited scene root.",
      },
      position: {
        type: "string",
        description:
          "Optional node position as 'x,y' (2D) or 'x,y,z' (3D), in the parent's local space. " +
          "The link's start/end positions are relative to this node position.",
      },
      start_position: {
        type: "string",
        description:
          "Start point of the connection, LOCAL to the link node, as 'x,y' (2D) or 'x,y,z' (3D).",
      },
      end_position: {
        type: "string",
        description:
          "End point of the connection, LOCAL to the link node, as 'x,y' (2D) or 'x,y,z' (3D).",
      },
      bidirectional: {
        type: "boolean",
        default: false,
        description:
          "When true, the link can be traversed in both directions (start → end and end → start). " +
          "Defaults to false (start → end only).",
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
        description:
          "Gate mode. 'enforce' (default): run checkpoint → mutate → validate → delta; new " +
          "errors fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the " +
          "cycle (paths_hint is still required).",
      },
    },
    additionalProperties: false,
  },
};
