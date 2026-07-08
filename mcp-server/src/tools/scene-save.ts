// `godot_open_mcp_scene_save` tool definition (P2.6).
//
// Saves the currently edited Godot scene (or save-as, or save-all). The handler
// lives in the bridge (POST /tools/godot_open_mcp_scene_save); this file is the
// catalog metadata only — name / description / input schema — advertised to AI
// clients over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/scene-save.ts (adapt
// fidelity): same mutating-tool shape (paths_hint + gate forward-compat fields),
// but the save surface is swapped for Godot:
//   - `path` (optional) is a save-as target (res://*.tscn), same name as Unity.
//   - `save_all` replaces Unity's `save_all` boolean (kept). Godot 4.3 has no
//     API to save a non-edited open scene directly, so save_all iterates the
//     open-scene tabs and saves each (re-opening the originally-edited scene
//     at the end).
//   - When neither is set, saves the edited scene back to its existing file
//     (fails with save_failed if the scene was never saved — pass `path` in
//     that case).
//   - Unity's `discard` (close-without-save) is dropped — P2.6 is save-only.
//   - `paths_hint` and `gate` are schema no-ops until P3.5.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const sceneSave: Tool = {
  name: "godot_open_mcp_scene_save",
  description:
    "Save the currently edited Godot scene. Three modes: (1) `save_all: true` — save every open " +
    "scene tab (not just the edited one); (2) `path` set (save-as) — save the edited scene to a new " +
    "res:// destination path; (3) neither — save the edited scene back to its existing file (fails " +
    "with save_failed if the scene was never saved; pass `path` to save it for the first time). " +
    "When `save_all` is true, `path` is ignored. Returns the saved scene path(s) in `saved` plus a " +
    "`count`. Save-as verifies the edited scene's file path was re-pointed to the target (Godot's " +
    "SaveSceneAs returns no error code). NOTE: this is a mutating tool, but the gate safety layer " +
    "is not wired yet — no checkpoint/validate/delta cycle and no editor Undo until the gate lands.",
  inputSchema: {
    type: "object",
    properties: {
      path: {
        type: "string",
        description:
          "Optional res:// destination path (ending in '.tscn' or '.scn') for a save-as. When " +
          "omitted (and save_all is false), the edited scene is saved back to its existing file. " +
          "Ignored when save_all is true.",
      },
      save_all: {
        type: "boolean",
        default: false,
        description:
          "When true, save every open scene tab. Godot 4.3 has no API to save a non-edited open " +
          "scene directly, so the handler iterates the open-scene paths, opens and saves each, " +
          "then restores the originally-edited scene as the active tab. The result's `saved` array " +
          "lists every path that was saved; any failures are listed in `failed`.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the scene res:// paths touched. Forward-compat no-op until the gate " +
          "flow (P3.5) lands.",
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
