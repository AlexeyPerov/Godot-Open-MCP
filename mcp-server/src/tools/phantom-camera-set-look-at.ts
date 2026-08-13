// `godot_open_mcp_phantom_camera_set_look_at` tool definition.
//
// Sets the phantom-camera addon's look_at_target (required) and, when supplied,
// the look_at_mode ordinal. The handler lives in the bridge
// (POST /tools/godot_open_mcp_phantom_camera_set_look_at); this file is the
// catalog metadata only.
//
// Greenfield fidelity — the addon is GDScript; the C# bridge operates via
// duck-typed property Set. The look_at_mode ordinal is the addon's LookAtMode
// enum, passed straight through (the addon is the authority). It is optional —
// when omitted only the target is set and the mode is left unchanged (absent is
// distinguished from an explicit 0). Mutating-tool shape (paths_hint required +
// gate default enforce) copied from the Phase 12 domain packs.
//
// This is a `phantom_camera` group tool — activate the group with manage_tools
// first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const phantomCameraSetLookAt: Tool = {
  name: "godot_open_mcp_phantom_camera_set_look_at",
  description:
    "Set the phantom-camera addon's look_at_target to a resolved scene node (the node this camera " +
    "should look at), and optionally set the look_at_mode ordinal at the same time. look_at_mode is " +
    "the addon's LookAtMode enum ordinal: 0 none, 1 mimic, 2 simple, 3 group. Omit look_at_mode to " +
    "change only the target (the current mode is left unchanged). Requires the phantom-camera addon " +
    "enabled (returns addon_not_found otherwise). The scene is marked unsaved. This is a " +
    "`phantom_camera` group tool — activate the group with manage_tools first. Mutating: runs the " +
    "gate cycle by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "target_node_path", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target PhantomCamera2D / PhantomCamera3D, relative to the " +
          "edited scene root. Must resolve to an addon virtual-camera node.",
      },
      target_node_path: {
        type: "string",
        description:
          "Scene-tree path of the node the camera should look at, relative to the edited scene " +
          "root. Must resolve to an existing node.",
      },
      look_at_mode: {
        type: "integer",
        description:
          "Optional addon LookAtMode ordinal: 0 none, 1 mimic, 2 simple, 3 group. When omitted " +
          "the mode is left unchanged (only the target is set); an explicit 0 sets the mode to " +
          "none. Passed straight through to the addon.",
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
