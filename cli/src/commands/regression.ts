// CLI command wrapper for `regression check`.
//
// Compares the current offline scan against a committed baseline by error-count
// delta, applying a global threshold and optional per-rule thresholds. No
// editor, no bridge. The baseline is produced by `baseline create` and is meant
// to be committed at `CI/godot-open-mcp-baseline.json`.
//
// Exit-code contract (inherited from P15.1 — byte-compatible with the MCP
// `godot_open_mcp_regression_check` tool and Unity's CI contract):
//   0 — no regression (within all thresholds).
//   1 — regression (global threshold breach OR any per-rule breach).
//   2 — baseline missing (the file does not exist).
//   3 — baseline invalid (unreadable / unparseable / schema-version mismatch).
//
// Threshold semantics (ported from Unity, same as the MCP tool):
//   - Global gate: `errorDelta = current.error - baseline.error`;
//     `regressed = errorDelta > threshold`. A delta equal to the threshold is
//     TOLERATED (strict `>`).
//   - Per-rule gate (only when `--per-category-threshold` is given at least
//     once): for each rule, `delta = currentError - baselineError`;
//     `regressed = delta > threshold` where threshold is the rule's explicit
//     entry or the global threshold as fallback. The overall verdict is the OR
//     of the global gate + every per-rule gate.
//   - A negative delta (errors decreased) never regresses.

import { isAbsolute, join } from "node:path";
import { EXIT } from "../exit-codes.js";
import type { CliCommandResult } from "../commands.js";
import { scanProjectOffline } from "../baseline/scan.js";
import {
  BASELINE_SCHEMA_VERSION,
  buildBaseline,
  loadBaseline,
  normalizeProfile,
  type PlatformProfile,
} from "../baseline/baseline-schema.js";
import {
  compareBaselines,
  formatRegressionSummary,
} from "../baseline/regression-compare.js";

export interface RegressionCliInput {
  /** Resolved project root (positional `path` / `--project` / cwd). */
  projectPath: string;
  /** Always `check` (the only regression subcommand in v1). */
  subcommand: "check";
  /** Baseline path relative to the project root (or absolute). */
  baselinePath?: string;
  /** Global error-count-delta threshold (default 0). */
  threshold?: number;
  /** Per-rule thresholds: `ruleId → max tolerated error-count increase`.
   *  `null`/`undefined`/empty → global-only compare. */
  perCategoryThresholds?: Map<string, number> | null;
  /** Platform profile tag (`mobile` | `console` | `desktop`, default `desktop`). */
  platformProfile?: string;
}

/** Default baseline path (relative to the project root). */
export const DEFAULT_BASELINE_PATH = "CI/godot-open-mcp-baseline.json";

/** Baseline-missing exit code (P15.1 contract). */
export const EXIT_BASELINE_MISSING = 2;
/** Baseline-invalid exit code (P15.1 contract). */
export const EXIT_BASELINE_INVALID = 3;

/**
 * Run the `regression check` command. Returns a `CliCommandResult`; the
 * dispatcher prints the JSON or human summary and exits with `result.exitCode`.
 */
export async function regressionCommand(
  input: RegressionCliInput,
): Promise<CliCommandResult> {
  const relPath = input.baselinePath && input.baselinePath !== "" ? input.baselinePath : DEFAULT_BASELINE_PATH;
  const absBaselinePath = resolveBaselinePath(relPath, input.projectPath);

  const loaded = await loadBaseline(absBaselinePath);
  if (!loaded.ok) {
    const exitCode = loaded.reason === "missing" ? EXIT_BASELINE_MISSING : EXIT_BASELINE_INVALID;
    const code = loaded.reason === "missing" ? "baseline_missing" : "baseline_invalid";
    const message =
      loaded.reason === "missing"
        ? `baseline file not found at '${loaded.path}'.`
        : loaded.message;
    return {
      exitCode,
      json: {
        command: "regression",
        subcommand: "check",
        error: { code, message, path: loaded.path },
        exitCode,
      },
      human: `Godot Open MCP regression: ${code} — ${message}`,
      errorLabel: code,
    };
  }
  const baseline = loaded.baseline;

  const profile = normalizeProfile(input.platformProfile) as PlatformProfile;
  const globalThreshold =
    typeof input.threshold === "number" && Number.isFinite(input.threshold) && input.threshold >= 0
      ? Math.trunc(input.threshold)
      : 0;
  const perCategory = input.perCategoryThresholds && input.perCategoryThresholds.size > 0
    ? input.perCategoryThresholds
    : null;

  const scan = await scanProjectOffline({ projectRoot: input.projectPath });
  const current = buildBaseline(
    scan.issues,
    scan.categoriesRun,
    scan.ciExcludedRules,
    profile,
  );

  const regression = compareBaselines(current, baseline, globalThreshold, perCategory);
  const summary = formatRegressionSummary(regression);
  const exitCode = regression.regressed ? EXIT.ERRORS : EXIT.SUCCESS;

  const json = {
    command: "regression",
    subcommand: "check",
    baselinePath: absBaselinePath,
    schemaVersion: BASELINE_SCHEMA_VERSION,
    platformProfile: profile,
    exitCode,
    regressed: regression.regressed,
    summary,
    regression,
    scannedFileCount: scan.scannedFiles.length,
    durationMs: scan.durationMs,
  };

  return {
    exitCode,
    json,
    human: summary,
    errorLabel: regression.regressed ? "regression_detected" : undefined,
  };
}

/** Resolve a baseline path to an absolute native path anchored at the project
 *  root (relative) or passed through (absolute). Mirrors the MCP tool. */
export function resolveBaselinePath(relOrAbsolute: string, projectRoot: string): string {
  if (relOrAbsolute === "") return projectRoot;
  return isAbsolute(relOrAbsolute) ? relOrAbsolute : join(projectRoot, relOrAbsolute);
}

// Re-export EXIT for the dispatcher's regression branch so the exit-code
// constants have a single home.
export { EXIT };
