// Shared CallToolResult error factory (P1.7).
//
// Centralizes the structured error envelope every error path in the MCP server
// emits so the wire shape stays consistent: `{ error: { code, message } }` by
// default, or a caller-supplied `detail` body when richer structured data is
// needed. Adapted from Unity Open MCP's mcp-server/src/results.ts (copy
// fidelity): identical named-argument factory + optional detail override.
//
// Later phases (tool-router, gate envelope handling) reuse this factory so a
// single test here pins the error shape every tool sees.

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

export interface ErrorResultInput {
  /** Stable machine-readable code (e.g. `bridge_offline`, `bridge_timeout`). */
  code: string;
  /** Human-readable explanation. */
  message: string;
  /**
   * Optional custom body. When provided (non-nullish), it replaces the default
   * `{ error: { code, message } }` envelope. Use this when a caller needs to
   * surface richer structured data (e.g. HTTP status + body from the bridge).
   */
  detail?: unknown;
}

export function makeErrorResult(input: ErrorResultInput): CallToolResult {
  const body = input.detail ?? { error: { code: input.code, message: input.message } };
  return {
    content: [{ type: "text", text: JSON.stringify(body) }],
    isError: true,
  };
}
