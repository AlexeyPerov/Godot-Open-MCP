// Baseline schema v1 + issue-key format (P15.1).
//
// The persisted shape of a Godot Open MCP regression baseline, plus the
// canonical issue-key string that links an issue to its rule/severity/path/
// code identity. Both are adapted (copy) from Unity Open MCP's
// `packages/verify/Editor/Batch/BaselineModels.cs` + `Editor/Core/IssueKey.cs`
// — the schema contract and the `{ruleId}|{severity}|{assetPath}|{issueCode}`
// key format are identical so a CI pipeline portable across both engines sees
// the same shape.
//
// Intentional Godot deltas:
//   - `scanner: "offline"` + `ciExcludedRules[]` record that Godot routes the
//     baseline through the offline disk scanner (no headless editor). Unity's
//     baseline omits both because it spawns headless Unity and runs every rule.
//   - `loadBaseline` returns a discriminated `BaselineLoadResult` so the
//     regression tool can map "missing" → exit 2 and "invalid" → exit 3 without
//     sniffing exception types (the C# side throws `FileNotFoundException` /
//     `InvalidOperationException`).
//
// Schema v1 is frozen. Any breaking change bumps `BASELINE_SCHEMA_VERSION` and
// `loadBaseline` rejects a mismatched version as `invalid` (exit 3) so a stale
// baseline never silently compares against a new schema.

import { readFile, writeFile, mkdir } from "node:fs/promises";
import { dirname } from "node:path";
import { existsSync } from "node:fs";

// ---------------------------------------------------------------------------
// Schema version
// ---------------------------------------------------------------------------

/** Frozen baseline schema version. Bump only on a breaking shape change. */
export const BASELINE_SCHEMA_VERSION = 1;

/** Platform profiles a baseline can be tagged with. Mirrors Unity's enum. */
export type PlatformProfile = "mobile" | "console" | "desktop";

const VALID_PROFILES: ReadonlySet<string> = new Set([
  "mobile",
  "console",
  "desktop",
]);

/** Coerce an unknown input to a valid profile, defaulting to `desktop`. */
export function normalizeProfile(value: unknown): PlatformProfile {
  return typeof value === "string" && VALID_PROFILES.has(value)
    ? (value as PlatformProfile)
    : "desktop";
}

// ---------------------------------------------------------------------------
// Severity
// ---------------------------------------------------------------------------

/** Issue severity bucket used by both the offline scan and the baseline. */
export type IssueSeverity = "Error" | "Warning";

/** Per-severity counts. `info` is reserved (always 0 in v1) for shape parity
 *  with Unity; the offline scan emits only Error/Warning. */
export interface SeveritySummary {
  error: number;
  warn: number;
  info: number;
}

export function emptySummary(): SeveritySummary {
  return { error: 0, warn: 0, info: 0 };
}

// ---------------------------------------------------------------------------
// Issue key — the canonical identity string
// ---------------------------------------------------------------------------

/**
 * Build the canonical issue key `{ruleId}|{SEVERITY}|{assetPath}|{issueCode}`.
 *
 * Ported (copy) from the C# `IssueKey.Build` / TS-side mirror so the offline
 * baseline produces keys byte-compatible with the live verify surface. Pipes in
 * any component are sanitized to `_` (a producer-side guard also rejects them,
 * but defense in depth keeps the key unambiguous).
 */
export function buildIssueKey(
  ruleId: string,
  severity: IssueSeverity,
  assetPath: string,
  issueCode: string,
): string {
  const sev = severity === "Error" ? "ERROR" : "WARN";
  return [
    sanitizeKeyComponent(ruleId),
    sev,
    sanitizeKeyComponent(assetPath),
    sanitizeKeyComponent(issueCode),
  ].join("|");
}

/** Replace `|` with `_` so a component never splits the key. */
function sanitizeKeyComponent(value: string): string {
  return value.indexOf("|") < 0 ? value : value.replace(/\|/g, "_");
}

// ---------------------------------------------------------------------------
// Baseline file shape (schema v1)
// ---------------------------------------------------------------------------

/** One rule's baseline entry: its severity counts + the issue keys it emitted. */
export interface RuleBaselineEntry {
  ruleId: string;
  error: number;
  warn: number;
  info: number;
  /** Canonical issue keys (`{ruleId}|{SEVERITY}|{assetPath}|{issueCode}`). */
  issueKeys: string[];
}

/** The persisted baseline. Serialized verbatim to JSON. */
export interface BaselineFile {
  schemaVersion: number;
  platformProfile: PlatformProfile;
  /** ISO-8601 UTC (`YYYY-MM-DDTHH:mm:ssZ`). */
  generatedAt: string;
  /** Marks the scan source. Always `"offline"` for Godot (no headless editor). */
  scanner: "offline";
  summary: SeveritySummary;
  rules: RuleBaselineEntry[];
  /** Rule ids that cannot run offline and are therefore CI-excluded. A live
   *  scan (validate_edit) may surface them; the offline baseline cannot. */
  ciExcludedRules: string[];
}

// ---------------------------------------------------------------------------
// Regression detail (the compare output)
// ---------------------------------------------------------------------------

/** Per-rule regression breakdown. Present only when per-category thresholds
 *  were supplied (otherwise the compare is global-only). */
export interface RuleRegressionDetail {
  ruleId: string;
  baselineError: number;
  currentError: number;
  errorDelta: number;
  errorThreshold: number;
  regressed: boolean;
}

/** Result of comparing a current scan against a baseline. */
export interface RegressionDetail {
  baselineSummary: SeveritySummary;
  currentSummary: SeveritySummary;
  /** `current.error - baseline.error`. Negative = improvement. */
  errorDelta: number;
  /** The global threshold used for the overall gate. */
  errorThreshold: number;
  /** Overall verdict: OR of the global gate + every per-rule gate. */
  regressed: boolean;
  /** `null` when no per-category thresholds were supplied. */
  perRule: RuleRegressionDetail[] | null;
}

// ---------------------------------------------------------------------------
// Load result — discriminated so the tool maps straight to exit codes
// ---------------------------------------------------------------------------

export type BaselineLoadResult =
  | { ok: true; baseline: BaselineFile }
  | { ok: false; reason: "missing"; path: string }
  | { ok: false; reason: "invalid"; path: string; message: string };

/**
 * Load + validate a baseline file. Never throws — every failure maps to a
 * discriminated branch the regression tool translates to an exit code:
 *   - file absent → `{ ok: false, reason: "missing" }` (exit 2)
 *   - unreadable / unparseable / wrong schema version → `{ ok: false, reason:
 *     "invalid" }` (exit 3)
 */
export async function loadBaseline(
  path: string,
): Promise<BaselineLoadResult> {
  if (!existsSync(path)) {
    return { ok: false, reason: "missing", path };
  }
  let text: string;
  try {
    text = await readFile(path, "utf-8");
  } catch (e) {
    return {
      ok: false,
      reason: "invalid",
      path,
      message: `cannot read baseline: ${(e as Error)?.message ?? "unknown error"}`,
    };
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch (e) {
    return {
      ok: false,
      reason: "invalid",
      path,
      message: `baseline is not valid JSON: ${(e as Error)?.message ?? "parse error"}`,
    };
  }
  if (parsed === null || typeof parsed !== "object") {
    return {
      ok: false,
      reason: "invalid",
      path,
      message: "baseline root is not an object.",
    };
  }
  const obj = parsed as Partial<BaselineFile>;
  if (typeof obj.schemaVersion !== "number") {
    return {
      ok: false,
      reason: "invalid",
      path,
      message: "baseline is missing a numeric 'schemaVersion'.",
    };
  }
  if (obj.schemaVersion !== BASELINE_SCHEMA_VERSION) {
    return {
      ok: false,
      reason: "invalid",
      path,
      message: `baseline schema version mismatch: expected ${BASELINE_SCHEMA_VERSION}, got ${obj.schemaVersion}. Regenerate the baseline with godot_open_mcp_baseline_create.`,
    };
  }
  if (!Array.isArray(obj.rules)) {
    return {
      ok: false,
      reason: "invalid",
      path,
      message: "baseline 'rules' is not an array.",
    };
  }
  const baseline = normalizeBaseline(obj);
  return { ok: true, baseline };
}

/** Coerce a parsed-but-untrusted object into a well-formed BaselineFile. */
function normalizeBaseline(obj: Partial<BaselineFile>): BaselineFile {
  const summary = isSummary(obj.summary)
    ? { ...obj.summary }
    : emptySummary();
  const rules: RuleBaselineEntry[] = (obj.rules ?? []).map((r) => ({
    ruleId: typeof r.ruleId === "string" ? r.ruleId : "",
    error: typeof r.error === "number" ? r.error : 0,
    warn: typeof r.warn === "number" ? r.warn : 0,
    info: typeof r.info === "number" ? r.info : 0,
    issueKeys: Array.isArray(r.issueKeys) ? r.issueKeys.filter((k) => typeof k === "string") : [],
  }));
  const ciExcludedRules = Array.isArray(obj.ciExcludedRules)
    ? obj.ciExcludedRules.filter((r) => typeof r === "string")
    : [];
  return {
    schemaVersion: BASELINE_SCHEMA_VERSION,
    platformProfile: normalizeProfile(obj.platformProfile),
    generatedAt: typeof obj.generatedAt === "string" ? obj.generatedAt : "",
    scanner: "offline",
    summary,
    rules,
    ciExcludedRules,
  };
}

function isSummary(value: unknown): value is SeveritySummary {
  if (value === null || typeof value !== "object") return false;
  const s = value as Partial<SeveritySummary>;
  return (
    typeof s.error === "number" &&
    typeof s.warn === "number" &&
    typeof s.info === "number"
  );
}

/**
 * Serialize + write a baseline, creating parent directories as needed. Throws
 * on I/O failure so the tool can surface a structured error.
 */
export async function saveBaseline(
  baseline: BaselineFile,
  path: string,
): Promise<void> {
  const json = JSON.stringify(baseline, null, 2) + "\n";
  const dir = dirname(path);
  if (dir !== "" && dir !== ".") {
    await mkdir(dir, { recursive: true });
  }
  await writeFile(path, json, "utf-8");
}

/**
 * Build a baseline file from a flat issue list + the rule ids that ran. One
 * `RuleBaselineEntry` per rule id in `categoriesRun`, each carrying its
 * severity counts + the canonical issue keys.
 */
export function buildBaseline(
  issues: ReadonlyArray<BaselineIssue>,
  categoriesRun: ReadonlyArray<string>,
  ciExcludedRules: ReadonlyArray<string>,
  profile: PlatformProfile,
): BaselineFile {
  const summary = emptySummary();
  for (const issue of issues) {
    if (issue.severity === "Error") summary.error++;
    else if (issue.severity === "Warning") summary.warn++;
  }

  const rules: RuleBaselineEntry[] = categoriesRun.map((ruleId) => {
    const ruleIssues = issues.filter((i) => i.ruleId === ruleId);
    const entry: RuleBaselineEntry = {
      ruleId,
      error: ruleIssues.filter((i) => i.severity === "Error").length,
      warn: ruleIssues.filter((i) => i.severity === "Warning").length,
      info: 0,
      issueKeys: ruleIssues.map((i) =>
        buildIssueKey(i.ruleId, i.severity, i.assetPath, i.issueCode),
      ),
    };
    return entry;
  });

  return {
    schemaVersion: BASELINE_SCHEMA_VERSION,
    platformProfile: profile,
    generatedAt: new Date().toISOString().replace(/\.\d{3}Z$/, "Z"),
    scanner: "offline",
    summary,
    rules,
    ciExcludedRules: [...ciExcludedRules],
  };
}

/** A flat issue the offline scan emits, shaped to build a baseline entry. */
export interface BaselineIssue {
  ruleId: string;
  severity: IssueSeverity;
  /** Canonical `res://` path of the affected asset. */
  assetPath: string;
  issueCode: string;
  description: string;
}

/** Look up a rule's error count in a baseline (0 when the rule is absent). */
export function errorCountFor(
  baseline: BaselineFile | null | undefined,
  ruleId: string,
): number {
  if (baseline === null || baseline === undefined) return 0;
  for (const r of baseline.rules) {
    if (r.ruleId === ruleId) return r.error;
  }
  return 0;
}
