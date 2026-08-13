// `godot_open_mcp_phantom_camera_set_target` tool definition.
//
// Sets the phantom-camera addon's follow_target property to a resolved scene
// node — the "which node should this camera track" knob. The handler lives in
// the bridge (POST /tools/godot_open_mcp_phantom_camera_set_target); this file
// is the catalog metadata only.
//
// Greenfield fidelity — the addon is GDScript; the C# bridge operates via
// duck-typed property Set. Mutating-tool shape (paths_hint required + gate
// default enforce) copied from the Phase 12 domain packs.
//
// This is a `phantom_camera` group tool — activate the group with manage_tools
// first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const phantomCameraSetTarget: Tool = {
  name: "godot_open_mcp_phantom_camera_set_target",
  description:
    "Set the phantom-camera addon's follow_target property to a resolved scene node (the node this " +
    "camera should track). Use phantom_camera_set_follow for the follow MODE and " +
    "phantom_camera_set_look_at for the look-at target. Requires the phantom-camera addon enabled " +
    "(returns addon_not_found otherwise). The scene is marked unsaved. This is a `phantom_camera` " +
    "group tool — activate the group with manage_tools first. Mutating: runs the gate cycle by " +
    "default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "target_node_path", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target PhantomCamera2D / PhantomCamera3D, relative to the " +
          "edited scene root ('Main/Cam', '/root/Main/Cam', or '.' for the root). Must resolve to " +
          "an addon virtual-camera node.",
      },
      target_node_path: {
        type: "string",
        description:
          "Scene-tree path of the node the camera should follow / track, relative to the edited " +
          "scene root. Must resolve to an existing node.",
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
