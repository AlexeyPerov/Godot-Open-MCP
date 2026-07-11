// `godot_open_mcp_screenshot_camera` tool definition (P4.8).
//
// Read-only (gate-free). Renders an off-screen capture from a Camera2D or Camera3D in the edited
// scene, returning the PNG as an MCP image content block (image/png). The handler lives in the
// bridge (POST /tools/godot_open_mcp_screenshot_camera); this file is the catalog metadata only.
//
// Adapted from Unity Open MCP's screenshot-camera.ts (free-camera / off-screen render concept,
// copy fidelity for the dimension bounds) and the Godot-MCP reference's Tool_Screenshot.Camera.cs
// (the SubViewport + World3D/World2D-sharing behavior). Unity's screenshot-camera takes an
// arbitrary world pose; the Godot tool targets an EXISTING Camera2D/Camera3D node in the scene —
// it shares the camera's world and clones its projection/transform into a transient SubViewport
// so the live scene camera is never moved. The bridge image envelope (mediaType + base64 data +
// metadata) is unwrapped by live-client.ts into [{ type: "image" }, { type: "text" metadata }].

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const screenshotCamera: Tool = {
  name: "godot_open_mcp_screenshot_camera",
  description:
    "Render an off-screen capture from a Camera2D or Camera3D node in the edited scene and return " +
    "it as an MCP image content block (image/png). Read-only (gate-free) — the source camera is " +
    "never moved; a transient SubViewport shares the camera's world (World3D/World2D) and clones " +
    "its transform/projection so the off-screen render sees the same content. Width/height default " +
    "to 1920x1080 and are clamped to a 3840 px longest edge (aspect preserved; clamped:true on " +
    "downscale). The target must be a Camera2D or Camera3D (node_ref with node_path or " +
    "instance_id). Returns two content blocks: the image, then a short text metadata block. " +
    "Headless or no-GPU environments produce a structured error (render_unavailable). " +
    "Requires a live Godot Editor connection.",
  inputSchema: {
    type: "object",
    properties: {
      node_ref: {
        type: "object",
        description:
          "Reference to the Camera2D or Camera3D node to capture from. Provide either " +
          "instance_id (Godot instance id, priority 1) or node_path (scene-tree path like " +
          "'Main/Camera3D', priority 2).",
        properties: {
          instance_id: {
            type: "integer",
            description: "Godot instance id of the camera node (priority 1 when non-zero).",
          },
          node_path: {
            type: "string",
            description:
              "Scene-tree path of the camera node, relative to the edited scene root " +
              "(e.g. 'Main/Camera3D'). Priority 2.",
          },
        },
        additionalProperties: false,
      },
      width: {
        type: "integer",
        default: 1920,
        minimum: 1,
        description: "Capture width in pixels (default 1920). Clamped to a 3840 px longest edge.",
      },
      height: {
        type: "integer",
        default: 1080,
        minimum: 1,
        description: "Capture height in pixels (default 1080). Clamped to a 3840 px longest edge.",
      },
    },
    additionalProperties: false,
  },
};
