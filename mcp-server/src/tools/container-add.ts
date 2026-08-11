// `godot_open_mcp_container_add` tool definition (P16.5).
//
// Adds a Godot container node (VBoxContainer / HBoxContainer / GridContainer /
// MarginContainer / ScrollContainer) under a parent in the currently edited
// scene, with starter full-rect anchors applied. The handler lives in the bridge
// (POST /tools/godot_open_mcp_container_add); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Adapted from Unity Open MCP's `ui_layout_group_add`
// (TypedTools/Extensions/UI/UITools.cs — adapt fidelity): same type-enum add
// shape, but Godot containers are Control subclasses (each a Container), not
// Unity HorizontalLayoutGroup / VerticalLayoutGroup / GridLayoutGroup components
// attached to a GameObject. Unity padding (RectOffset) / spacing (Vector2) /
// childControlWidth / childForceExpand are mapped to the Godot container
// vocabulary (separation / columns / margins) via container_set_layout.
//
// This is a `ui` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "ui" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const containerAdd: Tool = {
  name: "godot_open_mcp_container_add",
  description:
    "Add a Godot container node in the currently edited scene by `type` and return its NodeData. Types: 'vbox' " +
    "(VBoxContainer — vertical stack), 'hbox' (HBoxContainer — horizontal row), 'grid' (GridContainer — " +
    "column-major grid, configure columns via container_set_layout), 'margin' (MarginContainer — single child " +
    "with configurable margins), 'scroll' (ScrollContainer — scrolls a single child that overflows). Starter " +
    "`anchors_preset` defaults to 'full_rect' (fills the parent) so the container is visible without manual " +
    "layout; pass a different preset name to override. The new node's owner is the edited scene root; the scene " +
    "is marked unsaved.\n\n" +
    "Mutating — runs the full gate cycle (checkpoint → save → validate → delta) by default. This is a " +
    "`ui` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["type", "paths_hint"],
    properties: {
      type: {
        type: "string",
        enum: ["vbox", "hbox", "grid", "margin", "scroll"],
        description:
          "Which container family to instantiate. 'vbox' → VBoxContainer (children stack vertically). 'hbox' → " +
          "HBoxContainer (children row horizontally). 'grid' → GridContainer (children laid out in a grid; " +
          "columns default to 1 — set via container_set_layout). 'margin' → MarginContainer (frames a single " +
          "child with configurable margins). 'scroll' → ScrollContainer (scrolls a single child that overflows).",
      },
      name: {
        type: "string",
        description:
          "Optional name for the new container node. When omitted, Godot assigns a default name for the type.",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root (same resolver as " +
          "node_find / node_create). Defaults to the edited scene root. Containers are usually parented to " +
          "another Control or Container so the layout propagates.",
      },
      anchors_preset: {
        type: "string",
        description:
          "Optional Godot LayoutPreset name. Defaults to 'full_rect' (the container fills its parent — visible " +
          "without manual layout). Same preset vocabulary as control_create.",
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
