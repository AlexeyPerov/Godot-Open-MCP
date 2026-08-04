// CLI command wrapper for `verify`.
//
// Runs the offline whole-project scanner and reports the issue set with a
// CI-friendly exit code. No editor, no bridge — the scan parses `.tscn`/`.tres`
// from disk (the documented P15.1/P15.2 delta from Unity, which spawns headless
// Unity). The scan covers the same rules as `baseline_create` /
// `regression_check`: `broken_references` + `missing_scripts`. The richer P14
// rules live in the C# verify package and run via the live `validate_edit`
// surface; they are listed in `ciExcludedRules` so "absent offline" is never
// mistaken for "clean".
//
// Exit codes:
//   0 — no issues at/above the fail-on severity.
//   1 — one or more issues at/above the fail-on severity.
//
// `--fail-on` controls the gate:
//   error (default) — errors fail (exit 1); warnings are advisory (exit 0).
//   warn            — any error OR warning fails (exit 1).
//   none            — never fail (exit 0 even with errors); issues still listed.
//
// `--paths` is accepted for forward-compatibility and Unity CLI parity, but the
// v1 offline scanner is whole-project: it examines every `.tscn`/`.tres` under
// the project root regardless. This is documented honestly in --help and the CI
// README rather than silently ignored.

import { EXIT } from "../exit-codes.js";
import type { CliCommandResult } from "../commands.js";
import { scanProjectOffline } from "../baseline/scan.js";
import type { BaselineIssue, IssueSeverity } from "../baseline/baseline-schema.js";

/** The `--fail-on` gate level. */
export type FailOn = "error" | "warn" | "none";

export interface VerifyCliInput {
  /** Resolved project root (positional `path` / `--project` / cwd). */
  projectPath: string;
  /** Gate severity: `error` (default) | `warn` | `none`. */
  failOn: FailOn;
}

/**
 * Run the `verify` command. Returns a `CliCommandResult`; the dispatcher prints
 * the JSON or human summary and exits with `result.exitCode`.
 */
export async function verifyCommand(
  input: VerifyCliInput,
): Promise<CliCommandResult> {
  const scan = await scanProjectOffline({ projectRoot: input.projectPath });

  const errorCount = countBySeverity(scan.issues, "Error");
  const warnCount = countBySeverity(scan.issues, "Warning");

  const failed = shouldFail(input.failOn, errorCount, warnCount);

  const json = {
    command: "verify",
    projectPath: input.projectPath,
    failOn: input.failOn,
    summary: {
      errors: errorCount,
      warnings: warnCount,
    },
    rules: scan.categoriesRun,
    ciExcludedRules: scan.ciExcludedRules,
    issues: scan.issues,
    scannedFileCount: scan.scannedFiles.length,
    durationMs: scan.durationMs,
  };

  const human = formatVerifyHuman({
    errorCount,
    warnCount,
    issues: scan.issues,
    ciExcludedRules: scan.ciExcludedRules,
    scannedFileCount: scan.scannedFiles.length,
    failOn: input.failOn,
  });

  return {
    exitCode: failed ? EXIT.ERRORS : EXIT.SUCCESS,
    json,
    human,
    errorLabel: failed ? "verify_failed" : undefined,
  };
}

/** Count issues at a given severity. */
function countBySeverity(issues: ReadonlyArray<BaselineIssue>, sev: IssueSeverity): number {
  let n = 0;
  for (const issue of issues) {
    if (issue.severity === sev) n++;
  }
  return n;
}

/** Apply the `--fail-on` gate. */
function shouldFail(failOn: FailOn, errors: number, warns: number): boolean {
  switch (failOn) {
    case "none":
      return false;
    case "warn":
      return errors > 0 || warns > 0;
    case "error":
    default:
      return errors > 0;
  }
}

/** Human-readable summary + greppable issue list for CI logs. */
function formatVerifyHuman(args: {
  errorCount: number;
  warnCount: number;
  issues: ReadonlyArray<BaselineIssue>;
  ciExcludedRules: ReadonlyArray<string>;
  scannedFileCount: number;
  failOn: FailOn;
}): string {
  const { errorCount, warnCount, issues, ciExcludedRules, scannedFileCount, failOn } = args;
  const lines: string[] = [];

  const verdict = shouldFail(failOn, errorCount, warnCount) ? "FAIL" : "OK";
  lines.push(`Godot Open MCP verify: ${verdict} (--fail-on ${failOn})`);
  lines.push(`  errors: ${errorCount}  warnings: ${warnCount}  (scanned ${scannedFileCount} file(s))`);

  if (issues.length > 0) {
    lines.push("");
    lines.push("Issues:");
    for (const issue of issues) {
      const sev = issue.severity === "Error" ? "ERROR" : "WARN ";
      lines.push(
        `  [${sev}] ${issue.ruleId}/${issue.issueCode}  ${issue.assetPath}`,
      );
      lines.push(`        ${issue.description}`);
    }
  }

  if (ciExcludedRules.length > 0) {
    lines.push("");
    lines.push(
      `Offline scope: ${ciExcludedRules.join(", ")} run via the live verify surface (validate_edit), not in CI.`,
    );
  }

  return lines.join("\n");
}
