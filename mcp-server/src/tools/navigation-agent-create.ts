// `godot_open_mcp_navigation_agent_create` tool definition (P12.2).
//
// Creates a NavigationAgent2D or NavigationAgent3D node in the currently edited
// scene and returns its NodeData. The dimension arg selects the class. The
// handler lives in the bridge
// (POST /tools/godot_open_mcp_navigation_agent_create); this file is the catalog
// metadata only.
//
// Adapted from Unity Open MCP's navigation-agent-add (adapt fidelity): same
// mutating-tool shape (paths_hint required + gate default enforce), but the Godot
// agent is a node parented under the moving body it steers (a CharacterBody2D/3D
// or RigidBody), not a NavMeshAgent component attached to a GameObject. Tune its
// scalars afterwards with navigation_agent_configure (or seed them via
// navigation_defaults).
//
// This is a `navigation` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const navigationAgentCreate: Tool = {
  name: "godot_open_mcp_navigation_agent_create",
  description:
    "Create a NavigationAgent2D (dimension '2d') or NavigationAgent3D (dimension '3d') node in " +
    "the currently edited scene and return its NodeData. A navigation agent provides pathfinding " +
    "and avoidance for a moving body — parent it under the CharacterBody2D/3D or RigidBody it " +
    "steers (set the agent's target_position from the parent's script each frame). The new node's " +
    "owner is set to the edited scene root so it persists in the .tscn on save; the scene is " +
    "marked unsaved. Tune the agent's scalars (radius, max_speed, distances, avoidance) with " +
    "navigation_agent_configure afterwards, or consult navigation_defaults for starter values. " +
    "This is a `navigation` group tool — activate the group with manage_tools first. Mutating: " +
    "runs the gate cycle by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["dimension", "paths_hint"],
    properties: {
      dimension: {
        type: "string",
        enum: ["2d", "3d"],
        description: "Which agent class to create. '2d' → NavigationAgent2D; '3d' → NavigationAgent3D.",
      },
      name: {
        type: "string",
        description:
          "Optional name for the new agent. When omitted, Godot assigns a default name " +
          "(e.g. 'NavigationAgent3D').",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root. " +
          "Parent the agent under the body it steers (a CharacterBody2D/3D or RigidBody) so the " +
          "agent follows the body's transform. Defaults to the edited scene root.",
      },
      position: {
        type: "string",
        description:
          "Optional position as 'x,y' (2D) or 'x,y,z' (3D), in the parent's local space. Usually " +
          "left at the origin — the agent tracks its parent body, not a world position.",
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
