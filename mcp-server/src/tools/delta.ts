// `godot_open_mcp_delta` tool definition (P3.6).
//
// Compare the current project state against a previously captured checkpoint
// and return the new/resolved issue delta. The handler lives in the bridge
// (POST /tools/godot_open_mcp_delta); this file is the catalog metadata only —
// name / description / input schema — advertised to AI clients over stdio
// ListTools. CallTool routes through LiveClient → POST.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/delta.ts (copy for the
// shape; adapt for the Godot session-recovery note). Intentional deltas:
//   - `checkpoint_id` (required) is the opaque id returned by
//     `godot_open_mcp_checkpoint_create`.
//   - `paths` (optional) overrides the checkpoint's paths; defaults to the
//     paths captured at checkpoint time.
//   - Session-safe recovery: a checkpoint that is no longer in the in-memory
//     store (cleared on recompile/reload/restart) returns `passed:true` +
//     `unavailable:true` + recovery guidance, NOT a hard error.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const delta: Tool = {
  name: "godot_open_mcp_delta",
  description:
    "Compare the current project state against a previously captured checkpoint (from " +
    "`godot_open_mcp_checkpoint_create`) and return the before/after issue delta. `passed` is " +
    "strict on new errors: any new Error severity issue (present after the mutation but absent at " +
    "checkpoint) flips it to false; pre-existing issues never count as new. Returns a `summary` of " +
    "newErrors / newWarnings / resolvedErrors / resolvedWarnings, plus `newIssues[]` and " +
    "`resolvedIssues[]` (canonical `{ruleId}|{severity}|{assetPath}|{issueCode}` keys). " +
    "Checkpoints are session-scoped: if the checkpoint id is no longer available (cleared on " +
    "script recompile, assembly reload, or editor restart), the tool returns `passed:true` + " +
    "`unavailable:true` + `agentNextSteps[]` recovery guidance (NOT a hard error) so the agent can " +
    "fall back to `godot_open_mcp_validate_edit` for a direct current-state check. Use the " +
    "explicit checkpoint → mutate → delta workflow when you want gate safety telemetry across " +
    "separate tool calls.",
  inputSchema: {
    type: "object",
    properties: {
      checkpoint_id: {
        type: "string",
        description:
          "The opaque checkpoint id returned by `godot_open_mcp_checkpoint_create`. Required — " +
          "an empty/absent value returns missing_parameter.",
      },
      paths: {
        type: "array",
        items: { type: "string" },
        description:
          "Optional res:// paths to re-validate; defaults to the paths captured at checkpoint " +
          "time. Pass a narrower scope to delta only a subset.",
      },
    },
    required: ["checkpoint_id"],
    additionalProperties: false,
  },
};
