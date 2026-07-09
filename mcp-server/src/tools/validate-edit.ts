// `godot_open_mcp_validate_edit` tool definition (P3.6).
//
// A scoped read-only verify pass over res:// paths — the explicit form of the
// gate's validate step. The handler lives in the bridge (POST
// /tools/godot_open_mcp_validate_edit); this file is the catalog metadata only —
// name / description / input schema — advertised to AI clients over stdio
// ListTools. CallTool routes through LiveClient → POST, same as the other tools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/validate-edit.ts (copy for
// the shape; adapt for the Godot surface). Intentional deltas for v1:
//   - `paths` (required) is the res:// scope to validate.
//   - `categories` is the only rule filter (passed through as ruleIds to the
//     verify runner; null/empty runs every registered rule). Unity's
//     `include_rules` / `exclude_rules` are dropped — the verify package
//     registers exactly three cheap rules, so per-rule filtering is not worth
//     the surface yet.
//   - Unity's `platform_profile` / `profile` / `page_size` / `cursor` / `detail`
//     are all dropped. The result is the full issue list; paging is deferred
//     until a rule whose output volume warrants it lands.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const validateEdit: Tool = {
  name: "godot_open_mcp_validate_edit",
  description:
    "Run a scoped read-only verify pass over res:// paths and return the health verdict + every " +
    "issue found. This is the explicit form of the gate's validate step (the gate runs it " +
    "implicitly on every mutating call in enforce/warn mode; this tool exposes it directly so an " +
    "agent can inspect current state without mutating). `passed` is strict-error: any Error " +
    "severity issue flips it to false. Each issue carries ruleId (mirrored as categoryId so agents " +
    "can match the catalog field), severity, code (mirrored as issueCode), assetPath, description, " +
    "optional evidence, and fixCandidates[] / fixId + fixSafe when a fix provider can resolve it. " +
    "categoriesRun and rulesApplied list the rule ids that actually ran; durationMs is the wall-clock " +
    "scan time. Pass `categories` to narrow to a subset of known rule ids (null/empty runs all three " +
    "registered rules: broken_references, missing_scripts, import_health). An unknown rule id returns " +
    "a structured `error.code:unknown_rule` body (the tool still succeeds) listing the available " +
    "rules so the agent can self-correct.",
  inputSchema: {
    type: "object",
    properties: {
      paths: {
        type: "array",
        items: { type: "string" },
        minItems: 1,
        description:
          "res:// asset paths to validate (e.g. [\"res://Scenes/Main.tscn\"]). The verify rules " +
          "scan these paths; there is no whole-project fallback. Required — an empty/absent array " +
          "returns missing_parameter.",
      },
      categories: {
        type: "array",
        items: { type: "string" },
        description:
          "Optional rule-id filter. When omitted/empty, every registered rule runs " +
          "(broken_references, missing_scripts, import_health). Pass specific ids to narrow the " +
          "scan; an unknown id returns a structured unknown_rule body listing the available rules.",
      },
    },
    required: ["paths"],
    additionalProperties: false,
  },
};
