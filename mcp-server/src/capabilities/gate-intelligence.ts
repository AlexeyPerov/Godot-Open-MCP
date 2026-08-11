// Gate-intelligence pure logic (P17.3).
//
// The testable core shared by the three local gate-intelligence tools
// (`impact_preview` / `gate_budget_estimate` / `mutation_explain`). Pure
// functions over the TS `RULE_CATALOG` + `cost-hints` table — no I/O, no bridge
// dependency, no mutation. The `tool-router.ts` handlers are thin wrappers that
// parse args, call these functions, and stamp the result with
// `_source: "local"`.
//
// Adapted from Unity Open MCP's `GateIntelligenceTools.cs` (adapt fidelity):
//   - `resolveRules` mirrors `VerifyGateAdapter.ResolveRuleIds` semantics
//     (categories → include_rules → exclude_rules; empty-after-filter ⇒ empty,
//      NOT run-all).
//   - `classifyPath` mirrors the per-rule extension/kind classification that
//     lives inside each Godot rule's `Scan` (no equivalent TS surface existed).
//   - `classifyRisk` mirrors Unity's `ClassifyRisk` score + band math.
//   - `estimateBudget` mirrors the heuristic branch of Unity's
//     `GateBudgetEstimate` (Godot has no VerifyCacheService, so the cache/sample
//     branches are dropped — see the P17.3 plan's intentional deltas).
//   - `explainMutation` mirrors Unity's `MutationExplain.BuildNarrative` but
//     consumes caller-provided gate data (Godot has no server-side run history).
//
// Intentional delta: the Godot gate runs EVERY registered rule against a scope
// (no rule pre-selection at the adapter layer — see VerifyGateAdapter). So
// "auto-selected rules" for a scope = the full implemented roster, and
// `classifyPath` narrows to the rules whose `applicableExtensions` accept the
// path. This is the per-path view the gate envelope's `categoriesRun` cannot
// give (that field is always the full 8-rule roster).

import { RULE_CATALOG, type RuleCapability } from "./rule-catalog.js";
import { COST_BANDS, RULE_COST_HINTS, ruleCostFor, bandForTokens, type CostBand } from "./cost-hints.js";

// ---------------------------------------------------------------------------
// Path classification
// ---------------------------------------------------------------------------

/** Godot asset kinds derived from path extension. Kept aligned with the rule
 * catalog's `applicableAssetKinds` vocabulary. */
export type AssetKind =
  | "scene"
  | "resource"
  | "shader"
  | "import"
  | "script"
  | "animation"
  | "imported-asset"
  | "folder"
  | "unknown";

const EXTENSION_KIND: Record<string, AssetKind> = {
  ".tscn": "scene",
  ".scn": "scene",
  ".tres": "resource",
  ".res": "resource",
  ".gdshader": "shader",
  ".shader": "shader",
  ".import": "import",
  ".gd": "script",
  ".cs": "script",
};

const IMPORTED_ASSET_EXTENSIONS = new Set([
  ".png", ".jpg", ".jpeg", ".webp", ".svg", ".bmp",
  ".wav", ".ogg", ".oggvorbis", ".mp3", ".flac",
  ".glb", ".gltf", ".fbx", ".obj", ".dae",
  ".otf", ".ttf", ".woff", ".woff2",
  ".json", ".csv", ".txt",
]);

/** Lowercased extension including the leading dot, or "" when none. */
function extOf(path: string): string {
  const slash = path.lastIndexOf("/");
  const leaf = slash >= 0 ? path.slice(slash + 1) : path;
  const dot = leaf.lastIndexOf(".");
  return dot >= 0 ? leaf.slice(dot).toLowerCase() : "";
}

/** True when the path's leaf segment has no `.` (the rules' IsLikelyDirectory
 * heuristic). `res://`, `res://Sprites`, `res://Sprites/` are folders. */
export function isLikelyFolder(path: string): boolean {
  const slash = path.lastIndexOf("/");
  const leaf = slash >= 0 ? path.slice(slash + 1) : path;
  return leaf.length === 0 || !leaf.includes(".");
}

/** Classify a single path into an {@link AssetKind}. */
export function assetKindFor(path: string): AssetKind {
  if (isLikelyFolder(path)) return "folder";
  const ext = extOf(path);
  if (EXTENSION_KIND[ext]) return EXTENSION_KIND[ext];
  if (IMPORTED_ASSET_EXTENSIONS.has(ext)) return "imported-asset";
  return "unknown";
}

export interface PathClassification {
  path: string;
  isFolder: boolean;
  assetKind: AssetKind;
  /** Rule ids (from the resolved set) whose `applicableExtensions` accept this
   * path's extension. Folders carry the full resolved set (the gate walks
   * subtrees). */
  rulesForExtension: string[];
}

/** The rules whose declared `applicableExtensions` accept `path`'s extension. */
function rulesAcceptingPath(path: string, rules: RuleCapability[]): string[] {
  const ext = extOf(path);
  const accepted: string[] = [];
  for (const rule of rules) {
    const exts = rule.applicableExtensions;
    if (!exts || exts.length === 0) continue;
    if (exts.some((e) => e.toLowerCase() === ext)) accepted.push(rule.id);
  }
  return accepted;
}

/** Classify one path against the resolved rule set. */
export function classifyPath(path: string, rules: RuleCapability[]): PathClassification {
  const isFolder = isLikelyFolder(path);
  const assetKind = assetKindFor(path);
  const rulesForExtension = isFolder
    ? rules.map((r) => r.id)
    : rulesAcceptingPath(path, rules);
  return { path, isFolder, assetKind, rulesForExtension };
}

// ---------------------------------------------------------------------------
// Rule resolution
// ---------------------------------------------------------------------------

export interface RuleFilter {
  /** Explicit rule ids; when non-empty, the resolved set starts from these
   * (same semantics as `validate_edit` `categories`). */
  categories?: string[];
  /** Allow-list intersected with the resolved set. */
  includeRules?: string[];
  /** Deny-list; always wins. */
  excludeRules?: string[];
}

/**
 * Resolve the rule set for a scope. Mirrors the `categories` → `include_rules`
 * → `exclude_rules` precedence. An empty-after-filter result is an EMPTY array
 * (NOT the full roster) — the sentinel distinction Unity preserves so an
 * over-narrow filter surfaces as "no rules" rather than silently running all.
 *
 * Only `implemented` rules are returned (planned stubs never run in the gate).
 */
export function resolveRules(filter: RuleFilter = {}): RuleCapability[] {
  const implemented = RULE_CATALOG.filter((r) => r.implemented);
  let ids: Set<string>;
  if (filter.categories && filter.categories.length > 0) {
    ids = new Set(filter.categories);
  } else {
    ids = new Set(implemented.map((r) => r.id));
  }
  if (filter.includeRules && filter.includeRules.length > 0) {
    const inc = new Set(filter.includeRules);
    ids = new Set([...ids].filter((id) => inc.has(id)));
  }
  if (filter.excludeRules && filter.excludeRules.length > 0) {
    const exc = new Set(filter.excludeRules);
    ids = new Set([...ids].filter((id) => !exc.has(id)));
  }
  // Preserve RULE_CATALOG order; only implemented + surviving ids.
  return implemented.filter((r) => ids.has(r.id));
}

// ---------------------------------------------------------------------------
// Risk classification
// ---------------------------------------------------------------------------

/** Asset kinds whose breakage cascades (referenced by many others). Mirrors
 * Unity's "high-fallout kinds" set (prefab/scene/...→ scene/resource/script). */
const HIGH_FALLOUT_KINDS = new Set<AssetKind>(["scene", "resource", "script", "animation"]);

export type RiskBand = "low" | "moderate" | "high";
export type Confidence = "low" | "medium" | "high";

export interface RiskClassification {
  band: RiskBand;
  score: number;
  confidence: Confidence;
}

/** Coarse risk band for a planned scope. Mirrors Unity's ClassifyRisk score
 * math: path-count tiers + rule-count tiers + high-fallout-kind bonus. */
export function classifyRisk(
  pathCount: number,
  ruleCount: number,
  highFalloutKindCount: number,
): RiskClassification {
  let score = 0;
  if (pathCount >= 1) score += 1;
  if (pathCount >= 4) score += 1;
  if (pathCount >= 12) score += 1;
  if (ruleCount >= 1) score += 1;
  if (ruleCount >= 3) score += 1;
  score += Math.min(highFalloutKindCount, 4);
  let band: RiskBand;
  if (score <= 2) band = "low";
  else if (score <= 5) band = "moderate";
  else band = "high";
  // Shape-only classification (no disk existence check) caps confidence at medium.
  return { band, score, confidence: "medium" };
}

// ---------------------------------------------------------------------------
// Budget estimation
// ---------------------------------------------------------------------------

/** Estimated asset-expansion count for one folder path when no disk walk is
 * performed. Conservative default; the heuristic note flags it. */
const FOLDER_ASSET_ESTIMATE = 10;

export interface BudgetEstimate {
  basis: "heuristic";
  confidence: Confidence;
  /** Lower bound on the real gate validation duration (ms). */
  estimatedDurationMs: number;
  /** Upper bound on the issue count the gate might surface. */
  estimatedIssueBudget: number;
  /** Coarse output-cost band for the forecast validation result. */
  tokenBand: CostBand;
  /** Estimated token cost underpinning `tokenBand`. */
  estimatedTokens: number;
}

export interface BudgetResult {
  scope: { pathsHintCount: number; estimatedAssetCount: number; folderCount: number };
  rulesProjected: string[];
  estimate: BudgetEstimate;
  heuristicNote: string;
}

/**
 * Forecast validation duration + issue budget for a planned scope. Pure
 * heuristic from the cost-hints table + path classification — no live scan
 * (Godot has no VerifyCacheService; see the P17.3 plan deltas).
 *
 * `estimatedDurationMs` is a lower bound; `estimatedIssueBudget` is an upper
 * bound. Both assume one issue ≈ 60 tokens of validate_edit detail when mapping
 * to a token band.
 */
export function estimateBudget(paths: string[], rules: RuleCapability[]): BudgetResult {
  let assetCount = 0;
  let folderCount = 0;
  for (const p of paths) {
    if (isLikelyFolder(p)) {
      folderCount += 1;
      assetCount += FOLDER_ASSET_ESTIMATE;
    } else {
      assetCount += 1;
    }
  }
  if (assetCount < 1) assetCount = 1;

  const ruleIds = rules.map((r) => r.id);
  let durationMs = 0;
  let issueBudget = 0;
  for (const rule of rules) {
    const hint = ruleCostFor(rule.id);
    durationMs += hint.msPerAsset * assetCount;
    issueBudget += hint.issueWeight * assetCount;
  }

  const estimatedTokens = issueBudget * 60;
  const tokenBand = bandForTokens(estimatedTokens);

  return {
    scope: { pathsHintCount: paths.length, estimatedAssetCount: assetCount, folderCount },
    rulesProjected: ruleIds,
    estimate: {
      basis: "heuristic",
      confidence: "low",
      estimatedDurationMs: durationMs,
      estimatedIssueBudget: issueBudget,
      tokenBand,
      estimatedTokens,
    },
    heuristicNote:
      "Heuristic estimate from the cost-hints table — no live scan was run. " +
      "Duration is a lower bound; issue budget is an upper bound. Folders expand to " +
      `~${FOLDER_ASSET_ESTIMATE} assets each (estimate); run godot_open_mcp_validate_edit ` +
      "for actuals.",
  };
}

// ---------------------------------------------------------------------------
// Impact preview (composes resolution + classification + risk)
// ---------------------------------------------------------------------------

export interface ImpactPreviewResult {
  scope: { pathsHintCount: number; assetKinds: Record<string, number> };
  rulesProjected: string[];
  risk: RiskClassification;
  perPath: PathClassification[];
  heuristicNote: string;
}

/** Project the gate's view of a planned scope WITHOUT mutating. Resolves the
 * rule set, classifies each path, and reports a coarse risk band. */
export function previewImpact(paths: string[], filter: RuleFilter = {}): ImpactPreviewResult {
  const rules = resolveRules(filter);
  const ruleIds = rules.map((r) => r.id);

  const perPath = paths.map((p) => classifyPath(p, rules));
  const assetKinds: Record<string, number> = {};
  let highFallout = 0;
  for (const c of perPath) {
    assetKinds[c.assetKind] = (assetKinds[c.assetKind] ?? 0) + 1;
    if (HIGH_FALLOUT_KINDS.has(c.assetKind)) highFallout += 1;
  }
  const risk = classifyRisk(paths.length, ruleIds.length, highFallout);

  return {
    scope: { pathsHintCount: paths.length, assetKinds },
    rulesProjected: ruleIds,
    risk,
    perPath,
    heuristicNote:
      "Projection over the rule catalog + path shape — no rule scan ran. " +
      "Use godot_open_mcp_validate_edit to confirm actual issues before or after mutating.",
  };
}

// ---------------------------------------------------------------------------
// Mutation explain
// ---------------------------------------------------------------------------

export type GateOutcome = "passed" | "warned" | "failed" | "skipped" | "unavailable";

/** The gate-run data the caller provides (copied from the mutating tool's
 * `result.gate` block, or from a `godot_open_mcp_delta` result). All fields
 * optional — the tool degrades gracefully on partial input. */
export interface ExplainInput {
  outcome?: GateOutcome;
  newErrors?: number;
  newWarnings?: number;
  resolvedErrors?: number;
  resolvedWarnings?: number;
  agentNextSteps?: string[];
  /** Canonical issue keys (`ruleId|severity|assetPath|issueCode`). */
  newIssueKeys?: string[];
  resolvedIssueKeys?: string[];
  toolName?: string;
  totalMs?: number;
  checkpointMs?: number;
  validationMs?: number;
  categoriesRun?: string[];
  mutationError?: string;
}

export interface ExplainSummary {
  tool: string;
  outcome: GateOutcome | "unknown";
  newErrors: number;
  newWarnings: number;
  resolvedErrors: number;
  resolvedWarnings: number;
  totalGateDurationMs?: number;
  checkpointDurationMs?: number;
  validationDurationMs?: number;
  mutationError?: string;
}

export interface IssueKeyBreakdown {
  ruleId: string;
  severity: string;
  assetPath: string;
  issueCode: string;
  rootCause?: string;
}

export interface ExplainResult {
  narrative: string;
  summary: ExplainSummary;
  issuesByRule?: Record<string, number>;
  newIssues?: IssueKeyBreakdown[];
  resolvedIssues?: IssueKeyBreakdown[];
  agentNextSteps?: string[];
  categoriesRun?: string[];
  heuristicNote: string;
}

/** Parse a canonical issue key (`ruleId|severity|assetPath|issueCode`). Returns
 * null when malformed. */
export function parseIssueKey(key: string): {
  ruleId: string;
  severity: string;
  assetPath: string;
  issueCode: string;
} | null {
  const parts = key.split("|");
  if (parts.length !== 4) return null;
  const [ruleId, severity, assetPath, issueCode] = parts;
  if (!ruleId || !severity || !assetPath || !issueCode) return null;
  return { ruleId, severity, assetPath, issueCode };
}

/** Look up the rootCause code for a (ruleId, issueCode) pair in RULE_CATALOG. */
function rootCauseFor(ruleId: string, issueCode: string): string | undefined {
  const rule = RULE_CATALOG.find((r) => r.id === ruleId);
  if (!rule) return undefined;
  const issue = rule.issues.find((i) => i.code === issueCode);
  return issue?.rootCause;
}

function num(v: unknown): number {
  return typeof v === "number" && Number.isFinite(v) ? v : 0;
}

/** Enrich parsed keys with rootCause from the catalog. */
function enrichKeys(keys: string[] | undefined): IssueKeyBreakdown[] | undefined {
  if (!keys || keys.length === 0) return undefined;
  const out: IssueKeyBreakdown[] = [];
  for (const key of keys) {
    const parsed = parseIssueKey(key);
    if (!parsed) continue;
    out.push({ ...parsed, rootCause: rootCauseFor(parsed.ruleId, parsed.issueCode) });
  }
  return out.length > 0 ? out : undefined;
}

/** Build the per-rule histogram from enriched issue breakdowns. */
function countByRule(issues: IssueKeyBreakdown[] | undefined): Record<string, number> | undefined {
  if (!issues || issues.length === 0) return undefined;
  const counts: Record<string, number> = {};
  for (const i of issues) counts[i.ruleId] = (counts[i.ruleId] ?? 0) + 1;
  return counts;
}

function outcomeVerb(outcome: GateOutcome | "unknown"): string {
  switch (outcome) {
    case "passed": return "passed the gate";
    case "warned": return "completed with gate warnings";
    case "failed": return "failed the gate";
    case "skipped": return "ran with the gate skipped";
    case "unavailable": return "could not be compared (checkpoint unavailable)";
    default: return "ran";
  }
}

/** Turn a finished gate run into a human-readable narrative + structured
 * summary. Pure transform over caller-provided data. */
export function explainMutation(input: ExplainInput): ExplainResult {
  const outcome = input.outcome ?? "unknown";
  const newErrors = num(input.newErrors);
  const newWarnings = num(input.newWarnings);
  const resolvedErrors = num(input.resolvedErrors);
  const resolvedWarnings = num(input.resolvedWarnings);
  const tool = input.toolName ?? "the mutation";

  const newIssues = enrichKeys(input.newIssueKeys);
  const resolvedIssues = enrichKeys(input.resolvedIssueKeys);

  const parts: string[] = [];
  parts.push(`${tool} ${outcomeVerb(outcome)}.`);
  parts.push(
    `${newErrors} new error(s), ${newWarnings} new warning(s), ` +
      `${resolvedErrors} resolved error(s), ${resolvedWarnings} resolved warning(s).`,
  );
  if (typeof input.totalMs === "number") {
    parts.push(`Gate took ${input.totalMs} ms`);
    if (typeof input.checkpointMs === "number" || typeof input.validationMs === "number") {
      parts.push(
        `(checkpoint ${input.checkpointMs ?? 0} ms, validate ${input.validationMs ?? 0} ms)`,
      );
    }
    parts.push(".");
  }
  if (input.mutationError) parts.push(`Mutation error: ${input.mutationError}.`);

  const summary: ExplainSummary = {
    tool,
    outcome,
    newErrors,
    newWarnings,
    resolvedErrors,
    resolvedWarnings,
  };
  if (typeof input.totalMs === "number") summary.totalGateDurationMs = input.totalMs;
  if (typeof input.checkpointMs === "number") summary.checkpointDurationMs = input.checkpointMs;
  if (typeof input.validationMs === "number") summary.validationDurationMs = input.validationMs;
  if (input.mutationError) summary.mutationError = input.mutationError;

  const result: ExplainResult = {
    narrative: parts.join(" "),
    summary,
    heuristicNote:
      "Narrative generated locally from the gate-run data you supplied. Pass new_issue_keys / " +
      "resolved_issue_keys (from godot_open_mcp_delta) for a per-rule breakdown enriched with rootCause.",
  };
  const byRule = countByRule(newIssues);
  if (byRule) result.issuesByRule = byRule;
  if (newIssues) result.newIssues = newIssues;
  if (resolvedIssues) result.resolvedIssues = resolvedIssues;
  if (input.agentNextSteps && input.agentNextSteps.length > 0) result.agentNextSteps = input.agentNextSteps;
  if (input.categoriesRun && input.categoriesRun.length > 0) result.categoriesRun = input.categoriesRun;
  return result;
}

// Re-exports so the router handlers import everything from one module.
export { RULE_CATALOG, COST_BANDS, RULE_COST_HINTS };
