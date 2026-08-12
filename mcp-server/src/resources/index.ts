// MCP resource catalog (P18.2).
//
// Some MCP clients prefer `resources/` URIs over tool calls for read-only
// state snapshots (health, bridge status, tool groups). Godot exposes only
// tools today; this catalog advertises four read-only resource URIs that wrap
// existing tool/logic outputs — no new business logic. `resources/list`
// advertises them; `resources/read` routes through `resource-router.ts`.
//
// Adapted from Unity Open MCP's `mcp-server/src/resources/index.ts` (copy
// fidelity for the URI scheme, the four-URI roster, and the catalog shape).
// Intentional deltas:
//   - URI scheme is `godot-open-mcp://` (ADR-003 naming), not `unity-open-mcp://`.
//   - Descriptions point at the Godot tools whose logic each resource wraps
//     (`godot_open_mcp_bridge_status`, `godot_open_mcp_baseline_create`, etc.).
//
// All four are read-only. None mutate; none spawn Godot. The health/summary
// resource runs the offline whole-project scanner (`scanProjectOffline`) on
// each read — there is no cached health summary in Godot today (no bridge
// endpoint mirrors Unity's cached verify summary), so the resource re-runs the
// scan the baseline/regression tools already use.

import type { Resource } from "@modelcontextprotocol/sdk/types.js";

export const healthSummary: Resource = {
  uri: "godot-open-mcp://health/summary",
  name: "Health summary",
  mimeType: "application/json",
  description:
    "Project health summary (severity counts: error/warn/info) from a fresh " +
    "offline whole-project scan — the same scanner godot_open_mcp_baseline_create " +
    "and godot_open_mcp_regression_check use. Runs on every read (no cached " +
    "summary); the result also lists the rule ids that ran and the rule ids " +
    "that are CI-excluded because they require the live editor.",
};

export const healthBaseline: Resource = {
  uri: "godot-open-mcp://health/baseline",
  name: "Health baseline",
  mimeType: "application/json",
  description:
    "Last regression baseline (schemaVersion, platformProfile, severity " +
    "counts, ciExcludedRules) read from disk (convention: " +
    "CI/godot-open-mcp-baseline.json). Returns status `no_baseline` when the " +
    "file is absent — run godot_open_mcp_baseline_create to write one.",
};

export const bridgeStatus: Resource = {
  uri: "godot-open-mcp://bridge/status",
  name: "Bridge status",
  mimeType: "application/json",
  description:
    "Snapshot of the bridge status (status token, ready flag, classification, " +
    "ping probe, instance lock) — the exact body godot_open_mcp_bridge_status " +
    "returns. Performs one /ping probe per read, so an offline bridge reports " +
    "stopped/unreachable rather than a cached value.",
};

// Tool-group catalog resource. Static catalog (group ids, descriptions,
// default-enabled flags) discoverable before the first domain tool call — the
// same catalog godot_open_mcp_capabilities embeds. For the per-tool roster,
// call godot_open_mcp_capabilities; for session activation state, call
// godot_open_mcp_manage_tools(action="list_groups").
export const toolGroups: Resource = {
  uri: "godot-open-mcp://tool-groups",
  name: "Tool groups",
  mimeType: "application/json",
  description:
    "Tool-group catalog (ids, descriptions, default-enabled flags). Call " +
    "godot_open_mcp_manage_tools to activate a group before using its tools; " +
    "call godot_open_mcp_capabilities for the full per-tool roster.",
};

export const ALL_RESOURCES: Resource[] = [
  healthSummary,
  healthBaseline,
  bridgeStatus,
  toolGroups,
];
