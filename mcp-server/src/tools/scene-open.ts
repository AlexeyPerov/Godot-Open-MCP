// `godot_open_mcp_scene_open` tool definition (P2.6).
//
// Opens a Godot scene asset (a res://*.tscn / *.scn PackedScene) in the editor
// and makes it the active/edited scene. The handler lives in the bridge (POST
// /tools/godot_open_mcp_scene_open); this file is the catalog metadata only —
// name / description / input schema — advertised to AI clients over stdio
// ListTools. CallTool routes through LiveClient → POST, same as the node tools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/scene-open.ts (adapt
// fidelity): same mutating-tool shape (paths_hint + gate forward-compat fields),
// but the open surface is swapped for Godot:
//   - `path` replaces Unity's `path` (same name, res:// not a Unity asset path).
//   - `ignore_dirty` replaces Unity's `ignore_scene_dirty` (renamed for brevity
//     and parity with the node tools' snake_case convention). Default false:
//     the bridge refuses a dirty open with `scene_dirty` rather than popping
//     Godot's native save modal (the bridge avoids modal dialogs entirely).
//   - Unity's `mode` (Single/Additive) is dropped — Godot opens scenes in tabs;
//     no additive mode is exposed in P2.6.
//   - `paths_hint` and `gate` are schema no-ops until the gate flow lands
//     (P3.5). Present for forward-compat so an agent's mutating-tool call shape
//     does not change across phases.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const sceneOpen: Tool = {
  name: "godot_open_mcp_scene_open",
  description:
    "Open a Godot scene asset (a res://*.tscn / *.scn PackedScene) in the editor and make it the " +
    "active/edited scene. Pass `path` as the res:// path to the scene file. By default the bridge " +
    "refuses to open if the current scene has unsaved changes (scene_dirty) — pass " +
    "`ignore_dirty: true` to discard them, or call scene_save first. Returns the opened scene's " +
    "SceneSummary (path, name, isDirty, rootType, isActive) plus the previous scene's summary in " +
    "`previous` (null when no scene was edited). Use scene_list_opened to see all open scene tabs " +
    "afterwards. NOTE: this is a mutating tool, but the gate safety layer is not wired yet — no " +
    "checkpoint/validate/delta cycle and no editor Undo until the gate lands.",
  inputSchema: {
    type: "object",
    properties: {
      path: {
        type: "string",
        description:
          "res:// path of the scene file to open, e.g. 'res://levels/level_1.tscn'. Must start with " +
          "'res://' and end with '.tscn' or '.scn'.",
      },
      ignore_dirty: {
        type: "boolean",
        default: false,
        description:
          "When true, proceed even if the current edited scene has unsaved changes (discarding " +
          "them on open). Default false: the bridge refuses with scene_dirty so an agent does not " +
          "lose edits by switching scenes. Godot would otherwise pop a native save modal; the " +
          "bridge avoids modal dialogs entirely. Note: isDirty reflects bridge-tracked state from " +
          "tool mutations, not edits a human made directly in the editor (Godot 4.3 has no public " +
          "dirty-state query API).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the scene res:// paths touched. Forward-compat no-op until the gate " +
          "flow (P3.5) lands; the mutating handler runs without checkpoint/validate/delta until " +
          "then.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "off",
        description:
          "Gate mode. Forward-compat no-op until P3.5; default 'off' because the gate is not " +
          "wired yet.",
      },
    },
    additionalProperties: false,
  },
};
