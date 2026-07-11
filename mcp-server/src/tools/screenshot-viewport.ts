// `godot_open_mcp_screenshot_viewport` tool definition (P4.8).
//
// Read-only (gate-free). Captures the active Godot editor 2D or 3D viewport and returns the PNG
// as an MCP image content block (image/png). The handler lives in the bridge
// (POST /tools/godot_open_mcp_screenshot_viewport); this file is the catalog metadata only —
// name / description / input schema — advertised to AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/capture-inline.ts (image-content-block
// semantics, copy fidelity) and the Godot-MCP reference's Tool_Screenshot.Viewport.cs (the
// EditorInterface.GetEditorViewport2D/3D behavior). Unity's screenshot tool writes a temp file
// and returns a path; the Godot tool returns the PNG inline as an image content block so an agent
// that does not read the filesystem still sees the image. The bridge image envelope
// (mediaType + base64 data + metadata) is unwrapped by live-client.ts into [{ type: "image" },
// { type: "text" metadata }] — the base64 payload never appears inside a text JSON block on
// success.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const screenshotViewport: Tool = {
  name: "godot_open_mcp_screenshot_viewport",
  description:
    "Capture the active Godot editor viewport as a PNG image (returned as an MCP image content " +
    "block, image/png). Read-only (gate-free) — no project files are written; the editor viewport " +
    "is read back in-memory. Pass mode '2d' to capture the 2D editor viewport or '3d' (default) " +
    "for the 3D editor viewport. The encoded dimensions are clamped to a 3840 px longest edge " +
    "(aspect preserved) and the result reports clamped:true when downscaling was applied. " +
    "Returns two content blocks: the image, then a short text block with width/height/byteLength/" +
    "mode metadata. Headless or no-GPU environments produce a structured error (render_unavailable " +
    "/ empty_image) rather than a blank image. Requires a live Godot Editor connection.",
  inputSchema: {
    type: "object",
    properties: {
      mode: {
        type: "string",
        enum: ["2d", "3d"],
        default: "3d",
        description:
          "Which editor viewport to capture. '3d' (default) = the 3D editor viewport, " +
          "'2d' = the 2D editor viewport.",
      },
    },
    additionalProperties: false,
  },
};
