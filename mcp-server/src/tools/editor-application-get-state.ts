// `godot_open_mcp_editor_application_get_state` tool definition (P4.5).
//
// Read-only (gate-free). Returns a truthful snapshot of the Godot editor's play-process state:
// isPlaying (true while a play process is running), playingScene (the res:// path of the running
// scene when available), editorVersion (Godot version string), and observedAt (ISO-8601 UTC).
// The handler lives in the bridge (POST /tools/godot_open_mcp_editor_application_get_state); this
// file is the catalog metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/editor-status.ts (adapt fidelity): Unity's
// editor_status returns isPlaying/isCompiling/isPaused/currentScene/unityVersion/editorType. The
// Godot DTO drops isPaused/isCompiling/editorType (Godot has no editor-side equivalent — it launches
// the game as a SEPARATE OS process, not an in-editor playmode toggle) and renames currentScene →
// playingScene (the scene the play process is running, not the edited scene). observedAt is added so
// callers can correlate the snapshot with their own request timing.
//
// Godot-MCP reference (Tool_Editor.GetState / Tool_Editor.cs): the EditorInterface.IsPlayingScene() /
// GetPlayingScene() / Engine.GetVersionInfo() behavior pattern is lifted from there as read-only
// behavior guidance.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const editorApplicationGetState: Tool = {
  name: "godot_open_mcp_editor_application_get_state",
  description:
    "Get a truthful snapshot of the Godot editor's play-process state. Read-only (gate-free). " +
    "Returns isPlaying (true while a play process is running), playingScene (the res:// path of the " +
    "scene the play process is running, when available), editorVersion (Godot version string), and " +
    "observedAt (ISO-8601 UTC). Godot launches the game as a separate OS process — there is no " +
    "in-editor pause or compile state, so this tool reports only what is observable: whether a play " +
    "process is running and which scene it is running. Use editor_application_set_state to start or " +
    "stop the play process.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
