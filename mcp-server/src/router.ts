// P7.1 — routing seam.
//
// The single abstraction every `CallTool` dispatch flows through. `index.ts`
// validates tool registration (unknown names are rejected before the router is
// ever consulted); the router then selects execution policy per call:
//   - exact local/offline handler (when declared in the named-handler map), or
//   - the generic live route (LiveClient → bridge).
//
// Tool registration stays in `ALL_TOOLS` (`tools/index.ts`) — routing is
// runtime policy, not tool-definition metadata. P7.2–P7.4 extend the named
// map with offline handlers; they must not add new branches to `index.ts`.
//
// Adapted from Unity Open MCP's `mcp-server/src/router.ts` (copy fidelity):
// same minimal `route(toolName, args)` surface. Intentional deltas: no batch
// route, no resource router (Godot has no headless editor batch equivalent;
// resources arrive in a later phase).

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

export type { CallToolResult };

/**
 * Route a registered tool call to its execution policy. Implementations must
 * never throw — every failure path returns a structured `isError` result, the
 * same contract `index.ts`'s unknown-tool guard already upholds.
 */
export interface Router {
  route(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult>;
}
