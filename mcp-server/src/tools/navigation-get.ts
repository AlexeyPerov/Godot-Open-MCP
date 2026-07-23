// `godot_open_mcp_navigation_get` tool definition (P12.2).
//
// Reads the scalar configuration of any navigation node (region / agent / link,
// 2D or 3D). Read-only (gate-free). The handler lives in the bridge
// (POST /tools/godot_open_mcp_navigation_get); this file is the catalog metadata
// only.
//
// The read-only analog in Unity Open MCP is navigation-get (adapt fidelity): same
// bounded-inspect contract, but the Godot result includes the resolved type and
// dimension so an agent does not need a second probe to know which property set
// applies. Regions report their navigation resource path only (no vertex arrays
// in v1); agents report the full scalar set; links report their start/end +
// bidirectional flag.
//
// This is a `navigation` group tool — activate the group with manage_tools first.
// Read-only — no paths_hint, no gate.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const navigationGet: Tool = {
  name: "godot_open_mcp_navigation_get",
  description:
    "Read the scalar configuration of any navigation node — a NavigationRegion2D/3D, " +
    "NavigationAgent2D/3D, or NavigationLink2D/3D. Returns the resolved type and dimension so an " +
    "agent does not need a second probe to know which property set applies. Regions report their " +
    "navigation resource path (or null when unassigned); agents report the full scalar set " +
    "(radius, height, max_speed, distances, avoidance_enabled); links report their start/end " +
    "positions and bidirectional flag. Read-only (gate-free). This is a `navigation` group tool " +
    "— activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["node_path"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target navigation node, relative to the edited scene root. " +
          "Must resolve to a NavigationRegion2D/3D, NavigationAgent2D/3D, or NavigationLink2D/3D; " +
          "a different node type returns wrong_node_type.",
      },
    },
    additionalProperties: false,
  },
};
