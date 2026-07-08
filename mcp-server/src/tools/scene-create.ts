// `godot_open_mcp_scene_create` tool definition (P2.7).
//
// Creates a new Godot scene asset (.tscn) at a res:// path and opens it as the
// active scene. The handler lives in the bridge (POST
// /tools/godot_open_mcp_scene_create); this file is the catalog metadata only —
// name / description / input schema — advertised to AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/scene-create.ts (adapt
// fidelity): same mutating-tool shape (paths_hint + gate forward-compat fields), but
// the creation surface is swapped for Godot:
//   - Unity's `setup` (empty/default — DefaultGameObjects adds a camera + light) is
//     replaced by `root_type` (a Godot class like 'Node', 'Node2D', 'Node3D';
//     defaults to 'Node2D'). Godot scenes are single-rooted; the root node's class
//     is the primary structural choice.
//   - Unity's `mode` (single/additive) is dropped — Godot opens scenes in tabs;
//     `open` (default true) controls whether the new scene becomes the active tab.
//   - `root_name` is optional; defaults to a PascalCased derivation of the filename
//     stem (res://levels/level_2.tscn → Level2), matching the Godot editor's own
//     new-scene naming.
//   - `overwrite` (default false) refuses a create at an existing path; opt-in true
//     replaces it.
//   - `paths_hint` and `gate` are schema no-ops until the gate flow lands (P3.5).
//
// The result envelope is `{ created, opened, path, name, rootType, root? }` — when
// `open` is true, `root` is the new scene's root NodeData so an agent can chain
// straight into node_create to populate it.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const sceneCreate: Tool = {
  name: "godot_open_mcp_scene_create",
  description:
    "Create a new Godot scene asset at a res:// path (ending in '.tscn' or '.scn') and optionally open " +
    "it as the active scene. A root Node is created (class given by `root_type`, default 'Node2D'), " +
    "packed into a PackedScene, saved via ResourceSaver, then opened in the editor (unless " +
    "`open: false`). The root Node's name defaults to a PascalCased derivation of the filename stem " +
    "(res://levels/level_2.tscn → Level2); pass `root_name` to name it explicitly. Refuses an existing " +
    "path unless `overwrite: true`. Returns the new scene's path, name, rootType, and (when opened) a " +
    "`root` NodeData so you can chain into node_create / node_find. Use scene_save to persist further " +
    "edits. NOTE: this is a mutating tool, but the gate safety layer is not wired yet — no " +
    "checkpoint/validate/delta cycle and no editor Undo until the gate lands.",
  inputSchema: {
    type: "object",
    properties: {
      path: {
        type: "string",
        description:
          "res:// path for the new scene file, e.g. 'res://levels/level_2.tscn'. Must start with " +
          "'res://' and end with '.tscn' or '.scn'. Intermediate parent directories are created if " +
          "missing.",
      },
      root_type: {
        type: "string",
        description:
          "Godot class for the scene's root Node (e.g. 'Node', 'Node2D', 'Node3D'). Must be a " +
          "ClassDB-instantiable class. Defaults to 'Node2D' when omitted. Replaces Unity's `setup` " +
          "(empty/default) — Godot scenes are single-rooted, so the root class is the primary " +
          "structural choice.",
      },
      root_name: {
        type: "string",
        description:
          "Optional name for the root Node. When omitted, defaults to a PascalCased derivation of the " +
          "filename stem (res://levels/level_2.tscn → Level2), matching the Godot editor's own " +
          "new-scene naming.",
      },
      overwrite: {
        type: "boolean",
        default: false,
        description:
          "When true, replace a scene file that already exists at `path`. Default false: the handler " +
          "refuses with path_exists so an agent does not clobber an existing scene accidentally.",
      },
      open: {
        type: "boolean",
        default: true,
        description:
          "When true (default), open the newly-created scene as the active/edited scene. Pass false to " +
          "create the file without switching the edited scene (useful for batch scene setup).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the new scene res:// path. Forward-compat no-op until the gate flow " +
          "(P3.5) lands; the mutating handler runs without checkpoint/validate/delta until then.",
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
