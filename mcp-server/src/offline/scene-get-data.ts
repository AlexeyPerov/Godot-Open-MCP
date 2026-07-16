// Public offline `scene_get_data` result builder (P7.2).
//
// The single place that normalizes a parsed `.tscn` into the live
// `scene_get_data` envelope an agent already knows:
//   { path, name, isDirty, rootType, hierarchyDepth, root }
//
// plus the offline-only deltas documented in the spec:
//   - `isDirty: false` (offline state cannot reflect unsaved edits),
//   - `stateSource: "disk"` (marks the read as disk-origin, not live),
//   - `root.instanceId: null` (no live instance IDs offline),
//   - `root.scriptResourcePath` resolved from `script = ExtResource(...)`,
//   - `warnings: []` surfaced only when non-empty.
//
// Applies the `hierarchy_depth` contract identically to the live handler:
// 0 = root only, 1 = root + direct children, N = N layers, -1 = whole tree,
// positive values capped at 5 (same token-budget cap as the live read).
// `childCount` always reports total immediate children, even when `children`
// is depth-truncated.
//
// Adapted from Unity Open MCP's offline `readAssetOffline` (adapt fidelity
// for the parse → hierarchy → normalize flow). Intentional deltas: no GUID
// index, no component set (a Godot node IS its type + optional script), no
// script-class name derivation (the attached script's `res://` path is the
// identity offline).

import { readFile } from "node:fs/promises";
import {
  probeFile,
  requireTscn,
  resolveResPath,
  SCENE_BYTE_CAP,
} from "./project-paths.js";
import { parseTscn } from "./scene-parser.js";
import { buildHierarchy, type HierarchyNode } from "./scene-hierarchy.js";
import type { ExternalResource, OfflineWarning } from "./types.js";

/** The public NodeData-shaped node, normalized to match the live bridge DTO.
 *  `instanceId` is always `null` offline — never a fake hashed id. */
export interface OfflineNodeData {
  /** Always `null` offline. Present (not omitted) so the shape matches the
   *  live DTO field-for-field. */
  instanceId: null;
  name: string;
  /** Logical scene-tree path rooted at the scene root name (`<Root>/...`).
   *  NOT a live SceneTree identity. */
  path: string;
  /** Godot class name from the `[node type=...]` header, or a documented
   *  fallback for instanced nodes whose type is unknown offline. */
  type: string;
  /** `res://` path of the attached script resolved from
   *  `script = ExtResource(...)`, or `null` when none. */
  scriptResourcePath: string | null;
  /** Total immediate children (always the real count, even when `children`
   *  is depth-truncated). */
  childCount: number;
  /** Children populated per `hierarchy_depth`. `null` when depth is 0. */
  children: OfflineNodeData[] | null;
}

/** The offline `scene_get_data` envelope. Field-for-field compatible with the
 *  live bridge result, plus the documented offline deltas. */
export interface OfflineSceneGetDataResult {
  /** The `res://` path that was read. */
  path: string;
  /** Root node name (the scene's display name). */
  name: string;
  /** Always `false` offline — disk state has no unsaved edits. */
  isDirty: boolean;
  /** Root node's Godot class name (or fallback for instanced root). */
  rootType: string;
  /** The `hierarchy_depth` actually applied (after normalization + cap). */
  hierarchyDepth: number;
  /** The root NodeData, with children populated per `hierarchy_depth`. */
  root: OfflineNodeData;
  /** Marks the read as disk-origin so a client never mistakes it for live. */
  stateSource: "disk";
  /** Non-fatal warnings, only present when non-empty. */
  warnings?: OfflineWarning[];
}

/** Structured offline failure. The router maps `code` to the matching error
 *  envelope; `message` is safe to surface (no absolute native paths). */
export interface OfflineSceneError {
  code:
    | "invalid_path"
    | "path_outside_project"
    | "scene_not_found"
    | "scene_unreadable"
    | "scene_too_large"
    | "scene_parse_error"
    | "scene_hierarchy_invalid"
    | "offline_error";
  message: string;
}

/** Fallback type for a node whose class is not knowable offline — an
 *  instanced child scene carries no `type=` header (it inherits the instance's
 *  root type, which would require loading the referenced `.tscn`). */
const INSTANCED_TYPE_FALLBACK = "PackedSceneInstance";

/** The positive depth cap, matching the live handler's token-budget cap. */
const POSITIVE_DEPTH_CAP = 5;

/**
 * Normalize a raw `hierarchy_depth` argument using the same contract as the
 * live bridge handler: -1 = whole tree; 0 = root only; positive capped at 5.
 * Invalid/missing input defaults to 1 (root + direct children), same as live.
 */
export function normalizeHierarchyDepth(raw: unknown): number {
  if (typeof raw !== "number" || !Number.isFinite(raw)) return 1;
  const n = Math.trunc(raw);
  if (n < 0) return -1;
  if (n === 0) return 0;
  return Math.min(n, POSITIVE_DEPTH_CAP);
}

/**
 * Read and parse a `.tscn` offline, returning the normalized envelope or a
 * structured error. Performs no caching and mutates nothing on disk.
 *
 * Pipeline: resolve `res://` safely → probe the file (size + regular-file
 * check) → read → parse → build hierarchy → normalize with `hierarchy_depth`.
 */
export async function readSceneGetDataOffline(
  resPath: string,
  hierarchyDepthRaw: unknown,
  projectRoot: string,
): Promise<{ ok: true; result: OfflineSceneGetDataResult } | { ok: false; error: OfflineSceneError }> {
  // 1. Extension gate — binary `.scn` and other kinds are out of scope.
  if (!requireTscn(resPath)) {
    return err("invalid_path", `path must end in '.tscn' (got '${resPath}').`);
  }

  // 2. Safe `res://` resolution + containment.
  const resolved = resolveResPath(resPath, projectRoot);
  if (resolved.kind !== "ok") {
    return err(resolved.kind, resolved.message);
  }

  // 3. Probe — existence, regular file, size cap.
  const probe = probeFile(resolved.nativePath);
  switch (probe.kind) {
    case "ok":
      break;
    case "not_found":
      return err("scene_not_found", `scene file not found at '${resPath}'.`);
    case "not_regular":
      return err("scene_unreadable", `path is not a regular file: '${resPath}'.`);
    case "too_large":
      return err(
        "scene_too_large",
        `scene file is ${probe.size} bytes, exceeding the ${probe.cap}-byte offline read cap.`,
      );
    case "unreadable":
      return err("scene_unreadable", probe.message);
  }

  // 4. Read + parse.
  let text: string;
  try {
    text = await readFile(resolved.nativePath, "utf-8");
  } catch (e) {
    return err("scene_unreadable", readErrorMessage(e));
  }
  // Defense in depth: enforce the cap on the bytes we actually read too.
  if (Buffer.byteLength(text, "utf-8") > SCENE_BYTE_CAP) {
    return err(
      "scene_too_large",
      `scene file exceeds the ${SCENE_BYTE_CAP}-byte offline read cap.`,
    );
  }

  let parsed;
  try {
    parsed = parseTscn(text);
  } catch (e) {
    const pe = e as { code?: string; message?: string; line?: number };
    return err(
      "scene_parse_error",
      pe.code === "scene_parse_error"
        ? (pe.message ?? "failed to parse scene")
        : readErrorMessage(e),
    );
  }

  // 5. Build hierarchy.
  let tree: HierarchyNode;
  try {
    tree = buildHierarchy(parsed);
  } catch (e) {
    const he = e as { code?: string; message?: string };
    return err(
      "scene_hierarchy_invalid",
      he.code === "scene_hierarchy_invalid"
        ? (he.message ?? "scene hierarchy is invalid")
        : readErrorMessage(e),
    );
  }

  // 6. Normalize to the live envelope + apply depth.
  const depth = normalizeHierarchyDepth(hierarchyDepthRaw);
  const root = toOfflineNodeData(tree, parsed.extResources, depth);

  const result: OfflineSceneGetDataResult = {
    path: resPath,
    name: tree.parsed.name,
    isDirty: false,
    rootType: root.type,
    hierarchyDepth: depth,
    root,
    stateSource: "disk",
  };
  if (parsed.warnings.length > 0) result.warnings = parsed.warnings;
  return { ok: true, result };
}

/** Build the public NodeData-shaped node from a hierarchy node, applying the
 *  depth bound. `childCount` always reflects the real immediate-child total. */
function toOfflineNodeData(
  node: HierarchyNode,
  extResources: Map<string, ExternalResource>,
  depth: number,
): OfflineNodeData {
  const childCount = node.children.length;
  const scriptResourcePath = resolveScriptPath(node, extResources);
  // Type resolution: explicit header type wins; an instanced node with no
  // type uses the documented fallback so the field is never empty/ambiguous.
  const type = node.parsed.type ?? INSTANCED_TYPE_FALLBACK;

  // depth: -1 = whole subtree; 0 = no children; N = N layers (decremented
  // as we descend). children is null at depth 0 (matches live shape).
  let children: OfflineNodeData[] | null;
  if (depth === 0) {
    children = null;
  } else {
    const childDepth = depth === -1 ? -1 : depth - 1;
    children = node.children.map((c) => toOfflineNodeData(c, extResources, childDepth));
  }

  return {
    instanceId: null,
    name: node.parsed.name,
    path: node.path,
    type,
    scriptResourcePath,
    childCount,
    children,
  };
}

/** Resolve a node's `script = ExtResource("id")` to the ext_resource's
 *  `res://` path, or `null` when the node has no script / the ref is broken. */
function resolveScriptPath(
  node: HierarchyNode,
  extResources: Map<string, ExternalResource>,
): string | null {
  const ref = node.parsed.scriptRef;
  if (ref === null) return null;
  const ext = extResources.get(ref);
  if (ext === undefined) return null;
  // Only Script-typed ext_resources are meaningful as an attached script.
  // A node could theoretically reference a non-script ext_resource id; ignore
  // it rather than surfacing an unrelated resource path.
  if (ext.type !== "" && ext.type !== "Script") return null;
  return ext.path || null;
}

function err(code: OfflineSceneError["code"], message: string): { ok: false; error: OfflineSceneError } {
  return { ok: false, error: { code, message } };
}

function readErrorMessage(e: unknown): string {
  if (e instanceof Error) return e.message;
  return String(e);
}
