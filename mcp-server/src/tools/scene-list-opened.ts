// `godot_open_mcp_scene_list_opened` tool definition (P2.6).
//
// Lists every scene currently open in the Godot editor as a shallow snapshot.
// The handler lives in the bridge (POST
// /tools/godot_open_mcp_scene_list_opened); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over
// stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/scene-list-opened.ts
// (adapt fidelity): read-only (gate-free, no paths_hint/gate fields). The
// summary surface is swapped for Godot:
//   - Unity returns build index + isLoaded + path; Godot has neither build index
//     nor a loaded-flag for editor scenes, so the summary carries path, name,
//     isDirty, rootType, and isActive instead.
//   - Godot 4.3 exposes the open-scene set as a flat list of res:// paths
//     (EditorInterface.GetOpenScenes) plus the single edited root
//     (GetEditedSceneRoot). The active scene carries its root Node's name and
//     type; non-active open scenes report the path + file stem only (no root
//     accessor for non-active scenes in 4.3).
//   - isDirty reflects the bridge-tracked flag (set by tool mutations, cleared
//     on save/open), NOT Godot's internal editor dirty state — Godot 4.3 has no
//     public dirty-state query API.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const sceneListOpened: Tool = {
  name: "godot_open_mcp_scene_list_opened",
  description:
    "List every scene currently open in the Godot editor as a shallow snapshot (read-only). " +
    "Returns a `scenes` array of SceneSummary entries (path, name, isDirty, rootType, isActive) " +
    "plus `editedPath` (the res:// path of the active/edited scene, or null for a never-saved " +
    "scene). The active scene carries its root Node's name and type; non-active open scenes report " +
    "the path + file stem only (Godot 4.3 exposes no root accessor for non-active open scenes). A " +
    "freshly-created unsaved scene is surfaced explicitly even when not listed by the editor. " +
    "Note: isDirty reflects bridge-tracked state from tool mutations, not edits a human made " +
    "directly in the editor (Godot 4.3 has no public dirty-state query API).",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
