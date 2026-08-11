// `godot_open_mcp_control_modify` tool definition (P16.5).
//
// Bulk-patches one or many allow-listed control scalars on an existing Control
// node in the currently edited scene. The handler lives in the bridge
// (POST /tools/godot_open_mcp_control_modify); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Adapted from Unity Open MCP's `ui_element_modify`
// (TypedTools/Extensions/UI/UITools.cs — adapt fidelity): same bulk-patch
// {field → value} shape, but Godot's anchor / offset / size_flags layout model
// replaces Unity's RectTransform (anchorMin / anchorMax / sizeDelta / pivot).
//
// This is a `ui` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "ui" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const controlModify: Tool = {
  name: "godot_open_mcp_control_modify",
  description:
    "Bulk-patch allow-listed control scalars on an existing Control node in the edited scene. Pass a `fields` " +
    "object of {field: value} entries; each entry is applied through the same allow-listed + clamped path. " +
    "Allow-listed fields: 'text' (Button/Label/LineEdit/TextEdit/RichTextLabel/CheckBox/CheckButton), " +
    "'tooltip_text' (every Control), 'disabled' (BaseButton subclasses only), 'color' (modulate, 'r,g,b[,a]'), " +
    "'custom_minimum_size' ('x,y', clamped non-negative), 'offset_left' / 'offset_right' / 'offset_top' / " +
    "'offset_bottom' (float), 'size_flags_horizontal' / 'size_flags_vertical' (int bitmask or " +
    "'fill'/'expand'/'shrink_center'/'shrink_end', combinable with '+' or '|'), 'anchors_preset' (preset name " +
    "— sets anchors AND offsets together), 'value' (ProgressBar / Slider / SpinBox only). A single bad entry " +
    "does not abort the batch — unrecognized fields surface in `errors`; per-field type failures surface there " +
    "too. The scene is marked unsaved when at least one field lands.\n\n" +
    "Mutating — runs the full gate cycle (checkpoint → save → validate → delta) by default. This is a " +
    "`ui` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["node_path", "fields", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the Control node to mutate (relative to the edited scene root). Must be a " +
          "Control subclass; a non-Control node surfaces wrong_node_type.",
      },
      fields: {
        type: "object",
        additionalProperties: true,
        description:
          "Free-form {field: value} map. Each key is an allow-listed control field name (see description); " +
          "each value is the verbatim token the handler re-parses (strings quoted, numbers/bools bare, " +
          "'x,y' / 'r,g,b[,a]' for vectors + colors). Unknown fields surface in `errors` as unsupported_field.",
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
