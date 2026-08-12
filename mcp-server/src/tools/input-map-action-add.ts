// `godot_open_mcp_input_map_action_add` tool definition (P18.1).
//
// Adds a new action to the Godot InputMap via Godot's own `InputMap.AddAction`
// API and persists it to project.godot's `[input]` section through
// `ProjectSettings.SetSetting("input/<name>", ...)` + `ProjectSettings.Save`
// (never raw text edits — raw edits risk corrupting the file's
// `Object(InputEventKey,...)` escaping). The live InputMap is mutated first so
// the change takes effect immediately in the editor; it is then mirrored to
// ProjectSettings for persistence. The handler lives in the bridge
// (POST /tools/godot_open_mcp_input_map_action_add); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Adapted from Unity Open MCP's `inputsystem_action_add`
// (TypedTools/Extensions/InputSystem/InputSystemTools.cs — adapt fidelity): same
// add-checks-for-duplicates-then-persists shape, but Godot's InputMap is flat
// (action → events; no Unity ActionMap hierarchy) and writes route through the
// InputMap API mirrored to ProjectSettings (never raw text edits). Events are
// added separately via input_map_action_set_events (Godot InputEvent subclasses,
// not Unity binding path strings).
//
// This is an `input` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "input" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const inputMapActionAdd: Tool = {
  name: "godot_open_mcp_input_map_action_add",
  description:
    "Add a new action to the Godot InputMap via Godot's InputMap.AddAction API and persist it to " +
    "project.godot's [input] section (no raw text edits — the API validates the file's escaping). The " +
    "action starts with an empty event list; follow up with input_map_action_set_events to bind keys / " +
    "mouse / joypad events.\n\n" +
    "A name that already exists surfaces action_exists — use input_map_action_set_events to change an " +
    "existing action's events. The deadzone (optional, default 0.5 = Godot's own default for a new action) " +
    "is clamped to [0, 1].\n\n" +
    "Mutating: runs the gate cycle by default; paths_hint is res://project.godot (the single mutated file). " +
    "This is an `input` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["action", "paths_hint"],
    properties: {
      action: {
        type: "string",
        description:
          "The action name to add (e.g. 'jump', 'fire', 'interact'). Must not already exist in the " +
          "InputMap — a taken name surfaces action_exists. Matches the key Godot stores under " +
          "input/<action> in project.godot and the StringName Input.is_action_pressed reads at runtime.",
      },
      deadzone: {
        type: "number",
        minimum: 0,
        maximum: 1,
        default: 0.5,
        description:
          "Optional analog deadzone for the action (0–1, clamped). Default 0.5 — Godot's own default for a " +
          "new action. Below the deadzone, analog input is treated as zero.",
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
