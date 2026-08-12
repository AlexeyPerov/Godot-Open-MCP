// `godot_open_mcp_input_map_action_set_events` tool definition (P18.1).
//
// Replaces an existing Godot InputMap action's whole event list via Godot's own
// `InputMap` API (ActionEraseEvents + ActionAddEvent) and persists it to
// project.godot's `[input]` section through
// `ProjectSettings.SetSetting("input/<name>", ...)` + `ProjectSettings.Save`
// (never raw text edits). The live InputMap is mutated first so the change takes
// effect immediately in the editor; it is then mirrored to ProjectSettings for
// persistence. The handler lives in the bridge
// (POST /tools/godot_open_mcp_input_map_action_set_events); this file is the
// catalog metadata only — name / description / input schema — advertised to AI
// clients over stdio ListTools.
//
// Adapted from Unity Open MCP's binding-add shape
// (TypedTools/Extensions/InputSystem/InputSystemTools.cs — adapt fidelity): same
// "validate + persist the binding set" shape, but Godot's events are flat
// InputEvent subclasses addressed by a single `type` enum (key / mouse_button /
// joypad_button / joypad_motion) rather than Unity binding path strings, and one
// call replaces the WHOLE list (no Unity composite / interactions / processors).
//
// This is an `input` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "input" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const inputMapActionSetEvents: Tool = {
  name: "godot_open_mcp_input_map_action_set_events",
  description:
    "Replace an existing Godot InputMap action's whole event list via Godot's InputMap API and persist to " +
    "project.godot's [input] section (no raw text edits — the API validates the file's escaping). Pass an " +
    "action name plus an events[] array of typed event objects; the handler erases the action's current " +
    "events, builds each InputEvent from its type + fields, applies them, and persists once.\n\n" +
    "Each event's `type` selects the InputEvent subclass: 'key' (InputEventKey — needs physical_keycode " +
    "and/or keycode as Key ordinals), 'mouse_button' (InputEventMouseButton — needs button_index as a " +
    "MouseButton ordinal), 'joypad_button' (InputEventJoypadButton — needs button_index as a JoyButton " +
    "ordinal), 'joypad_motion' (InputEventJoypadMotion — needs axis as a JoyAxis ordinal + axis_value). " +
    "All int fields are Godot enum ordinals — read them from input_map_get's output to round-trip. device " +
    "defaults to -1 (any device) when omitted.\n\n" +
    "An unbuildable event (unknown type / missing required field) is skipped with a warning (non-aborting) " +
    "so a batch's good events still land; a fully-unbuildable batch surfaces no_applicable_events and " +
    "leaves the existing events intact. Mutating: runs the gate cycle by default; paths_hint is " +
    "res://project.godot. This is an `input` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["action", "events", "paths_hint"],
    properties: {
      action: {
        type: "string",
        description:
          "The existing action name whose event list to replace (e.g. 'jump', 'ui_accept'). Must already " +
          "exist — a missing name surfaces action_not_found (use input_map_action_add to create it first).",
      },
      events: {
        type: "array",
        minItems: 1,
        items: {
          type: "object",
          required: ["type"],
          properties: {
            type: {
              type: "string",
              enum: ["key", "mouse_button", "joypad_button", "joypad_motion"],
              description:
                "Which InputEvent subclass to build. 'key': InputEventKey. 'mouse_button': " +
                "InputEventMouseButton. 'joypad_button': InputEventJoypadButton. 'joypad_motion': " +
                "InputEventJoypadMotion.",
            },
            physical_keycode: {
              type: "integer",
              description:
                "Key ordinal for 'key' events (Godot Key enum — e.g. 65 = A, 32 = Space). The physical " +
                "key by layout position (layout-independent). Required for 'key' unless keycode is set.",
            },
            keycode: {
              type: "integer",
              description:
                "Logical Key ordinal for 'key' events (Godot Key enum). The key by current layout label. " +
                "Optional; most game bindings use physical_keycode instead.",
            },
            unicode: {
              type: "integer",
              description: "Optional unicode codepoint for 'key' events.",
            },
            button_index: {
              type: "integer",
              description:
                "Button ordinal for 'mouse_button' (Godot MouseButton enum — e.g. 1 = left, 2 = right, " +
                "3 = middle) or 'joypad_button' (Godot JoyButton enum — e.g. 0 = A / cross, 1 = B / circle).",
            },
            axis: {
              type: "integer",
              description:
                "Axis ordinal for 'joypad_motion' (Godot JoyAxis enum — e.g. 0 = left stick X, 1 = left " +
                "stick Y). Required for 'joypad_motion'.",
            },
            axis_value: {
              type: "number",
              description:
                "Axis value for 'joypad_motion' (-1 to 1, the threshold the action fires at). Required for " +
                "'joypad_motion'.",
            },
            device: {
              type: "integer",
              default: -1,
              description:
                "Device id. Default -1 = any device (the sensible default for keyboard / mouse bindings). " +
                "0+ targets a specific gamepad slot.",
            },
            doubleclick: {
              type: "boolean",
              description: "Optional double-click flag for 'mouse_button' events.",
            },
          },
          additionalProperties: false,
        },
        description:
          "Non-empty replacement event list. Each event is applied independently; an unbuildable event " +
          "(unknown type / missing required field) is skipped with a warning and does NOT abort the batch. " +
          "An entirely unbuildable batch surfaces no_applicable_events and leaves the existing events intact.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the project file. Pass [\"res://project.godot\"]. Mandatory even when gate " +
          "is 'off' (handler-level guard).",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → mutate → validate → delta; new errors fail " +
          "the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the cycle (paths_hint " +
          "is still required).",
      },
    },
    additionalProperties: false,
  },
};
