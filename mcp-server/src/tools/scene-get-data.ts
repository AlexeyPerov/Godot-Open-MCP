// `godot_open_mcp_scene_get_data` tool definition (P2.7).
//
// Read-only. Returns a live snapshot of the edited scene's hierarchy as a NodeData
// tree (the same DTO node_find returns), driven by a `hierarchy_depth` bound. The
// handler lives in the bridge (POST /tools/godot_open_mcp_scene_get_data); this file
// is the catalog metadata only — name / description / input schema — advertised to
// AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/scene-get-data.ts (adapt
// fidelity): Unity's profile/detail/depth/max_nodes/paging surface is replaced by a
// single `hierarchy_depth` axis for the P2.7 live read:
//   - Unity's `detail`/`profile` (summary/balanced/full) collapse to a depth-driven
//     tree. Heavy per-node property export is deferred to P4 (the P2.7 NodeData
//     carries name/path/type/instanceId/childCount/scriptResourcePath, same as
//     node_find).
//   - Unity's paging (page_size/cursor) is deferred — Godot scenes in P2 are read
//     whole up to the depth cap; a token-budget pager is a later phase.
//   - `path` is optional: when omitted, reads the edited scene; when set and NOT the
//     edited scene, the handler refuses with scene_not_edited (live-only in P2;
//     offline .tscn parse lands in P7.2). Switching scenes is a mutating op that
//     belongs to scene_open.
//   - `hierarchy_depth` mirrors Godot-MCP's Tool_Scene.GetData arg: 0 = root only,
//     1 (default) = root + direct children, N = N layers, -1 = whole tree (positive
//     capped at 5 to bound the token budget).
//
// The result envelope is `{ path, name, isDirty, rootType, hierarchyDepth, root }`
// where `root` is a NodeData (with `children` populated per the depth). This lets an
// agent chain straight into node_find / node_modify on a resolved child path.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const sceneGetData: Tool = {
  name: "godot_open_mcp_scene_get_data",
  description:
    "Read the hierarchy of the currently edited Godot scene as a structured NodeData tree (read-only). " +
    "Returns the scene's path, name, isDirty, rootType, and a `root` NodeData with children populated " +
    "per `hierarchy_depth`. `hierarchy_depth`: 0 = root node only (no children); 1 (default) = root + " +
    "direct children; N = N layers; -1 = the whole tree (positive values capped at 5 to bound the " +
    "response). Each NodeData carries instanceId, name, path, type, scriptResourcePath, and childCount " +
    "(plus `children` when depth > 0). Optionally pass `path` to assert the edited scene matches a " +
    "specific res:// path (the handler refuses with scene_not_edited if it does not — P2.7 is a live " +
    "read only; offline .tscn parse lands later). Use scene_list_opened to enumerate open scenes. " +
    "Prefer this over reading the .tscn file directly: it reflects unsaved editor state and is " +
    "token-budgeted by hierarchy_depth.",
  inputSchema: {
    type: "object",
    properties: {
      path: {
        type: "string",
        description:
          "Optional res:// path of the scene to read. When omitted, reads the currently edited scene. " +
          "When set, the handler verifies it matches the edited scene's path and refuses with " +
          "scene_not_edited otherwise (P2.7 is a live read; call scene_open to switch scenes first).",
      },
      hierarchy_depth: {
        type: "integer",
        default: 1,
        minimum: -1,
        description:
          "Depth of the node tree to include. 0 = root node only; 1 (default) = root + direct " +
          "children; N = N layers of children; -1 = the entire tree (unbounded walk). Positive values " +
          "are capped at 5 to bound the response token budget — deeper trees blow the size; raise the " +
          "cap by passing a larger value is NOT supported, drill in with node_find instead.",
      },
    },
    additionalProperties: false,
  },
};
