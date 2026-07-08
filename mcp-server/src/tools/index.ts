// Tool registry — single export point for the stdio MCP server.
//
// Every MCP tool is defined in `src/tools/{tool-name}.ts` and added to the
// `ALL_TOOLS` array below. The registry started empty in the P1.5 scaffold;
// the first tool (`godot_open_mcp_ping`) lands here in P1.7 alongside the
// live bridge client. Subsequent phases (P2.x editor tools, P3.x gate, ...)
// append their tools to this array.
//
// Per mcp-server/AGENTS.md:
//   - tool names follow the `godot_open_mcp_*` convention,
//   - every tool definition carries `name`, `description`, `inputSchema`,
//     and a handler (the handler lives in LiveClient / tool-router; the
//     definition here is catalog metadata only),
//   - the bridge-side C# handler must stay in sync when a schema changes.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { ping } from "./ping.js";
import { nodeFind } from "./node-find.js";
import { nodeCreate } from "./node-create.js";

/** Ordered list of every tool exposed over stdio MCP. */
export const ALL_TOOLS: Tool[] = [
  ping,
  nodeFind,
  nodeCreate,
];
