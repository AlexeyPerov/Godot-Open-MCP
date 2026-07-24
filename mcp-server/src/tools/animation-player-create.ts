// `godot_open_mcp_animation_player_create` tool definition (P12.4).
//
// Creates an AnimationPlayer node in the currently edited scene and returns its
// NodeData (same shape as node_create). AnimationPlayer is a single class (no 2D/3D
// split), so there is no dimension arg. The handler lives in the bridge
// (POST /tools/godot_open_mcp_animation_player_create); this file is the catalog
// metadata only.
//
// Adapted from Unity Open MCP's animation surface (adapt fidelity): same mutating-
// tool shape (paths_hint required + gate default enforce), but the Godot surface is
// a node, not an Animator component — create makes a node under a parent (default:
// edited scene root). No initial libraries/clips — use animation_library_add and
// animation_create afterwards.
//
// This is an `animation` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const animationPlayerCreate: Tool = {
  name: "godot_open_mcp_animation_player_create",
  description:
    "Create an AnimationPlayer node in the currently edited scene and return its NodeData (same " +
    "shape as node_create). An AnimationPlayer is the Godot equivalent of Unity's Animator + " +
    "AnimationClip container — it owns AnimationLibrary resources (named slots), each of which " +
    "owns Animation clips. After creating the player, call animation_library_add to add an empty " +
    "library, then animation_create to add a clip. The new node's owner is set to the edited " +
    "scene root so it persists in the .tscn on save; the scene is marked unsaved. AnimationPlayer " +
    "derives from Node (not Node2D/Node3D), so it has no spatial position — the optional position " +
    "arg is accepted for forward-compat but ignored. This is an `animation` group tool — activate " +
    "the group with manage_tools first. Mutating: runs the gate cycle by default; paths_hint is " +
    "the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      name: {
        type: "string",
        description:
          "Optional name for the new AnimationPlayer. When omitted, Godot assigns the default " +
          "name 'AnimationPlayer'.",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root. " +
          "Accepts 'Main', 'Main/Effects', '/root/Main/Effects', or '.' for the root itself " +
          "(same resolver as node_find / node_create). Defaults to the edited scene root.",
      },
      position: {
        type: "string",
        description:
          "Accepted for forward-compat but ignored — AnimationPlayer derives from Node (not " +
          "Node2D/Node3D) and has no spatial position.",
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
