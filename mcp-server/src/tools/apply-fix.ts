// `godot_open_mcp_apply_fix` tool definition (P3.7).
//
// Apply (or preview) a fix for a canonical issue id returned by validate_edit /
// scan_paths / the gate delta. The handler lives in the bridge (POST
// /tools/godot_open_mcp_apply_fix); this file is the catalog metadata only — name /
// description / input schema — advertised to AI clients over stdio ListTools. CallTool
// routes through LiveClient → POST, same as the other tools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/apply-fix.ts (copy for the shape;
// adapt for the Godot surface). Intentional deltas for v1:
//   - `issue_id` (required) is the canonical `{ruleId}|{severity}|{assetPath}|{issueCode}`
//     key every verify surface emits.
//   - `fix_id` is optional: omit it to list every fix that can resolve the issue
//     (safe vs unsafe); pass it to preview (dry-run) or apply.
//   - `dry_run` defaults to TRUE — a fix is never applied unless the agent explicitly
//     opts out of the preview. A dry-run apply bypasses the gate (it mutates nothing).
//   - `target_*` judgment-call params (target_guid / target_texture / target_shader in
//     Unity) are omitted: P3.7 ships only `remove_missing_script`, which takes no extra
//     params. A later phase that adds an unsafe provider (e.g. a relink fix) widens the
//     schema.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const applyFix: Tool = {
  name: "godot_open_mcp_apply_fix",
  description:
    "Apply (or preview) a structured fix for a verify issue. Pass `issue_id` (the canonical " +
    "{ruleId}|{severity}|{assetPath}|{issueCode} key every verify surface emits) and either preview or " +
    "apply a fix. `dry_run` defaults to TRUE — call with dry_run:false to actually apply. When `fix_id` is " +
    "omitted, returns the list of fix ids that can resolve the issue (each fix's Safe flag is in the " +
    "capabilities catalog) so you can choose safe vs unsafe. When `fix_id` is set and dry_run is true, " +
    "returns the fix description + Safe flag. When `fix_id` is set and dry_run is false, applies the fix " +
    "and returns success + touchedPaths. A non-dry-run apply runs through the gate with safe auto-fix " +
    "rollback: if the fix fails or introduces new errors under enforce, it is restored to its pre-fix state " +
    "and the response carries a top-level `rollback` block (rolledBack, reason, restoredPaths) — no project " +
    "change remains. Structured errors: missing_parameter (empty issue_id), invalid_issue_id (malformed " +
    "key), fix_not_applicable, fix_failed, fix_error. An unknown fix_id returns ok:true with an " +
    "error.code:unknown_fix body listing available + applicable fix ids. The initial Safe:true provider is " +
    "`remove_missing_script` (resolves missing_scripts|missing_script by removing the broken script " +
    "attachment from a .tscn/.tres).",
  inputSchema: {
    type: "object",
    properties: {
      issue_id: {
        type: "string",
        description:
          "The canonical issue key to fix: {ruleId}|{severity}|{assetPath}|{issueCode}. Copy it verbatim " +
          "from a validate_edit / scan_paths / gate-delta issue. Required.",
      },
      fix_id: {
        type: "string",
        description:
          "The fix to apply (e.g. `remove_missing_script`). Omit to list every fix that can resolve the " +
          "issue. Discover fix ids + their Safe flags via godot_open_mcp_capabilities.",
      },
      dry_run: {
        type: "boolean",
        default: true,
        description:
          "True (default) = preview: return the fix description + Safe flag without changing the project. " +
          "False = apply the fix for real (runs through the gate with rollback safety).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "res:// paths the gate should checkpoint and re-validate (e.g. the issue's assetPath). Required " +
          "for a non-dry-run apply under enforce/warn (there is no whole-project fallback); a dry-run apply " +
          "does not need it.",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        description:
          "Gate mode for a non-dry-run apply. Default `off` (no checkpoint/validate cycle). Pass `enforce` " +
          "to roll back the fix if it introduces new errors, or `warn` to report new errors without " +
          "rolling back.",
      },
    },
    required: ["issue_id"],
    additionalProperties: false,
  },
};
