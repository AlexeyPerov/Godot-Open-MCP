// `godot_open_mcp_gate_budget_estimate` tool definition (P17.3).
//
// Read-only, gate-free. Forecasts the validation duration + issue budget for a
// planned `paths_hint` scope before mutating, using the cost-hints table. Returns
// estimatedDurationMs (a lower bound), estimatedIssueBudget (an upper bound), a
// token band, and the resolved rule set. Resolved locally — no live scan runs.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/gate-budget-estimate.ts
// (adapt fidelity for the schema shape). Intentional delta: Unity offers `cache`
// + `sample` modes backed by a live VerifyCacheService / checkpoint scan; Godot
// has neither, so this tool is heuristic-only (no `mode` param). The estimate is
// derived from the per-rule cost-hints table + path classification — treat it as
// a coarse forecast and run validate_edit for actuals.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const gateBudgetEstimate: Tool = {
  name: "godot_open_mcp_gate_budget_estimate",
  description:
    "Forecast validation duration + issue budget for a planned mutation scope " +
    "before mutating. Returns estimatedDurationMs (a lower bound on the real " +
    "gate path), estimatedIssueBudget (an upper bound on issues the gate might " +
    "surface), a coarse output-cost token band (small / medium / large), the " +
    "resolved rule set, and the basis + confidence of the estimate. Read-only " +
    "and gate-free — the estimate is heuristic (per-rule cost bands × estimated " +
    "asset count; folders expand to a fixed estimate). Run validate_edit for " +
    "actuals. categories / include_rules / exclude_rules filter the resolved " +
    "rule set (same precedence as validate_edit).",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      paths_hint: {
        type: "array",
        items: { type: "string" },
        minItems: 1,
        description:
          "Scope to forecast — the same vocabulary the gate uses. `res://` file " +
          "paths count as one asset each; folder paths expand to a fixed asset " +
          "estimate (noted in the result).",
      },
      categories: {
        type: "array",
        items: { type: "string" },
        description:
          "Optional explicit verify rule ids. When non-empty, the resolved set " +
          "starts from these (same semantics as validate_edit). Omit to " +
          "auto-select all implemented rules.",
      },
      include_rules: {
        type: "array",
        items: { type: "string" },
        description:
          "Optional allow-list intersected with the resolved rule set.",
      },
      exclude_rules: {
        type: "array",
        items: { type: "string" },
        description:
          "Optional deny-list. Always wins over categories and include_rules.",
      },
    },
    additionalProperties: false,
  },
};
