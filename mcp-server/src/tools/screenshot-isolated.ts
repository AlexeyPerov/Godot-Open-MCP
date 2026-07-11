// `godot_open_mcp_screenshot_isolated` tool definition (P4.8).
//
// Read-only (gate-free). Renders a Node3D alone in an isolated world from one of six orthographic
// directions, returning the PNG as an MCP image content block (image/png). The handler lives in
// the bridge (POST /tools/godot_open_mcp_screenshot_isolated); this file is the catalog metadata
// only.
//
// Adapted from Unity Open MCP's screenshot.ts isolated view (image-content + background concept,
// copy fidelity) and the Godot-MCP reference's Tool_Screenshot.Isolated.cs (the OwnWorld3D +
// duplicate-without-scripts + AABB framing behavior). Unity's isolated view produces a 2x2
// composite (Front/Right/Back/Top); the Godot tool renders ONE view per call (camera_view) at a
// square resolution, matching the Godot-MCP reference. The target's scripts are NOT duplicated so
// _EnterTree/_Ready side effects never run — the capture is a static visual snapshot. The bridge
// image envelope is unwrapped by live-client.ts into [{ type: "image" }, { type: "text" metadata
// incl. computed bounds }].

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const screenshotIsolated: Tool = {
  name: "godot_open_mcp_screenshot_isolated",
  description:
    "Render a Node3D in an isolated world from one of six orthographic directions and return the " +
    "PNG as an MCP image content block (image/png). Read-only (gate-free) — the target is " +
    "duplicated WITHOUT its scripts (so _EnterTree/_Ready side effects never run) into a fresh " +
    "SubViewport with its own World3D, framed by a transient camera computed from the combined " +
    "AABB of the target's VisualInstance3D descendants. An empty/degenerate geometry falls back " +
    "to a 0.1-unit bounds box (reported via usedFallbackBounds in the metadata). Background is " +
    "either a solid color (#RRGGBB, default #404040, via a transient WorldEnvironment) or " +
    "transparent (alpha-clear). The metadata block includes the computed bounds (center, size, " +
    "radius), camera distance, and the final clip planes. Requires a live Godot Editor connection " +
    "with a GPU; headless environments produce render_unavailable.",
  inputSchema: {
    type: "object",
    properties: {
      node_ref: {
        type: "object",
        description:
          "Reference to the Node3D to capture in isolation. Provide either instance_id " +
          "(Godot instance id, priority 1) or node_path (scene-tree path, priority 2).",
        properties: {
          instance_id: {
            type: "integer",
            description: "Godot instance id of the target Node3D (priority 1 when non-zero).",
          },
          node_path: {
            type: "string",
            description:
              "Scene-tree path of the target Node3D, relative to the edited scene root " +
              "(e.g. 'Player/Body'). Priority 2.",
          },
        },
        additionalProperties: false,
      },
      camera_view: {
        type: "string",
        enum: ["front", "back", "left", "right", "top", "bottom"],
        default: "front",
        description:
          "Direction to view the target from. 'front' (default) looks along -Z, 'back' along +Z, " +
          "'left' along -X, 'right' along +X, 'top' along +Y (down at the XZ plane), 'bottom' " +
          "along -Y.",
      },
      background: {
        type: "string",
        enum: ["solid_color", "transparent"],
        default: "solid_color",
        description:
          "Background mode. 'solid_color' (default) uses background_color via a transient " +
          "WorldEnvironment; 'transparent' produces an alpha-clear background.",
      },
      background_color: {
        type: "string",
        default: "#404040",
        description:
          "Hex background color (#RGB / #RRGGBB / #RRGGBBAA) for the 'solid_color' background. " +
          "Default '#404040'. Ignored when background is 'transparent'.",
      },
      field_of_view: {
        type: "number",
        default: 60,
        minimum: 1,
        maximum: 179,
        description: "Perspective field of view in degrees (default 60, range [1, 179]).",
      },
      near_clip_plane: {
        type: "number",
        default: 0.05,
        description:
          "Near clip plane in meters (default 0.05). Loosened (moved closer) by the framing math " +
          "to guarantee the target is inside the clip range.",
      },
      far_clip_plane: {
        type: "number",
        default: 4000,
        description:
          "Far clip plane in meters (default 4000). Loosened (moved farther) by the framing math " +
          "to guarantee the target is inside the clip range.",
      },
      padding: {
        type: "number",
        default: 1.2,
        minimum: 0.01,
        maximum: 100,
        description:
          "Framing padding multiplier (default 1.2). Larger values zoom out so the target has " +
          "more empty space around it.",
      },
      resolution: {
        type: "integer",
        default: 512,
        minimum: 1,
        description:
          "Square output resolution in pixels (default 512). The SubViewport is resolution x " +
          "resolution; clamped to a 3840 px longest edge.",
      },
    },
    additionalProperties: false,
  },
};
