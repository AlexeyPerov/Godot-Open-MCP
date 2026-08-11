// `godot_open_mcp_impact_preview` tool definition (P17.3).
//
// Read-only, gate-free. Projects the gate's view of a planned `paths_hint`
// scope WITHOUT mutating: resolves the auto-selected verify rule set, classifies
// each path (folder / asset kind / rules-for-extension), and reports a coarse
// risk band with a confidence level. Resolved locally in the MCP process over
// the rule catalog + path shape — no rule scan runs, no bridge round-trip.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/impact-preview.ts (adapt
// fidelity for the schema shape; adapt for Godot asset kinds). Intentional
// delta: Godot has no server-side rule resolver, so resolution + classification
// run purely over the TS `RULE_CATALOG` (the Godot gate runs every implemented
// rule against a scope; this tool narrows to the rules whose declared
// `applicableExtensions` accept each path).
//
// Use godot_open_mcp_validate_edit to confirm actual issues before or after
// mutating — impact_preview only projects scope, it does not detect issues.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const impactPreview: Tool = {
  name: "godot_open_mcp_impact_preview",
  description:
    "Project the gate's view of a planned mutation scope WITHOUT mutating. " +
    "Resolves the verify rule set that would run for the given paths_hint, " +
    "classifies each path (folder / asset kind / which rules apply to its " +
    "extension), and reports a coarse risk band (low / moderate / high) with a " +
    "confidence level. Read-only and gate-free — it does not run a rule scan, " +
    "only projects scope over the rule catalog + path shape. Use " +
    "validate_edit to confirm actual issues before or after mutating. " +
    "categories / include_rules / exclude_rules filter the resolved rule set " +
    "(same precedence as validate_edit).",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      paths_hint: {
        type: "array",
        items: { type: "string" },
        minItems: 1,
        description:
          "Scope to project — the same vocabulary the gate uses. `res://` asset " +
          "paths (files) or folder scopes; the classifier derives the asset kind " +
          "from the extension and the rules that apply from each rule's declared " +
          "extensions. Folders carry the full resolved rule set.",
      },
      categories: {
        type: "array",
        items: { type: "string" },
        description:
          "Optional explicit verify rule ids. When non-empty, the resolved set " +
          "starts from these (same semantics as validate_edit `categories`). " +
          "Omit to auto-select all implemented rules.",
      },
      include_rules: {
        type: "array",
        items: { type: "string" },
        description:
          "Optional allow-list intersected with the resolved rule set (same " +
          "semantics as validate_edit).",
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
