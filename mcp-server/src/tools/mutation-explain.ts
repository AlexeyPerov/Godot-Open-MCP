// `godot_open_mcp_mutation_explain` tool definition (P17.3).
//
// Read-only, gate-free. Turns a finished gate run (the delta + agent next steps
// a mutating tool returned in its `result.gate` block) into a human-readable
// narrative + structured summary. Resolved locally over caller-provided data —
// no bridge round-trip.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/mutation-explain.ts (adapt
// fidelity for the schema shape). Intentional delta: Unity reads the latest run
// from a server-side `BridgeGateRunHistory` ring buffer and can re-derive a
// delta from a checkpoint. Godot has NO server-side run history (the gate keeps
// its fingerprint on the stack for one dispatch only), so this tool consumes the
// gate-run data the caller supplies — copy the fields from the mutating tool's
// `result.gate` block (and optionally `new_issue_keys` / `resolved_issue_keys`
// from a godot_open_mcp_delta call) into the request. Pass issue keys for a
// per-rule breakdown enriched with rootCause.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const mutationExplain: Tool = {
  name: "godot_open_mcp_mutation_explain",
  description:
    "Explain a finished mutation + gate delta in human-readable form. Pass the " +
    "gate-run fields from the mutating tool's `result.gate` block (outcome, the " +
    "delta counts, agent_next_steps, durations) to get a narrative + structured " +
    "summary. Optionally pass new_issue_keys / resolved_issue_keys (canonical " +
    "`ruleId|severity|assetPath|issueCode` strings, e.g. from godot_open_mcp_delta) " +
    "for a per-rule breakdown enriched with rootCause. Read-only and gate-free — " +
    "it never mutates and never calls the bridge; the narrative is generated " +
    "locally from the data you supply. Degrades gracefully on partial input.",
  inputSchema: {
    type: "object",
    properties: {
      outcome: {
        type: "string",
        enum: ["passed", "warned", "failed", "skipped", "unavailable"],
        description:
          "Gate outcome from `result.gate.outcome`. Optional — omit when " +
          "unknown and the narrative will say so.",
      },
      new_errors: {
        type: "integer",
        minimum: 0,
        description: "New errors from `result.gate.delta.newErrors`.",
      },
      new_warnings: {
        type: "integer",
        minimum: 0,
        description: "New warnings from `result.gate.delta.newWarnings`.",
      },
      resolved_errors: {
        type: "integer",
        minimum: 0,
        description: "Resolved errors from `result.gate.delta.resolvedErrors`.",
      },
      resolved_warnings: {
        type: "integer",
        minimum: 0,
        description: "Resolved warnings from `result.gate.delta.resolvedWarnings`.",
      },
      agent_next_steps: {
        type: "array",
        items: { type: "string" },
        description: "The `agentNextSteps` array from `result.gate`. Passed through verbatim.",
      },
      new_issue_keys: {
        type: "array",
        items: { type: "string" },
        description:
          "Canonical new-issue keys (`ruleId|severity|assetPath|issueCode`), e.g. " +
          "from a godot_open_mcp_delta result. Drives the per-rule breakdown + " +
          "rootCause enrichment.",
      },
      resolved_issue_keys: {
        type: "array",
        items: { type: "string" },
        description: "Canonical resolved-issue keys, e.g. from a delta result.",
      },
      tool_name: {
        type: "string",
        description: "The mutating tool that ran (e.g. `godot_open_mcp_node_modify`).",
      },
      total_ms: {
        type: "integer",
        minimum: 0,
        description: "Total gate duration from `result.gate.totalMs`.",
      },
      checkpoint_ms: {
        type: "integer",
        minimum: 0,
        description: "Checkpoint duration from `result.gate.checkpointMs`.",
      },
      validation_ms: {
        type: "integer",
        minimum: 0,
        description: "Validation duration from `result.gate.validateMs`.",
      },
      categories_run: {
        type: "array",
        items: { type: "string" },
        description: "The `categoriesRun` array from `result.gate` (rule ids that ran).",
      },
      mutation_error: {
        type: "string",
        description: "The mutation's error message when the tool itself faulted.",
      },
    },
    additionalProperties: false,
  },
};
