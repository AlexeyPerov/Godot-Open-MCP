// Offline compressed `res://` directory listing (P17.1).
//
// Walks the project tree straight from disk (no editor) and returns a
// compressed folder → kind → count + sample-file-names view. Skips Godot's
// import cache + VCS internals (mirrors `collectProjectFiles`'s skip set) and
// never surfaces the `.import` / `.uid` sidecars as standalone assets — they
// ride their parent file. Useful for understanding project structure before
// drilling into specific assets with `read_asset` / `search_assets`.
//
// Re-parses per request — no cache (MCP-server offline-read philosophy).
//
// Adapted from Unity Open MCP's `listAssetsOffline` in
// mcp-server/src/offline/api.ts (copy fidelity for the folder → kind → count
// shape + sample list). Godot deltas:
//   - Root is `res://`; kinds are Godot's text-format extensions.
//   - Sidecars (`.import`/`.uid`) are folded into their parent, not listed.
//   - `profile` (compact/balanced/full) controls the sample-per-folder cap
//     and whether the per-kind breakdown is inlined.

import { extname } from "node:path";

import {
  applyPaging,
  attachPagination,
  type DetailLevel,
  type PaginationBlock,
} from "../output-profile.js";
import { collectProjectFiles } from "./project-index.js";

// ---------------------------------------------------------------------------
// Public types
// ---------------------------------------------------------------------------

/** Coarse kind derived from extension (mirrors search-assets.ts). */
export type ListKind =
  | "scene"
  | "resource"
  | "script"
  | "shader"
  | "texture"
  | "audio"
  | "font"
  | "other";

export interface KindBucket {
  count: number;
  /** Sample file basenames (extension stripped) up to the sample cap. */
  sample: string[];
}

export interface FolderListing {
  /** Canonical `res://…/` folder path. */
  folder: string;
  /** Per-kind breakdown (compact = counts only; balanced/full adds samples). */
  kinds: Record<string, KindBucket>;
  /** Total files in this folder (all kinds). */
  fileCount: number;
}

export interface ListAssetsOfflineResult {
  /** Folder the listing was scoped to (default `res://`). */
  root: string;
  /** Echoed `type` filter when supplied. */
  typeFilter?: string;
  folders: FolderListing[];
  totalFiles: number;
  totalFolders: number;
  /** Project-wide kind → count rollup. */
  kindSummary: Record<string, number>;
  /** Folders whose sample list was truncated past the per-kind sample cap. */
  truncated: number;
  detail: DetailLevel;
  pagination?: PaginationBlock;
}

export interface ListAssetsOfflineOpts {
  /** `res://` folder to list under (default `res://`). */
  folder?: string;
  /** Comma-separated kinds to filter (e.g. `scene,resource`). */
  type?: string;
  /** Sample cap per kind per folder (default derived from profile). */
  maxPerFolder?: number;
  detail?: DetailLevel;
  pageSize?: number;
  cursor?: string;
  projectRoot: string;
}

/** Extensions surfaced by the listing. Broader than the search/list scan so a
 *  folder overview shows media assets too (textures/audio/fonts) — they are
 *  listed by extension only, never parsed. */
const LISTABLE_EXTENSIONS: ReadonlySet<string> = new Set([
  ".tscn",
  ".scn",
  ".tres",
  ".res",
  ".gd",
  ".cs",
  ".gdshader",
  ".shader",
  ".png",
  ".jpg",
  ".jpeg",
  ".webp",
  ".svg",
  ".wav",
  ".ogg",
  ".mp3",
  ".ttf",
  ".otf",
  ".json",
  ".cfg",
]);

/** Sidecar extensions that are folded into their parent file and never listed
 *  on their own (Godot writes them next to the imported asset). */
const SIDECAR_EXTENSIONS: ReadonlySet<string> = new Set([".import", ".uid"]);

const TOOL_KEY = "list_assets";

const SAMPLE_CAP_COMPACT = 3;
const SAMPLE_CAP_BALANCED = 6;
const SAMPLE_CAP_FULL = 12;

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

/**
 * Offline compressed `res://` directory listing. Returns folder → kind → count
 * with sample file names. Never throws — an unreadable project returns an empty
 * result.
 */
export async function listAssetsOffline(
  opts: ListAssetsOfflineOpts,
): Promise<ListAssetsOfflineResult> {
  const detail: DetailLevel = opts.detail ?? "summary";
  const typeFilter = opts.type ? parseKindSet(opts.type) : null;
  const sampleCap =
    typeof opts.maxPerFolder === "number" && opts.maxPerFolder > 0
      ? opts.maxPerFolder
      : detail === "verbose"
        ? SAMPLE_CAP_FULL
        : detail === "normal"
          ? SAMPLE_CAP_BALANCED
          : SAMPLE_CAP_COMPACT;

  const rootFolder = normalizeFolder(opts.folder ?? "res://");

  // Collect every listable file under the project root, then filter to the
  // requested folder subtree.
  const all = await collectProjectFiles(opts.projectRoot, LISTABLE_EXTENSIONS);
  const filtered = all.filter(
    (p) => rootFolder === "res://" || p.startsWith(rootFolder),
  );

  const folderMap = new Map<string, FolderListing>();
  const folders: FolderListing[] = [];
  const kindSummary: Record<string, number> = {};
  const truncatedSet = new Set<string>();
  let totalFiles = 0;

  for (const resPath of filtered) {
    const ext = extname(resPath).toLowerCase();
    if (SIDECAR_EXTENSIONS.has(ext)) continue;
    const kind = kindForExt(ext);
    if (typeFilter !== null && !typeFilter.has(kind)) continue;

    const folder = folderForPath(resPath, rootFolder);
    let listing = folderMap.get(folder);
    if (!listing) {
      listing = { folder, kinds: {}, fileCount: 0 };
      folderMap.set(folder, listing);
      folders.push(listing);
    }
    listing.fileCount++;
    totalFiles++;
    kindSummary[kind] = (kindSummary[kind] ?? 0) + 1;

    let bucket = listing.kinds[kind];
    if (!bucket) {
      bucket = { count: 0, sample: [] };
      listing.kinds[kind] = bucket;
    }
    bucket.count++;
    if (bucket.sample.length < sampleCap) {
      const name = baseName(resPath);
      if (!bucket.sample.includes(name)) bucket.sample.push(name);
    } else {
      truncatedSet.add(folder);
    }
  }

  const truncatedFolders = truncatedSet.size;

  folders.sort((a, b) => a.folder.localeCompare(b.folder));

  // Compact profile: drop the sample lists to keep the response small.
  if (detail === "summary") {
    for (const f of folders) {
      for (const k of Object.keys(f.kinds)) {
        f.kinds[k]!.sample = [];
      }
    }
  }

  let page = folders;
  let pagination: PaginationBlock | undefined;
  const pageSize =
    typeof opts.pageSize === "number" && opts.pageSize > 0
      ? Math.floor(opts.pageSize)
      : 0;
  if (pageSize > 0) {
    const { page: p, block } = applyPaging(folders, TOOL_KEY, {
      page_size: pageSize,
      cursor: opts.cursor,
    });
    page = p;
    pagination = block;
  }

  const result: ListAssetsOfflineResult = {
    root: rootFolder,
    folders: page,
    totalFiles,
    totalFolders: folders.length,
    kindSummary,
    truncated: truncatedFolders,
    detail,
  };
  if (opts.type) result.typeFilter = opts.type;
  if (pagination !== undefined) return attachPagination(result, pagination);
  return result;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function normalizeFolder(folder: string): string {
  const trimmed = folder.trim();
  if (trimmed === "" || trimmed === "res://") return "res://";
  const withSlash = trimmed.endsWith("/") ? trimmed : trimmed + "/";
  return withSlash;
}

function folderForPath(resPath: string, rootFolder: string): string {
  // `res://foo/bar.tscn` → `res://foo/`; `res://root.tscn` → `res://`.
  const withoutScheme = resPath.slice("res://".length);
  const slash = withoutScheme.lastIndexOf("/");
  if (slash < 0) return "res://";
  const folder = "res://" + withoutScheme.slice(0, slash + 1);
  // When listing a subtree, keep folder paths relative to the project root
  // (absolute `res://` form) — the root filter already scoped the file set.
  void rootFolder;
  return folder;
}

function baseName(resPath: string): string {
  const file = resPath.slice(resPath.lastIndexOf("/") + 1);
  const dot = file.lastIndexOf(".");
  return dot > 0 ? file.slice(0, dot) : file;
}

function parseKindSet(raw: string): Set<ListKind> {
  const out = new Set<ListKind>();
  for (const part of raw.split(",")) {
    const k = kindForToken(part.trim().toLowerCase());
    if (k !== null) out.add(k);
  }
  return out;
}

function kindForToken(token: string): ListKind | null {
  switch (token) {
    case "scene":
    case "tscn":
      return "scene";
    case "resource":
    case "tres":
      return "resource";
    case "script":
    case "gd":
    case "cs":
      return "script";
    case "shader":
    case "gdshader":
      return "shader";
    case "texture":
    case "png":
    case "jpg":
    case "svg":
      return "texture";
    case "audio":
    case "wav":
    case "ogg":
    case "mp3":
      return "audio";
    case "font":
    case "ttf":
    case "otf":
      return "font";
    default:
      return null;
  }
}

function kindForExt(ext: string): ListKind {
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
    case ".gdshader":
    case ".shader":
      return "shader";
    case ".png":
    case ".jpg":
    case ".jpeg":
    case ".webp":
    case ".svg":
      return "texture";
    case ".wav":
    case ".ogg":
    case ".mp3":
      return "audio";
    case ".ttf":
    case ".otf":
      return "font";
    default:
      return "other";
  }
}
