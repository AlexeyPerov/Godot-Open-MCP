// `godot_open_mcp_scene_get_data` tool definition (P2.7; offline fallback in P7.2).
//
// Read-only. Returns a snapshot of the edited scene's hierarchy as a NodeData
// tree (the same DTO node_find returns), driven by a `hierarchy_depth` bound. The
// live handler lives in the bridge (POST /tools/godot_open_mcp_scene_get_data); this
// file is the catalog metadata only — name / description / input schema — advertised to
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
//   - `path` is optional LIVE: when omitted, reads the edited scene; when set and
//     NOT the edited scene, the live handler refuses with scene_not_edited.
//     OFFLINE (P7.2) `path` is REQUIRED — no edited-scene context exists when the
//     bridge is down, so the offline parser must be told which res://...tscn to
//     read; omitting it offline returns path_required_offline.
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
    "Read the hierarchy of a Godot scene as a structured NodeData tree (read-only). Live-first: when " +
    "the Godot editor is running, reads the currently edited scene and reflects unsaved editor state. " +
    "When the editor is unavailable, falls back to parsing the .tscn file from disk (Godot closed or " +
    "the bridge down) — in that case `path` is REQUIRED and the result carries `stateSource: \"disk\"`, " +
    "`isDirty: false`, and null instance IDs (offline reads cannot expose live instance IDs or unsaved " +
    "state). Returns the scene's path, name, isDirty, rootType, hierarchyDepth, and a `root` NodeData. " +
    "`hierarchy_depth`: 0 = root node only; 1 (default) = root + direct children; N = N layers; -1 = " +
    "the whole tree (positive values capped at 5). Each NodeData carries instanceId (null offline), " +
    "name, path, type, scriptResourcePath, and childCount (plus `children` when depth > 0). When live, " +
    "`path` optionally asserts the edited scene matches (refuses with scene_not_edited otherwise). When " +
    "offline, `path` selects which .tscn to read from disk.",
  inputSchema: {
    type: "object",
    properties: {
      path: {
        type: "string",
        description:
          "res:// path of the scene to read. LIVE (bridge online, optional): when omitted reads the " +
          "currently edited scene; when set, the handler verifies it matches the edited scene and " +
          "refuses with scene_not_edited otherwise (call scene_open to switch). OFFLINE (bridge " +
          "unavailable, REQUIRED): no edited-scene context exists, so a res://...tscn path must be " +
          "supplied; omitting it returns path_required_offline. The path is resolved safely beneath the " +
          "project root — traversal and symlink escapes are rejected.",
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
