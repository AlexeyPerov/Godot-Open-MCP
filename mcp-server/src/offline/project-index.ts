// Deterministic one-level `res://` directory listing from disk (P7.3).
//
// Backs the offline `godot_open_mcp_filesystem_list` fallback. When the Godot
// editor is unreachable, this module lists the immediate children of a `res://`
// directory straight from the native filesystem — no EditorFileSystem, no import
// index, no resource loads. It preserves the live tool's response shape
// (`{ path, directoryCount, fileCount, entries, pagination }`) so an agent
// cannot tell the two sources apart by structure.
//
// Adapted from Unity Open MCP's offline directory walk (adapt fidelity for the
// bounded, paged, deterministic ordering + skip-directory discipline). The
// Godot-specific deltas:
//   - Root is `res://`, not `Assets`. Project marker is `project.godot`.
//   - Skip set swaps Unity's `Library/Temp/Obj` for Godot's `.godot/` import
//     cache; VCS + node_modules are shared.
//   - Resource type is best-effort by FILE EXTENSION — there is no importer to
//     query offline. Unknown extensions surface `resourceType: null` rather
//     than a guess; UIDs are always `null` (offline cannot read the UID table).
//   - One level per call (the live tool's contract); recursive trees are out
//     of scope.
//
// Security: every path is resolved through {@link resolveResDir}, which rejects
// traversal, non-`res://` schemes, and symlink escapes. A symlinked entry
// inside the listed directory is classified against its canonical target:
// targets outside the project are excluded (never listed), in-project symlinked
// directories appear as directories.

import { readdir, stat } from "node:fs/promises";
import type { Dirent } from "node:fs";
import { extname, join } from "node:path";

import {
  canonicalRoot,
  isInside,
  resolveResDir,
} from "./project-paths.js";

/** Maximum combined entries (directories + files) the listing will ever
 *  materialize before paging. Bounds memory + token budget for pathological
 *  huge directories; the remainder is paged. */
export const MAX_LISTING_ENTRIES = 5000;

/** Internal skip set: directories the listing NEVER surfaces, regardless of
 *  `include_hidden`. These are engine/import/VCS internals a caller has no
 *  business listing. */
const INTERNAL_SKIP_DIRS = new Set<string>([
  ".godot", // Godot's import cache + generated .imports
  ".git", // VCS internals
  ".hg", // VCS internals
  ".svn", // VCS internals
  "node_modules", // JS deps (a Godot project may sit beside a web toolchain)
]);

/** Internal skip set: FILES the listing NEVER surfaces, regardless of
 *  `include_hidden`. Godot's EditorFileSystem does not index these as
 *  resources, so excluding them keeps the offline listing in parity with the
 *  live tool (otherwise offline would surface files the live read hides). */
const INTERNAL_SKIP_FILES = new Set<string>([
  "project.godot", // the project marker/config — handled by ProjectSettings, not the asset index
]);

/** A single listed entry — field-for-field compatible with the live
 *  `FileSystemEntry` shape. `resourceType` + `uid` are best-effort offline. */
export interface OfflineListEntry {
  /** Native basename (last path segment). */
  name: string;
  /** Canonical `res://` path. Directories end with `/`; files never do. */
  path: string;
  /** True for directories, false for files. */
  isDirectory: boolean;
  /** Best-effort resource type from the file extension, or `null` when the
   *  extension is unknown / the entry is a directory. Offline cannot query the
   *  importer; this is an extension-based guess, never importer authority. */
  resourceType: string | null;
  /** Always `null` offline — the UID table lives in `.godot/` import state,
   *  which the listing refuses to read. */
  uid: null;
}

/** Pagination block matching the live contract. */
export interface OfflineListPagination {
  /** Opaque continuation cursor, or `null` when this is the last page. */
  nextCursor: string | null;
}

/** The offline `filesystem_list` envelope. Field-for-field compatible with the
 *  live result, plus the documented offline deltas (`stateSource`). */
export interface OfflineListResult {
  /** Canonical `res://` directory path that was listed (trailing slash). */
  path: string;
  /** Total immediate sub-directories (full count, not page-bounded). */
  directoryCount: number;
  /** Total immediate files (full count, not page-bounded). */
  fileCount: number;
  /** Current page of entries (directories first, then files, each group sorted
   *  by name). */
  entries: OfflineListEntry[];
  /** Paging cursor — `nextCursor: null` marks the last page. */
  pagination: OfflineListPagination;
  /** Marks the read as disk-origin so a client never mistakes it for live. */
  stateSource: "disk";
}

/** Structured listing failure. The router maps `code` to the matching error
 *  envelope. */
export interface OfflineListError {
  code:
    | "project_not_found"
    | "project_config_unreadable"
    | "invalid_path"
    | "path_outside_project"
    | "directory_not_found"
    | "directory_unreadable"
    | "invalid_cursor"
    | "stale_cursor"
    | "offline_error";
  message: string;
}

/** Listing outcome — success carries the envelope; failure carries a structured
 *  error. */
export type ListProjectDirectoryResult =
  | { ok: true; result: OfflineListResult }
  | { ok: false; error: OfflineListError };

/** Cursor payload (opaque to the caller; we base64-encode a small JSON object).
 *  Carries the offset + a fingerprint so a mutated directory between pages is
 *  rejected as `stale_cursor` rather than silently skipping/duplicating. */
interface CursorPayload {
  /** Absolute offset into the FULL ordered entries array (dirs + files). */
  offset: number;
  /** Fingerprint of the directory's full ordered entry list at page-1 time.
   *  Recomputed on the next call; a mismatch → `stale_cursor`. */
  fingerprint: string;
  /** Canonical `res://` directory the cursor belongs to. A cursor from another
   *  path is `invalid_cursor`. */
  path: string;
}

const CURSOR_VERSION = 1;

interface EncodedCursor {
  v: number;
  p: CursorPayload;
}

/** Default + bounds for the page_size argument (matches the live tool's
 *  schema). */
const DEFAULT_PAGE_SIZE = 100;
const MAX_PAGE_SIZE = 500;
const MIN_PAGE_SIZE = 1;

/**
 * List the immediate children of one `res://` directory from disk, with
 * deterministic ordering + paging. The single public entry point the router
 * calls for the offline `filesystem_list` fallback.
 *
 * Pipeline:
 *   1. Resolve + validate the `res://` directory safely (traversal / symlink
 *      escape refusal). Bare/empty/`res://` → project root.
 *   2. Stat the directory; missing → `directory_not_found`, unreadable →
 *      `directory_unreadable`.
 *   3. `readdir({ withFileTypes: true })` for immediate children.
 *   4. Filter internals (`.godot/`, VCS, `node_modules/`) always; filter
 *      hidden (dotfile) entries unless `include_hidden`.
 *   5. Classify symlinks against their canonical target — escapes excluded,
 *         in-project symlinked dirs counted as dirs.
 *   6. Build separate dir/file arrays; sort each by name (locale-independent
 *      ordinal compare); concatenate dirs-then-files.
 *   7. Compute a fingerprint of the full ordered list for cursor consistency.
 *   8. Apply cursor (decode + validate path + fingerprint) then page slice.
 *
 * Never throws — every failure maps to a structured `OfflineListError`.
 *
 * @param resDir       `res://` directory to list (bare/empty → project root).
 * @param projectRoot  Native project root (the directory containing
 *                     `project.godot`).
 * @param opts         `{ pageSize?, cursor?, includeHidden? }`.
 */
export async function listProjectDirectoryOffline(
  resDir: string,
  projectRoot: string,
  opts: {
    pageSize?: number;
    cursor?: string;
    includeHidden?: boolean;
    /** Override the hard entry cap (testing only; defaults to
     *  {@link MAX_LISTING_ENTRIES}). Exposed so a test can prove the cap
     *  behavior without materializing thousands of real files. */
    maxEntries?: number;
  } = {},
): Promise<ListProjectDirectoryResult> {
  const includeHidden = opts.includeHidden === true;
  const pageSize = normalizePageSize(opts.pageSize);
  const maxEntries =
    typeof opts.maxEntries === "number" && opts.maxEntries > 0
      ? opts.maxEntries
      : MAX_LISTING_ENTRIES;

  // 1. Safe resolution.
  const resolved = resolveResDir(resDir, projectRoot);
  if (resolved.kind !== "ok") {
    return listErr(resolved.kind, resolved.message);
  }

  // 2. Directory exists + is a directory.
  let dirStat;
  try {
    dirStat = await stat(resolved.nativePath);
  } catch (e) {
    const code = (e as NodeJS.ErrnoException)?.code;
    if (code === "ENOENT") {
      return listErr(
        "directory_not_found",
        `directory not found at '${truncate(resDir)}'.`,
      );
    }
    if (code === "EACCES") {
      return listErr(
        "directory_unreadable",
        `permission denied reading directory '${truncate(resDir)}'.`,
      );
    }
    return listErr(
      "directory_unreadable",
      `cannot stat directory: ${(e as Error)?.message ?? code ?? "unknown error"}`,
    );
  }
  if (!dirStat.isDirectory()) {
    // The input names a file, not a directory.
    return listErr(
      "invalid_path",
      `path is not a directory: '${truncate(resDir)}'.`,
    );
  }

  // 3. Read immediate children.
  let dirents: Dirent[];
  try {
    dirents = await readdir(resolved.nativePath, { withFileTypes: true });
  } catch (e) {
    const code = (e as NodeJS.ErrnoException)?.code;
    if (code === "EACCES") {
      return listErr(
        "directory_unreadable",
        `permission denied listing directory '${truncate(resDir)}'.`,
      );
    }
    return listErr(
      "directory_unreadable",
      `cannot read directory: ${(e as Error)?.message ?? code ?? "unknown error"}`,
    );
  }

  // Canonical project root for symlink classification (realpath'd, no trailing
  // sep). resolveResDir already guaranteed containment of the listed dir, but
  // the per-entry symlink check needs the canonical root to compare targets
  // against.
  const projectReal = canonicalRoot(projectRoot);
  if (projectReal === null) {
    return listErr(
      "path_outside_project",
      "project root cannot be canonicalized for symlink classification.",
    );
  }

  // 4-6. Filter + classify + sort into the full ordered entries array.
  const dirs: OfflineListEntry[] = [];
  const files: OfflineListEntry[] = [];
  const canonicalDirRes = canonicalRoot(resolved.nativePath) ?? resolved.nativePath;
  for (const dirent of dirents) {
    const name = dirent.name;
    // Internal skip ALWAYS wins — include_hidden must not expose .godot/,
    // VCS internals, or the project marker file (documented safeguard).
    if (INTERNAL_SKIP_DIRS.has(name)) continue;
    if (INTERNAL_SKIP_FILES.has(name)) continue;
    if (!includeHidden && isHiddenName(name)) continue;

    const entry = await classifyEntry(
      dirent,
      resolved.nativePath,
      canonicalDirRes,
      projectReal,
    );
    if (entry === null) continue; // symlink-escape excluded silently.
    if (entry.isDirectory) dirs.push(entry);
    else files.push(entry);
  }

  dirs.sort(compareByName);
  files.sort(compareByName);
  const fullOrdered = [...dirs, ...files];
  const directoryCount = dirs.length;
  const fileCount = files.length;

  // Hard cap — a pathological directory beyond this is paged, and the tail is
  // unreachable in one response (matches the live tool's bounded discipline).
  const capped = fullOrdered.slice(0, maxEntries);

  // 7. Fingerprint for cursor consistency.
  const fingerprint = fingerprintEntries(capped);

  // Canonical res:// path for the listed directory (trailing slash form).
  const canonicalResDir = toResDirPath(resolved.nativePath, projectReal);

  // 8. Cursor decode + validation, then page slice.
  let offset = 0;
  if (typeof opts.cursor === "string" && opts.cursor !== "") {
    const decoded = decodeCursor(opts.cursor);
    if (decoded === null) {
      return listErr("invalid_cursor", "cursor is malformed or unrecognized.");
    }
    if (decoded.path !== canonicalResDir) {
      return listErr(
        "invalid_cursor",
        "cursor does not belong to this directory path.",
      );
    }
    if (decoded.fingerprint !== fingerprint) {
      return listErr(
        "stale_cursor",
        "directory contents changed since the previous page; re-list from the first page.",
      );
    }
    offset = decoded.offset;
    if (offset < 0 || offset > capped.length) {
      return listErr("invalid_cursor", "cursor offset is out of range.");
    }
  }

  const page = capped.slice(offset, offset + pageSize);
  const nextOffset = offset + page.length;
  const hasNext = nextOffset < capped.length;
  const nextCursor = hasNext
    ? encodeCursor({
        offset: nextOffset,
        fingerprint,
        path: canonicalResDir,
      })
    : null;

  const result: OfflineListResult = {
    path: canonicalResDir,
    directoryCount,
    fileCount,
    entries: page,
    pagination: { nextCursor },
    stateSource: "disk",
  };
  return { ok: true, result };
}

// ---------------------------------------------------------------------------
// Entry classification — symlink-aware kind resolution.
// ---------------------------------------------------------------------------

/**
 * Classify a single `Dirent` into a public entry, resolving symlinks to their
 * canonical target to decide directory-ness + project containment.
 *
 * - A regular directory → directory entry.
 * - A regular file → file entry (resource type from extension).
 * - A symlink → resolve its target:
 *     - target missing → exclude (broken link; surfacing it as a phantom entry
 *       would mislead an agent).
 *     - target outside project → exclude (symlink escape; documented safeguard).
 *     - target inside project → classify by the TARGET's kind (a symlink to a
 *       directory is a directory entry; a symlink to a file is a file entry).
 *
 * Returns `null` when the entry should be excluded (broken or escaping
 * symlink). Never throws — stat failures on a non-symlink entry surface as
 * `directory_unreadable` via the caller's readdir path (already handled).
 */
async function classifyEntry(
  dirent: Dirent,
  parentDir: string,
  canonicalParent: string,
  projectReal: string,
): Promise<OfflineListEntry | null> {
  const name = dirent.name;
  const nativePath = join(parentDir, name);
  const resPath = toResEntryPath(canonicalParent, name, projectReal);

  if (dirent.isSymbolicLink()) {
    // Resolve the symlink target via stat (follows the link). A broken link
    // (ENOENT) or an escaping target is excluded.
    let targetStat;
    try {
      targetStat = await stat(nativePath);
    } catch {
      // Broken symlink — exclude silently rather than surfacing a phantom.
      return null;
    }
    const targetReal = canonicalRoot(nativePath);
    if (targetReal === null || !isInside(targetReal, projectReal)) {
      // Symlink target escapes the project — exclude (security safeguard).
      return null;
    }
    if (targetStat.isDirectory()) {
      return makeDirEntry(name, resPath);
    }
    return makeFileEntry(name, resPath);
  }

  if (dirent.isDirectory()) {
    return makeDirEntry(name, resPath);
  }
  if (dirent.isFile()) {
    return makeFileEntry(name, resPath);
  }
  // Block/device/FIFO/socket — exclude (not a project resource).
  return null;
}

function makeDirEntry(name: string, resPath: string): OfflineListEntry {
  return {
    name,
    // Directory entries always end with a trailing slash (matches the live
    // tool's directory convention + the FileSystemEntry contract).
    path: ensureTrailingSlash(resPath),
    isDirectory: true,
    resourceType: null,
    uid: null,
  };
}

function makeFileEntry(name: string, resPath: string): OfflineListEntry {
  return {
    name,
    path: resPath,
    isDirectory: false,
    resourceType: resourceTypeForExtension(extname(name)),
    uid: null,
  };
}

// ---------------------------------------------------------------------------
// res:// path normalization for output.
// ---------------------------------------------------------------------------

/**
 * Canonical `res://` directory path for the listed directory. Always ends in
 * `/` (matches the live tool's directory convention). The project root maps to
 * bare `res://`.
 *
 * Computed from the canonical (realpath'd) native dir so the output reflects
 * the on-disk identity, not the (possibly symlinked) input. The caller has
 * already resolved + canonicalized the native dir via {@link resolveResDir};
 * this helper only re-derives the `res://` form relative to the project root.
 */
function toResDirPath(nativeDir: string, projectReal: string): string {
  const real = canonicalRoot(nativeDir) ?? nativeDir;
  if (real === projectReal) return "res://";
  const rel = real.slice(projectReal.length).replace(/\\/g, "/");
  const trimmed = rel.startsWith("/") ? rel.slice(1) : rel;
  return "res://" + trimmed + "/";
}

/** `res://` path for a single entry (child of the canonical parent dir). */
function toResEntryPath(
  canonicalParent: string,
  name: string,
  projectReal: string,
): string {
  if (canonicalParent === projectReal) {
    return "res://" + name;
  }
  const rel = canonicalParent.slice(projectReal.length).replace(/\\/g, "/");
  const trimmed = rel.startsWith("/") ? rel.slice(1) : rel;
  return "res://" + trimmed + "/" + name;
}

function ensureTrailingSlash(p: string): string {
  return p.endsWith("/") ? p : p + "/";
}

// ---------------------------------------------------------------------------
// Filtering helpers.
// ---------------------------------------------------------------------------

/** True for a hidden (dotfile) name. Applied unless `include_hidden`. */
function isHiddenName(name: string): boolean {
  return name.length > 0 && name.startsWith(".");
}

/**
 * Locale-independent ordinal comparator on entry names. Pinning to
 * `String.prototype.charCodeAt` ordering (NOT `localeCompare`) makes the
 * output deterministic across environments — `localeCompare` would order
 * case-insensitively and vary by ICU data. Directories and files each sort by
 * this comparator independently, then directories precede files.
 */
function compareByName(a: OfflineListEntry, b: OfflineListEntry): number {
  if (a.name < b.name) return -1;
  if (a.name > b.name) return 1;
  return 0;
}

// ---------------------------------------------------------------------------
// Resource-type mapping (best-effort, extension-based).
// ---------------------------------------------------------------------------

/** Best-effort Godot resource type for a file extension. Returns `null` for
 *  unknown extensions — offline never guesses importer authority. Keeping the
 *  table conservative avoids claiming a type the importer would not assign. */
export function resourceTypeForExtension(ext: string): string | null {
  const lower = ext.toLowerCase();
  switch (lower) {
    case ".tscn":
      return "PackedScene";
    case ".tres":
      // Generic Resource — the serialized sub-type is inside the file, not the
      // extension. Do not guess (e.g. StandardMaterial3D also uses .tres).
      return "Resource";
    case ".gd":
      return "GDScript";
    case ".cs":
      return "CSharpScript";
    case ".gdshader":
      return "Shader";
    case ".gdshaderinc":
      // Shader include — a fragment, not a standalone Shader resource. Surface
      // null rather than overclaiming "Shader".
      return null;
    default:
      return null;
  }
}

// ---------------------------------------------------------------------------
// Paging cursor encode/decode (opaque, stateless, fingerprinted).
// ---------------------------------------------------------------------------

/**
 * Compute a lightweight fingerprint of the full ordered entry list. Used to
 * detect directory mutation between pages: the cursor carries the page-1
 * fingerprint, and a mismatch on the next call → `stale_cursor`.
 *
 * The fingerprint mixes sorted `name|kind` pairs via FNV-1a. We deliberately
 * do NOT include mtime/size (the spec suggests it as an option) because:
 *   - Per-entry stat would more than double the listing cost for a marginal
 *     consistency gain (the one-level dir is re-read every page anyway).
 *   - name + kind already catches additions/removals/renames/rekind, which are
 *     the mutations that would actually skip/duplicate entries.
 * A pure content change (editing a file's bytes) does NOT change the entry
 * list and so correctly does NOT invalidate the cursor.
 */
function fingerprintEntries(entries: OfflineListEntry[]): string {
  let hash = 0x811c9dc5;
  for (const e of entries) {
    const blob = e.name + "|" + (e.isDirectory ? "d" : "f");
    for (let i = 0; i < blob.length; i++) {
      hash ^= blob.charCodeAt(i);
      // FNV-1a multiply with 32-bit FNV prime, kept in uint32 range.
      hash = Math.imul(hash, 0x01000193);
    }
  }
  // Unsigned hex string — stable across runs.
  return (hash >>> 0).toString(16).padStart(8, "0");
}

/** Encode a cursor payload to an opaque base64 string. */
function encodeCursor(payload: CursorPayload): string {
  const envelope: EncodedCursor = { v: CURSOR_VERSION, p: payload };
  return Buffer.from(JSON.stringify(envelope), "utf-8").toString("base64url");
}

/** Decode an opaque cursor string. Returns `null` on any malformation. */
function decodeCursor(raw: string): CursorPayload | null {
  let json: string;
  try {
    json = Buffer.from(raw, "base64url").toString("utf-8");
  } catch {
    return null;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    return null;
  }
  if (parsed === null || typeof parsed !== "object") return null;
  const env = parsed as Partial<EncodedCursor>;
  if (env.v !== CURSOR_VERSION) return null;
  const p = env.p;
  if (p === null || typeof p !== "object") return null;
  if (
    typeof p.offset !== "number" ||
    !Number.isFinite(p.offset) ||
    typeof p.fingerprint !== "string" ||
    typeof p.path !== "string"
  ) {
    return null;
  }
  return {
    offset: Math.trunc(p.offset),
    fingerprint: p.fingerprint,
    path: p.path,
  };
}

// ---------------------------------------------------------------------------
// Argument normalization + helpers.
// ---------------------------------------------------------------------------

/** Normalize a `page_size` argument to the live contract: default 100, clamped
 *  to [1, 500]. */
function normalizePageSize(raw: unknown): number {
  if (typeof raw !== "number" || !Number.isFinite(raw)) {
    return DEFAULT_PAGE_SIZE;
  }
  const n = Math.trunc(raw);
  if (n < MIN_PAGE_SIZE) return MIN_PAGE_SIZE;
  if (n > MAX_PAGE_SIZE) return MAX_PAGE_SIZE;
  return n;
}

function truncate(s: string, n = 60): string {
  return s.length > n ? s.slice(0, n) + "…" : s;
}

function listErr(
  code: OfflineListError["code"],
  message: string,
): { ok: false; error: OfflineListError } {
  return { ok: false, error: { code, message } };
}
