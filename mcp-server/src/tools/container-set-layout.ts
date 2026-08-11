// `godot_open_mcp_container_set_layout` tool definition (P16.5).
//
// Bulk-patches allow-listed container layout properties on an existing
// Container node (VBoxContainer / HBoxContainer / GridContainer /
// MarginContainer / ScrollContainer) in the currently edited scene. The handler
// lives in the bridge (POST /tools/godot_open_mcp_container_set_layout); this
// file is the catalog metadata only — name / description / input schema —
// advertised to AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's `ui_layout_group_add` + `ui_element_modify`
// (TypedTools/Extensions/UI/UITools.cs — adapt fidelity): the bulk-patch fields
// map shape comes from ui_element_modify, but the field vocabulary maps to
// Godot's container layout model (separation / columns / margins / alignment),
// not Unity's padding / spacing / childControlWidth.
//
// This is a `ui` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "ui" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const containerSetLayout: Tool = {
  name: "godot_open_mcp_container_set_layout",
  description:
    "Bulk-patch allow-listed container layout properties on an existing Container node (VBox / HBox / Grid / " +
    "Margin / Scroll). Pass a `fields` object of {field: value} entries. Allow-listed fields: 'separation' " +
    "(int, clamped non-negative — VBox/HBox only), 'columns' (int, clamped non-negative — GridContainer only), " +
    "'alignment' (name 'begin'/'center'/'end' — VBox/HBox only), 'margin_left' / 'margin_right' / 'margin_top' / " +
    "'margin_bottom' (int, clamped non-negative — MarginContainer only), 'anchors_preset' (preset name — every " +
    "Container, inherited from Control), 'custom_minimum_size' ('x,y', clamped non-negative — every Container). " +
    "A single bad entry does not abort the batch — unrecognized fields surface in `errors`; per-field type " +
    "failures surface there too. The scene is marked unsaved when at least one field lands.\n\n" +
    "Mutating — runs the full gate cycle (checkpoint → save → validate → delta) by default. This is a " +
    "`ui` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["node_path", "fields", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the Container node to mutate (relative to the edited scene root). Must be a " +
          "Container subclass (VBoxContainer / HBoxContainer / GridContainer / MarginContainer / " +
          "ScrollContainer); a non-Container node surfaces wrong_node_type.",
      },
      fields: {
        type: "object",
        additionalProperties: true,
        description:
          "Free-form {field: value} map. Each key is an allow-listed container layout field name (see " +
          "description); each value is the verbatim token (ints bare for separation/columns/margins, preset " +
          "name string for anchors_preset, 'x,y' for custom_minimum_size). Unknown fields surface in `errors`.",
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
