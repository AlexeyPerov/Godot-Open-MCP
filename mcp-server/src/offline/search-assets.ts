// Offline project-wide asset search (P17.1).
//
// Multi-criteria search across the `res://` tree. Each criterion that matches
// contributes a reason tag so the agent knows WHY an asset was returned and
// which drill-down to run next:
//   - `by_name`       — file basename substring.
//   - `by_kind`       — extension-derived kind filter (scene/resource/script/
//                       shader/import).
//   - `by_node_type`  — `.tscn` node `type=` attribute substring (e.g.
//                       `Camera3D`, `RigidBody3D`).
//   - `by_script`     — `.tscn`/`.tres` attached script path substring
//                       (`script = ExtResource("…")` resolved, or an
//                       `[ext_resource]` `path=` ending in `.gd`/`.cs`).
//   - `references_uid`— the asset references the given `uid://` token (a
//                       lightweight reverse lookup; `find_references` is the
//                       authoritative tool for one target).
//
// Re-parses per request — no cache (MCP-server offline-read philosophy).
//
// Adapted from Unity Open MCP's `searchAssetsOffline` in
// mcp-server/src/offline/api.ts (adapt fidelity): same reason-tagged match
// shape and `profile` + `page_size`/`cursor` paging contract. Godot deltas:
//   - Identity is `res://` path + `uid://` token (no Unity GUID).
//   - Criteria map onto Godot's INI-style `[ext_resource]`/`[node]` grammar,
//     not Unity's YAML component tree.
//   - `references_uid` reuses the P13.1 token-scanning approach.

import { readFile } from "node:fs/promises";
import { basename, extname } from "node:path";

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
  type UidPathIndex,
} from "./project-index.js";
import { resolveResPath } from "./project-paths.js";

// ---------------------------------------------------------------------------
// Public types
// ---------------------------------------------------------------------------

/** Coarse kind derived from extension. Matches read-asset.ts::AssetKind but is
 *  kept local so the search surface is self-describing. */
export type SearchKind =
  | "scene"
  | "resource"
  | "script"
  | "shader"
  | "import"
  | "other";

/** Reason tags carried by each match (one per criterion that fired). */
export type SearchReason =
  | "by_name"
  | "by_kind"
  | "by_node_type"
  | "by_script"
  | "references_uid";

/** A node-type hit inside a scene match (balanced/full only). */
export interface SearchNodeHit {
  /** Node path inside the scene, or the node name for roots. */
  path: string;
  /** Node `type=` attribute. */
  type: string;
}

export interface SearchMatch {
  /** Canonical `res://` path of the matching asset. */
  assetPath: string;
  /** The asset's own uid when known. */
  uid?: string;
  /** Extension-derived kind. */
  kind: SearchKind;
  /** Why this asset matched (one tag per criterion that fired). */
  reasons: SearchReason[];
  /** Node-type hits (balanced/full only; omitted under compact). */
  nodes?: SearchNodeHit[];
  /** Attached script paths (balanced/full only; omitted under compact). */
  scripts?: string[];
}

export interface SearchAssetsOfflineResult {
  query: {
    name?: string;
    kind?: string;
    node_type?: string;
    script?: string;
    uid?: string;
  };
  matchCount: number;
  matches: SearchMatch[];
  byKind: Record<string, number>;
  detail: DetailLevel;
  truncated: number;
  pagination?: PaginationBlock;
}

export interface SearchAssetsOfflineOpts {
  /** Case-insensitive substring on file basename. */
  name?: string;
  /** Comma-separated kinds (scene/resource/script/shader/import). */
  kind?: string;
  /** Case-insensitive substring on `.tscn` node `type=` attribute. */
  nodeType?: string;
  /** Case-insensitive substring on attached script path. */
  script?: string;
  /** `uid://…` token — matches assets that reference this uid. */
  uid?: string;
  detail?: DetailLevel;
  /** Cap when page_size is omitted. `<= 0` ⇒ unlimited. */
  maxResults?: number;
  pageSize?: number;
  cursor?: string;
  projectRoot: string;
}

/** Extensions scanned by search. Broader than the reverse-reference scan so
 *  `by_name` can find scripts/shaders too. */
const SEARCH_EXTENSIONS: ReadonlySet<string> = new Set([
  ".tscn",
  ".tres",
  ".gd",
  ".cs",
  ".gdshader",
  ".import",
]);

const TOOL_KEY = "search_assets";

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

/**
 * Offline project-wide asset search. Returns reason-tagged matches grouped by
 * kind. Never throws — structural failures surface as empty results.
 */
export async function searchAssetsOffline(
  opts: SearchAssetsOfflineOpts,
): Promise<SearchAssetsOfflineResult> {
  const detail: DetailLevel = opts.detail ?? "summary";
  const nameQuery = (opts.name ?? "").trim().toLowerCase();
  const nodeTypeQuery = (opts.nodeType ?? "").trim().toLowerCase();
  const scriptQuery = (opts.script ?? "").trim().toLowerCase();
  const kindFilter = opts.kind ? parseKindSet(opts.kind) : null;
  const uidQuery =
    typeof opts.uid === "string" && opts.uid !== ""
      ? (extractUidToken(opts.uid) ?? opts.uid.trim())
      : "";

  const maxResults =
    typeof opts.maxResults === "number" && opts.maxResults > 0
      ? opts.maxResults
      : Number.POSITIVE_INFINITY;

  // No criteria → return everything (by_name on "" matches all basenames). The
  // agent almost always passes at least one filter; this is the safe default.
  const hasAnyCriteria =
    nameQuery !== "" ||
    nodeTypeQuery !== "" ||
    scriptQuery !== "" ||
    kindFilter !== null ||
    uidQuery !== "";

  const index = await buildUidPathIndex(opts.projectRoot);

  // Resolve the queried uid → path once (a lightweight reverse lookup matches
  // both the uid token AND the resolved path, mirroring find_references).
  let uidResolvedPath = "";
  if (uidQuery !== "") {
    const mapped = index.uidToPath.get(uidQuery);
    if (mapped !== undefined) uidResolvedPath = mapped;
  }

  const candidates = await collectProjectFiles(opts.projectRoot, SEARCH_EXTENSIONS);

  const matches: SearchMatch[] = [];
  for (const resPath of candidates) {
    if (matches.length >= maxResults) break;
    const kind = kindForPath(resPath);
    if (kindFilter !== null && !kindFilter.has(kind)) continue;

    const native = resolveResPath(resPath, opts.projectRoot);
    if (native.kind !== "ok") continue;

    let content: string;
    try {
      content = await readFile(native.nativePath, "utf-8");
    } catch {
      continue;
    }

    const match = checkMatch(resPath, content, kind, index, {
      nameQuery,
      nodeTypeQuery,
      scriptQuery,
      uidQuery,
      uidResolvedPath,
      hasKindFilter: kindFilter !== null,
      detail,
      hasAnyCriteria,
    });
    if (match) {
      if (match.uid === undefined) {
        const u = index.pathToUid.get(resPath);
        if (u !== undefined) match.uid = u;
      }
      matches.push(match);
    }
  }

  // Deterministic order for paging stability.
  matches.sort((a, b) =>
    a.assetPath < b.assetPath ? -1 : a.assetPath > b.assetPath ? 1 : 0,
  );

  const byKind: Record<string, number> = {};
  for (const m of matches) byKind[m.kind] = (byKind[m.kind] ?? 0) + 1;

  const total = matches.length;
  let page: SearchMatch[];
  let truncated = 0;
  let pagination: PaginationBlock | undefined;

  if (detail === "summary") {
    // Compact: counts + byKind only (no per-match list).
    page = [];
    truncated = total;
  } else {
    const pageSize =
      typeof opts.pageSize === "number" && opts.pageSize > 0
        ? Math.floor(opts.pageSize)
        : 0;
    if (pageSize > 0) {
      const { page: p, block } = applyPaging(matches, TOOL_KEY, {
        page_size: pageSize,
        cursor: opts.cursor,
      });
      page = p;
      truncated = block.truncated;
      pagination = block;
    } else {
      page = matches;
      truncated = 0;
    }
  }

  const result: SearchAssetsOfflineResult = {
    query: {
      name: opts.name,
      kind: opts.kind,
      node_type: opts.nodeType,
      script: opts.script,
      uid: opts.uid,
    },
    matchCount: total,
    matches: page,
    byKind,
    detail,
    truncated,
  };
  if (pagination !== undefined) return attachPagination(result, pagination);
  return result;
}

// ---------------------------------------------------------------------------
// Per-file matcher
// ---------------------------------------------------------------------------

interface MatchContext {
  nameQuery: string;
  nodeTypeQuery: string;
  scriptQuery: string;
  uidQuery: string;
  uidResolvedPath: string;
  hasKindFilter: boolean;
  detail: DetailLevel;
  hasAnyCriteria: boolean;
}

function checkMatch(
  resPath: string,
  content: string,
  kind: SearchKind,
  index: UidPathIndex,
  ctx: MatchContext,
): SearchMatch | null {
  const reasons: SearchReason[] = [];
  const fileName = basename(resPath).toLowerCase();
  const expand = ctx.detail !== "summary";
  const nodeHits: SearchNodeHit[] = [];
  const scriptPaths: string[] = [];

  // by_kind — the file survived the kind filter (applied by the caller). Tag
  // the reason whenever a kind filter is active so the agent sees the channel.
  if (ctx.hasKindFilter) reasons.push("by_kind");

  // by_name — file basename substring.
  if (ctx.nameQuery !== "" && fileName.includes(ctx.nameQuery)) {
    reasons.push("by_name");
  }

  const isText = kind === "scene" || kind === "resource";
  if (isText) {
    const lines = content.split(/\r?\n/);

    // Build ext_resource id → path map for script resolution.
    const extPaths = new Map<string, string>();
    const extScripts: string[] = [];
    for (const line of lines) {
      const t = line.trim();
      if (!t.startsWith("[ext_resource")) continue;
      const id = extractHeaderAttr(t, "id") ?? "";
      const path = extractHeaderAttr(t, "path") ?? "";
      if (id !== "") extPaths.set(id, path);
      const lower = path.toLowerCase();
      if (lower.endsWith(".gd") || lower.endsWith(".cs")) extScripts.push(path);
    }

    // by_script — attached script substring.
    if (ctx.scriptQuery !== "") {
      // Direct `[ext_resource path="…gd"]` hit.
      let hit = extScripts.some((p) => p.toLowerCase().includes(ctx.scriptQuery));
      // Resolved `script = ExtResource("id")` usage.
      const resolved = new Set<string>();
      for (const line of lines) {
        const id = extResourceIdFromUsage(line);
        if (id === null) continue;
        const path = extPaths.get(id);
        if (path && (path.toLowerCase().endsWith(".gd") || path.toLowerCase().endsWith(".cs"))) {
          resolved.add(path);
        }
      }
      for (const p of resolved) {
        if (p.toLowerCase().includes(ctx.scriptQuery)) hit = true;
        if (expand && !scriptPaths.includes(p)) scriptPaths.push(p);
      }
      // Also surface the matching ext_resource script paths on expand.
      if (expand) {
        for (const p of extScripts) {
          if (p.toLowerCase().includes(ctx.scriptQuery) && !scriptPaths.includes(p)) {
            scriptPaths.push(p);
          }
        }
      }
      if (hit) reasons.push("by_script");
    } else if (expand && extScripts.length > 0) {
      // No script filter but expand requested — surface the attached scripts.
      for (const p of extScripts) {
        if (!scriptPaths.includes(p)) scriptPaths.push(p);
      }
    }

    // by_node_type — scene node type substring (scenes only).
    if (ctx.nodeTypeQuery !== "" && kind === "scene") {
      let hit = false;
      for (const line of lines) {
        const t = line.trim();
        if (!t.startsWith("[node ")) continue;
        const type = extractHeaderAttr(t, "type") ?? "";
        if (type === "") continue;
        if (type.toLowerCase().includes(ctx.nodeTypeQuery)) {
          hit = true;
          if (expand) {
            const name = extractHeaderAttr(t, "name") ?? "";
            const parent = extractHeaderAttr(t, "parent") ?? "";
            nodeHits.push({ path: nodePath(name, parent), type });
          }
        }
      }
      if (hit) reasons.push("by_node_type");
    }
  }

  // references_uid — the asset text contains the queried uid token OR the
  // path the uid resolves to (a lightweight reverse lookup; the asset's OWN
  // uid/path is never a self-reference).
  if (ctx.uidQuery !== "") {
    const ownUid = index.pathToUid.get(resPath) ?? "";
    const ownPath = resPath;
    const uidHit =
      ownUid !== ctx.uidQuery && content.includes(ctx.uidQuery);
    const pathHit =
      ctx.uidResolvedPath !== "" &&
      ctx.uidResolvedPath !== ownPath &&
      content.includes(ctx.uidResolvedPath);
    if (uidHit || pathHit) reasons.push("references_uid");
  }

  // No criteria at all → treat as a broad `by_name` (list everything).
  if (!ctx.hasAnyCriteria) reasons.push("by_name");
  if (reasons.length === 0) return null;

  const match: SearchMatch = {
    assetPath: resPath,
    kind,
    reasons,
  };
  if (expand) {
    if (nodeHits.length > 0) match.nodes = nodeHits;
    if (scriptPaths.length > 0) match.scripts = scriptPaths;
  }
  return match;
}

function nodePath(name: string, parent: string): string {
  if (parent === "" || parent === ".") return name;
  return `${parent}/${name}`;
}

function extResourceIdFromUsage(line: string): string | null {
  const m = /ExtResource\("([^"]+)"\)/.exec(line);
  return m ? m[1]! : null;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function parseKindSet(raw: string): Set<SearchKind> {
  const out = new Set<SearchKind>();
  for (const part of raw.split(",")) {
    const k = kindForToken(part.trim().toLowerCase());
    if (k !== null) out.add(k);
  }
  return out;
}

function kindForToken(token: string): SearchKind | null {
  switch (token) {
    case "scene":
    case "tscn":
    case "prefab":
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
    case "import":
      return "import";
    default:
      return null;
  }
}

function kindForPath(resPath: string): SearchKind {
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
    case ".gdshader":
    case ".shader":
      return "shader";
    case ".import":
      return "import";
    default:
      return "other";
  }
}

// ---------------------------------------------------------------------------
// Shared attribute extractor (kept local to avoid cross-module coupling).
// ---------------------------------------------------------------------------

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
