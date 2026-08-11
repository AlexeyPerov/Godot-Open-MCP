// cost-hints table tests (P17.3).
//
// Pins the structure of the cost-hints calibration: the cost-band ranges, the
// per-rule cost hints (parity with RULE_CATALOG's 8 implemented rules), the
// per-tool per-profile table shape, and the builder output. The exact numeric
// constants are heuristic estimates (refined on fixture evidence) so the tests
// pin structure + coverage, not the literal ms/weight values.

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  COST_BANDS,
  TOOL_COST_HINTS,
  RULE_COST_HINTS,
  RECOMMENDED_PAGE_SIZE,
  RECOMMENDED_TOOL_CHAINS,
  buildCostHints,
  bandForTokens,
  ruleCostFor,
  costHintRuleIds,
} from "./cost-hints.js";
import { RULE_CATALOG } from "./rule-catalog.js";

// ---------------------------------------------------------------------------
// Cost bands
// ---------------------------------------------------------------------------

test("COST_BANDS covers small / medium / large with ascending ranges", () => {
  assert.equal(COST_BANDS.small.minTokens, 0);
  assert.ok(COST_BANDS.small.maxTokens > COST_BANDS.small.minTokens);
  assert.equal(COST_BANDS.medium.minTokens, COST_BANDS.small.maxTokens);
  assert.ok(COST_BANDS.medium.maxTokens > COST_BANDS.medium.minTokens);
  assert.equal(COST_BANDS.large.minTokens, COST_BANDS.medium.maxTokens);
  // large is unbounded — the 0 sentinel.
  assert.equal(COST_BANDS.large.maxTokens, 0);
});

test("bandForTokens maps thresholds onto the right band", () => {
  assert.equal(bandForTokens(0), "small");
  assert.equal(bandForTokens(COST_BANDS.small.maxTokens - 1), "small");
  assert.equal(bandForTokens(COST_BANDS.small.maxTokens), "medium");
  assert.equal(bandForTokens(COST_BANDS.medium.maxTokens - 1), "medium");
  assert.equal(bandForTokens(COST_BANDS.large.minTokens), "large");
  assert.equal(bandForTokens(999_999), "large");
});

// ---------------------------------------------------------------------------
// Per-rule cost hints — parity with RULE_CATALOG
// ---------------------------------------------------------------------------

test("RULE_COST_HINTS covers every implemented rule id exactly once", () => {
  const implemented = new Set(RULE_CATALOG.filter((r) => r.implemented).map((r) => r.id));
  const hinted = new Set(costHintRuleIds());
  assert.deepEqual(
    [...implemented].sort(),
    [...hinted].sort(),
    "every implemented rule must have a cost hint",
  );
  assert.equal(costHintRuleIds().length, new Set(costHintRuleIds()).size, "no duplicate rule ids");
});

test("every RULE_COST_HINTS entry has positive msPerAsset + issueWeight", () => {
  for (const hint of RULE_COST_HINTS) {
    assert.ok(hint.msPerAsset > 0, `${hint.rule} msPerAsset must be positive`);
    assert.ok(hint.issueWeight > 0, `${hint.rule} issueWeight must be positive`);
    assert.ok(hint.rule.length > 0);
  }
});

test("ruleCostFor returns a sane default for an unknown rule", () => {
  const def = ruleCostFor("does_not_exist");
  assert.equal(def.rule, "does_not_exist");
  assert.ok(def.msPerAsset > 0);
  assert.ok(def.issueWeight > 0);
});

test("ruleCostFor resolves a known rule", () => {
  const hint = ruleCostFor("broken_references");
  assert.equal(hint.rule, "broken_references");
  assert.equal(hint.msPerAsset, RULE_COST_HINTS[0].msPerAsset);
});

// ---------------------------------------------------------------------------
// Per-tool cost hints
// ---------------------------------------------------------------------------

test("every TOOL_COST_HINTS entry maps compact→small, balanced→medium, full→large", () => {
  // The Unity-invariant: the band is the signal; per-tool differences live in
  // the profileControls / pageSizeBounds copy.
  for (const hint of TOOL_COST_HINTS) {
    assert.equal(hint.profiles.compact.band, "small");
    assert.equal(hint.profiles.balanced.band, "medium");
    assert.equal(hint.profiles.full.band, "large");
    assert.ok(hint.tool.startsWith("godot_open_mcp_"));
    assert.ok(hint.profileControls.length > 0);
    assert.ok(hint.pageSizeBounds.length > 0);
  }
});

test("TOOL_COST_HINTS entries are unique by tool name", () => {
  const names = TOOL_COST_HINTS.map((h) => h.tool);
  assert.equal(names.length, new Set(names).size);
});

test("RECOMMENDED_PAGE_SIZE keys are a subset of the TOOL_COST_HINTS tools", () => {
  const hinted = new Set(TOOL_COST_HINTS.map((h) => h.tool));
  for (const tool of Object.keys(RECOMMENDED_PAGE_SIZE)) {
    assert.ok(hinted.has(tool), `${tool} page size without a cost hint entry`);
    assert.ok(RECOMMENDED_PAGE_SIZE[tool] > 0);
  }
});

// ---------------------------------------------------------------------------
// Recommended tool chains + builder
// ---------------------------------------------------------------------------

test("RECOMMENDED_TOOL_CHAINS includes the mutate-then-verify chain", () => {
  const ids = RECOMMENDED_TOOL_CHAINS.map((c) => c.id);
  assert.ok(ids.includes("mutate-then-verify"));
  const mvt = RECOMMENDED_TOOL_CHAINS.find((c) => c.id === "mutate-then-verify")!;
  assert.ok(mvt.steps.some((s) => s.includes("impact_preview")));
  assert.ok(mvt.steps.some((s) => s.includes("gate_budget_estimate")));
  assert.ok(mvt.steps.some((s) => s.includes("mutation_explain")));
});

test("buildCostHints returns the bands + tools + chains + guidance", () => {
  const block = buildCostHints();
  assert.equal(block.bands, COST_BANDS);
  assert.equal(block.tools, TOOL_COST_HINTS);
  assert.equal(block.recommendedPageSize, RECOMMENDED_PAGE_SIZE);
  assert.ok(block.guidance.length > 0);
  assert.ok(block.recommendedToolChains.length > 0);
});
