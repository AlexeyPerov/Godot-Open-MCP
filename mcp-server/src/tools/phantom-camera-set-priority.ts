// `godot_open_mcp_phantom_camera_set_priority` tool definition.
//
// Sets the phantom-camera addon's priority integer — higher wins, so raising it
// switches the active camera. The handler lives in the bridge
// (POST /tools/godot_open_mcp_phantom_camera_set_priority); this file is the
// catalog metadata only.
//
// Greenfield fidelity — the addon is GDScript; the C# bridge operates via
// duck-typed property Set. Mutating-tool shape (paths_hint required + gate
// default enforce) copied from the Phase 12 domain packs.
//
// This is a `phantom_camera` group tool — activate the group with manage_tools
// first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const phantomCameraSetPriority: Tool = {
  name: "godot_open_mcp_phantom_camera_set_priority",
  description:
    "Set the phantom-camera addon's priority integer on a virtual camera. Higher priority wins, so " +
    "raising it switches the active camera (the addon's PhantomCameraHost picks the highest-" +
    "priority PhantomCamera). Requires the phantom-camera addon enabled (returns addon_not_found " +
    "otherwise). The scene is marked unsaved. This is a `phantom_camera` group tool — activate the " +
    "group with manage_tools first. Mutating: runs the gate cycle by default; paths_hint is the " +
    "edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "priority", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target PhantomCamera2D / PhantomCamera3D, relative to the " +
          "edited scene root. Must resolve to an addon virtual-camera node.",
      },
      priority: {
        type: "integer",
        description:
          "Priority integer (higher wins). Raising it above the currently-active PhantomCamera " +
          "switches the active camera.",
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
