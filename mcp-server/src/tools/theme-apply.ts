// `godot_open_mcp_theme_apply` tool definition (P16.5).
//
// Loads a Godot `Theme` resource (.tres) and assigns it to a Control subtree in
// the currently edited scene. The handler lives in the bridge
// (POST /tools/godot_open_mcp_theme_apply); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Greenfield for Godot — Unity uGUI has no `Theme` resource equivalent (Unity
// Open MCP's UITools does not carry a theme_apply tool). Godot's Theme resource
// model is its own: a Theme is a .tres that bundles font / color / style / icon
// overrides per Control type, and Controls inherit it down the tree until a
// child overrides it.
//
// This is a `ui` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "ui" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const themeApply: Tool = {
  name: "godot_open_mcp_theme_apply",
  description:
    "Load a Godot `Theme` resource (.tres) and assign it to a Control subtree in the edited scene. Resolves the " +
    "target Control by node_path, loads the Theme at theme_path (a res:// .tres), assigns it to the Control's " +
    "`Theme` property, and (when `recursive: true`) also assigns it to every descendant Control explicitly " +
    "(overrides any per-child theme). By default Godot's natural theme inheritance cascades the root's Theme to " +
    "descendants that do not override it, so `recursive` is opt-in. The scene is marked unsaved.\n\n" +
    "Mutating — runs the full gate cycle (checkpoint → save → validate → delta) by default. This is a " +
    "`ui` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["node_path", "theme_path", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the Control that receives the theme (relative to the edited scene root). Must be " +
          "a Control subclass; a non-Control node surfaces wrong_node_type. The Control's descendants inherit " +
          "the theme (Godot's natural cascade) unless they carry their own.",
      },
      theme_path: {
        type: "string",
        description:
          "res:// path to a `Theme` resource (.tres). Godot imports/saves themes as .tres; the handler loads it " +
          "via ResourceLoader.Load<Theme>. A non-Theme resource surfaces wrong_resource_type.",
      },
      recursive: {
        type: "boolean",
        default: false,
        description:
          "When true, assign the Theme to every descendant Control explicitly (overrides per-child themes). " +
          "Default false — Godot's natural theme inheritance cascades the root's Theme to descendants that do " +
          "not override it. Use true only when you want to force every descendant to use this Theme.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the edited scene res:// path. The gate validates only these paths after " +
          "the mutation. Mandatory even when gate is 'off' (handler-level guard).",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → save → validate → delta; new errors " +
          "fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the cycle " +
          "(paths_hint is still required).",
      },
    },
    additionalProperties: false,
  },
};
