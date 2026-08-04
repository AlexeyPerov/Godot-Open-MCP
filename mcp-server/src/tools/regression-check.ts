// `godot_open_mcp_regression_check` tool definition (P15.1).
//
// Compares the current offline scan against a baseline file and returns a
// compact regression summary suitable for CI logs. The exit-code contract
// (surfaced as `exitCode` in the result body) is:
//   0 — no regression
//   1 — regression (error-count increase > threshold, or a per-category breach)
//   2 — baseline missing
//   3 — baseline invalid (unreadable / unparseable / schema-version mismatch)
//
// Adapted from Unity Open MCP's `mcp-server/src/tools/regression-check.ts`
// (copy for the schema shape + threshold semantics; adapt the route to
// `offline`). Threshold semantics match Unity: strict `>` (a delta equal to the
// threshold is tolerated); the overall verdict is the OR of the global gate and
// every per-rule gate; rules absent from `per_category_thresholds` fall back to
// `regression_threshold`.
//
// Route: always `offline` — never probes the bridge, never spawns Godot.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const regressionCheck: Tool = {
  name: "godot_open_mcp_regression_check",
  description:
    "Compare the current offline scan against a baseline file and return a " +
    "compact regression summary suitable for CI logs. Returns exitCode 1 when " +
    "the error-count increase exceeds regression_threshold (global) or any " +
    "per_category_thresholds breach; 0 when no regression; 2 when the baseline " +
    "is missing; 3 when the baseline is invalid (unreadable / unparseable / " +
    "schema-version mismatch). per_category_thresholds maps a ruleId to its " +
    "max tolerated error-count increase; rules absent from the map fall back " +
    "to regression_threshold. Works offline — no Godot editor required.",
  inputSchema: {
    type: "object",
    required: ["baseline_path"],
    properties: {
      baseline_path: {
        type: "string",
        description:
          "Path to a baseline JSON file created by " +
          "godot_open_mcp_baseline_create (relative to the project root or " +
          "absolute).",
      },
      regression_threshold: {
        type: "integer",
        default: 0,
        minimum: 0,
        description:
          "Max allowed increase in total Error count before the check fails " +
          "(applied globally). A delta equal to the threshold is tolerated.",
      },
      per_category_thresholds: {
        type: "object",
        additionalProperties: { type: "integer", minimum: 0 },
        description:
          "Per-ruleId max tolerated error-count increase. Each key is a " +
          "ruleId; the value overrides regression_threshold for that rule. " +
          'Example: {"broken_references": 2}. Rules not named here use ' +
          "regression_threshold. The overall verdict is the OR of the global " +
          "gate and every per-rule gate.",
      },
      platform_profile: {
        enum: ["mobile", "console", "desktop"],
        default: "desktop",
        description:
          "Platform profile recorded in the current scan metadata " +
          "(informational; does not change which rules run).",
      },
    },
    additionalProperties: false,
  },
};
