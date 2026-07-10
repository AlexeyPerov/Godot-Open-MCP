// Capability-discovery builder (P3.8).
//
// Aggregates the capability surface (tools + verify rules + fixes) that
// `godot_open_mcp_capabilities` returns. Pure transformation module: dependencies (registered tools,
// rule/fix catalogs) are passed in by the caller so this file has zero runtime cross-file imports and
// loads cleanly under `node --experimental-strip-types`.
//
// Adapted from Unity Open MCP's mcp-server/src/capabilities/build-capabilities.ts (copy for the
// CapabilitiesResult / filter contract; the builder itself is a slim greenfield cut). Intentional
// deltas for v1 (per the minimal P3.8 scope):
//   - No routing summary / cost hints / lifecycle taxonomy / tool-group availability. Unity's builder
//     annotates every tool with routePolicy / batchCapable / lifecycle / cost hints + emits a
//     RoutingSummary, CostHintsBlock, LifecycleBlock, and ToolGroupCapability[]. Those surfaces have no
//     Godot-side backing implementation yet (tool-groups, batch allow-lists, offline classification,
//     lifecycle classes arrive in later phases). The slim builder returns {tools, rules, fixes, counts}
//     only; the omitted blocks are additive and safe to add when their phases land.
//   - No `bridgeReachable` flag. Unity probes the live bridge to annotate per-group availability; the
//     Godot capabilities tool is built entirely locally (no bridge hop), so there is nothing to annotate.
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

/** One tool's capability entry — name + description + implemented flag (every registered tool ships as implemented). */
export interface ToolCapability {
  name: string;
  implemented: boolean;
  status: CapabilityStatus;
  description: string;
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

  switch (filter.kind) {
    case "tools":
      return { tools, rules: [], fixes: [], counts };
    case "rules":
      return { tools: [], rules, fixes: [], counts };
    case "fixes":
      return { tools: [], rules: [], fixes, counts };
    default:
      return { tools, rules, fixes, counts };
  }
}
