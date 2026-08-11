// gate-intelligence pure-logic tests (P17.3).
//
// Pins the pure functions the three local tools compose: rule resolution,
// path classification, risk banding, budget estimation, and mutation
// narrative generation. No router, no bridge, no I/O — these are the
// unit-testable transforms advertised to AI clients.

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  resolveRules,
  classifyPath,
  classifyRisk,
  estimateBudget,
  previewImpact,
  explainMutation,
  parseIssueKey,
  assetKindFor,
  isLikelyFolder,
  type RuleFilter,
} from "./gate-intelligence.js";
import { RULE_CATALOG } from "./rule-catalog.js";

const ALL_RULE_IDS = RULE_CATALOG.filter((r) => r.implemented).map((r) => r.id).sort();

// ---------------------------------------------------------------------------
// Rule resolution
// ---------------------------------------------------------------------------

test("resolveRules with no filter returns every implemented rule", () => {
  const ids = resolveRules({}).map((r) => r.id).sort();
  assert.deepEqual(ids, ALL_RULE_IDS);
});

test("resolveRules with categories narrows to those rules", () => {
  const filter: RuleFilter = { categories: ["broken_references", "missing_scripts"] };
  const ids = resolveRules(filter).map((r) => r.id).sort();
  assert.deepEqual(ids, ["broken_references", "missing_scripts"]);
});

test("resolveRules with include_rules intersects the full set", () => {
  const filter: RuleFilter = { includeRules: ["broken_references", "no_such_rule"] };
  const ids = resolveRules(filter).map((r) => r.id).sort();
  assert.deepEqual(ids, ["broken_references"]);
});

test("resolveRules with exclude_rules subtracts from the full set", () => {
  const filter: RuleFilter = { excludeRules: ["broken_references", "missing_scripts"] };
  const ids = resolveRules(filter).map((r) => r.id).sort();
  assert.deepEqual(ids, ALL_RULE_IDS.filter((id) => id !== "broken_references" && id !== "missing_scripts"));
});

test("resolveRules empty-after-filter returns empty (NOT the full set)", () => {
  // The sentinel distinction: a narrow filter that matches nothing surfaces as
  // "no rules", never silently run-all.
  assert.deepEqual(resolveRules({ categories: ["does_not_exist"] }), []);
  assert.deepEqual(resolveRules({ excludeRules: ALL_RULE_IDS }), []);
});

test("resolveRules precedence: categories then include then exclude", () => {
  const filter: RuleFilter = {
    categories: ["broken_references", "missing_scripts", "import_health"],
    includeRules: ["broken_references", "missing_scripts"],
    excludeRules: ["missing_scripts"],
  };
  assert.deepEqual(
    resolveRules(filter).map((r) => r.id),
    ["broken_references"],
  );
});

test("resolveRules never returns planned (non-implemented) rules", () => {
  for (const r of resolveRules({})) assert.equal(r.implemented, true);
});

// ---------------------------------------------------------------------------
// Path classification
// ---------------------------------------------------------------------------

test("isLikelyFolder flags paths whose leaf has no dot", () => {
  assert.ok(isLikelyFolder("res://"));
  assert.ok(isLikelyFolder("res://Sprites"));
  assert.ok(isLikelyFolder("res://Sprites/"));
  assert.ok(!isLikelyFolder("res://Main.tscn"));
  assert.ok(!isLikelyFolder("res://Data.tres"));
});

test("assetKindFor maps Godot extensions", () => {
  assert.equal(assetKindFor("res://Main.tscn"), "scene");
  assert.equal(assetKindFor("res://Data.tres"), "resource");
  assert.equal(assetKindFor("res://Water.gdshader"), "shader");
  assert.equal(assetKindFor("res://icon.png.import"), "import");
  assert.equal(assetKindFor("res://Player.gd"), "script");
  assert.equal(assetKindFor("res://Sprites/"), "folder");
  assert.equal(assetKindFor("res://icon.png"), "imported-asset");
});

test("classifyPath derives rules-for-extension from the resolved set", () => {
  const rules = resolveRules({});
  const c = classifyPath("res://Main.tscn", rules);
  assert.equal(c.assetKind, "scene");
  assert.equal(c.isFolder, false);
  // .tscn is accepted by broken_references, missing_scripts, project_health,
  // scene_structure_health, script_audit.
  assert.ok(c.rulesForExtension.includes("broken_references"));
  assert.ok(c.rulesForExtension.includes("scene_structure_health"));
  // .tscn is NOT accepted by import_health (.import-only).
  assert.ok(!c.rulesForExtension.includes("import_health"));
});

test("classifyPath gives folders the full resolved rule set", () => {
  const rules = resolveRules({});
  const c = classifyPath("res://Sprites/", rules);
  assert.equal(c.isFolder, true);
  assert.equal(c.assetKind, "folder");
  assert.equal(c.rulesForExtension.length, rules.length);
});

test("classifyPath for an unsupported extension yields no rules", () => {
  const c = classifyPath("res://notes.md", resolveRules({}));
  assert.equal(c.assetKind, "unknown");
  assert.deepEqual(c.rulesForExtension, []);
});

// ---------------------------------------------------------------------------
// Risk classification
// ---------------------------------------------------------------------------

test("classifyRisk bands are monotonic in score", () => {
  // pathCount + ruleCount + high-fallout drive the score up.
  // low ≤ 2; moderate 3–5; high ≥ 6.
  assert.equal(classifyRisk(1, 0, 0).band, "low"); // score 1
  assert.equal(classifyRisk(1, 1, 0).band, "low"); // score 2
  assert.equal(classifyRisk(4, 1, 0).band, "moderate"); // score 3 (paths 1+4, rule 1)
  assert.equal(classifyRisk(12, 3, 4).band, "high"); // score 9
});

test("classifyRisk caps confidence at medium (shape-only)", () => {
  assert.equal(classifyRisk(1, 1, 0).confidence, "medium");
});

// ---------------------------------------------------------------------------
// Budget estimation
// ---------------------------------------------------------------------------

test("estimateBudget returns a positive duration lower bound + issue budget", () => {
  const result = estimateBudget(["res://Main.tscn"], resolveRules({}));
  assert.equal(result.scope.pathsHintCount, 1);
  assert.equal(result.scope.estimatedAssetCount, 1);
  assert.ok(result.estimate.estimatedDurationMs > 0);
  assert.ok(result.estimate.estimatedIssueBudget > 0);
  assert.equal(result.estimate.basis, "heuristic");
  assert.equal(result.estimate.confidence, "low");
  assert.ok(["small", "medium", "large"].includes(result.estimate.tokenBand));
  assert.ok(result.heuristicNote.length > 0);
});

test("estimateBudget expands folders to a fixed estimate", () => {
  const result = estimateBudget(["res://Sprites/"], resolveRules({}));
  assert.equal(result.scope.folderCount, 1);
  assert.ok(result.scope.estimatedAssetCount > 1, "folder must expand to >1 asset");
});

test("estimateBudget rulesProjected matches the resolved rule set", () => {
  const result = estimateBudget(["res://a.tres"], resolveRules({ categories: ["import_health"] }));
  assert.deepEqual(result.rulesProjected, ["import_health"]);
});

// ---------------------------------------------------------------------------
// Impact preview (composition)
// ---------------------------------------------------------------------------

test("previewImpact classifies every path + reports a risk band", () => {
  const result = previewImpact(["res://Main.tscn", "res://Sprites/"], {});
  assert.equal(result.scope.pathsHintCount, 2);
  assert.equal(result.perPath.length, 2);
  assert.equal(result.perPath[0].assetKind, "scene");
  assert.equal(result.perPath[1].isFolder, true);
  assert.ok(["low", "moderate", "high"].includes(result.risk.band));
  assert.deepEqual(result.rulesProjected.sort(), ALL_RULE_IDS);
});

test("previewImpact assetKinds tallies the per-path kinds", () => {
  const result = previewImpact(["res://a.tscn", "res://b.tscn", "res://c.tres"], {});
  assert.equal(result.scope.assetKinds.scene, 2);
  assert.equal(result.scope.assetKinds.resource, 1);
});

// ---------------------------------------------------------------------------
// Mutation explain
// ---------------------------------------------------------------------------

test("parseIssueKey splits the canonical 4-part key", () => {
  const k = parseIssueKey("broken_references|ERROR|res://Main.tscn|broken_scene_reference");
  assert.deepEqual(k, {
    ruleId: "broken_references",
    severity: "ERROR",
    assetPath: "res://Main.tscn",
    issueCode: "broken_scene_reference",
  });
});

test("parseIssueKey returns null for malformed keys", () => {
  assert.equal(parseIssueKey("only|three|parts"), null);
  assert.equal(parseIssueKey(""), null);
  assert.equal(parseIssueKey("a||b|c|d"), null); // empty segment
});

test("explainMutation produces a narrative + summary from gate-run data", () => {
  const result = explainMutation({
    outcome: "failed",
    newErrors: 2,
    newWarnings: 1,
    resolvedErrors: 0,
    resolvedWarnings: 0,
    toolName: "godot_open_mcp_node_modify",
    totalMs: 120,
    agentNextSteps: ["Fix the issue and retry."],
  });
  assert.ok(result.narrative.includes("failed the gate"));
  assert.ok(result.narrative.includes("2 new error(s)"));
  assert.ok(result.narrative.includes("120 ms"));
  assert.equal(result.summary.outcome, "failed");
  assert.equal(result.summary.newErrors, 2);
  assert.equal(result.summary.tool, "godot_open_mcp_node_modify");
  assert.deepEqual(result.agentNextSteps, ["Fix the issue and retry."]);
});

test("explainMutation enriches issue keys with rootCause + groups by rule", () => {
  const result = explainMutation({
    outcome: "failed",
    newErrors: 1,
    newIssueKeys: [
      "broken_references|ERROR|res://Main.tscn|broken_scene_reference",
      "missing_scripts|ERROR|res://Player.tscn|missing_script",
      "broken_references|ERROR|res://Level.tscn|broken_scene_reference",
    ],
  });
  assert.ok(result.newIssues);
  assert.equal(result.newIssues!.length, 3);
  // rootCause enrichment from RULE_CATALOG.
  const broken = result.newIssues!.find((i) => i.ruleId === "broken_references")!;
  assert.equal(broken.rootCause, "missing_uid_reference");
  // Per-rule histogram.
  assert.equal(result.issuesByRule!["broken_references"], 2);
  assert.equal(result.issuesByRule!["missing_scripts"], 1);
});

test("explainMutation degrades gracefully on empty input", () => {
  const result = explainMutation({});
  assert.ok(result.narrative.length > 0);
  assert.equal(result.summary.outcome, "unknown");
  assert.equal(result.summary.newErrors, 0);
  assert.equal(result.newIssues, undefined);
  assert.equal(result.issuesByRule, undefined);
});

test("explainMutation surfaces mutation_error when provided", () => {
  const result = explainMutation({ outcome: "failed", mutationError: "boom" });
  assert.ok(result.narrative.includes("boom"));
  assert.equal(result.summary.mutationError, "boom");
});
