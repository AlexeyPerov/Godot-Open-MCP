// Regression compare tests (P15.1).
//
// Covers the global-threshold gate, per-category thresholds (override + global
// fallback), the OR-of-all-gates verdict, improvement (negative delta never
// regresses), and the CI-log summary formatter.
//
// Adapted from Unity Open MCP's BaselineStoreTests.cs (copy for the threshold
// semantics — strict `>`, OR of global + per-rule, fallback to global).

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  compareBaselines,
  formatRegressionSummary,
} from "./regression-compare.js";
import {
  buildBaseline,
  emptySummary,
  type BaselineFile,
  type BaselineIssue,
} from "./baseline-schema.js";

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function makeBaseline(
  errorCount: number,
  warnCount: number,
  rules: { ruleId: string; error: number }[] = [],
): BaselineFile {
  // Build a baseline with explicit per-rule error counts (independent of the
  // issue list) by constructing the file shape directly.
  const issues: BaselineIssue[] = [];
  for (const rule of rules) {
    for (let i = 0; i < rule.error; i++) {
      issues.push({
        ruleId: rule.ruleId,
        severity: "Error",
        assetPath: `res://${rule.ruleId}/${i}.tscn`,
        issueCode: "broken_scene_reference",
        description: "x",
      });
    }
  }
  // Pad warnings so the summary matches errorCount/warnCount.
  while (issues.filter((i) => i.severity === "Error").length < errorCount) {
    issues.push({
      ruleId: "padding",
      severity: "Error",
      assetPath: `res://pad-e/${issues.length}.tscn`,
      issueCode: "broken_scene_reference",
      description: "x",
    });
  }
  const baseline = buildBaseline(
    issues,
    [...new Set(issues.map((i) => i.ruleId))],
    [],
    "desktop",
  );
  // Force the summary to the requested counts so global tests are exact.
  baseline.summary = { error: errorCount, warn: warnCount, info: 0 };
  // Ensure per-rule entries reflect the requested rule breakdown (including
  // rules with explicit zero counts).
  const ruleIds = new Set<string>(rules.map((r) => r.ruleId));
  for (const rid of ruleIds) {
    const existing = baseline.rules.find((r) => r.ruleId === rid);
    const requested = rules.find((r) => r.ruleId === rid)!;
    if (existing) {
      existing.error = requested.error;
    } else {
      baseline.rules.push({ ruleId: rid, error: requested.error, warn: 0, info: 0, issueKeys: [] });
    }
  }
  return baseline;
}

// ---------------------------------------------------------------------------
// Global threshold
// ---------------------------------------------------------------------------

test("no regression when error delta is within the global threshold", () => {
  const baseline = makeBaseline(2, 0);
  const current = makeBaseline(3, 0); // +1 error
  const detail = compareBaselines(current, baseline, 1, null); // threshold 1 → +1 tolerated
  assert.equal(detail.regressed, false);
  assert.equal(detail.errorDelta, 1);
  assert.equal(detail.errorThreshold, 1);
  assert.equal(detail.perRule, null); // global-only path
});

test("regression when error delta exceeds the global threshold", () => {
  const baseline = makeBaseline(2, 0);
  const current = makeBaseline(5, 0); // +3 errors
  const detail = compareBaselines(current, baseline, 1, null); // threshold 1 → +3 > 1
  assert.equal(detail.regressed, true);
  assert.equal(detail.errorDelta, 3);
});

test("delta equal to threshold is tolerated (strict greater-than)", () => {
  const baseline = makeBaseline(0, 0);
  const current = makeBaseline(2, 0); // +2
  const detail = compareBaselines(current, baseline, 2, null); // threshold 2 → +2 == 2 OK
  assert.equal(detail.regressed, false);
});

test("improvement (negative delta) never regresses", () => {
  const baseline = makeBaseline(5, 0);
  const current = makeBaseline(2, 0); // -3
  const detail = compareBaselines(current, baseline, 0, null);
  assert.equal(detail.regressed, false);
  assert.equal(detail.errorDelta, -3);
});

test("default threshold 0 fails on any error increase", () => {
  const baseline = makeBaseline(0, 0);
  const current = makeBaseline(1, 0);
  const detail = compareBaselines(current, baseline, 0, null);
  assert.equal(detail.regressed, true);
});

// ---------------------------------------------------------------------------
// Per-category thresholds
// ---------------------------------------------------------------------------

test("per-category threshold overrides the global for that rule", () => {
  const baseline = makeBaseline(0, 0, [{ ruleId: "broken_references", error: 0 }]);
  const current = makeBaseline(3, 0, [{ ruleId: "broken_references", error: 3 }]); // +3
  // Global threshold 5 tolerates the +3; per-rule threshold 5 also tolerates it.
  // (When perCat is present, the overall verdict is OR(global, every per-rule).)
  const perCat = new Map([["broken_references", 5]]);
  const detail = compareBaselines(current, baseline, 5, perCat);
  assert.equal(detail.regressed, false);
  assert.equal(detail.perRule!.length, 1);
  const rule = detail.perRule![0]!;
  assert.equal(rule.ruleId, "broken_references");
  assert.equal(rule.errorDelta, 3);
  assert.equal(rule.errorThreshold, 5); // overridden
  assert.equal(rule.regressed, false);
});

test("per-category breach fails even when the global is within budget", () => {
  const baseline = makeBaseline(0, 0, [
    { ruleId: "broken_references", error: 0 },
    { ruleId: "missing_scripts", error: 0 },
  ]);
  const current = makeBaseline(3, 0, [
    { ruleId: "broken_references", error: 3 }, // +3, threshold 1 → FAIL
    { ruleId: "missing_scripts", error: 0 }, // +0, OK
  ]);
  const perCat = new Map([["broken_references", 1]]);
  const detail = compareBaselines(current, baseline, 10, perCat); // global 10 would tolerate +3
  assert.equal(detail.regressed, true); // per-rule FAIL wins
});

test("rule absent from per_category_thresholds falls back to global", () => {
  const baseline = makeBaseline(0, 0, [{ ruleId: "broken_references", error: 0 }]);
  const current = makeBaseline(2, 0, [{ ruleId: "broken_references", error: 2 }]);
  const perCat = new Map([["missing_scripts", 5]]); // broken_references not named
  const detail = compareBaselines(current, baseline, 0, perCat); // global 0 → +2 fails
  assert.equal(detail.regressed, true);
  const rule = detail.perRule!.find((r) => r.ruleId === "broken_references")!;
  assert.equal(rule.errorThreshold, 0); // fell back to global
});

test("overall verdict is the OR of the global gate and every per-rule gate", () => {
  // Global fails (+2 > 0) but per-rule is within budget → still regressed (OR).
  const baseline = makeBaseline(0, 0, [{ ruleId: "broken_references", error: 0 }]);
  const current = makeBaseline(2, 0, [{ ruleId: "broken_references", error: 2 }]);
  const perCat = new Map([["broken_references", 5]]); // rule OK
  const detail = compareBaselines(current, baseline, 0, perCat); // global FAIL
  assert.equal(detail.regressed, true); // global FAIL → OR is true
});

test("per-rule includes rules present only on one side + explicitly-named keys", () => {
  const baseline = makeBaseline(0, 0, [{ ruleId: "broken_references", error: 0 }]);
  const current = makeBaseline(0, 0, [{ ruleId: "missing_scripts", error: 0 }]);
  const perCat = new Map([
    ["broken_references", 0],
    ["script_audit", 2], // named explicitly but absent from both sides
  ]);
  const detail = compareBaselines(current, baseline, 0, perCat);
  const ruleIds = detail.perRule!.map((r) => r.ruleId);
  assert.ok(ruleIds.includes("broken_references")); // baseline-only
  assert.ok(ruleIds.includes("missing_scripts")); // current-only
  assert.ok(ruleIds.includes("script_audit")); // explicitly named
});

test("a rule newly appearing with errors respects its threshold", () => {
  const baseline = makeBaseline(0, 0); // no rules at all
  const current = makeBaseline(2, 0, [{ ruleId: "broken_references", error: 2 }]);
  const perCat = new Map([["broken_references", 1]]); // +2 > 1 → FAIL
  const detail = compareBaselines(current, baseline, 0, perCat);
  assert.equal(detail.regressed, true);
  const rule = detail.perRule!.find((r) => r.ruleId === "broken_references")!;
  assert.equal(rule.baselineError, 0);
  assert.equal(rule.currentError, 2);
  assert.equal(rule.regressed, true);
});

// ---------------------------------------------------------------------------
// Summary formatter
// ---------------------------------------------------------------------------

test("formatRegressionSummary emits OK verdict with no breach", () => {
  const baseline = makeBaseline(2, 1);
  const current = makeBaseline(2, 1);
  const detail = compareBaselines(current, baseline, 0, null);
  const text = formatRegressionSummary(detail);
  assert.ok(text.includes("Godot Open MCP regression: OK"));
  assert.ok(text.includes("no global threshold breach"));
  assert.ok(text.includes("delta=+0"));
});

test("formatRegressionSummary emits REGRESSION verdict with a breach", () => {
  const baseline = makeBaseline(1, 0);
  const current = makeBaseline(4, 0);
  const detail = compareBaselines(current, baseline, 0, null);
  const text = formatRegressionSummary(detail);
  assert.ok(text.includes("Godot Open MCP regression: REGRESSION"));
  assert.ok(text.includes("global error delta +3 > 0"));
  assert.ok(text.includes("delta=+3"));
});

test("formatRegressionSummary lists per-rule FAIL/ok lines", () => {
  const baseline = makeBaseline(0, 0, [{ ruleId: "broken_references", error: 0 }]);
  const current = makeBaseline(2, 0, [{ ruleId: "broken_references", error: 2 }]);
  const perCat = new Map([["broken_references", 1]]);
  const detail = compareBaselines(current, baseline, 0, perCat);
  const text = formatRegressionSummary(detail);
  assert.ok(text.includes("broken_references: FAIL"));
  assert.ok(text.includes("delta=+2"));
  assert.ok(text.includes("threshold=1"));
});

test("emptySummary returns all-zero counts", () => {
  assert.deepEqual(emptySummary(), { error: 0, warn: 0, info: 0 });
});
