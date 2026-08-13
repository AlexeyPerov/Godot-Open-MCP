// `godot_open_mcp_phantom_camera_set_follow` tool definition.
//
// Sets the phantom-camera addon's follow_mode ordinal (required) and, when
// supplied, the follow target. The handler lives in the bridge
// (POST /tools/godot_open_mcp_phantom_camera_set_follow); this file is the
// catalog metadata only.
//
// Greenfield fidelity — the addon is GDScript; the C# bridge operates via
// duck-typed property Set. The follow_mode ordinal is the addon's FollowMode
// enum, passed straight through (the addon is the authority). Mutating-tool
// shape (paths_hint required + gate default enforce) copied from the Phase 12
// domain packs.
//
// This is a `phantom_camera` group tool — activate the group with manage_tools
// first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const phantomCameraSetFollow: Tool = {
  name: "godot_open_mcp_phantom_camera_set_follow",
  description:
    "Set the phantom-camera addon's follow_mode ordinal on a virtual camera, and optionally set the " +
    "follow target at the same time (for just the target node use phantom_camera_set_target). " +
    "follow_mode is the addon's FollowMode enum ordinal: 0 none, 1 glued, 2 simple_follow, 3 " +
    "group_follow, 4 path_follow, 5 framed, 6 third_person. Requires the phantom-camera addon " +
    "enabled (returns addon_not_found otherwise). The scene is marked unsaved. This is a " +
    "`phantom_camera` group tool — activate the group with manage_tools first. Mutating: runs the " +
    "gate cycle by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "follow_mode", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target PhantomCamera2D / PhantomCamera3D, relative to the " +
          "edited scene root. Must resolve to an addon virtual-camera node.",
      },
      follow_mode: {
        type: "integer",
        description:
          "Addon FollowMode ordinal: 0 none, 1 glued, 2 simple_follow, 3 group_follow, 4 " +
          "path_follow, 5 framed, 6 third_person. Passed straight through to the addon.",
      },
      target_node_path: {
        type: "string",
        description:
          "Optional follow target node path, relative to the edited scene root. When supplied, " +
          "the addon's follow_target is set BEFORE the mode (so a mode that needs a target does " +
          "not reject it). Omit to change only the mode.",
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
