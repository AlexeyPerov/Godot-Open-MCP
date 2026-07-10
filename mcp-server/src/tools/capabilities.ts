// `godot_open_mcp_capabilities` tool definition (P3.8).
//
// Discover the full capability surface — every registered tool, every verify rule with its issue codes
// and severities, and every fix provider — in one call. The response is built LOCALLY in the MCP server
// (no bridge hop): the CallTool handler in src/index.ts special-cases this tool name and calls
// buildCapabilities({tools: ALL_TOOLS, rules: RULE_CATALOG, fixes: FIX_CATALOG}) directly, mirroring
// Unity Open MCP's tool-router `routeCapabilities` (local-only). This file is the catalog metadata only
// (name / description / input schema).
//
// Adapted from Unity Open MCP's mcp-server/src/tools/agent-capabilities.ts (copy for the schema;
// `godot_open_mcp_*` prefix per ADR-003). The `kind` filter (tools | rules | fixes) and `include_planned`
// default-true match the Unity contract.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const capabilities: Tool = {
  name: "godot_open_mcp_capabilities",
  description:
    "Discover the full capability surface in one call: every tool (name + description), every verify " +
    "rule (id, applicable asset kinds, issue codes + severities, fix mappings), and every fix provider " +
    "(id, which rule/issue codes it resolves, whether it is safe to auto-apply). Each capability carries " +
    "an `implemented` boolean; planned-but-unbuilt items return with `status: \"planned\"` and actionable " +
    "guidance instead of failing. Call this first to learn what is available before using apply_fix or " +
    "validate_edit blindly. The response also carries `counts` (implemented/planned tallies for tools, " +
    "rules, fixes). Built locally in the MCP server — no bridge round-trip. Pass `kind` to narrow to a " +
    "single surface (tools | rules | fixes), or `include_planned:false` to see only implemented items.",
  inputSchema: {
    type: "object",
    properties: {
      kind: {
        enum: ["tools", "rules", "fixes"],
        description:
          "Filter to a single surface. Omit to return tools + rules + fixes together.",
      },
      include_planned: {
        type: "boolean",
        default: true,
        description:
          "Include planned-but-unbuilt capabilities (status \"planned\"). Set false to see only " +
          "implemented items. The v1 catalog has no planned entries yet.",
      },
    },
    additionalProperties: false,
  },
};
