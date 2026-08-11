// `godot_open_mcp_control_create` tool definition (P16.5).
//
// Creates a Godot UI Control subclass in the currently edited scene by `type`
// (Button / Label / LineEdit / TextEdit / TextureRect / ColorRect / ProgressBar /
// CheckBox / CheckButton / HSlider / SpinBox / OptionButton / VSeparator /
// NinePatchRect / RichTextLabel) and applies starter full-rect anchors so the
// control is visible without manual layout. The handler lives in the bridge
// (POST /tools/godot_open_mcp_control_create); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Adapted from Unity Open MCP's `ui_element_add`
// (TypedTools/Extensions/UI/UITools.cs — adapt fidelity): same type-enum create
// shape (one tool with a `type` enum, not one tool per Control subclass), but
// Godot UI is a tree of Control nodes (each a CanvasItem), not Unity uGUI
// components attached to a GameObject under a Canvas. Unity Canvas /
// CanvasScaler / GraphicRaycaster / EventSystem are intentionally NOT ported
// (no Godot equivalents — the viewport is the canvas).
//
// This is a `ui` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "ui" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const controlCreate: Tool = {
  name: "godot_open_mcp_control_create",
  description:
    "Create a Godot UI Control subclass in the currently edited scene by `type` and return its NodeData. " +
    "Types: 'button' (Button), 'label' (Label), 'lineedit' (LineEdit — single-line text input), 'textedit' " +
    "(TextEdit — multiline text), 'texturerect' (TextureRect — image display), 'colorrect' (ColorRect — solid " +
    "color panel), 'progressbar' (ProgressBar), 'checkbox' (CheckBox), 'checkbutton' (CheckButton — checkbox " +
    "styled as a toggle button), 'slider' (HSlider), 'spinbox' (SpinBox), 'optionbutton' (OptionButton — " +
    "dropdown), 'separator' (VSeparator), 'ninepatchrect' (NinePatchRect — 9-slice image), 'richtextlabel' " +
    "(RichTextLabel — BBCode text). Starter `anchors_preset` defaults to 'full_rect' (fills the parent) so the " +
    "control is visible without manual layout; pass a different preset name to override. Optional starter `text` " +
    "applies to controls with a Text property (Button / Label / LineEdit / TextEdit / RichTextLabel / CheckBox / " +
    "CheckButton). The new node's owner is the edited scene root; the scene is marked unsaved.\n\n" +
    "Mutating — runs the full gate cycle (checkpoint → save → validate → delta) by default. This is a " +
    "`ui` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["type", "paths_hint"],
    properties: {
      type: {
        type: "string",
        enum: [
          "button",
          "label",
          "lineedit",
          "textedit",
          "texturerect",
          "colorrect",
          "progressbar",
          "checkbox",
          "checkbutton",
          "slider",
          "spinbox",
          "optionbutton",
          "separator",
          "ninepatchrect",
          "richtextlabel",
        ],
        description:
          "Which Control subclass to instantiate. 'button' → Button. 'label' → Label (read-only text). " +
          "'lineedit' → LineEdit (single-line input). 'textedit' → TextEdit (multiline input). 'texturerect' " +
          "→ TextureRect (image). 'colorrect' → ColorRect (solid panel). 'progressbar' → ProgressBar. " +
          "'checkbox' → CheckBox. 'checkbutton' → CheckButton (toggle-button-styled checkbox). 'slider' → " +
          "HSlider (horizontal slider; use node_modify for VSlider). 'spinbox' → SpinBox. 'optionbutton' → " +
          "OptionButton (dropdown). 'separator' → VSeparator. 'ninepatchrect' → NinePatchRect (9-slice). " +
          "'richtextlabel' → RichTextLabel (BBCode).",
      },
      name: {
        type: "string",
        description:
          "Optional name for the new control node. When omitted, Godot assigns a default name for the type.",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root (same resolver as " +
          "node_find / node_create). Defaults to the edited scene root. UI controls are usually parented to a " +
          "Control or Container so the layout propagates.",
      },
      text: {
        type: "string",
        description:
          "Optional starter text for controls that expose a Text property (Button / Label / LineEdit / TextEdit " +
          "/ RichTextLabel / CheckBox / CheckButton). Ignored for controls with no Text property (surfaces in " +
          "the `warnings` array; the node is still created).",
      },
      anchors_preset: {
        type: "string",
        description:
          "Optional Godot LayoutPreset name. Defaults to 'full_rect' (the control fills its parent — visible " +
          "without manual layout). Other presets: 'top_left', 'top_right', 'bottom_right', 'bottom_left', " +
          "'center_left', 'center_top', 'center_right', 'center_bottom', 'center', 'left_wide', 'top_wide', " +
          "'right_wide', 'bottom_wide', 'vcenter_wide', 'hcenter_wide'. An unrecognized name surfaces in " +
          "`warnings` (the control keeps its default anchors).",
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
