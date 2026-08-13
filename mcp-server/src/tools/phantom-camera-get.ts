// `godot_open_mcp_phantom_camera_get` tool definition.
//
// Reads the scalar configuration of a PhantomCamera — priority, follow_mode,
// follow_target (as a scene path), look_at_mode, look_at_target (as a scene
// path), plus the resolved type. Read-only (gate-free). The handler lives in
// the bridge (POST /tools/godot_open_mcp_phantom_camera_get); this file is the
// catalog metadata only.
//
// Greenfield fidelity — the addon is GDScript; the C# bridge reads properties
// via duck-typed GodotObject.Get (a missing property on an older addon version
// degrades to 0 / null rather than crashing). Read-only analog of the csg_get
// config-dump pattern.
//
// This is a `phantom_camera` group tool — activate the group with manage_tools
// first. Read-only — no paths_hint, no gate.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const phantomCameraGet: Tool = {
  name: "godot_open_mcp_phantom_camera_get",
  description:
    "Read the scalar configuration of a phantom-camera addon virtual camera — priority, " +
    "follow_mode, follow_target (as a scene path), look_at_mode, look_at_target (as a scene path), " +
    "plus the resolved type (PhantomCamera2D / PhantomCamera3D). Read-only (gate-free). Requires " +
    "the phantom-camera addon enabled (returns addon_not_found otherwise). This is a " +
    "`phantom_camera` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["node_path"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target PhantomCamera2D / PhantomCamera3D, relative to the " +
          "edited scene root. Must resolve to an addon virtual-camera node.",
      },
    },
    additionalProperties: false,
  },
};
