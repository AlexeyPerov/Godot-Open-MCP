// P18.1 input-map pack — schema + group-assignment parity tests.
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// (name prefix, non-empty description, read-only tool shape without paths_hint,
// mutating-tool shape with paths_hint required + gate default enforce + events[]
// array) and the group assignment (all three tools map to `input`). The live
// round-trip (POST /tools/godot_open_mcp_input_map_* → bridge handler → result
// envelope) is exercised by the headless Godot smoke; this file only asserts the
// contract advertised over stdio.
//
// Adapted from settings-tools.test.ts (copy fidelity for the catalog-metadata
// test shape), with assertions specific to the input pack's read-only vs
// mutating split, the event-type enum, and the events[] array-of-objects
// contract.

import { test } from "node:test";
import assert from "node:assert/strict";

import { inputMapGet } from "./input-map-get.js";
import { inputMapActionAdd } from "./input-map-action-add.js";
import { inputMapActionSetEvents } from "./input-map-action-set-events.js";
import { groupFor, toolsInGroup } from "../capabilities/tool-groups.js";

const ALL_INPUT_TOOLS = [inputMapGet, inputMapActionAdd, inputMapActionSetEvents];

// ---------------------------------------------------------------------------
// Names + group assignment
// ---------------------------------------------------------------------------

test("every input tool name follows the godot_open_mcp_input_map_* convention", () => {
  for (const t of ALL_INPUT_TOOLS) {
    assert.match(t.name, /^godot_open_mcp_input_map_[a-z0-9_]+$/);
  }
});

test("every input tool is assigned to the input group", () => {
  for (const t of ALL_INPUT_TOOLS) {
    assert.equal(groupFor(t.name), "input", `${t.name} must map to input`);
  }
});

test("toolsInGroup(input) returns exactly the three pack tools", () => {
  assert.deepEqual(toolsInGroup("input"), [
    "godot_open_mcp_input_map_action_add",
    "godot_open_mcp_input_map_action_set_events",
    "godot_open_mcp_input_map_get",
  ]);
});

// ---------------------------------------------------------------------------
// Shared catalog-metadata invariants
// ---------------------------------------------------------------------------

test("every input tool has a non-empty description", () => {
  for (const t of ALL_INPUT_TOOLS) {
    assert.ok(typeof t.description === "string");
    assert.ok((t.description ?? "").length > 0, `${t.name} description is empty`);
  }
});

test("every input tool declares an object input schema with additionalProperties:false", () => {
  for (const t of ALL_INPUT_TOOLS) {
    assert.equal(t.inputSchema.type, "object", `${t.name} schema type`);
    assert.equal(t.inputSchema.additionalProperties, false, `${t.name} additionalProperties`);
  }
});

test("every input tool description tells the agent to activate the input group", () => {
  // The group is default-off; the description must surface the activation step so an
  // agent does not call a hidden tool blind. All three tools are in the group — they
  // must mention activation (same contract as the settings tools).
  for (const t of ALL_INPUT_TOOLS) {
    assert.match(
      t.description ?? "",
      /activate the group with manage_tools/,
      `${t.name} description must mention manage_tools activation`,
    );
  }
});

// ---------------------------------------------------------------------------
// input_map_get — read-only shape
// ---------------------------------------------------------------------------

test("input_map_get is read-only (no paths_hint, no gate)", () => {
  const props = inputMapGet.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "get must not declare paths_hint");
  assert.equal(props.gate, undefined, "get must not declare gate");
  assert.deepEqual(Object.keys(props).sort(), ["action"]);
});

test("input_map_get exposes only the optional action filter", () => {
  const props = inputMapGet.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["action"]);
  assert.deepEqual((inputMapGet.inputSchema.required ?? []).sort(), []);
});

// ---------------------------------------------------------------------------
// input_map_action_add — mutating shape
// ---------------------------------------------------------------------------

test("input_map_action_add is mutating (paths_hint required, gate default enforce)", () => {
  assert.ok(
    (inputMapActionAdd.inputSchema.required ?? []).includes("paths_hint"),
    "action_add must require paths_hint",
  );
  const gate = (inputMapActionAdd.inputSchema.properties as Record<string, { default?: string }>).gate;
  assert.equal(gate?.default, "enforce", "action_add gate default must be enforce");
});

test("input_map_action_add gate enum is [enforce, warn, off]", () => {
  const gate = (inputMapActionAdd.inputSchema.properties as Record<string, { enum?: string[] }>).gate;
  assert.deepEqual(gate?.enum, ["enforce", "warn", "off"], "action_add gate enum");
});

test("input_map_action_add paths_hint is an array of strings", () => {
  const ph = (inputMapActionAdd.inputSchema.properties as Record<string, { type?: string; items?: { type?: string } }>).paths_hint;
  assert.equal(ph?.type, "array", "action_add paths_hint type");
  assert.equal(ph?.items?.type, "string", "action_add paths_hint items type");
});

test("input_map_action_add requires action + paths_hint", () => {
  assert.deepEqual(
    (inputMapActionAdd.inputSchema.required ?? []).sort(),
    ["action", "paths_hint"],
  );
});

test("input_map_action_add deadzone defaults to 0.5 and is clamped to [0,1]", () => {
  const deadzone = (inputMapActionAdd.inputSchema.properties as Record<string, {
    default?: number;
    minimum?: number;
    maximum?: number;
  }>).deadzone;
  assert.equal(deadzone?.default, 0.5, "deadzone default");
  assert.equal(deadzone?.minimum, 0, "deadzone minimum");
  assert.equal(deadzone?.maximum, 1, "deadzone maximum");
});

test("input_map_action_add exposes the mutating property set", () => {
  const props = inputMapActionAdd.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["action", "deadzone", "gate", "paths_hint"]);
});

// ---------------------------------------------------------------------------
// input_map_action_set_events — mutating shape + events[] contract
// ---------------------------------------------------------------------------

test("input_map_action_set_events is mutating (paths_hint required, gate default enforce)", () => {
  assert.ok(
    (inputMapActionSetEvents.inputSchema.required ?? []).includes("paths_hint"),
    "action_set_events must require paths_hint",
  );
  const gate = (inputMapActionSetEvents.inputSchema.properties as Record<string, { default?: string }>).gate;
  assert.equal(gate?.default, "enforce", "action_set_events gate default must be enforce");
});

test("input_map_action_set_events requires action + events + paths_hint", () => {
  assert.deepEqual(
    (inputMapActionSetEvents.inputSchema.required ?? []).sort(),
    ["action", "events", "paths_hint"],
  );
});

test("input_map_action_set_events events is a non-empty array of {type} event objects", () => {
  const events = (inputMapActionSetEvents.inputSchema.properties as Record<string, {
    type?: string;
    minItems?: number;
    items?: { type?: string; required?: string[] };
  }>).events;
  assert.equal(events?.type, "array", "events type");
  assert.equal(events?.minItems, 1, "events minItems must be 1");
  assert.equal(events?.items?.type, "object", "events items type");
  assert.deepEqual(events?.items?.required, ["type"], "each event must require type");
});

test("input_map_action_set_events event type enum covers the four InputEvent kinds", () => {
  const events = (inputMapActionSetEvents.inputSchema.properties as Record<string, {
    items?: { properties?: Record<string, { enum?: string[] }> };
  }>).events;
  const typeEnum = events?.items?.properties?.type?.enum;
  assert.deepEqual(typeEnum, ["key", "mouse_button", "joypad_button", "joypad_motion"]);
});

test("input_map_action_set_events event object additionalProperties is false", () => {
  // An event object must only carry its declared fields — no stray keys.
  const events = (inputMapActionSetEvents.inputSchema.properties as Record<string, {
    items?: { additionalProperties?: boolean };
  }>).events;
  assert.equal(events?.items?.additionalProperties, false, "event additionalProperties");
});

test("input_map_action_set_events exposes the mutating property set", () => {
  const props = inputMapActionSetEvents.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["action", "events", "gate", "paths_hint"]);
});
