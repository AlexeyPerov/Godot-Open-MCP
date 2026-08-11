// Offline generic asset reader (P17.1).
//
// Produces a token-budgeted structured summary for any `.tres`/`.tscn`/
// `.gdshader`/`.import` (or any small text asset) straight from disk. The
// summary shape is chosen per asset kind:
//   - `.tscn` — scene header (format/load_steps/uid) + ext_resources + node
//     roster (root names + types) + integrity signals.
//   - `.tres` — resource header (type/format/uid) + ext_resources +
//     sub_resources + top-level property names/values + integrity signals.
//   - `.gdshader` — shader_type + render_mode + uniform declarations
//     (name/type/hint).
//   - `.import` — remap (source/dest_files/uid/importer/type) + integrity
//     (missing source).
//   - other text — size/lineCount + a bounded first-line peek.
//
// Re-parses per request — no cache (MCP-server offline-read philosophy).
//
// Adapted from Unity Open MCP's `readAssetOffline` in
// mcp-server/src/offline/api.ts (adapt fidelity): same token-budgeted summary
// intent and profile (compact/balanced/full) + page_size/cursor contract. Godot
// deltas:
//   - Identity is `res://` path + `uid://` handle (no Unity GUID/meta files).
//   - Asset kinds are Godot's text formats; the parser is INI/line-oriented,
//     not YAML.
//   - Integrity signals are Godot-specific (`missing_reference` on an
//     `[ext_resource]` whose path does not exist on disk; `orphaned_import` on
//     a `.import` whose `source=` is gone).

import { readFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { extname } from "node:path";

import {
  applyPaging,
  attachPagination,
  type DetailLevel,
  type PaginationBlock,
} from "../output-profile.js";
import {
  buildUidPathIndex,
  extractUidToken,
  type UidPathIndex,
} from "./project-index.js";
import { resolveResPath } from "./project-paths.js";

// ---------------------------------------------------------------------------
// Public types
// ---------------------------------------------------------------------------

/** Coarse asset kind derived from the file extension. */
export type AssetKind =
  | "scene"
  | "resource"
  | "shader"
  | "import"
  | "script"
  | "text";

/** Integrity signal surfaced by the read. */
export interface IntegritySignal {
  /** Stable code: `missing_reference` | `orphaned_import` | `parse_failure`. */
  code: string;
  /** Human-readable detail (which path / line). */
  detail: string;
}

/** One `[ext_resource]` declaration on a `.tscn`/`.tres`. */
export interface AssetExtResource {
  id: string;
  type: string;
  path: string;
  uid: string;
  /** True when `path` resolves to a file that does not exist on disk. */
  missing: boolean;
}

/** One `[sub_resource]` declaration on a `.tscn`/`.tres`. */
export interface AssetSubResource {
  id: string;
  type: string;
}

/** One node summary on a `.tscn` (compact: name + type). */
export interface AssetNodeSummary {
  name: string;
  type: string;
  /** Node path (parent chain), or "" for a scene root. */
  path: string;
  /** Attached script path when declared, or "". */
  script: string;
}

/** One shader uniform declaration on a `.gdshader`. */
export interface AssetUniform {
  name: string;
  type: string;
  /** Hint text after the `:` verbatim (e.g. `hint_range(0,1)`), or "". */
  hint: string;
}

/** One top-level property on a `.tres` `[resource]` body. */
export interface AssetProperty {
  name: string;
  /** Value text verbatim (bounded), or "" when only the name is captured. */
  value: string;
}

/** `.import` remap block. */
export interface AssetImportRemap {
  source: string;
  dest_files: string[];
  uid: string;
  importer: string;
  type: string;
  /** True when the `source=` path does not exist on disk. */
  sourceMissing: boolean;
}

export interface ReadAssetOfflineResult {
  assetPath: string;
  uid: string;
  kind: AssetKind;
  /** Best-effort Godot resource/script type (e.g. `StandardMaterial3D`,
   *  `GDScript`, `Shader`), or "" when unknown. */
  resourceType: string;
  size: number;
  lineCount: number;
  profile: DetailLevel;
  // Header fields populated per kind (see builders below).
  format: number | null;
  loadSteps: number | null;
  /** `.tres` `[gd_resource type=]`; `.tscn` is always `PackedScene`. */
  headerType: string;
  // Counts (always present so compact profile has signal).
  extResourceCount: number;
  subResourceCount: number;
  nodeCount: number;
  uniformCount: number;
  propertyCount: number;
  // Expandable lists (balanced/full). Empty under compact unless noted.
  extResources: AssetExtResource[];
  subResources: AssetSubResource[];
  nodes: AssetNodeSummary[];
  uniforms: AssetUniform[];
  properties: AssetProperty[];
  importRemap: AssetImportRemap | null;
  integrity: IntegritySignal[];
  truncated: number;
  pagination?: PaginationBlock;
}

export interface ReadAssetOfflineOpts {
  assetPath: string;
  detail?: DetailLevel;
  pageSize?: number;
  cursor?: string;
  /** Single-page cap when page_size is omitted. `<= 0` ⇒ unlimited. */
  maxResults?: number;
  /** Full profile: cap on nodes/properties/uniforms before paging takes over. */
  maxPerSection?: number;
  projectRoot: string;
}

const TOOL_KEY = "read_asset";

// Per-section caps under balanced (no paging): keep the response bounded
// without forcing the caller to page. Full raises these (paging still wins).
const BALANCED_NODE_CAP = 40;
const BALANCED_PROPERTY_CAP = 40;
const BALANCED_UNIFORM_CAP = 40;
const FULL_NODE_CAP = 200;
const FULL_PROPERTY_CAP = 200;
const FULL_UNIFORM_CAP = 200;

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

/**
 * Read one asset from disk and return a token-budgeted structured summary.
 * Never throws — unreadable/missing assets surface as a structured result with
 * a `parse_failure` integrity signal and zeroed counts so the caller can branch.
 */
export async function readAssetOffline(
  opts: ReadAssetOfflineOpts,
): Promise<ReadAssetOfflineResult> {
  const detail: DetailLevel = opts.detail ?? "summary";
  const maxPerSection = opts.maxPerSection ?? 0;

  const native = resolveResPath(opts.assetPath, opts.projectRoot);
  if (native.kind !== "ok") {
    return missingResult(opts.assetPath, detail, native.message);
  }

  let content: string;
  try {
    content = await readFile(native.nativePath, "utf-8");
  } catch (e) {
    return missingResult(
      opts.assetPath,
      detail,
      e instanceof Error ? e.message : "unreadable file",
    );
  }

  const index = await buildUidPathIndex(opts.projectRoot);
  const ext = extname(opts.assetPath).toLowerCase();
  const kind = kindForExt(ext);

  switch (kind) {
    case "scene":
      return buildSceneResult(opts, content, detail, index, ext);
    case "resource":
      return buildResourceResult(opts, content, detail, index, ext);
    case "shader":
      return buildShaderResult(opts, content, detail);
    case "import":
      return buildImportResult(opts, content, detail);
    default:
      return buildTextResult(opts, content, detail);
  }
}

// ---------------------------------------------------------------------------
// `.tscn` — scene summary (reuses the line-token grammar; nodes roster is the
// expandable list). Intentionally light: `scene-parser.parseTscn` is the
// authoritative tree builder used by `scene_get_data`; read_asset only needs
// identity fields, so it re-implements a bounded scan to avoid coupling the
// summary shape to the parser's internal model.
// ---------------------------------------------------------------------------

function buildSceneResult(
  opts: ReadAssetOfflineOpts,
  content: string,
  detail: DetailLevel,
  index: UidPathIndex,
  ext: string,
): ReadAssetOfflineResult {
  const lines = content.split(/\r?\n/);
  const header = parseGdHeader(lines, "gd_scene");
  const extResources = collectExtResources(lines, opts.projectRoot);
  const subResources = collectSubResources(lines);
  const nodeHeaders = collectNodeHeaders(lines);

  const integrity: IntegritySignal[] = [];
  for (const er of extResources) {
    if (er.missing) {
      integrity.push({
        code: "missing_reference",
        detail: `ext_resource id=${er.id} path=${er.path} not found on disk`,
      });
    }
  }

  const base = assembleBase({
    assetPath: opts.assetPath,
    uid: header.uid || index.pathToUid.get(opts.assetPath) || "",
    kind: "scene",
    resourceType: "PackedScene",
    content,
    detail,
    header,
    extResources,
    subResources,
    integrity,
  });

  // Nodes are the expandable list for scenes.
  const allNodes: AssetNodeSummary[] = nodeHeaders.map((n) => ({
    name: n.name,
    type: n.type,
    path: nodePath(n.name, n.parent),
    script: n.script,
  }));
  base.nodeCount = allNodes.length;
  fillSection(base, "nodes", allNodes, detail, opts);

  void ext;
  return finalizePaging(base, allNodes.length, detail, opts);
}

// ---------------------------------------------------------------------------
// `.tres` — resource summary.
// ---------------------------------------------------------------------------

function buildResourceResult(
  opts: ReadAssetOfflineOpts,
  content: string,
  detail: DetailLevel,
  index: UidPathIndex,
  ext: string,
): ReadAssetOfflineResult {
  const lines = content.split(/\r?\n/);
  const header = parseGdHeader(lines, "gd_resource");
  const extResources = collectExtResources(lines, opts.projectRoot);
  const subResources = collectSubResources(lines);
  const properties = collectResourceProperties(lines);

  const integrity: IntegritySignal[] = [];
  for (const er of extResources) {
    if (er.missing) {
      integrity.push({
        code: "missing_reference",
        detail: `ext_resource id=${er.id} path=${er.path} not found on disk`,
      });
    }
  }

  const base = assembleBase({
    assetPath: opts.assetPath,
    uid: header.uid || index.pathToUid.get(opts.assetPath) || "",
    kind: "resource",
    resourceType: header.type || "Resource",
    content,
    detail,
    header,
    extResources,
    subResources,
    integrity,
  });

  // Properties are the expandable list for resources.
  const allProps: AssetProperty[] = properties;
  base.propertyCount = allProps.length;
  fillSection(base, "properties", allProps, detail, opts);

  void ext;
  return finalizePaging(base, allProps.length, detail, opts);
}

// ---------------------------------------------------------------------------
// `.gdshader` — uniform roster.
// ---------------------------------------------------------------------------

const UNIFORM_RE =
  /^\s*uniform\s+([A-Za-z_][A-Za-z0-9_]*)\s+([A-Za-z_][A-Za-z0-9_]*)(?:\s*:\s*([^=;]+?))?\s*(?:=.*)?;?\s*$/;

function buildShaderResult(
  opts: ReadAssetOfflineOpts,
  content: string,
  detail: DetailLevel,
): ReadAssetOfflineResult {
  const lines = content.split(/\r?\n/);
  let shaderType = "";
  const renderModes: string[] = [];
  const uniforms: AssetUniform[] = [];
  const integrity: IntegritySignal[] = [];

  for (const line of lines) {
    const trimmed = line.trim();
    if (shaderType === "") {
      const m = /^shader_type\s+([A-Za-z_][A-Za-z0-9_]*);/.exec(trimmed);
      if (m) shaderType = m[1]!;
      continue;
    }
    if (trimmed.startsWith("render_mode")) {
      const body = trimmed.slice("render_mode".length).replace(/[;\s]+$/g, "");
      for (const part of body.split(",")) {
        const t = part.trim();
        if (t !== "") renderModes.push(t);
      }
      continue;
    }
    const u = UNIFORM_RE.exec(line);
    if (u) {
      // Group 1 = type (`vec4`/`float`/…), group 2 = name, group 3 = hint.
      uniforms.push({
        name: u[2]!,
        type: u[1]!,
        hint: (u[3] ?? "").trim(),
      });
    }
  }

  if (shaderType === "") {
    integrity.push({
      code: "parse_failure",
      detail: "no shader_type declaration found",
    });
  }

  const base = assembleBase({
    assetPath: opts.assetPath,
    uid: "",
    kind: "shader",
    resourceType: "Shader",
    content,
    detail,
    header: { format: null, loadSteps: null, uid: "", type: shaderType },
    extResources: [],
    subResources: [],
    integrity,
  });

  // Stash shader_type + render_mode in the property list (name/value form) so
  // compact carries the headline without a dedicated field, and uniforms are
  // the expandable list.
  base.properties = [{ name: "shader_type", value: shaderType }];
  if (renderModes.length > 0) {
    base.properties.push({ name: "render_mode", value: renderModes.join(", ") });
  }
  base.propertyCount = base.properties.length;

  base.uniformCount = uniforms.length;
  fillSection(base, "uniforms", uniforms, detail, opts);

  return finalizePaging(base, uniforms.length, detail, opts);
}

// ---------------------------------------------------------------------------
// `.import` — remap block.
// ---------------------------------------------------------------------------

function buildImportResult(
  opts: ReadAssetOfflineOpts,
  content: string,
  detail: DetailLevel,
): ReadAssetOfflineResult {
  const remap = parseImportRemap(content, opts.projectRoot);
  const integrity: IntegritySignal[] = [];
  if (remap.sourceMissing) {
    integrity.push({
      code: "orphaned_import",
      detail: `import source=${remap.source} not found on disk`,
    });
  }

  const base = assembleBase({
    assetPath: opts.assetPath,
    uid: remap.uid,
    kind: "import",
    resourceType: remap.type,
    content,
    detail,
    header: { format: null, loadSteps: null, uid: remap.uid, type: remap.type },
    extResources: [],
    subResources: [],
    integrity,
  });

  base.importRemap = remap;
  // No expandable list — the remap block is small and always carried.
  base.properties = [
    { name: "source", value: remap.source },
    { name: "importer", value: remap.importer },
    {
      name: "dest_files",
      value: remap.dest_files.join(", "),
    },
  ];
  base.propertyCount = base.properties.length;

  return finalizePaging(base, 0, detail, opts);
}

// ---------------------------------------------------------------------------
// Fallback: arbitrary small text.
// ---------------------------------------------------------------------------

function buildTextResult(
  opts: ReadAssetOfflineOpts,
  content: string,
  detail: DetailLevel,
): ReadAssetOfflineResult {
  const lines = content.split(/\r?\n/);
  const first = lines[0] ?? "";
  const base = assembleBase({
    assetPath: opts.assetPath,
    uid: "",
    kind: "text",
    resourceType: "",
    content,
    detail,
    header: { format: null, loadSteps: null, uid: "", type: "" },
    extResources: [],
    subResources: [],
    integrity: [],
  });
  if (detail !== "summary") {
    base.properties = [{ name: "first_line", value: first.slice(0, 120) }];
    base.propertyCount = base.properties.length;
  }
  return finalizePaging(base, 0, detail, opts);
}

// ---------------------------------------------------------------------------
// Shared assembly + paging helpers
// ---------------------------------------------------------------------------

interface ParsedHeader {
  format: number | null;
  loadSteps: number | null;
  uid: string;
  type: string;
}

interface AssembleInput {
  assetPath: string;
  uid: string;
  kind: AssetKind;
  resourceType: string;
  content: string;
  detail: DetailLevel;
  header: ParsedHeader;
  extResources: AssetExtResource[];
  subResources: AssetSubResource[];
  integrity: IntegritySignal[];
}

function assembleBase(input: AssembleInput): ReadAssetOfflineResult {
  const lines = input.content.split(/\r?\n/);
  return {
    assetPath: input.assetPath,
    uid: input.uid,
    kind: input.kind,
    resourceType: input.resourceType,
    size: Buffer.byteLength(input.content, "utf-8"),
    lineCount: lines.length,
    profile: input.detail,
    format: input.header.format,
    loadSteps: input.header.loadSteps,
    headerType: input.header.type,
    extResourceCount: input.extResources.length,
    subResourceCount: input.subResources.length,
    nodeCount: 0,
    uniformCount: 0,
    propertyCount: 0,
    // compact: counts + integrity + headline only. balanced/full expand below.
    extResources: input.detail === "summary" ? [] : input.extResources,
    subResources: input.detail === "summary" ? [] : input.subResources,
    nodes: [],
    uniforms: [],
    properties: [],
    importRemap: null,
    integrity: input.integrity,
    truncated: 0,
  };
}

/**
 * Fill one expandable section list under the chosen detail profile, honoring
 * the per-section cap. Paging (when `opts.pageSize` is set) is applied in
 * {@link finalizePaging} instead — this helper only enforces the inline cap.
 */
function fillSection(
  base: ReadAssetOfflineResult,
  section: "nodes" | "properties" | "uniforms",
  all: AssetNodeSummary[] | AssetProperty[] | AssetUniform[],
  detail: DetailLevel,
  opts: ReadAssetOfflineOpts,
): void {
  if (detail === "summary") return;
  const defaultCap =
    detail === "verbose"
      ? section === "nodes"
        ? FULL_NODE_CAP
        : section === "properties"
          ? FULL_PROPERTY_CAP
          : FULL_UNIFORM_CAP
      : section === "nodes"
        ? BALANCED_NODE_CAP
        : section === "properties"
          ? BALANCED_PROPERTY_CAP
          : BALANCED_UNIFORM_CAP;
  const cap = opts.maxPerSection && opts.maxPerSection > 0
    ? opts.maxPerSection
    : defaultCap;
  const slice = all.slice(0, cap);
  if (section === "nodes") base.nodes = slice as AssetNodeSummary[];
  else if (section === "properties") base.properties = slice as AssetProperty[];
  else base.uniforms = slice as AssetUniform[];
}

/**
 * Apply paging over the section list chosen per kind. When `page_size` is set
 * the full (un-capped) list is paged; otherwise the inline cap from
 * {@link fillSection} stands and `truncated` reports the remainder.
 */
function finalizePaging(
  base: ReadAssetOfflineResult,
  totalSection: number,
  detail: DetailLevel,
  opts: ReadAssetOfflineOpts,
): ReadAssetOfflineResult {
  if (detail === "summary") {
    base.truncated = 0;
    return base;
  }

  const pageSize =
    typeof opts.pageSize === "number" && opts.pageSize > 0
      ? Math.floor(opts.pageSize)
      : 0;

  if (pageSize > 0) {
    // Page over the kind-specific expandable list. Dispatch on kind so the
    // element type is concrete (the union would defeat applyPaging's generic).
    if (base.kind === "scene") {
      const { page, block } = applyPaging(base.nodes, TOOL_KEY, {
        page_size: pageSize,
        cursor: opts.cursor,
      });
      base.nodes = page;
      base.truncated = block.truncated;
      return attachPagination(base, block);
    }
    if (base.kind === "shader") {
      const { page, block } = applyPaging(base.uniforms, TOOL_KEY, {
        page_size: pageSize,
        cursor: opts.cursor,
      });
      base.uniforms = page;
      base.truncated = block.truncated;
      return attachPagination(base, block);
    }
    // resource / import / text page over properties.
    const { page, block } = applyPaging(base.properties, TOOL_KEY, {
      page_size: pageSize,
      cursor: opts.cursor,
    });
    base.properties = page;
    base.truncated = block.truncated;
    return attachPagination(base, block);
  }

  // No paging: report how many of the kind's list were omitted by the cap.
  const carried = sectionCarriedCount(base);
  base.truncated = Math.max(0, totalSection - carried);
  return base;
}

function sectionCarriedCount(base: ReadAssetOfflineResult): number {
  if (base.kind === "scene") return base.nodes.length;
  if (base.kind === "shader") return base.uniforms.length;
  return base.properties.length;
}

function missingResult(
  assetPath: string,
  detail: DetailLevel,
  message: string,
): ReadAssetOfflineResult {
  return {
    assetPath,
    uid: "",
    kind: "text",
    resourceType: "",
    size: 0,
    lineCount: 0,
    profile: detail,
    format: null,
    loadSteps: null,
    headerType: "",
    extResourceCount: 0,
    subResourceCount: 0,
    nodeCount: 0,
    uniformCount: 0,
    propertyCount: 0,
    extResources: [],
    subResources: [],
    nodes: [],
    uniforms: [],
    properties: [],
    importRemap: null,
    integrity: [{ code: "parse_failure", detail: message }],
    truncated: 0,
  };
}

// ---------------------------------------------------------------------------
// INI-style section scanners shared by `.tscn` / `.tres`
// ---------------------------------------------------------------------------

/** Parse the `[gd_scene]` / `[gd_resource]` header line for format/load_steps/uid/type. */
function parseGdHeader(lines: string[], expected: "gd_scene" | "gd_resource"): ParsedHeader {
  const out: ParsedHeader = {
    format: null,
    loadSteps: null,
    uid: "",
    type: "",
  };
  for (const line of lines) {
    const t = line.trim();
    if (t === "" || t.startsWith(";")) continue;
    if (t.startsWith(`[${expected}`)) {
      out.format = numAttr(t, "format");
      out.loadSteps = numAttr(t, "load_steps");
      out.uid = uidAttr(t);
      out.type = strAttr(t, "type") ?? "";
      return out;
    }
    // First non-empty non-comment line that is not the expected header → bail.
    if (t.startsWith("[")) return out;
  }
  return out;
}

/** Collect every `[ext_resource ...]` declaration with existence-checked `path`. */
function collectExtResources(
  lines: string[],
  projectRoot: string,
): AssetExtResource[] {
  const out: AssetExtResource[] = [];
  for (const line of lines) {
    const t = line.trim();
    if (!t.startsWith("[ext_resource")) continue;
    const id = strAttr(t, "id") ?? "";
    const type = strAttr(t, "type") ?? "";
    const path = strAttr(t, "path") ?? "";
    const uid = uidAttr(t);
    let missing = false;
    if (path.startsWith("res://")) {
      const resolved = resolveResPath(path, projectRoot);
      missing = resolved.kind !== "ok" || !existsSync(resolved.kind === "ok" ? resolved.nativePath : "");
    }
    out.push({ id, type, path, uid, missing });
  }
  return out;
}

/** Collect every `[sub_resource ...]` declaration. */
function collectSubResources(lines: string[]): AssetSubResource[] {
  const out: AssetSubResource[] = [];
  for (const line of lines) {
    const t = line.trim();
    if (!t.startsWith("[sub_resource")) continue;
    out.push({
      id: strAttr(t, "id") ?? "",
      type: strAttr(t, "type") ?? "",
    });
  }
  return out;
}

interface NodeHeader {
  name: string;
  type: string;
  parent: string;
  script: string;
}

/** Collect every `[node ...]` header + a best-effort `script = ExtResource(...)`
 *  resolved against the file's own ext_resource declarations. */
function collectNodeHeaders(lines: string[]): NodeHeader[] {
  const out: NodeHeader[] = [];
  const extPaths = new Map<string, string>();
  for (const line of lines) {
    const t = line.trim();
    if (t.startsWith("[ext_resource")) {
      const id = strAttr(t, "id") ?? "";
      const path = strAttr(t, "path") ?? "";
      if (id !== "") extPaths.set(id, path);
    }
  }

  let current: NodeHeader | null = null;
  for (const line of lines) {
    const t = line.trim();
    if (t.startsWith("[node ")) {
      if (current) out.push(current);
      current = {
        name: strAttr(t, "name") ?? "",
        type: strAttr(t, "type") ?? "",
        parent: strAttr(t, "parent") ?? "",
        script: "",
      };
      continue;
    }
    if (t.startsWith("[")) {
      if (current) {
        out.push(current);
        current = null;
      }
      continue;
    }
    if (current && t.startsWith("script")) {
      const id = extResourceIdFromUsage(t);
      if (id !== null) current.script = extPaths.get(id) ?? "";
    }
  }
  if (current) out.push(current);
  return out;
}

function extResourceIdFromUsage(line: string): string | null {
  const m = /ExtResource\("([^"]+)"\)/.exec(line);
  return m ? m[1]! : null;
}

/** Reconstruct a node path from `name` + `parent` (Godot's parent attribute). */
function nodePath(name: string, parent: string): string {
  if (parent === "" || parent === ".") return name;
  // parent is relative to the root (e.g. "Player/Sprite"); the node's path is
  // parent + "/" + name.
  return `${parent}/${name}`;
}

/** Collect top-level `key = value` lines under the `[resource]` section of a
 *  `.tres`. Values are kept verbatim (bounded) — Godot Variant evaluation is
 *  out of scope. */
function collectResourceProperties(lines: string[]): AssetProperty[] {
  const out: AssetProperty[] = [];
  let inResource = false;
  for (const line of lines) {
    const t = line.trim();
    if (t.startsWith("[")) {
      inResource = t === "[resource]" || t.startsWith("[resource ");
      continue;
    }
    if (!inResource) continue;
    if (t === "" || t.startsWith(";")) continue;
    const eq = t.indexOf("=");
    if (eq <= 0) continue;
    const key = t.slice(0, eq).trim();
    const value = t.slice(eq + 1).trim();
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(key)) continue;
    out.push({ name: key, value: value.slice(0, 200) });
  }
  return out;
}

// ---------------------------------------------------------------------------
// `.import` parser
// ---------------------------------------------------------------------------

function parseImportRemap(content: string, projectRoot: string): AssetImportRemap {
  const remap: AssetImportRemap = {
    source: "",
    dest_files: [],
    uid: "",
    importer: "",
    type: "",
    sourceMissing: false,
  };
  let section = "";
  for (const raw of content.split(/\r?\n/)) {
    const t = raw.trim();
    if (t.startsWith("[") && t.endsWith("]")) {
      section = t.slice(1, -1).trim();
      continue;
    }
    const eq = t.indexOf("=");
    if (eq <= 0) continue;
    const key = t.slice(0, eq).trim();
    const value = t.slice(eq + 1).trim();
    if (section === "remap") {
      if (key === "source") remap.source = unquote(value);
      else if (key === "uid") {
        remap.uid = extractUidToken(unquote(value)) ?? unquote(value);
      } else if (key === "importer") remap.importer = unquote(value);
      else if (key === "type") remap.type = unquote(value);
      else if (key === "dest_files") {
        remap.dest_files = parseStringArray(value);
      }
    }
  }
  if (remap.source.startsWith("res://")) {
    const resolved = resolveResPath(remap.source, projectRoot);
    remap.sourceMissing =
      resolved.kind !== "ok" ||
      !existsSync(resolved.kind === "ok" ? resolved.nativePath : "");
  }
  return remap;
}

function parseStringArray(raw: string): string[] {
  // Godot writes `dest_files=["res://...", "res://..."]`
  const out: string[] = [];
  const re = /"([^"]+)"/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(raw)) !== null) out.push(m[1]!);
  return out;
}

function unquote(raw: string): string {
  if (raw.length >= 2 && raw[0] === '"' && raw[raw.length - 1] === '"') {
    return raw.slice(1, -1);
  }
  return raw;
}

// ---------------------------------------------------------------------------
// Attribute extractors
// ---------------------------------------------------------------------------

function strAttr(line: string, key: string): string | null {
  return extractHeaderAttr(line, key);
}

function numAttr(line: string, key: string): number | null {
  // Godot writes numeric header attributes UNQUOTED (`format=3`, `load_steps=2`),
  // unlike string attributes (`uid="…"`, `path="…"`). Try the quoted form first,
  // then fall back to the bare `key=<digits>` form.
  const quoted = extractHeaderAttr(line, key);
  const raw = quoted ?? extractBareNumAttr(line, key);
  if (raw === null) return null;
  const n = Number.parseInt(raw, 10);
  return Number.isFinite(n) ? n : null;
}

/** Extract a bare `key=<digits>` value (no quotes) from a header line. */
function extractBareNumAttr(line: string, key: string): string | null {
  const probe = " " + key + "=";
  let idx = line.indexOf(probe);
  if (idx < 0) {
    const alt = key + "=";
    idx = line.indexOf(alt);
    if (idx < 0) return null;
    if (idx > 0 && /[A-Za-z0-9_]/.test(line[idx - 1]!)) return null;
  } else {
    idx += 1; // advance past the leading space of the probe
  }
  const start = idx + key.length + 1; // skip "key="
  let end = start;
  while (end < line.length && /[0-9-]/.test(line[end]!)) end++;
  if (end === start) return null;
  return line.slice(start, end);
}

function uidAttr(line: string): string {
  const raw = extractHeaderAttr(line, "uid");
  if (raw === null) return "";
  return extractUidToken(raw) ?? raw;
}

/** Extract `key="value"` from a Godot header line. Boundary-safe so `id`
 *  does not match inside `uid`. Mirrors references.ts::extractHeaderAttr. */
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
// Kind classification
// ---------------------------------------------------------------------------

function kindForExt(ext: string): AssetKind {
  switch (ext) {
    case ".tscn":
    case ".scn":
      return "scene";
    case ".tres":
    case ".res":
      return "resource";
    case ".gdshader":
    case ".shader":
      return "shader";
    case ".import":
      return "import";
    case ".gd":
    case ".cs":
      return "script";
    default:
      return "text";
  }
}
