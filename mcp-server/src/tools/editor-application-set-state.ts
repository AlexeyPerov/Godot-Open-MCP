// `godot_open_mcp_editor_application_set_state` tool definition (P4.5).
//
// Mutating (default gate "enforce"). Starts the main/current/custom scene or stops the play process,
// with a bounded observation window that never claims an unobserved state. The handler lives in the
// bridge (POST /tools/godot_open_mcp_editor_application_set_state); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/editor-set-state.ts (adapt fidelity): Unity's
// editor_set_state exposes play/pause/stop on an in-editor playmode toggle with a scene_dirty guard.
// The Godot port exposes start/stop only (no pause — Godot's play process is a separate OS process
// with no editor-side pause concept). The selector is Godot-specific: "main" (PlayMainScene),
// "current" (PlayCurrentScene, requires an edited scene), or an explicit res://...tscn path
// (PlayCustomScene). The bounded settle-wait discipline (observe requested-vs-observed state within a
// finite deadline, never claim an unobserved state) is lifted from Unity's EditorSettleWait.
//
// Godot-MCP reference (Tool_Editor.SetState): the EditorInterface play API surface and the
// main/current/res:// selector resolution are lifted from there as read-only behavior guidance. The
// structured error contract, bounded settle wait, gate integration, and requested-vs-observed DTO are
// greenfield for this port.
//
// Starting while already playing returns already_playing unless the requested scene is observably the
// same (idempotent). Stopping while stopped is an idempotent success. A timeout surfaces the last
// observed state so a caller can safely follow up with editor_application_get_state.
//
// paths_hint is mandatory: the explicit scene path (for a custom scene), the edited scene path (for
// "current"), or "res://project.godot" (the documented project scope for "main" and for a stop). This
// is both the gate scope and a handler-level guard that fires even when an agent overrides with
// gate:"off".

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const editorApplicationSetState: Tool = {
  name: "godot_open_mcp_editor_application_set_state",
  description:
    "Start or stop the Godot editor's play process. Mutating — runs the full gate cycle " +
    "(checkpoint → play/stop → validate → delta) by default, even though the play lifecycle writes no " +
    "files (it changes editor/project runtime state). Two actions: (a) is_playing:true starts a play " +
    "process for the selected scene; (b) is_playing:false stops any running play process. The 'scene' " +
    "selector resolves as 'main' (PlayMainScene, default), 'current' (PlayCurrentScene, requires an " +
    "edited scene), or an explicit res://...tscn/.scn path (PlayCustomScene). The transition is " +
    "observed with a bounded deadline (timeout_ms); the tool never claims a state it did not observe — " +
    "a timeout surfaces the last observed state as state_transition_timeout. Starting while already " +
    "playing returns already_playing unless the requested scene is observably the same (idempotent); " +
    "stopping while stopped is an idempotent success. paths_hint is mandatory: the explicit scene " +
    "path, the edited scene path for 'current', or 'res://project.godot' for 'main' / a stop.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      is_playing: {
        type: "boolean",
        default: false,
        description:
          "True to start a play process for the selected scene; false (default) to stop any running " +
          "play process. An explicit false is a legitimate stop request (distinct from omitting the " +
          "field, which also defaults to stop).",
      },
      scene: {
        type: "string",
        description:
          "Scene selector — only meaningful when is_playing is true. 'main' (default): run the " +
          "project's main scene via PlayMainScene. 'current': run the scene currently being edited via " +
          "PlayCurrentScene (requires a saved edited scene — a freshly-created unsaved scene yields " +
          "current_scene_unavailable). An explicit res://...tscn or res://...scn path: run that scene " +
          "via PlayCustomScene (must exist). Ignored when is_playing is false.",
      },
      timeout_ms: {
        type: "integer",
        default: 5000,
        minimum: 1000,
        maximum: 60000,
        description:
          "Bounded state-transition timeout in milliseconds. The handler observes " +
          "EditorInterface.IsPlayingScene() until it matches the requested state or the deadline " +
          "elapses. Default 5000, clamped to [1000, 60000]. A timeout yields state_transition_timeout " +
          "with the last observed state.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the explicit scene path (for a custom scene), the edited scene path (for " +
          "'current'), or 'res://project.godot' (the documented project scope for 'main' and for a " +
          "stop). Mandatory even when gate is 'off' (handler-level guard). There is no implicit " +
          "whole-project gate fallback.",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → play/stop → validate → delta; new errors " +
          "fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the cycle " +
          "(paths_hint is still required). The play lifecycle writes no files, so the verify delta is " +
          "clean in the common case — the gate still runs because the tool changes editor runtime state.",
      },
    },
    additionalProperties: false,
  },
};
