// Offline reverse-reference lookup (P13.1).
//
// Scans `.tscn`/`.tres` (and optionally `.gd` string literals) for assets that
// reference a given `res://` path or `uid://` handle. Re-parses per request —
// no cache (MCP-server offline-read philosophy).
//
// Adapted from Unity Open MCP's `findReferencesOffline` in
// mcp-server/src/offline/api.ts (adapt fidelity): same byKind/byFolder rollups,
// compact/balanced/full detail axis, and max_results / paging sentinel. Godot
// deltas:
//   - References are line-addressable `[ext_resource]` / `uid://` tokens, not
//     YAML GUID PPtrs.
//   - uid↔path resolution is bidirectional via {@link buildUidPathIndex}.
//   - `SubResource("id")` is intra-file only — omitted from reverse edges.
//   - `.gd` `preload`/`load` scanning is opt-in (`include_scripts`, default off).

import { readFile } from "node:fs/promises";
import { dirname, extname } from "node:path";

import {
  applyPaging,
  attachPagination,
  type DetailLevel,
  type PaginationBlock,
} from "../output-profile.js";
import {
  buildUidPathIndex,
  collectProjectFiles,
  extractUidToken,
  REFERENCE_SCAN_EXTENSIONS,
  SCRIPT_SCAN_EXTENSIONS,
  type UidPathIndex,
} from "./project-index.js";
import { resolveResPath } from "./project-paths.js";

// ---------------------------------------------------------------------------
// Public types
// ---------------------------------------------------------------------------

export interface ReferencedByEntry {
  /** `res://` path of the referencing asset. */
  assetPath: string;
  /** Referencing asset's own uid when known. */
  uid?: string;
  /** Coarse kind: `scene` | `resource` | `script` | `other`. */
  kind: string;
  /** Parent folder as `res://…/` (or `res://` for project-root files). */
  folder: string;
  /** Full profile only: field / header locations (capped). */
  locations?: string[];
}

export interface FindReferencesOfflineResult {
  queriedAssetPath: string;
  queriedAssetUid: string;
  /** True when the queried uid has no current path in the offline index. */
  unresolvedUid: boolean;
  referencedBy: ReferencedByEntry[];
  totalCount: number;
  byKind: Record<string, number>;
  byFolder: Record<string, number>;
  detail: DetailLevel;
  /** Remainder after max_results / paging slice (0 when compact). */
  truncated: number;
  pagination?: PaginationBlock;
}

export interface FindReferencesOfflineOpts {
  assetPath?: string;
  uid?: string;
  detail?: DetailLevel;
  /** Cap when page_size is omitted. `<= 0` ⇒ unlimited (paging sentinel). */
  maxResults?: number;
  maxPerFile?: number;
  pageSize?: number;
  cursor?: string;
  /** Opt-in `.gd`/`.cs` string-literal scan (default false). */
  includeScripts?: boolean;
  projectRoot: string;
}

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

const TOOL_KEY = "find_references";

/**
 * Offline reverse dependency lookup. Returns assets that reference the target
 * path and/or uid. Never throws — structural failures surface as empty results
 * with `unresolvedUid` when applicable.
 */
export async function findReferencesOffline(
  opts: FindReferencesOfflineOpts,
): Promise<FindReferencesOfflineResult> {
  const detail: DetailLevel = opts.detail ?? "summary";
  const maxResults =
    typeof opts.maxResults === "number" && opts.maxResults > 0
      ? opts.maxResults
      : Number.POSITIVE_INFINITY;
  const maxPerFile = opts.maxPerFile ?? 5;
  const includeScripts = opts.includeScripts === true;

  const index = await buildUidPathIndex(opts.projectRoot);
  const resolved = resolveTarget(opts, index);

  if (resolved.targetPath === "" && resolved.targetUid === "") {
    return emptyResult(detail, resolved);
  }

  const extensions = new Set<string>(REFERENCE_SCAN_EXTENSIONS);
  if (includeScripts) {
    for (const e of SCRIPT_SCAN_EXTENSIONS) extensions.add(e);
  }

  const candidates = await collectProjectFiles(opts.projectRoot, extensions);
  const hits: ReferencedByEntry[] = [];

  for (const resPath of candidates) {
    // Skip self-reference.
    if (resolved.targetPath !== "" && resPath === resolved.targetPath) {
      continue;
    }

    const native = resolveResPath(resPath, opts.projectRoot);
    if (native.kind !== "ok") continue;

    let content: string;
    try {
      content = await readFile(native.nativePath, "utf-8");
    } catch {
      continue;
    }

    // Fast reject: skip files that mention neither the path nor the uid.
    const mentionsPath =
      resolved.targetPath !== "" && content.includes(resolved.targetPath);
    const mentionsUid =
      resolved.targetUid !== "" && content.includes(resolved.targetUid);
    if (!mentionsPath && !mentionsUid) continue;

    const ext = extname(resPath).toLowerCase();
    let locations: string[] | undefined;

    if (ext === ".tscn" || ext === ".tres") {
      const match = scanResourceText(
        content,
        resolved.targetPath,
        resolved.targetUid,
        detail === "verbose" ? maxPerFile : 0,
      );
      if (!match.matches) continue;
      if (detail === "verbose") locations = match.locations;
    } else if (includeScripts && (ext === ".gd" || ext === ".cs")) {
      const match = scanScriptLiterals(
        content,
        resolved.targetPath,
        resolved.targetUid,
        detail === "verbose" ? maxPerFile : 0,
      );
      if (!match.matches) continue;
      if (detail === "verbose") locations = match.locations;
    } else {
      continue;
    }

    const kind = kindForPath(resPath);
    const folder = folderForPath(resPath);
    const referencerUid = index.pathToUid.get(resPath);

    const entry: ReferencedByEntry = {
      assetPath: resPath,
      kind,
      folder,
    };
    if (referencerUid !== undefined) entry.uid = referencerUid;
    if (locations !== undefined && locations.length > 0) {
      entry.locations = locations;
    }
    hits.push(entry);
  }

  // Deterministic order for paging stability.
  hits.sort((a, b) =>
    a.assetPath < b.assetPath ? -1 : a.assetPath > b.assetPath ? 1 : 0,
  );

  const byKind: Record<string, number> = {};
  const byFolder: Record<string, number> = {};
  for (const hit of hits) {
    byKind[hit.kind] = (byKind[hit.kind] ?? 0) + 1;
    byFolder[hit.folder] = (byFolder[hit.folder] ?? 0) + 1;
  }

  const totalCount = hits.length;
  let referencedBy: ReferencedByEntry[];
  let truncated = 0;
  let pagination: PaginationBlock | undefined;

  if (detail === "summary") {
    referencedBy = [];
  } else {
    const pageSize =
      typeof opts.pageSize === "number" && opts.pageSize > 0
        ? Math.floor(opts.pageSize)
        : 0;

    if (pageSize > 0) {
      const { page, block } = applyPaging(hits, TOOL_KEY, {
        page_size: pageSize,
        cursor: opts.cursor,
      });
      referencedBy = page;
      truncated = block.truncated;
      pagination = block;
    } else {
      referencedBy = hits.slice(0, maxResults);
      truncated = Math.max(0, hits.length - referencedBy.length);
    }
  }

  const result: FindReferencesOfflineResult = {
    queriedAssetPath: resolved.targetPath,
    queriedAssetUid: resolved.targetUid,
    unresolvedUid: resolved.unresolvedUid,
    referencedBy,
    totalCount,
    byKind,
    byFolder,
    detail,
    truncated,
  };
  if (pagination !== undefined) {
    return attachPagination(result, pagination);
  }
  return result;
}

// ---------------------------------------------------------------------------
// Target resolution
// ---------------------------------------------------------------------------

interface ResolvedTarget {
  targetPath: string;
  targetUid: string;
  unresolvedUid: boolean;
}

function resolveTarget(
  opts: FindReferencesOfflineOpts,
  index: UidPathIndex,
): ResolvedTarget {
  let targetPath = "";
  let targetUid = "";
  let unresolvedUid = false;

  if (typeof opts.uid === "string" && opts.uid !== "") {
    const uid = extractUidToken(opts.uid);
    if (uid !== null) {
      targetUid = uid;
      const mapped = index.uidToPath.get(uid);
      if (mapped !== undefined) {
        targetPath = mapped;
      } else {
        unresolvedUid = true;
      }
    }
  }

  if (typeof opts.assetPath === "string" && opts.assetPath !== "") {
    // Prefer an explicit path when both are supplied (xor is schema-enforced;
    // defense in depth still accepts a path override).
    const path = normalizeResPath(opts.assetPath);
    if (path !== null) {
      targetPath = path;
      if (targetUid === "") {
        targetUid = index.pathToUid.get(path) ?? "";
      }
      // Path was given — even if the uid is unknown, we are not "unresolved".
      unresolvedUid = false;
    }
  } else if (targetPath === "" && targetUid !== "") {
    // uid-only query with no mapping — already flagged unresolvedUid.
  }

  return { targetPath, targetUid, unresolvedUid };
}

function normalizeResPath(raw: string): string | null {
  const trimmed = raw.trim();
  if (!trimmed.startsWith("res://")) return null;
  // Strip trailing slash on files (directories are not valid targets here).
  return trimmed.endsWith("/") ? trimmed.slice(0, -1) : trimmed;
}

function emptyResult(
  detail: DetailLevel,
  resolved: ResolvedTarget,
): FindReferencesOfflineResult {
  return {
    queriedAssetPath: resolved.targetPath,
    queriedAssetUid: resolved.targetUid,
    unresolvedUid: resolved.unresolvedUid,
    referencedBy: [],
    totalCount: 0,
    byKind: {},
    byFolder: {},
    detail,
    truncated: 0,
  };
}

// ---------------------------------------------------------------------------
// `.tscn` / `.tres` scanner
// ---------------------------------------------------------------------------

interface ScanMatch {
  matches: boolean;
  locations: string[];
}

/**
 * Scan one resource text for reverse edges to the target. An edge exists when:
 *   - an `[ext_resource]` declares `path=` / `uid=` matching the target, or
 *   - a bare `uid://…` token matching the target uid appears anywhere.
 * `SubResource` usages are ignored (intra-file). ExtResource usages that
 * resolve to a matching decl contribute full-profile locations.
 */
function scanResourceText(
  content: string,
  targetPath: string,
  targetUid: string,
  maxLocations: number,
): ScanMatch {
  const lines = content.split(/\r?\n/);
  const matchingIds = new Set<string>();
  const locations: string[] = [];
  let matches = false;

  const pushLoc = (label: string): void => {
    if (maxLocations <= 0) return;
    if (locations.length >= maxLocations) return;
    locations.push(label);
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!;
    const lineNo = i + 1;

    if (line.length > 0 && line[0] === "[") {
      if (line.startsWith("[ext_resource")) {
        const id = extractHeaderAttr(line, "id");
        const path = extractHeaderAttr(line, "path");
        const uid = extractHeaderAttr(line, "uid");
        const pathHit =
          targetPath !== "" && path !== null && path === targetPath;
        const uidHit =
          targetUid !== "" &&
          uid !== null &&
          extractUidToken(uid) === targetUid;
        if (pathHit || uidHit) {
          matches = true;
          if (id !== null) matchingIds.add(id);
          pushLoc(
            `ext_resource${id !== null ? ` id=${id}` : ""} (line ${lineNo})`,
          );
        }
      }
      // Also catch bare uid on [gd_scene]/uid=…] of a *different* file —
      // that is the file's own identity, not a reference TO the target. Skip.
      continue;
    }

    // Bare uid:// tokens anywhere in body (rare outside ext_resource, but
    // accepted by the frozen token set).
    if (targetUid !== "" && line.includes(targetUid)) {
      // Avoid double-counting the ext_resource header line (handled above).
      if (!(line.length > 0 && line[0] === "[")) {
        matches = true;
        pushLoc(`uid_token (line ${lineNo})`);
      }
    }

    // ExtResource("id") usages that resolve to a matching decl.
    if (matchingIds.size > 0 && line.includes("ExtResource(")) {
      for (const id of matchingIds) {
        if (!lineIncludesExtResource(line, id)) continue;
        matches = true;
        const field = fieldLabelForUsage(line);
        pushLoc(
          field !== null
            ? `${field} = ExtResource("${id}") (line ${lineNo})`
            : `ExtResource("${id}") (line ${lineNo})`,
        );
      }
    }
  }

  return { matches, locations };
}

function lineIncludesExtResource(line: string, id: string): boolean {
  const token = `ExtResource("${id}")`;
  return line.includes(token);
}

function fieldLabelForUsage(line: string): string | null {
  const trimmed = line.trim();
  // `script = ExtResource("…")` or `instance=ExtResource("…")` in a header.
  const eq = trimmed.indexOf("=");
  if (eq <= 0) return null;
  const left = trimmed.slice(0, eq).trim();
  // Header attribute form: `instance=ExtResource(...)` may appear mid-header.
  if (left.includes(" ")) {
    const parts = left.split(/\s+/);
    return parts[parts.length - 1] ?? null;
  }
  return left.length > 0 ? left : null;
}

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

// ---------------------------------------------------------------------------
// Optional `.gd` / `.cs` literal scanner
// ---------------------------------------------------------------------------

const SCRIPT_PATH_LITERAL =
  /(?:preload|load|ResourceLoader\.load)\s*\(\s*"((?:res|uid):\/\/[^"]+)"/g;

function scanScriptLiterals(
  content: string,
  targetPath: string,
  targetUid: string,
  maxLocations: number,
): ScanMatch {
  const locations: string[] = [];
  let matches = false;
  const lines = content.split(/\r?\n/);

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!;
    SCRIPT_PATH_LITERAL.lastIndex = 0;
    let m: RegExpExecArray | null;
    while ((m = SCRIPT_PATH_LITERAL.exec(line)) !== null) {
      const lit = m[1]!;
      const pathHit = targetPath !== "" && lit === targetPath;
      const uidHit =
        targetUid !== "" && extractUidToken(lit) === targetUid;
      if (!pathHit && !uidHit) continue;
      matches = true;
      if (maxLocations > 0 && locations.length < maxLocations) {
        locations.push(`script_literal (line ${i + 1})`);
      }
    }
  }

  return { matches, locations };
}

// ---------------------------------------------------------------------------
// Kind / folder helpers
// ---------------------------------------------------------------------------

function kindForPath(resPath: string): string {
  const ext = extname(resPath).toLowerCase();
  switch (ext) {
    case ".tscn":
    case ".scn":
      return "scene";
    case ".tres":
    case ".res":
      return "resource";
    case ".gd":
    case ".cs":
      return "script";
    default:
      return "other";
  }
}

function folderForPath(resPath: string): string {
  // `res://foo/bar.tscn` → `res://foo/`; `res://root.tscn` → `res://`
  const withoutScheme = resPath.slice("res://".length);
  const dir = dirname(withoutScheme).replace(/\\/g, "/");
  if (dir === "." || dir === "") return "res://";
  return "res://" + dir + "/";
}
