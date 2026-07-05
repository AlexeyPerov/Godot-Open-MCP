// `godot_open_mcp_ping` tool definition (P1.7).
//
// The first tool registered in the stdio MCP catalog. It is the canonical
// end-to-end probe: AI client → MCP server → live bridge `GET /ping`. The
// handler lives in LiveClient (./live-client.ts) — tool definitions in this
// folder carry only the catalog metadata (name / description / input schema),
// not the call path. The MCP server's CallTool dispatcher routes
// `godot_open_mcp_ping` to LiveClient.route, which performs the HTTP fetch
// against the resolved bridge port and normalizes success vs failure into a
// structured CallToolResult.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/ping.ts (copy fidelity):
// identical schema (empty input object, no properties) and a one-line
// description. Only the tool name prefix differs (`godot_open_mcp_*` per
// ADR-003).

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const ping: Tool = {
  name: "godot_open_mcp_ping",
  description: "Bridge health check.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
