// Tool registry — single export point for the stdio MCP server.
//
// Every MCP tool is defined in `src/tools/{tool-name}.ts` and added to the
// `ALL_TOOLS` array below. The registry is intentionally empty in this
// scaffold (P1.5): the first tool (`godot_open_mcp_ping`) lands in P1.7 once
// instance discovery (P1.6) and the live bridge client are in place.
//
// Per mcp-server/AGENTS.md:
//   - tool names follow the `godot_open_mcp_*` convention,
//   - every tool definition carries `name`, `description`, `inputSchema`,
//     and a handler,
//   - the bridge-side C# handler must stay in sync when a schema changes.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

/** Ordered list of every tool exposed over stdio MCP. Empty until P1.7. */
export const ALL_TOOLS: Tool[] = [];
