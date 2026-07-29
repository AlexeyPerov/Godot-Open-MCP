// Offline forward + reverse dependency lookup (P13.2).
//
// Returns what an asset depends on (forward `[ext_resource]` headers), what
// depends on it (reverse — reuses P13.1 findReferencesOffline), broken forward
// edges, optional cycle detection, and optional transitive impact BFS.
//
// Adapted from Unity Open MCP's `dependenciesOffline` in
// mcp-server/src/offline/api.ts (adapt fidelity). Godot deltas:
//   - Identity is `uid://` + `res://` path, not Unity GUIDs.
//   - Forward edges come from `[ext_resource]` header declarations only.
//   - Reverse edges delegate to findReferencesOffline (or a one-pass graph
//     when includeImpact is true).
//   - Always offline — no live bridge variant in v1.

import { readFile } from "node:fs/promises";
import { extname } from "node:path";

import { findReferencesOffline, type ReferencedByEntry } from "./references.js";
import {
  buildUidPathIndex,
  collectProjectFiles,
  extractUidToken,
  REFERENCE_SCAN_EXTENSIONS,
  type UidPathIndex,
} from "./project-index.js";
import { resolveResPath } from "./project-paths.js";

// ---------------------------------------------------------------------------
// Public types
// ---------------------------------------------------------------------------

export type DependenciesDetail = "summary" | "normal";

export interface ForwardEdge {
  /** Target `uid://` when declared on the `[ext_resource]` line. */
  uid: string;
  /** Resolved target `res://` path (empty when unresolved). */
  assetPath: string;
  /** In-file `[ext_resource]` id token. */
  extResourceId: string;
  resolved: boolean;
}

export interface ReverseEdge {
  assetPath: string;
  uid: string;
  kind: string;
}

export interface ImpactEntry {
  assetPath: string;
  /** Hop distance from the queried asset (1 = direct reverse edge). */
  depth: number;
}

export interface DependenciesOfflineResult {
  queriedAssetPath: string;
  queriedAssetUid: string;
  forwardDependencies: ForwardEdge[];
  forwardCount: number;
  /** Distinct unresolved forward-edge target uids. */
  brokenForwardUids: string[];
  /** Dependency cycles through the queried asset (each a path list). */
  cycles: string[][];
  reverseDependencies: ReverseEdge[];
  reverseCount: number;
  impact?: {
    affected: ImpactEntry[];
    affectedCount: number;
    maxDepth: number;
    truncated: boolean;
  };
  detail: DependenciesDetail;
  /** Reverse-edge roster truncation remainder (max_results cap). */
  truncated: number;
  /** Non-fatal: forward extraction could not read/parse the queried asset. */
  forwardSkipped?: string;
  _source: "offline";
}

export interface DependenciesOfflineOpts {
  assetPath?: string;
  uid?: string;
  detail?: DependenciesDetail;
  maxResults?: number;
  includeImpact?: boolean;
  maxImpactDepth?: number;
  maxCycleDepth?: number;
  projectRoot: string;
}

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

export async function dependenciesOffline(
  opts: DependenciesOfflineOpts,
): Promise<DependenciesOfflineResult> {
  const detail: DependenciesDetail = opts.detail ?? "normal";
  const maxResults = opts.maxResults ?? 100;
  const includeImpact = opts.includeImpact === true;
  const maxImpactDepth = clampImpactDepth(opts.maxImpactDepth ?? 5);
  const maxCycleDepth = opts.maxCycleDepth ?? 8;

  const index = await buildUidPathIndex(opts.projectRoot);
  const target = resolveTarget(opts, index);

  if (target.queriedPath === "" && target.queriedUid === "") {
    return emptyDependencies("", "", detail);
  }

  let forwardEdges: ForwardEdge[] = [];
  let brokenUids: string[] = [];
  let cycles: string[][] = [];
  let forwardSkipped: string | undefined;

  if (target.queriedPath !== "") {
    const native = resolveResPath(target.queriedPath, opts.projectRoot);
    if (native.kind !== "ok") {
      forwardSkipped = "queried asset not readable on disk";
    } else {
      try {
        const text = await readFile(native.nativePath, "utf-8");
        forwardEdges = collectAndResolveForwardEdges(
          text,
          index,
          opts.projectRoot,
        );
        brokenUids = brokenForwardUids(forwardEdges);
        if (forwardEdges.length > 0) {
          cycles = await detectCyclesOffline(
            target.queriedPath,
            forwardEdges,
            index,
            opts.projectRoot,
            maxCycleDepth,
          );
        }
      } catch (err) {
        forwardSkipped =
          err instanceof Error ? err.message : String(err);
      }
    }
  } else {
    forwardSkipped = "queried uid has no resolvable path for forward scan";
  }

  const reverseGraph = includeImpact
    ? await buildReverseEdgeGraph(opts.projectRoot, index)
    : null;

  let reverseEdges: ReverseEdge[];
  if (reverseGraph) {
    reverseEdges = findReferencesFromGraph(
      reverseGraph.graph,
      target.queriedUid,
      target.queriedPath,
    );
  } else {
    const refs = await findReferencesOffline({
      assetPath:
        target.queriedPath !== "" ? target.queriedPath : undefined,
      uid: target.queriedUid !== "" ? target.queriedUid : undefined,
      detail: "normal",
      maxResults: 0,
      projectRoot: opts.projectRoot,
    });
    reverseEdges = refs.referencedBy.map(mapReferencedByToReverse);
  }

  let impact: DependenciesOfflineResult["impact"];
  if (includeImpact && reverseGraph) {
    impact = computeTransitiveImpactFromGraph(
      target.queriedUid,
      target.queriedPath,
      reverseGraph.graph,
      reverseGraph.pathToUid,
      maxImpactDepth,
    );
  }

  const displayForward = detail === "summary" ? [] : forwardEdges;
  const totalReverse = reverseEdges.length;
  let displayReverse: ReverseEdge[];
  let truncated = 0;
  if (detail === "summary") {
    displayReverse = [];
  } else if (maxResults > 0 && totalReverse > maxResults) {
    displayReverse = reverseEdges.slice(0, maxResults);
    truncated = totalReverse - maxResults;
  } else {
    displayReverse = reverseEdges;
  }

  return {
    queriedAssetPath: target.queriedPath,
    queriedAssetUid: target.queriedUid,
    forwardDependencies: displayForward,
    forwardCount: forwardEdges.length,
    brokenForwardUids: brokenUids,
    cycles,
    reverseDependencies: displayReverse,
    reverseCount: totalReverse,
    impact,
    detail,
    truncated,
    ...(forwardSkipped !== undefined ? { forwardSkipped } : {}),
    _source: "offline",
  };
}

// ---------------------------------------------------------------------------
// Target resolution
// ---------------------------------------------------------------------------

interface ResolvedTarget {
  queriedPath: string;
  queriedUid: string;
}

function resolveTarget(
  opts: DependenciesOfflineOpts,
  index: UidPathIndex,
): ResolvedTarget {
  let queriedPath = "";
  let queriedUid = "";

  if (typeof opts.uid === "string" && opts.uid !== "") {
    const uid = extractUidToken(opts.uid);
    if (uid !== null) {
      queriedUid = uid;
      queriedPath = index.uidToPath.get(uid) ?? "";
    }
  }

  if (typeof opts.assetPath === "string" && opts.assetPath !== "") {
    const path = normalizeResPath(opts.assetPath);
    if (path !== null) {
      queriedPath = path;
      if (queriedUid === "") {
        queriedUid = index.pathToUid.get(path) ?? "";
      }
    }
  }

  return { queriedPath, queriedUid };
}

function normalizeResPath(raw: string): string | null {
  const trimmed = raw.trim();
  if (!trimmed.startsWith("res://")) return null;
  return trimmed.endsWith("/") ? trimmed.slice(0, -1) : trimmed;
}

function emptyDependencies(
  path: string,
  uid: string,
  detail: DependenciesDetail,
): DependenciesOfflineResult {
  return {
    queriedAssetPath: path,
    queriedAssetUid: uid,
    forwardDependencies: [],
    forwardCount: 0,
    brokenForwardUids: [],
    cycles: [],
    reverseDependencies: [],
    reverseCount: 0,
    detail,
    truncated: 0,
    _source: "offline",
  };
}

function clampImpactDepth(raw: number): number {
  if (!Number.isFinite(raw)) return 5;
  return Math.min(20, Math.max(1, Math.floor(raw)));
}

function mapReferencedByToReverse(entry: ReferencedByEntry): ReverseEdge {
  return {
    assetPath: entry.assetPath,
    uid: entry.uid ?? "",
    kind: entry.kind,
  };
}

// ---------------------------------------------------------------------------
// Forward-edge extraction + resolution
// ---------------------------------------------------------------------------

function collectAndResolveForwardEdges(
  text: string,
  index: UidPathIndex,
  projectRoot: string,
): ForwardEdge[] {
  const edges: ForwardEdge[] = [];
  const seen = new Set<string>();
  const lines = text.split(/\r?\n/);

  for (const line of lines) {
    if (!line.startsWith("[ext_resource")) continue;
    const id = extractHeaderAttr(line, "id") ?? "";
    const declaredPath = extractHeaderAttr(line, "path") ?? "";
    const uidRaw = extractHeaderAttr(line, "uid");
    const uid = uidRaw !== null ? (extractUidToken(uidRaw) ?? uidRaw) : "";
    const key = `${uid}|${declaredPath}|${id}`;
    if (seen.has(key)) continue;
    seen.add(key);

    const uidPresent = uid !== "";
    const pathPresent = declaredPath !== "";
    const uidOk = uidPresent && index.uidToPath.has(uid);
    let pathOk = false;
    if (pathPresent) {
      pathOk = resolveResPath(declaredPath, projectRoot).kind === "ok";
    }

    const resolved = uidOk || pathOk;
    let assetPath = "";
    if (resolved) {
      assetPath = uidOk
        ? (index.uidToPath.get(uid) ?? declaredPath)
        : declaredPath;
    }

    edges.push({
      uid,
      assetPath,
      extResourceId: id,
      resolved,
    });
  }

  return edges;
}

function brokenForwardUids(edges: ForwardEdge[]): string[] {
  const broken: string[] = [];
  const seen = new Set<string>();
  for (const edge of edges) {
    if (edge.resolved) continue;
    if (edge.uid !== "" && seen.add(edge.uid)) {
      broken.push(edge.uid);
    }
  }
  return broken;
}

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
// Cycle detection (forward graph DFS)
// ---------------------------------------------------------------------------

async function detectCyclesOffline(
  startPath: string,
  startEdges: ForwardEdge[],
  index: UidPathIndex,
  projectRoot: string,
  maxDepth: number,
): Promise<string[][]> {
  const cycles: string[][] = [];
  const edgeCache = new Map<string, ForwardEdge[]>();
  edgeCache.set(startPath, startEdges);

  async function edgesOf(path: string): Promise<ForwardEdge[]> {
    const cached = edgeCache.get(path);
    if (cached) return cached;
    const native = resolveResPath(path, projectRoot);
    if (native.kind !== "ok") {
      edgeCache.set(path, []);
      return [];
    }
    try {
      const text = await readFile(native.nativePath, "utf-8");
      const edges = collectAndResolveForwardEdges(text, index, projectRoot);
      edgeCache.set(path, edges);
      return edges;
    } catch {
      edgeCache.set(path, []);
      return [];
    }
  }

  const visiting = new Set<string>();
  const trail: string[] = [];

  async function dfs(current: string): Promise<void> {
    if (trail.length > maxDepth) return;
    const edges = await edgesOf(current);
    for (const edge of edges) {
      if (!edge.resolved || edge.assetPath === "") continue;
      if (edge.assetPath === startPath) {
        cycles.push([...trail, startPath]);
        continue;
      }
      if (visiting.has(edge.assetPath)) continue;
      visiting.add(edge.assetPath);
      trail.push(edge.assetPath);
      await dfs(edge.assetPath);
      trail.pop();
      visiting.delete(edge.assetPath);
    }
  }

  for (const edge of startEdges) {
    if (!edge.resolved || edge.assetPath === "" || edge.assetPath === startPath) {
      continue;
    }
    visiting.clear();
    visiting.add(edge.assetPath);
    trail.length = 0;
    trail.push(startPath, edge.assetPath);
    await dfs(edge.assetPath);
  }

  return cycles;
}

// ---------------------------------------------------------------------------
// Reverse-edge graph (for impact BFS)
// ---------------------------------------------------------------------------

type ReverseGraphKey = string;

function graphKeyForUid(uid: string): ReverseGraphKey {
  return `uid:${uid}`;
}

function graphKeyForPath(path: string): ReverseGraphKey {
  return `path:${path}`;
}

async function buildReverseEdgeGraph(
  projectRoot: string,
  index: UidPathIndex,
): Promise<{
  graph: Map<ReverseGraphKey, ReverseEdge[]>;
  pathToUid: Map<string, string>;
}> {
  const graph = new Map<ReverseGraphKey, ReverseEdge[]>();
  const pathToUid = new Map<string, string>(index.pathToUid);

  const candidates = await collectProjectFiles(
    projectRoot,
    REFERENCE_SCAN_EXTENSIONS,
  );

  for (const assetPath of candidates) {
    const native = resolveResPath(assetPath, projectRoot);
    if (native.kind !== "ok") continue;

    let content: string;
    try {
      content = await readFile(native.nativePath, "utf-8");
    } catch {
      continue;
    }

    const ownUid = index.pathToUid.get(assetPath) ?? "";
    const kind = kindForPath(assetPath);
    const edge: ReverseEdge = { assetPath, uid: ownUid, kind };

    const referencedKeys = new Set<ReverseGraphKey>();
    const lines = content.split(/\r?\n/);
    for (const line of lines) {
      if (!line.startsWith("[ext_resource")) continue;
      const path = extractHeaderAttr(line, "path");
      const uidRaw = extractHeaderAttr(line, "uid");
      if (path !== null && path !== "") {
        referencedKeys.add(graphKeyForPath(path));
      }
      if (uidRaw !== null) {
        const uid = extractUidToken(uidRaw);
        if (uid !== null) referencedKeys.add(graphKeyForUid(uid));
      }
    }

    for (const key of referencedKeys) {
      let list = graph.get(key);
      if (!list) {
        list = [];
        graph.set(key, list);
      }
      list.push(edge);
    }
  }

  return { graph, pathToUid };
}

function findReferencesFromGraph(
  graph: Map<ReverseGraphKey, ReverseEdge[]>,
  targetUid: string,
  targetPath: string,
): ReverseEdge[] {
  const seen = new Set<string>();
  const out: ReverseEdge[] = [];

  const keys: ReverseGraphKey[] = [];
  if (targetUid !== "") keys.push(graphKeyForUid(targetUid));
  if (targetPath !== "") keys.push(graphKeyForPath(targetPath));

  for (const key of keys) {
    const list = graph.get(key);
    if (!list) continue;
    for (const edge of list) {
      if (edge.assetPath === targetPath) continue;
      if (seen.has(edge.assetPath)) continue;
      seen.add(edge.assetPath);
      out.push(edge);
    }
  }

  out.sort((a, b) =>
    a.assetPath < b.assetPath ? -1 : a.assetPath > b.assetPath ? 1 : 0,
  );
  return out;
}

function computeTransitiveImpactFromGraph(
  startUid: string,
  startPath: string,
  graph: Map<ReverseGraphKey, ReverseEdge[]>,
  pathToUid: Map<string, string>,
  maxDepth: number,
): NonNullable<DependenciesOfflineResult["impact"]> {
  const affected: ImpactEntry[] = [];
  const seen = new Set<string>();
  if (startPath !== "") seen.add(startPath);

  let frontier: string[] = [];
  let truncated = false;

  const seed = findReferencesFromGraph(graph, startUid, startPath);
  for (const e of seed) {
    if (seen.has(e.assetPath)) continue;
    seen.add(e.assetPath);
    frontier.push(e.assetPath);
    affected.push({ assetPath: e.assetPath, depth: 1 });
  }

  for (let depth = 2; depth <= maxDepth; depth++) {
    if (frontier.length === 0) break;
    const next: string[] = [];
    for (const nodePath of frontier) {
      const nodeUid = pathToUid.get(nodePath) ?? "";
      const refs = findReferencesFromGraph(graph, nodeUid, nodePath);
      for (const e of refs) {
        if (seen.has(e.assetPath)) continue;
        seen.add(e.assetPath);
        next.push(e.assetPath);
        affected.push({ assetPath: e.assetPath, depth });
      }
    }
    frontier = next;
    if (depth === maxDepth && frontier.length > 0) {
      truncated = true;
    }
  }

  return {
    affected,
    affectedCount: affected.length,
    maxDepth,
    truncated,
  };
}

function kindForPath(resPath: string): string {
  const ext = extname(resPath).toLowerCase();
  switch (ext) {
    case ".tscn":
    case ".scn":
      return "scene";
    case ".tres":
    case ".res":
      return "resource";
    default:
      return "other";
  }
}
