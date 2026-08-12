// `godot_open_mcp_input_map_get` tool definition (P18.1).
//
// Reads the Godot InputMap via Godot's own `InputMap` API (the runtime
// authority — reflects the live editor state, not just the on-disk file).
// Lists every action + its deadzone + its events, or reads a single named
// action. The handler lives in the bridge
// (POST /tools/godot_open_mcp_input_map_get); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Adapted from Unity Open MCP's `inputsystem_*` action/binding read shape
// (TypedTools/Extensions/InputSystem/InputSystemTools.cs — adapt fidelity):
// same "list actions + their bindings" shape, but the vocabulary is Godot's flat
// InputMap (action → InputEvent[]; no Unity ActionMap / Action / Binding /
// composite hierarchy) and events are Godot InputEvent subclasses addressed by a
// single `type` enum (key / mouse_button / joypad_button / joypad_motion) rather
// than Unity binding path strings.
//
// This is an `input` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "input" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const inputMapGet: Tool = {
  name: "godot_open_mcp_input_map_get",
  description:
    "Read the Godot InputMap via Godot's own InputMap API. With no `action`, lists every action " +
    "(built-in ui_* / move_* defaults plus your custom actions) with its deadzone + event count + " +
    "serialized events. With `action` set, reads a single named action.\n\n" +
    "Each event is a clean { type, ...kind-specific fields } object: key events carry physical_keycode / " +
    "keycode / unicode / device (int Key ordinals), mouse_button events carry button_index / doubleclick / " +
    "device (MouseButton ordinals), joypad_button events carry button_index / device (JoyButton ordinals), " +
    "joypad_motion events carry axis / axis_value / device (JoyAxis ordinals). The int ordinals are Godot's " +
    "own enum values — feed them straight back into input_map_action_set_events to round-trip.\n\n" +
    "No scene required; no mutation. This is an `input` group tool — activate the group with manage_tools " +
    "first. Read-only.",
  inputSchema: {
    type: "object",
    required: [],
    properties: {
      action: {
        type: "string",
        description:
          "Optional single action name to read (e.g. 'jump', 'ui_accept'). Omit to list every InputMap " +
          "action + its events. An absent name returns action_not_found.",
      },
    },
    additionalProperties: false,
  },
};
