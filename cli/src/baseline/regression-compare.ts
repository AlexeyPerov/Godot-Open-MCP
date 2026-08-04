// [CLI COPY] Byte-for-byte copy of
// `mcp-server/src/baseline/regression-compare.ts`. See the header in
// `baseline-schema.ts` for the copy rationale (CLI = zero-dep, no cross-package
// import). A parity test guards the sync.
//
// Regression compare logic (P15.1).
//
// Diff a current scan against a baseline by error-count delta, applying a
// global threshold and optional per-rule thresholds, and emit a compact
// `RegressionDetail` plus a CI-log-friendly summary string.
//
// Adapted (copy) from Unity Open MCP's `BaselineStore.Compare` in
// `packages/verify/Editor/Batch/BaselineStore.cs` — the threshold semantics
// (strict `>`, OR of global + per-rule, fallback to global) and the detail
// shape are identical so CI contracts port across engines.
//
// Intentional deltas:
//   - Operates on the TS `BaselineFile` shape (no C# `VerifyResult` dependency).
//   - Emits a compact human-readable summary string suitable for CI logs in
//     addition to the structured `RegressionDetail` (Unity's CLI formats this
//     on the C# side; here the compare owns it so the MCP tool and any future
//     CLI wrapper share one formatter).
//   - Per-rule detail is keyed on the union of baseline rule ids + current rule
//     ids + explicitly-named threshold keys (same as Unity) so a rule that
//     newly appears with errors still respects its threshold.

import {
  type BaselineFile,
  type RegressionDetail,
  type RuleRegressionDetail,
  type SeveritySummary,
  emptySummary,
  errorCountFor,
} from "./baseline-schema.js";

/**
 * Compare a current scan against a baseline.
 *
 * Threshold semantics (ported from Unity):
 *   - **Global gate:** `errorDelta = current.error - baseline.error`;
 *     `globalRegressed = errorDelta > globalThreshold`. A delta equal to the
 *     threshold is TOLERATED (strict `>`).
 *   - **Per-rule gate** (only when `perCategoryThresholds` is non-empty): for
 *     each ruleId in the union of baseline rules + current rules + explicit
 *     threshold keys, `delta = currentError - baselineError` and
 *     `ruleRegressed = delta > threshold` where `threshold` is the rule's entry
 *     in the map or `globalThreshold` as the fallback. The overall `regressed`
 *     verdict is the OR of the global gate + every per-rule gate.
 *   - A negative delta (errors decreased) never regresses.
 *
 * When `perCategoryThresholds` is null/empty, the compare is global-only and
 * `perRule` is `null` (legacy shape) — matching Unity's behavior.
 */
export function compareBaselines(
  current: BaselineFile,
  baseline: BaselineFile,
  globalThreshold: number,
  perCategoryThresholds: ReadonlyMap<string, number> | null,
): RegressionDetail {
  const baselineSummary = baseline.summary ?? emptySummary();
  const currentSummary = current.summary ?? emptySummary();

  const errorDelta = currentSummary.error - baselineSummary.error;
  const globalRegressed = errorDelta > globalThreshold;

  const detail: RegressionDetail = {
    baselineSummary,
    currentSummary,
    errorDelta,
    errorThreshold: globalThreshold,
    regressed: globalRegressed,
    perRule: null,
  };

  if (perCategoryThresholds === null || perCategoryThresholds.size === 0) {
    return detail;
  }

  // Union of ruleIds: baseline + current + explicitly named threshold keys.
  const ruleIds = new Set<string>();
  for (const r of baseline.rules) ruleIds.add(r.ruleId);
  for (const r of current.rules) ruleIds.add(r.ruleId);
  for (const key of perCategoryThresholds.keys()) ruleIds.add(key);

  const sortedIds = [...ruleIds].sort();
  const perRule: RuleRegressionDetail[] = [];
  for (const ruleId of sortedIds) {
    const baseErr = errorCountFor(baseline, ruleId);
    const currErr = errorCountFor(current, ruleId);
    const delta = currErr - baseErr;
    const threshold = perCategoryThresholds.has(ruleId)
      ? perCategoryThresholds.get(ruleId)!
      : globalThreshold;
    const ruleRegressed = delta > threshold;
    perRule.push({
      ruleId,
      baselineError: baseErr,
      currentError: currErr,
      errorDelta: delta,
      errorThreshold: threshold,
      regressed: ruleRegressed,
    });
    if (ruleRegressed) detail.regressed = true;
  }

  detail.perRule = perRule;
  return detail;
}

/**
 * Format a `RegressionDetail` as a compact multi-line summary for CI logs.
 * Designed to be greppable: the verdict line carries `REGRESSION` or `OK`, and
 * each per-rule line is prefixed with `FAIL` or `ok`.
 *
 * Example:
 * ```
 * Godot Open MCP regression: REGRESSION (global error delta +3 > 0)
 *   errors: baseline=2 current=5 delta=+3 (threshold=0)
 *   warnings: baseline=1 current=1
 *   broken_references: FAIL baseline=2 current=5 delta=+3 (threshold=0)
 *   missing_scripts: ok baseline=0 current=0 delta=+0 (threshold=0)
 * ```
 */
export function formatRegressionSummary(detail: RegressionDetail): string {
  const verdict = detail.regressed ? "REGRESSION" : "OK";
  const lines: string[] = [];

  const globalReason =
    detail.errorDelta > detail.errorThreshold
      ? `global error delta ${signed(detail.errorDelta)} > ${detail.errorThreshold}`
      : `no global threshold breach`;

  lines.push(`Godot Open MCP regression: ${verdict} (${globalReason})`);
  lines.push(
    `  errors: baseline=${detail.baselineSummary.error} current=${detail.currentSummary.error} delta=${signed(detail.errorDelta)} (threshold=${detail.errorThreshold})`,
  );
  lines.push(
    `  warnings: baseline=${detail.baselineSummary.warn} current=${detail.currentSummary.warn}`,
  );

  if (detail.perRule !== null) {
    for (const r of detail.perRule) {
      const tag = r.regressed ? "FAIL" : "ok";
      lines.push(
        `  ${r.ruleId}: ${tag} baseline=${r.baselineError} current=${r.currentError} delta=${signed(r.errorDelta)} (threshold=${r.errorThreshold})`,
      );
    }
  }

  return lines.join("\n");
}

/** Format an integer with an explicit leading sign (`+` / `-` / `+0`) so CI
 *  log deltas are unambiguous and aligned. */
function signed(n: number): string {
  if (n > 0) return `+${n}`;
  if (n < 0) return `${n}`;
  return "+0";
}

/** Convenience: summarize a SeveritySummary delta for ad-hoc logging. */
export function summarizeDelta(
  baseline: SeveritySummary,
  current: SeveritySummary,
): { errorDelta: number; warnDelta: number } {
  return {
    errorDelta: current.error - baseline.error,
    warnDelta: current.warn - baseline.warn,
  };
}
