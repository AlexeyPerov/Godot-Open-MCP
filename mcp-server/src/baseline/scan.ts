// Offline whole-project scan for the baseline/regression surface (P15.1).
//
// Produces a flat `BaselineIssue[]` + the list of rule ids that ran, reusing
// the existing offline building blocks (`collectProjectFiles`,
// `buildUidPathIndex`, `resolveResPath`) so there is no second scanner. This is
// the Godot analog of Unity's `VerifyRunner.RunScoped` over the whole project —
// but routed through the offline disk parser because Godot has no headless
// editor batch mode (the documented P15.1 delta).
//
// Scope v1: the two rules that (a) carry the highest CI signal and (b) are
// unambiguously detectable offline from text alone — `broken_references` and
// `missing_scripts`. The richer P14 rules (project_health, scene_structure,
// materials, script_audit, animation) live in the C# verify package and run
// through the live `validate_edit` / `checkpoint_create` surface; porting them
// to a TS offline scanner is a larger task tracked separately. The baseline
// records `ciExcludedRules` so a consumer never mistakes "absent offline" for
// "clean".
//
// Detection logic mirrors the C# rules (packages/verify/Editor/Rules/*) and the
// P13 reverse-reference scanner: an `[ext_resource]` is broken when its
// `path=` does not exist on disk AND its `uid=` (when present) is not in the
// offline uid→path index; a script attachment is missing when the resolved
// ext_resource is a Script type that does not exist. Both reuse the same
// "declared but unresolvable" test so a single broken ext_resource surfaces at
// most once per rule it triggers.

import { readFile } from "node:fs/promises";
import { existsSync } from "node:fs";

import {
  buildUidPathIndex,
  collectProjectFiles,
  extractUidToken,
  resolveUidToPath,
  type UidPathIndex,
} from "../offline/project-index.js";
import { resolveResPath } from "../offline/project-paths.js";
import type { BaselineIssue, IssueSeverity } from "./baseline-schema.js";

// ---------------------------------------------------------------------------
// Result
// ---------------------------------------------------------------------------

/** Rule ids this scanner runs (the offline-detectable subset). */
export const OFFLINE_RULE_IDS = ["broken_references", "missing_scripts"] as const;

/**
 * Rule ids implemented in the verify package but NOT by this offline scanner.
 * Recorded in the baseline's `ciExcludedRules` so a consumer never mistakes
 * "absent offline" for "clean". Live-only rules (none in v1) would also live
 * here.
 */
export const CI_EXCLUDED_RULES = [
  "import_health",
  "project_health",
  "scene_structure_health",
  "materials_shader_health",
  "script_audit",
  "animation_analysis",
] as const;

export interface OfflineScanResult {
  issues: BaselineIssue[];
  /** Rule ids that actually ran (always OFFLINE_RULE_IDS in v1). */
  categoriesRun: string[];
  /** Rule ids NOT covered by the offline scan (recorded in the baseline). */
  ciExcludedRules: string[];
  /** Canonical `res://` paths the scanner examined. */
  scannedFiles: string[];
  /** Wall-clock scan time in milliseconds. */
  durationMs: number;
}

export interface OfflineScanOpts {
  projectRoot: string;
}

/**
 * Run the offline whole-project scan. Never throws — unreadable files are
 * skipped silently (a partial scan is better than failing the whole baseline).
 */
export async function scanProjectOffline(
  opts: OfflineScanOpts,
): Promise<OfflineScanResult> {
  const start = Date.now();
  const projectRoot = opts.projectRoot;
  const index = await buildUidPathIndex(projectRoot);
  const files = await collectProjectFiles(projectRoot, new Set([".tscn", ".tres"]));

  const issues: BaselineIssue[] = [];
  const scanned: string[] = [];

  for (const resPath of files) {
    scanned.push(resPath);
    const native = resolveResPath(resPath, projectRoot);
    if (native.kind !== "ok") continue;

    let text: string;
    try {
      text = await readFile(native.nativePath, "utf-8");
    } catch {
      continue;
    }

    const fileIssues = scanResourceText(resPath, text, index, projectRoot);
    issues.push(...fileIssues);
  }

  scanned.sort();

  return {
    issues,
    categoriesRun: [...OFFLINE_RULE_IDS],
    ciExcludedRules: [...CI_EXCLUDED_RULES],
    scannedFiles: scanned,
    durationMs: Date.now() - start,
  };
}

// ---------------------------------------------------------------------------
// Per-file scanner
// ---------------------------------------------------------------------------

interface ParsedExtResource {
  id: string;
  type: string;
  path: string;
  uid: string | null;
  line: number;
}

/**
 * Scan one resource's text for broken references + missing scripts.
 *
 * A `[ext_resource]` is broken when neither its `path=` resolves to an existing
 * file NOR its `uid=` resolves through the offline index to an existing file.
 * The same unresolvable declaration additionally emits `missing_script` when
 * its type is `Script` and it is attached to a node via
 * `script = ExtResource("id")`.
 *
 * Reuses the header-attr extraction idiom from the P13 reverse-reference
 * scanner (`references.ts`) so attr parsing stays consistent.
 */
function scanResourceText(
  resPath: string,
  content: string,
  index: UidPathIndex,
  projectRoot: string,
): BaselineIssue[] {
  const issues: BaselineIssue[] = [];
  const lines = content.split(/\r?\n/);

  const extResources = new Map<string, ParsedExtResource>();
  const scriptAttachments = new Set<string>(); // ext_resource ids attached via `script =`

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!;
    const lineNo = i + 1;

    if (line.length > 0 && line[0] === "[") {
      if (line.startsWith("[ext_resource")) {
        const id = extractHeaderAttr(line, "id");
        const type = extractHeaderAttr(line, "type") ?? "";
        const path = extractHeaderAttr(line, "path") ?? "";
        const uidRaw = extractHeaderAttr(line, "uid");
        const uid = uidRaw !== null ? extractUidToken(uidRaw) : null;
        if (id !== null) {
          extResources.set(id, { id, type, path, uid, line: lineNo });
        }
      }
      continue;
    }

    // Body line: detect `script = ExtResource("id")`.
    if (line.includes("ExtResource(")) {
      const m = /script\s*=\s*ExtResource\("([^"]+)"\)/.exec(line);
      if (m !== null) {
        scriptAttachments.add(m[1]!);
      }
    }
  }

  for (const ext of extResources.values()) {
    if (resolveExtResourcePath(ext, index, projectRoot)) continue; // resolvable → not broken

    // broken_scene_reference: the ext_resource points at nothing.
    issues.push(makeBrokenReferenceIssue(resPath, ext));

    // missing_script: only when this broken ext_resource is an attached script.
    if (ext.type === "Script" && scriptAttachments.has(ext.id)) {
      issues.push(makeMissingScriptIssue(resPath, ext));
    }
  }

  return issues;
}

/**
 * Resolve an ext_resource to an existing `res://` path, or `null` when broken.
 *
 * Resolution order (any hit = resolvable):
 *   1. `uid=` resolves through the offline index → check the mapped path exists.
 *   2. `path=` is a `res://` path → check it exists on disk via `resolveResPath`.
 *
 * A `path=` that exists on disk wins even when the uid is absent/stale — the
 * file is present, so the reference is not broken (the uid would be healed on
 * the next editor reimport).
 */
function resolveExtResourcePath(
  ext: ParsedExtResource,
  index: UidPathIndex,
  projectRoot: string,
): string | null {
  // uid-first: the uid index is the authoritative identity map.
  if (ext.uid !== null) {
    const byUid = resolveUidToPath(index, ext.uid);
    if (byUid !== null && resPathExistsOnDisk(byUid, projectRoot)) {
      return byUid;
    }
  }
  // path fallback: resolve + existence-check.
  if (ext.path.startsWith("res://") && resPathExistsOnDisk(ext.path, projectRoot)) {
    return ext.path;
  }
  return null;
}

/** Resolve a `res://` path under the project root and check it exists on disk.
 *  Returns false for traversal/symlink-escape (resolveResPath refusal) too. */
function resPathExistsOnDisk(resPath: string, projectRoot: string): boolean {
  const resolved = resolveResPath(resPath, projectRoot);
  if (resolved.kind !== "ok") return false;
  return existsSync(resolved.nativePath);
}

// ---------------------------------------------------------------------------
// Issue builders
// ---------------------------------------------------------------------------

function makeBrokenReferenceIssue(
  resPath: string,
  ext: ParsedExtResource,
): BaselineIssue {
  const severity: IssueSeverity = "Error";
  const target =
    ext.path !== ""
      ? ext.path
      : ext.uid !== null
        ? ext.uid
        : "<unknown>";
  return {
    ruleId: "broken_references",
    severity,
    assetPath: resPath,
    issueCode: "broken_scene_reference",
    description: `[ext_resource id="${ext.id}" type="${ext.type}"] (line ${ext.line}) references ${target}, which does not resolve on disk or in the offline uid index.`,
  };
}

function makeMissingScriptIssue(
  resPath: string,
  ext: ParsedExtResource,
): BaselineIssue {
  const severity: IssueSeverity = "Error";
  return {
    ruleId: "missing_scripts",
    severity,
    assetPath: resPath,
    issueCode: "missing_script",
    description: `Node script attachment ExtResource("${ext.id}") (declared line ${ext.line}) points at ${ext.path || ext.uid || "<unknown>"}, which does not exist on disk.`,
  };
}

// ---------------------------------------------------------------------------
// Header-attr extraction (mirrors references.ts extractHeaderAttr)
// ---------------------------------------------------------------------------

/** Extract `key="value"` from a Godot header line. Leading-space / boundary
 *  probe so `id` does not match inside `uid`. */
function extractHeaderAttr(line: string, key: string): string | null {
  const probe = " " + key + '="';
  let idx = line.indexOf(probe);
  if (idx < 0) {
    const alt = key + '="';
    idx = line.indexOf(alt);
    if (idx < 0) return null;
    if (idx > 0 && /[A-Za-z0-9_]/.test(line[idx - 1]!)) return null;
    const start = idx + alt.length;
    const end = line.indexOf('"', start);
    if (end < 0) return null;
    return line.slice(start, end);
  }
  const start = idx + probe.length;
  const end = line.indexOf('"', start);
  if (end < 0) return null;
  return line.slice(start, end);
}
