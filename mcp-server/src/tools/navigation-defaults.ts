// `godot_open_mcp_navigation_defaults` tool definition (P12.2).
//
// First tool of the navigation domain pack. A pure helper — returns recommended
// starter scalars for a 2D or 3D NavigationAgent as a JSON object an agent can
// spread into navigation_agent_create / navigation_agent_configure. No scene
// required; no gate surface. The handler lives in the bridge
// (POST /tools/godot_open_mcp_navigation_defaults); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Adapted from the Unity Open MCP navigation_modify embedded-defaults pattern
// (adapt fidelity): same read-only helper shape (no paths_hint, no gate), but
// surfaced as its own tool because the Godot catalog lists it explicitly —
// agents rely on a single-call defaults probe rather than parsing prose.
//
// This is a `navigation` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "navigation" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const navigationDefaults: Tool = {
  name: "godot_open_mcp_navigation_defaults",
  description:
    "Return the recommended starter scalars (radius, height, max_speed, path_desired_distance, " +
    "target_desired_distance, avoidance_enabled) for a 2D or 3D NavigationAgent. Pure helper — " +
    "no scene required, no mutation. The 2D defaults are in pixels (radius 10, max_speed 200, " +
    "distances 20); the 3D defaults are in meters (radius 0.5, height 1.8, max_speed 5, distances " +
    "1). Spread the result into navigation_agent_configure, or use the values as initial guidance " +
    "when calling navigation_agent_create. This is a `navigation` group tool — activate the group " +
    "with manage_tools first. Read-only.",
  inputSchema: {
    type: "object",
    required: ["dimension"],
    properties: {
      dimension: {
        type: "string",
        enum: ["2d", "3d"],
        description:
          "Which agent family to return defaults for. '2d' selects NavigationAgent2D (pixel " +
          "units); '3d' selects NavigationAgent3D (meter units).",
      },
    },
    additionalProperties: false,
  },
};
