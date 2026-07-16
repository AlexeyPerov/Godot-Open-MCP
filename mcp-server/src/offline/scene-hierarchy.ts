// Parent-link tree reconstruction for the offline `.tscn` reader (P7.2).
//
// Rebuilds the Godot node tree from `[node ... parent=...]` declarations the
// parser emitted, producing a single-rooted hierarchy with canonical relative
// paths. The root is the first parentless node; `"."` parent means the root;
// any other parent value is a Godot node path relative to the root.
//
// Adapted from Unity Open MCP's `buildHierarchy` (adapt fidelity — the
// parent-link → tree → assign-path pattern is the same; Unity links via
// Transform m_Father fileIDs, Godot links via `parent` node paths).
// Intentional deltas:
//   - Single-rooted: a valid Godot scene has exactly one parentless node
//     (the scene root). Multiple roots or zero roots is a structured error.
//   - Paths are Godot scene-tree paths rooted at the scene root name
//     (`<RootName>`, `<RootName>/Child`, ...). These are LOGICAL paths derived
//     from disk text — they are NOT live SceneTree identities and carry no
//     instance IDs. The public builder marks `stateSource: "disk"`.
//   - Orphan parents, duplicate canonical paths, and cycles are detected and
//     surfaced as `scene_hierarchy_invalid` rather than dropping nodes.

import type { ParsedSceneNode, ParsedTscn } from "./types.js";

/** Structured hierarchy failure. The router maps `code` to the offline error
 *  envelope. */
export interface HierarchyError {
  code: "scene_hierarchy_invalid";
  message: string;
}

/** A reconstructed node in the tree. */
export interface HierarchyNode {
  /** Source declaration (name/type/scriptRef/order carried verbatim). */
  parsed: ParsedSceneNode;
  /** Logical scene-tree path: `<Root>` or `<Root>/Child/...`. */
  path: string;
  /** Depth from the root (root = 0). */
  depth: number;
  /** Children in declaration order. */
  children: HierarchyNode[];
}

/**
 * Build the single-rooted hierarchy from a parsed scene. Throws
 * {@link HierarchyError} for an orphan parent, a duplicate canonical path, a
 * cycle, or a missing/multiple root.
 *
 * Canonical relative key per Godot semantics:
 *   - root node (no `parent`) → key `"."`
 *   - `parent="."` → the node is a direct child of root → key = its name
 *   - `parent="Child"` → key = `Child/<name>`
 *   - `parent="Child/Grand"` → key = `Child/Grand/<name>`
 *
 * So the lookup map is keyed by the Godot node path a parent header names,
 * and each node registers itself under `<parentKey>/<name>` (or `"."` for the
 * root). A parent whose key is not in the map when a child looks it up is an
 * orphan.
 */
export function buildHierarchy(parsed: ParsedTscn): HierarchyNode {
  const byKey = new Map<string, HierarchyNode>();
  const order: HierarchyNode[] = [];

  // First pass: create nodes keyed by their canonical relative path. The root
  // (parent === null) is keyed under ".".
  let root: HierarchyNode | null = null;
  for (const pn of parsed.nodes) {
    const key = canonicalKey(pn);
    if (byKey.has(key)) {
      throw {
        code: "scene_hierarchy_invalid",
        message:
          `Duplicate node path '${displayKey(key)}' (declared again at line ${pn.line}). ` +
          "Godot scenes must have unique node paths.",
      } satisfies HierarchyError;
    }
    const node: HierarchyNode = {
      parsed: pn,
      path: "",
      depth: 0,
      children: [],
    };
    byKey.set(key, node);
    order.push(node);
    if (pn.parent === null) {
      if (root !== null) {
        throw {
          code: "scene_hierarchy_invalid",
          message:
            `Scene has multiple root nodes (parentless): '${root.parsed.name}' and '${pn.name}'. ` +
            "A Godot scene must have exactly one root.",
        } satisfies HierarchyError;
      }
      root = node;
    }
  }
  if (root === null) {
    throw {
      code: "scene_hierarchy_invalid",
      message: "Scene has no root node (no [node] without a parent attribute).",
    } satisfies HierarchyError;
  }

  // Second pass: link children to parents. Sibling order follows declaration
  // order (the `order` array is already file order; we push children in the
  // order their nodes were declared, which matches Godot's scene tree).
  for (const pn of parsed.nodes) {
    if (pn.parent === null) continue; // root
    const parentKey = parentCanonicalKey(pn.parent);
    const parent = byKey.get(parentKey);
    const childKey = canonicalKey(pn);
    const child = byKey.get(childKey)!;
    if (parent === undefined) {
      throw {
        code: "scene_hierarchy_invalid",
        message:
          `Node '${pn.name}' (line ${pn.line}) references parent '${pn.parent}' which is not declared ` +
          "earlier in the scene.",
      } satisfies HierarchyError;
    }
    // Cycle guard: a node parenting itself or an ancestor. With Godot's
    // forward-declaration convention this is structurally impossible in a
    // valid file, but refuse it explicitly rather than building a loop.
    if (parent === child) {
      throw {
        code: "scene_hierarchy_invalid",
        message: `Node '${pn.name}' (line ${pn.line}) parents itself.`,
      } satisfies HierarchyError;
    }
    parent.children.push(child);
  }

  // Assign logical paths + depths from the root name.
  const rootName = root.parsed.name || "Root";
  assignPaths(root, rootName, 0);
  return root;
}

/** Canonical relative key for a node: `"."` for the root, else its parent
 *  key + "/" + name. */
function canonicalKey(pn: ParsedSceneNode): string {
  if (pn.parent === null) return ".";
  if (pn.parent === ".") return pn.name;
  return `${pn.parent}/${pn.name}`;
}

/** The canonical key a `parent=` attribute refers to: `"."` stays `"."`,
 *  anything else is the attribute verbatim (Godot parent paths are already
 *  root-relative node paths). */
function parentCanonicalKey(parentAttr: string): string {
  return parentAttr;
}

/** Human-friendly key for error messages (hide the `"."` sentinel). */
function displayKey(key: string): string {
  return key === "." ? "<root>" : key;
}

/** Walk the tree assigning the logical path (`<Root>/...`) + depth. */
function assignPaths(
  node: HierarchyNode,
  path: string,
  depth: number,
): void {
  node.path = path;
  node.depth = depth;
  for (const child of node.children) {
    assignPaths(child, `${path}/${child.parsed.name}`, depth + 1);
  }
}
