// Cost-hints calibration table (P17.3).
//
// Per-tool per-profile token bands + per-rule scan-cost hints, used by
// `godot_open_mcp_gate_budget_estimate` to forecast validation duration and
// output cost before a mutation, and advertised (via `buildCostHints`) as a
// capability surface agents can consult to pick a profile/page size.
//
// Adapted from Unity Open MCP's mcp-server/src/capabilities/cost-hints.ts
// (adapt fidelity): the `CostBand` type, the per-tool per-profile table shape,
// the `RECOMMENDED_PAGE_SIZE` roster, and the `buildCostHints` builder are lifted
// from Unity. Intentional deltas for Godot:
//   - The per-tool table is recalibrated for Godot's asset/parser shapes
//     (Godot `.tscn`/`.tres` text is typically denser per node than Unity YAML;
//      the token ceilings are heuristic estimates, NOT measured against a live
//      fixture in this environment — treat as coarse bands and refine on
//      fixture changes). The "treat as estimates" safeguard is copied verbatim.
//   - `RULE_COST_HINTS` is greenfield for Godot: per-rule `msPerAsset` +
//     `issueWeight` consumed by `gate_budget_estimate`. Unity computes the
//     equivalent inline in `GateIntelligenceTools.GateBudgetEstimate` against a
//     live VerifyCacheService snapshot; Godot has no cache service, so the
//     estimate is pure-heuristic from this table.
//
// The calibration values are deliberately conservative (lean toward
// over-estimating cost so an agent errs toward smaller scopes). When real
// fixture measurements land, update the numbers here and the cost-hints test
// pins the structure (not the exact constants).

import { RULE_CATALOG } from "./rule-catalog.js";

// ---------------------------------------------------------------------------
// Cost bands
// ---------------------------------------------------------------------------

/**
 * Coarse output-size band. `gate_budget_estimate` maps its forecast validation
 * output onto one of these so an agent gets a "small/medium/large" cost signal
 * without a token counter.
 *
 * `large.maxTokens = 0` is the sentinel for "unbounded — use page_size" (copied
 * from Unity so the contract reads identically).
 */
export type CostBand = "small" | "medium" | "large";

export interface CostBandRange {
  /** Inclusive lower bound (tokens). */
  minTokens: number;
  /**
   * Exclusive upper bound (tokens). `0` is the sentinel for "unbounded — rely on
   * `page_size`" rather than a literal zero ceiling.
   */
  maxTokens: number;
}

export const COST_BANDS: Record<CostBand, CostBandRange> = {
  small: { minTokens: 0, maxTokens: 800 },
  medium: { minTokens: 800, maxTokens: 4000 },
  large: { minTokens: 4000, maxTokens: 0 },
};

/**
 * Resolve a token estimate onto a {@link CostBand}. A non-positive `maxTokens`
 * (the unbounded sentinel) always maps to `large`.
 */
export function bandForTokens(tokens: number): CostBand {
  if (tokens >= COST_BANDS.large.minTokens) return "large";
  if (tokens >= COST_BANDS.medium.minTokens) return "medium";
  return "small";
}

// ---------------------------------------------------------------------------
// Per-tool per-profile cost hints (heavy read tools)
// ---------------------------------------------------------------------------

/**
 * One profile's cost characterization for a heavy tool: the output band a
 * compact/balanced/full request tends to land in, plus a one-line note on what
 * the profile knob controls for that tool.
 */
export interface ToolProfileCost {
  band: CostBand;
}

export interface ToolCostHint {
  /** Tool name (`godot_open_mcp_*`). */
  tool: string;
  /** What the `profile` knob controls for this tool (catalog copy). */
  profileControls: string;
  /** What `page_size` bounds for this tool (catalog copy). */
  pageSizeBounds: string;
  /** Per-profile cost. */
  profiles: { compact: ToolProfileCost; balanced: ToolProfileCost; full: ToolProfileCost };
}

/**
 * Per-tool per-profile cost table for Godot's heavy read tools. Every profile
 * for every tool maps compact→small, balanced→medium, full→large — the same
 * invariant Unity pins (the bands are the signal; the per-tool differences live
 * in `profileControls` / `pageSizeBounds`). Recalibrated ceilings live in
 * {@link COST_BANDS}.
 */
export const TOOL_COST_HINTS: ToolCostHint[] = [
  {
    tool: "godot_open_mcp_read_asset",
    profileControls: "headline-only (compact) vs expanded per-kind roster (balanced/full)",
    pageSizeBounds: "per-kind roster (nodes / uniforms / properties)",
    profiles: { compact: { band: "small" }, balanced: { band: "medium" }, full: { band: "large" } },
  },
  {
    tool: "godot_open_mcp_search_assets",
    profileControls: "reason-tag counts (compact) vs per-asset detail (balanced/full)",
    pageSizeBounds: "result list",
    profiles: { compact: { band: "small" }, balanced: { band: "medium" }, full: { band: "large" } },
  },
  {
    tool: "godot_open_mcp_list_assets",
    profileControls: "per-kind counts only (compact) vs sample file names (balanced/full)",
    pageSizeBounds: "folder list",
    profiles: { compact: { band: "small" }, balanced: { band: "medium" }, full: { band: "large" } },
  },
  {
    tool: "godot_open_mcp_find_references",
    profileControls: "counts only (compact) vs per-asset reference list (balanced/full)",
    pageSizeBounds: "referencing-assets list",
    profiles: { compact: { band: "small" }, balanced: { band: "medium" }, full: { band: "large" } },
  },
  {
    tool: "godot_open_mcp_dependencies",
    profileControls: "edge counts (compact) vs per-asset edge detail (balanced/full)",
    pageSizeBounds: "forward/reverse edge list",
    profiles: { compact: { band: "small" }, balanced: { band: "medium" }, full: { band: "large" } },
  },
  {
    tool: "godot_open_mcp_scene_get_data",
    profileControls: "depth + verbosity of the node tree",
    pageSizeBounds: "node roster depth",
    profiles: { compact: { band: "small" }, balanced: { band: "medium" }, full: { band: "large" } },
  },
  {
    tool: "godot_open_mcp_filesystem_list",
    profileControls: "folder → kind counts vs sample file names",
    pageSizeBounds: "folder list",
    profiles: { compact: { band: "small" }, balanced: { band: "medium" }, full: { band: "large" } },
  },
  {
    tool: "godot_open_mcp_validate_edit",
    profileControls: "issue counts (compact) vs per-issue detail + evidence (balanced/full)",
    pageSizeBounds: "issue list",
    profiles: { compact: { band: "small" }, balanced: { band: "medium" }, full: { band: "large" } },
  },
];

/**
 * Default `page_size` per heavy tool when the caller omits it. Copied from
 * Unity's roster shape; the numeric defaults are Godot-calibrated starting
 * points (refine on fixture evidence).
 */
export const RECOMMENDED_PAGE_SIZE: Record<string, number> = {
  godot_open_mcp_read_asset: 40,
  godot_open_mcp_search_assets: 25,
  godot_open_mcp_list_assets: 25,
  godot_open_mcp_find_references: 50,
  godot_open_mcp_dependencies: 50,
  godot_open_mcp_scene_get_data: 50,
  godot_open_mcp_filesystem_list: 25,
  godot_open_mcp_validate_edit: 25,
};

// ---------------------------------------------------------------------------
// Per-rule scan-cost hints (gate_budget_estimate input)
// ---------------------------------------------------------------------------

/**
 * Per-rule heuristic cost. `gate_budget_estimate` multiplies `msPerAsset` by the
 * estimated asset count to get a duration lower bound, and `issueWeight` by the
 * asset count to get an issue-budget upper bound.
 *
 * Greenfield for Godot (Unity computes the equivalent inline against a live
 * cache snapshot). Values are conservative estimates — refine on fixture
 * evidence. The relative ordering reflects each rule's work:
 *   - structural/walk rules (project_health, scene_structure) are cheaper per
 *     asset (single-file parse);
 *   - cross-file rules (broken_references, missing_scripts, materials/shader,
 *     animation) resolve external refs and cost more;
 *   - import_health is cheapest (sidecar scan, no scene parse).
 */
export interface RuleCostHint {
  /** Rule id — matches `RULE_CATALOG[].id` / the C# `IVerifyRule.Id`. */
  rule: string;
  /** Estimated scan milliseconds per asset (lower bound on real cost). */
  msPerAsset: number;
  /**
   * Relative issue-budget weight per asset. Multiplied by the asset count to
   * estimate the worst-case issue count the rule might surface.
   */
  issueWeight: number;
}

export const RULE_COST_HINTS: RuleCostHint[] = [
  { rule: "broken_references", msPerAsset: 3, issueWeight: 2 },
  { rule: "missing_scripts", msPerAsset: 3, issueWeight: 2 },
  { rule: "import_health", msPerAsset: 1, issueWeight: 1 },
  { rule: "project_health", msPerAsset: 2, issueWeight: 3 },
  { rule: "scene_structure_health", msPerAsset: 2, issueWeight: 4 },
  { rule: "materials_shader_health", msPerAsset: 3, issueWeight: 2 },
  { rule: "script_audit", msPerAsset: 3, issueWeight: 2 },
  { rule: "animation_analysis", msPerAsset: 4, issueWeight: 3 },
];

/** Look up a rule's cost hint by id. Returns a sane default if absent. */
export function ruleCostFor(ruleId: string): RuleCostHint {
  for (const hint of RULE_COST_HINTS) {
    if (hint.rule === ruleId) return hint;
  }
  return { rule: ruleId, msPerAsset: 2, issueWeight: 2 };
}

// ---------------------------------------------------------------------------
// Recommended tool chains (catalog copy)
// ---------------------------------------------------------------------------

/** A named, ordered tool chain advertised to agents as a recommended workflow. */
export interface RecommendedToolChain {
  id: string;
  steps: string[];
}

/**
 * Recommended workflows. The `mutate-then-verify` chain is the one that ties the
 * gate-intelligence tools into agent guidance (preview before, explain after).
 * Adapted from Unity's `RECOMMENDED_TOOL_CHAINS` (adapt for Godot tool names).
 */
export const RECOMMENDED_TOOL_CHAINS: RecommendedToolChain[] = [
  {
    id: "discover",
    steps: [
      "godot_open_mcp_capabilities",
      "godot_open_mcp_filesystem_list",
      "godot_open_mcp_search_assets",
    ],
  },
  {
    id: "asset-inspect",
    steps: ["godot_open_mcp_read_asset", "godot_open_mcp_dependencies"],
  },
  {
    id: "find-references",
    steps: ["godot_open_mcp_find_references", "godot_open_mcp_read_asset"],
  },
  {
    id: "mutate-then-verify",
    steps: [
      "godot_open_mcp_impact_preview",
      "godot_open_mcp_gate_budget_estimate",
      "<mutate with gate=enforce + non-empty paths_hint>",
      "godot_open_mcp_mutation_explain (over the response gate block)",
      "on gate failure, prefer godot_open_mcp_apply_fix with dry_run first",
    ],
  },
];

// ---------------------------------------------------------------------------
// Builder (capability surface)
// ---------------------------------------------------------------------------

export interface CostHintsBlock {
  bands: Record<CostBand, CostBandRange>;
  tools: ToolCostHint[];
  recommendedPageSize: Record<string, number>;
  recommendedToolChains: RecommendedToolChain[];
  guidance: string;
}

/**
 * Build the cost-hints capability block. Exported so `build-capabilities.ts` can
 * advertise it in a future phase (P17.3 ships the data + the budget tool that
 * consumes it; wiring into the capabilities response is out of scope for this
 * phase's touch map).
 *
 * The `guidance` string is the contract one-liner (adapted from Unity): start
 * compact, expand on demand, bound with page_size.
 */
export function buildCostHints(): CostHintsBlock {
  return {
    bands: COST_BANDS,
    tools: TOOL_COST_HINTS,
    recommendedPageSize: RECOMMENDED_PAGE_SIZE,
    recommendedToolChains: RECOMMENDED_TOOL_CHAINS,
    guidance:
      "Start with the default compact profile on every heavy tool, then expand to balanced/full only " +
      "when the compact view lacks the detail you need. Set page_size to bound any profile; follow " +
      "pagination.next_cursor to resume. Cost bands are heuristic estimates — gate_budget_estimate " +
      "gives a scope-specific forecast before mutating.",
  };
}

/**
 * All rule ids the cost-hints table knows about. Used by the cost-hints test to
 * assert parity with `RULE_CATALOG` (every implemented rule has a cost hint).
 */
export function costHintRuleIds(): string[] {
  return RULE_COST_HINTS.map((h) => h.rule);
}

/** Re-export so callers can cross-check against the catalog in one import. */
export { RULE_CATALOG };
