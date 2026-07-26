// Capability-discovery builder (P3.8; route policy + routing summary in P7.5;
// tool-group catalog block in P8.1).
//
// Aggregates the capability surface (tools + verify rules + fixes + the
// tool-group catalog) that `godot_open_mcp_capabilities` returns. Transformation
// module: the registered tools and the rule/fix catalogs are injected by the
// caller; the tool-group catalog (`tool-groups.ts`) and the route-policy
// vocabulary (`route-policy.ts`) are imported directly because they are static
// single-source-of-truth modules shared with the router — importing them here
// is the anti-drift hook (the catalog the capabilities surface advertises is
// the same catalog P8.2's ListTools filter and P8.3's manage_tools consult).
//
// Adapted from Unity Open MCP's mcp-server/src/capabilities/build-capabilities.ts (copy for the
// CapabilitiesResult / filter contract + the per-tool `routePolicy` annotation + the top-level
// routing summary + the `toolGroups` catalog block). Intentional deltas from Unity:
//   - No `batchCapable` / `lifecycle` / `costHints` / `bridgeReachable` surfaces.
//     Those have no Godot-side backing implementation yet (lifecycle classes, cost
//     hints arrive in later phases). The `toolGroups` block lands here in P8.1
//     (compiled-state catalog only — no per-session `active` flags; those arrive
//     with manage_tools in P8.3).
//   - Route policies come from `route-policy.ts` (the single source of truth shared with
//     `tool-router.ts`) — no `batch` / `batchCapable` vocabulary, and `live-first` replaces
//     Unity's `offline-first` for scene/filesystem reads.
//   - No `bridgeReachable` flag. Unity probes the live bridge to annotate per-group availability;
//     the Godot capabilities tool is built entirely locally (no bridge hop), so there is nothing
//     to annotate. Every group reports `available: true` in P8; P12 will flip unavailable
//     domain packs without reshaping the field.
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
import { TOOL_GROUPS, groupFor } from "./tool-groups.js";

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

/**
 * One group's capability entry as it appears in the capabilities response
 * (P8.1). Compiled-state catalog only — does NOT reflect per-session
 * activation. Per-session activation state arrives with `manage_tools`
 * `list_groups` in P8.3; the two concerns are intentionally split (same
 * shape as Unity's ToolGroupCapability, minus the compile-gating fields).
 */
export interface ToolGroupCapability {
  /** Stable lowercase group id (e.g. `"core"`, `"typed-editor"`). */
  id: string;
  description: string;
  /** True when the group is enabled by default for fresh sessions. */
  defaultEnabled: boolean;
  /**
   * Tools currently assigned to this group (may be empty for domain stubs).
   * Sorted; derived from the same `tool-groups.ts` assignment table that
   * `groupFor` consults so the roster and the per-tool classification cannot
   * drift.
   */
  tools: string[];
  /**
   * P8: always `true` — Godot has no bridge compile inventory for domain
   * packs yet. Reserved so P12 can flip unavailable packs to `false` without
   * reshaping the field or breaking capabilities consumers.
   */
  available: boolean;
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
  /**
   * Tool-group catalog (P8.1). Compiled-state only — lets an agent learn
   * which groups exist and what they contain before any tool call. Per-
   * session activation state is NOT here; it arrives with `manage_tools`
   * `list_groups` in P8.3. Independent of the `kind` filter (agents asking
   * for rules/fixes still benefit from group discovery) and small enough to
   * always include — same rationale as `routing`.
   */
  toolGroups: ToolGroupCapability[];
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

  // P8.1 — tool-group catalog. Independent of the kind filter (an agent
  // asking for rules/fixes still benefits from group discovery) and small
  // enough to always include — same rationale as `routing`. Built from the
  // static `TOOL_GROUPS` catalog + the injected tool roster so the
  // `available: true` / `tools: [...]` block matches what the router
  // actually dispatches. Per-session activation is NOT here — that arrives
  // with manage_tools `list_groups` in P8.3.
  const toolGroups = buildToolGroups(deps.tools.map((t) => t.name));

  switch (filter.kind) {
    case "tools":
      return { tools, rules: [], fixes: [], toolGroups, counts, routing };
    case "rules":
      return { tools: [], rules, fixes: [], toolGroups, counts, routing };
    case "fixes":
      return { tools: [], rules: [], fixes, toolGroups, counts, routing };
    default:
      return { tools, rules, fixes, toolGroups, counts, routing };
  }
}

/**
 * Build the `toolGroups` catalog block (P8.1). Buckets the injected tool
 * names by group via `groupFor`; meta-tools (null group) are intentionally
 * absent from every group block. The roster order within a group is sorted
 * so the capabilities response is stable across calls. The catalog order is
 * preserved as-is (the static `TOOL_GROUPS` array).
 *
 * `available` is always `true` — the domain packs ship embedded in the bridge
 * addon (no per-pack compile inventory in Godot), so the field stays uniform.
 * Domain groups remain `defaultEnabled: false` until a client activates them.
 */
function buildToolGroups(registeredToolNames: string[]): ToolGroupCapability[] {
  const toolsByGroup = new Map<string, string[]>();
  for (const name of registeredToolNames) {
    const g = groupFor(name);
    if (g === null) continue; // always-visible meta-tool
    const list = toolsByGroup.get(g) ?? [];
    list.push(name);
    toolsByGroup.set(g, list);
  }

  return TOOL_GROUPS.map((group) => ({
    id: group.id,
    description: group.description,
    defaultEnabled: group.defaultEnabled,
    tools: (toolsByGroup.get(group.id) ?? []).slice().sort(),
    available: true,
  }));
}
