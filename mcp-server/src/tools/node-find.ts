// `godot_open_mcp_node_find` tool definition (P2.2).
//
// The first real read-only editor tool. Locates Godot Nodes in the currently
// edited scene so an agent can inspect scene state before mutating it. The
// handler lives in the bridge (POST /tools/godot_open_mcp_node_find); this file
// is the catalog metadata only — name / description / input schema — advertised
// to AI clients over stdio ListTools. CallTool routes through LiveClient →
// POST, same as the ping tool.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/gameobject-find.ts
// (adapt fidelity): same two-mode shape (targeted vs list), same notFound /
// truncated / max_results contract. The filter set is swapped for Godot:
//   - `node_path` replaces Unity's `instance_id`/`path` (Godot scenes are
//     single-rooted under the edited scene root; the Unity cross-scene
//     instance_id resolver is deferred — see the bridge handler doc).
//   - `name` remains (first-match targeted fallback).
//   - `type` (Godot class name) replaces Unity's `component` filter.
//   - `name_contains` is unchanged.
//   - Unity's `tag` / `root_only` are dropped — Godot has no Unity-style tags
//     on nodes, and `root_only` is expressed as hierarchy_depth: 0 on list mode.
//   - `hierarchy_depth` (Godot-MCP NodeData pattern) is added so a find can
//     return a subtree in one call.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const nodeFind: Tool = {
  name: "godot_open_mcp_node_find",
  description:
    "Find Nodes in the currently edited Godot scene. Read-only (gate-free). Two modes: " +
    "(a) targeted lookup by node_path or name — returns a single-node result (empty list when " +
    "not found, with notFound=true); (b) list mode (omit both) — walks the edited scene with " +
    "optional type / name_contains filters, bounded by max_results. Each result is a NodeData " +
    "payload (instanceId, name, path, type, scriptResourcePath, childCount, optional children) " +
    "the agent can chain into later mutating tools. Prefer node_path over name — name matches " +
    "the first hit only and can be ambiguous in a large scene. Path forms accepted: " +
    "'Main/Player', '/root/Main/Player', or '.' for the edited scene root.",
  inputSchema: {
    type: "object",
    properties: {
      node_path: {
        type: "string",
        description:
          "Targeted mode (priority 1): scene-tree path relative to the edited scene root. " +
          "Accepts 'Main/Player', '/root/Main/Player', or '.' for the root itself.",
      },
      name: {
        type: "string",
        description:
          "Targeted mode (priority 2): node name. First match in depth-first order; prefer " +
          "node_path when the scene may contain duplicate names.",
      },
      type: {
        type: "string",
        description:
          "List mode: filter by Godot class name (e.g. 'Node3D', 'Sprite2D'). Case-sensitive " +
          "exact match against each node's GetClass().",
      },
      name_contains: {
        type: "string",
        description: "List mode: case-insensitive substring filter on node name.",
      },
      hierarchy_depth: {
        type: "integer",
        default: 0,
        minimum: 0,
        description:
          "Depth of children to include in each NodeData. 0 = the matched node only (children " +
          "null); 1 = direct children; 2 = grandchildren; etc. Applies in both targeted and " +
          "list modes.",
      },
      max_results: {
        type: "integer",
        default: 50,
        minimum: 1,
        description:
          "List mode: max nodes returned. The remainder count is reported in 'truncated' so an " +
          "agent knows whether to page.",
      },
    },
    additionalProperties: false,
  },
};
