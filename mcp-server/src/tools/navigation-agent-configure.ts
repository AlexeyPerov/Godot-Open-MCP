// `godot_open_mcp_navigation_agent_configure` tool definition (P12.2).
//
// Patches clamped scalar properties on a NavigationAgent2D or NavigationAgent3D.
// Each scalar is applied independently (non-aborting). The handler lives in the
// bridge (POST /tools/godot_open_mcp_navigation_agent_configure); this file is
// the catalog metadata only.
//
// Adapted from Unity Open MCP's navigation-modify (adapt fidelity): same
// mutating-tool shape (paths_hint required + gate default enforce) and the same
// flat-known-fields style used by node_modify. The Godot delta: the same property
// names (radius / height / max_speed / path_desired_distance /
// target_desired_distance / avoidance_enabled) apply to BOTH the 2D and 3D agent
// classes; the handler clamps each scalar to its valid range (radius > 0,
// height >= 0, max_speed >= 0, distances > 0) and echoes the clamped values.
//
// This is a `navigation` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const navigationAgentConfigure: Tool = {
  name: "godot_open_mcp_navigation_agent_configure",
  description:
    "Patch clamped scalar properties on a NavigationAgent2D or NavigationAgent3D. Only the " +
    "fields you send are applied — omitted scalars are left unchanged (the result echoes every " +
    "applied value, clamped to its valid range). radius and the distances are clamped to strictly " +
    "positive; height and max_speed are clamped to non-negative. A non-numeric value is silently " +
    "skipped (non-aborting — a bad value on one key does not skip the rest). Use " +
    "navigation_defaults for starter values, then navigation_get to read back the full config. " +
    "The scene is marked unsaved. This is a `navigation` group tool — activate the group with " +
    "manage_tools first. Mutating: runs the gate cycle by default; paths_hint is the edited scene " +
    "res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target NavigationAgent2D or NavigationAgent3D, relative to the " +
          "edited scene root. A node of any other type returns wrong_node_type.",
      },
      radius: {
        type: "number",
        exclusiveMinimum: 0,
        description:
          "Agent radius (avoidance cylinder). Clamped to strictly positive. In pixels for 2D, " +
          "meters for 3D.",
      },
      height: {
        type: "number",
        minimum: 0,
        description:
          "Agent height (meaningful in 3D — the agent cylinder height). Clamped to non-negative. " +
          "3D-only: passing this for a NavigationAgent2D returns unsupported_property " +
          "(NavigationAgent2D exposes no Height property).",
      },
      max_speed: {
        type: "number",
        minimum: 0,
        description:
          "Maximum speed the agent uses to reach its target. Clamped to non-negative. Set 0 to " +
          "let the agent stop when it reaches the target.",
      },
      path_desired_distance: {
        type: "number",
        exclusiveMinimum: 0,
        description:
          "Distance at which the agent considers itself on the next path position. Clamped to " +
          "strictly positive.",
      },
      target_desired_distance: {
        type: "number",
        exclusiveMinimum: 0,
        description:
          "Distance at which the agent considers its target reached (sets is_target_reached). " +
          "Clamped to strictly positive.",
      },
      avoidance_enabled: {
        type: "boolean",
        description:
          "Enable RVO avoidance so the agent steers around other avoidance-enabled agents. " +
          "Defaults to false on a fresh agent.",
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
