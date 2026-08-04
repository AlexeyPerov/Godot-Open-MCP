// CLI command wrapper for `baseline create|update`.
//
// Runs the offline whole-project scan and writes a schema-v1 baseline JSON —
// the CI regression reference. `create` and `update` are aliases (both
// overwrite); the distinction is kept for Unity CLI parity and for operators
// who want to signal intent in their pipeline logs. No editor, no bridge.
//
// The default path is `CI/godot-open-mcp-baseline.json` relative to the project
// root; parent directories are created when missing. The baseline is meant to
// be committed and compared against by `regression check` in CI.
//
// Exit codes:
//   0 — baseline written successfully.
//   1 — scan ran but the baseline could not be written (I/O error).

import { isAbsolute, join } from "node:path";
import { EXIT } from "../exit-codes.js";
import type { CliCommandResult } from "../commands.js";
import { scanProjectOffline } from "../baseline/scan.js";
import {
  BASELINE_SCHEMA_VERSION,
  buildBaseline,
  normalizeProfile,
  saveBaseline,
  type PlatformProfile,
} from "../baseline/baseline-schema.js";

export interface BaselineCliInput {
  /** Resolved project root (positional `path` / `--project` / cwd). */
  projectPath: string;
  /** `create` or `update` (alias). */
  subcommand: "create" | "update";
  /** Baseline path relative to the project root (or absolute). */
  baselinePath?: string;
  /** Platform profile tag (`mobile` | `console` | `desktop`, default `desktop`). */
  platformProfile?: string;
}

/** Default baseline path (relative to the project root). */
export const DEFAULT_BASELINE_PATH = "CI/godot-open-mcp-baseline.json";

/**
 * Run the `baseline create|update` command. Returns a `CliCommandResult`; the
 * dispatcher prints the JSON or human summary and exits with `result.exitCode`.
 */
export async function baselineCommand(
  input: BaselineCliInput,
): Promise<CliCommandResult> {
  const profile = normalizeProfile(input.platformProfile) as PlatformProfile;
  const relPath =
    input.baselinePath && input.baselinePath !== ""
      ? input.baselinePath
      : DEFAULT_BASELINE_PATH;
  const absBaselinePath = resolveBaselinePath(relPath, input.projectPath);

  const scan = await scanProjectOffline({ projectRoot: input.projectPath });
  const baseline = buildBaseline(
    scan.issues,
    scan.categoriesRun,
    scan.ciExcludedRules,
    profile,
  );

  try {
    await saveBaseline(baseline, absBaselinePath);
  } catch (e) {
    const message = `failed to write baseline to '${absBaselinePath}': ${(e as Error)?.message ?? "unknown error"}`;
    return {
      exitCode: EXIT.ERRORS,
      json: {
        command: "baseline",
        subcommand: input.subcommand,
        error: { code: "baseline_write_failed", message, path: absBaselinePath },
      },
      human: message,
      errorLabel: "baseline_write_failed",
    };
  }

  const json = {
    command: "baseline",
    subcommand: input.subcommand,
    baselinePath: absBaselinePath,
    schemaVersion: BASELINE_SCHEMA_VERSION,
    platformProfile: profile,
    summary: baseline.summary,
    rules: baseline.rules.map((r) => ({
      ruleId: r.ruleId,
      error: r.error,
      warn: r.warn,
      issueKeyCount: r.issueKeys.length,
    })),
    ciExcludedRules: baseline.ciExcludedRules,
    scannedFileCount: scan.scannedFiles.length,
    durationMs: scan.durationMs,
  };

  return {
    exitCode: EXIT.SUCCESS,
    json,
    human: formatBaselineHuman({
      subcommand: input.subcommand,
      baselinePath: absBaselinePath,
      baseline,
      scannedFileCount: scan.scannedFiles.length,
    }),
  };
}

/** Resolve a baseline path to an absolute native path anchored at the project
 *  root (relative) or passed through (absolute). Mirrors the MCP tool. */
export function resolveBaselinePath(relOrAbsolute: string, projectRoot: string): string {
  if (relOrAbsolute === "") return projectRoot;
  return isAbsolute(relOrAbsolute) ? relOrAbsolute : join(projectRoot, relOrAbsolute);
}

/** Human-readable summary for a successful baseline write. */
function formatBaselineHuman(args: {
  subcommand: string;
  baselinePath: string;
  baseline: ReturnType<typeof buildBaseline>;
  scannedFileCount: number;
}): string {
  const { subcommand, baselinePath, baseline, scannedFileCount } = args;
  const verb = subcommand === "update" ? "updated" : "created";
  const lines: string[] = [
    `Baseline ${verb}: ${baselinePath}`,
    `  errors: ${baseline.summary.error}  warnings: ${baseline.summary.warn}  (scanned ${scannedFileCount} file(s))`,
    `  schemaVersion: ${baseline.schemaVersion}  platformProfile: ${baseline.platformProfile}`,
  ];
  if (baseline.rules.length > 0) {
    lines.push("  rules:");
    for (const r of baseline.rules) {
      lines.push(`    ${r.ruleId}: error=${r.error} warn=${r.warn} (${r.issueKeys.length} issue key(s))`);
    }
  }
  if (baseline.ciExcludedRules.length > 0) {
    lines.push(
      `  ciExcludedRules: ${baseline.ciExcludedRules.join(", ")}`,
    );
  }
  return lines.join("\n");
}
