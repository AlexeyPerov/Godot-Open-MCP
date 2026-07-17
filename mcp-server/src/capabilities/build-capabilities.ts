// Capability-discovery builder (P3.8; route policy + routing summary in P7.5).
//
// Aggregates the capability surface (tools + verify rules + fixes) that
// `godot_open_mcp_capabilities` returns. Pure transformation module: dependencies (registered tools,
// rule/fix catalogs) are passed in by the caller so this file has zero runtime cross-file imports and
// loads cleanly under `node --experimental-strip-types`.
//
// Adapted from Unity Open MCP's mcp-server/src/capabilities/build-capabilities.ts (copy for the
// CapabilitiesResult / filter contract + the per-tool `routePolicy` annotation + the top-level
// routing summary). Intentional deltas from Unity:
//   - No `batchCapable` / `lifecycle` / `costHints` / `toolGroups` / `bridgeReachable` surfaces.
//     Those have no Godot-side backing implementation yet (tool-groups, lifecycle classes, cost
//     hints arrive in later phases). The builder returns {tools, rules, fixes, counts, routing};
//     the omitted blocks are additive and safe to add when their phases land.
//   - Route policies come from `route-policy.ts` (the single source of truth shared with
//     `tool-router.ts`) — no `batch` / `batchCapable` vocabulary, and `live-first` replaces
//     Unity's `offline-first` for scene/filesystem reads.
//   - No `bridgeReachable` flag. Unity probes the live bridge to annotate per-group availability;
//     the Godot capabilities tool is built entirely locally (no bridge hop), so there is nothing
//     to annotate.
//
// Per packages/verify/AGENTS.md §Capability catalog sync, KEEP the rule/fix entries in sync with the
// C# verify package — the drift-detection tests in rule-catalog.test.ts + build-capabilities.test.ts
// pin the contract.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import type {
  RuleCapability,
  FixCapability,
  CapabilityStatus,
} from "./rule-catalog.js";
import {
  routePolicyFor,
  ROUTING_SUMMARY,
  type RoutePolicy,
  type RoutingSummary,
} from "./route-policy.js";

/** One tool's capability entry — name + description + implemented flag (every registered tool ships as implemented). */
export interface ToolCapability {
  name: string;
  implemented: boolean;
  status: CapabilityStatus;
  description: string;
  /**
   * Execution policy this tool follows (P7.5). Descriptive metadata only — it
   * does NOT let callers override routing. Mirrors the router's classification
   * via the shared `route-policy.ts` module so the catalog and the router
   * cannot drift. See `docs/api/mcp-tools.md` §Route policy.
   */
  routePolicy: RoutePolicy;
}

export interface CapabilitiesCounts {
  toolsImplemented: number;
  toolsPlanned: number;
  rulesImplemented: number;
  rulesPlanned: number;
  fixesImplemented: number;
  fixesPlanned: number;
}

export interface CapabilitiesResult {
  tools: ToolCapability[];
  rules: RuleCapability[];
  fixes: FixCapability[];
  counts: CapabilitiesCounts;
  /**
   * One-shot routing narrative for agents (P7.5). Lets an agent learn the route
   * vocabulary and that the router prefers the live bridge by default, without
   * reading repo docs. Concise on purpose — per-tool `routePolicy` lives on
   * each {@link ToolCapability} entry, not here. Independent of the `kind`
   * filter — agents asking for rules/fixes still benefit from the routing
   * narrative.
   */
  routing: RoutingSummary;
}

export interface CapabilitiesFilter {
  /** Filter to a single surface (`tools` | `rules` | `fixes`). Omit for all. */
  kind?: "tools" | "rules" | "fixes";
  /** When false, omit planned/unimplemented capabilities. */
  includePlanned?: boolean;
}

/**
 * Dependencies — injected by the caller so this module stays import-free. `tools` is the full registered
 * tool set (ALL_TOOLS); `rules` and `fixes` are the catalog arrays from rule-catalog.ts.
 */
export interface BuildCapabilitiesDeps {
  tools: Tool[];
  rules: RuleCapability[];
  fixes: FixCapability[];
}

/**
 * Build the capability surface. Every registered tool ships as `implemented:true`. The `kind` filter
 * narrows to one surface; `includePlanned` (default true) controls whether planned rules/fixes are
 * included. With the minimal v1 catalog (no planned entries), `includePlanned:false` is a no-op for
 * rules/fixes but is honored so the contract holds once planned entries land.
 */
export function buildCapabilities(
  deps: BuildCapabilitiesDeps,
  filter: CapabilitiesFilter = {},
): CapabilitiesResult {
  const includePlanned = filter.includePlanned !== false;

  const tools: ToolCapability[] = deps.tools.map((tool) => ({
    name: tool.name,
    implemented: true,
    status: "implemented",
    description: tool.description ?? "",
    // P7.5 — per-tool policy from the shared route-policy module. This is the
    // catalog's classification; tool-router.ts imports the same override sets
    // so the two cannot drift (the parity test in route-policy.test.ts pins
    // it). Descriptive metadata only — does not let callers override routing.
    routePolicy: routePolicyFor(tool.name),
  }));

  const rules = includePlanned
    ? deps.rules
    : deps.rules.filter((r) => r.implemented);
  const fixes = includePlanned
    ? deps.fixes
    : deps.fixes.filter((f) => f.implemented);

  const counts: CapabilitiesCounts = {
    toolsImplemented: tools.length,
    toolsPlanned: 0, // PLANNED_TOOLS arrives with a later phase; every registered tool is implemented today.
    rulesImplemented: deps.rules.filter((r) => r.implemented).length,
    rulesPlanned: deps.rules.filter((r) => !r.implemented).length,
    fixesImplemented: deps.fixes.filter((f) => f.implemented).length,
    fixesPlanned: deps.fixes.filter((f) => !f.implemented).length,
  };

  // P7.5 — routing summary is constant and independent of the kind filter:
  // an agent asking for `kind: "rules"` still benefits from the routing
  // narrative. The per-tool `routePolicy` is the authoritative roster; this
  // summary is the vocabulary + the live-default note.
  const routing = ROUTING_SUMMARY;

  switch (filter.kind) {
    case "tools":
      return { tools, rules: [], fixes: [], counts, routing };
    case "rules":
      return { tools: [], rules, fixes: [], counts, routing };
    case "fixes":
      return { tools: [], rules: [], fixes, counts, routing };
    default:
      return { tools, rules, fixes, counts, routing };
  }
}
