// P16.1 project-settings pack — schema + group-assignment parity tests.
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// (name prefix, non-empty description, read-only tool shape without paths_hint,
// mutating-tool shape with paths_hint required + gate default enforce + fields[]
// array) and the group assignment (both tools map to `settings`). The live
// round-trip (POST /tools/godot_open_mcp_settings_* → bridge handler → result
// envelope) is exercised by the headless Godot smoke; this file only asserts the
// contract advertised over stdio.
//
// Adapted from csg-tools.test.ts (copy fidelity for the catalog-metadata test
// shape), with assertions specific to the settings pack's read-only vs mutating
// split, the section enum, and the fields[] patch-array contract.

import { test } from "node:test";
import assert from "node:assert/strict";

import { settingsGetProject } from "./settings-get-project.js";
import { settingsSetProject } from "./settings-set-project.js";
import { groupFor, toolsInGroup } from "../capabilities/tool-groups.js";

const ALL_SETTINGS_TOOLS = [settingsGetProject, settingsSetProject];

// ---------------------------------------------------------------------------
// Names + group assignment
// ---------------------------------------------------------------------------

test("every settings tool name follows the godot_open_mcp_settings_* convention", () => {
  for (const t of ALL_SETTINGS_TOOLS) {
    assert.match(t.name, /^godot_open_mcp_settings_[a-z0-9_]+$/);
  }
});

test("every settings tool is assigned to the settings group", () => {
  for (const t of ALL_SETTINGS_TOOLS) {
    assert.equal(groupFor(t.name), "settings", `${t.name} must map to settings`);
  }
});

test("toolsInGroup(settings) returns exactly the two pack tools", () => {
  assert.deepEqual(toolsInGroup("settings"), [
    "godot_open_mcp_settings_get_project",
    "godot_open_mcp_settings_set_project",
  ]);
});

// ---------------------------------------------------------------------------
// Shared catalog-metadata invariants
// ---------------------------------------------------------------------------

test("every settings tool has a non-empty description", () => {
  for (const t of ALL_SETTINGS_TOOLS) {
    assert.ok(typeof t.description === "string");
    assert.ok((t.description ?? "").length > 0, `${t.name} description is empty`);
  }
});

test("every settings tool declares an object input schema with additionalProperties:false", () => {
  for (const t of ALL_SETTINGS_TOOLS) {
    assert.equal(t.inputSchema.type, "object", `${t.name} schema type`);
    assert.equal(t.inputSchema.additionalProperties, false, `${t.name} additionalProperties`);
  }
});

test("every settings tool description tells the agent to activate the settings group", () => {
  // The group is default-off; the description must surface the activation step so an
  // agent does not call a hidden tool blind. Both get and set are in the group — they
  // must mention activation (same contract as the csg read-only tools).
  for (const t of ALL_SETTINGS_TOOLS) {
    assert.match(
      t.description ?? "",
      /activate the group with manage_tools/,
      `${t.name} description must mention manage_tools activation`,
    );
  }
});

// ---------------------------------------------------------------------------
// settings_get_project — read-only shape
// ---------------------------------------------------------------------------

test("settings_get_project is read-only (no paths_hint, no gate)", () => {
  const props = settingsGetProject.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "get must not declare paths_hint");
  assert.equal(props.gate, undefined, "get must not declare gate");
  assert.deepEqual(Object.keys(props).sort(), ["section"]);
});

test("settings_get_project exposes only section", () => {
  const props = settingsGetProject.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["section"]);
  assert.deepEqual(
    (settingsGetProject.inputSchema.required ?? []).sort(),
    ["section"],
  );
});

test("settings_get_project section enum covers the seven writable sections plus all", () => {
  const section = (settingsGetProject.inputSchema.properties as Record<string, { enum?: string[] }>).section;
  assert.deepEqual(section?.enum, [
    "rendering",
    "physics",
    "input",
    "layer_names",
    "autoload",
    "application",
    "display",
    "all",
  ]);
});

// ---------------------------------------------------------------------------
// settings_set_project — mutating shape
// ---------------------------------------------------------------------------

test("settings_set_project is mutating (paths_hint required, gate default enforce)", () => {
  assert.ok(
    (settingsSetProject.inputSchema.required ?? []).includes("paths_hint"),
    "set must require paths_hint",
  );
  const gate = (settingsSetProject.inputSchema.properties as Record<string, { default?: string }>).gate;
  assert.equal(gate?.default, "enforce", "set gate default must be enforce");
});

test("settings_set_project gate enum is [enforce, warn, off]", () => {
  const gate = (settingsSetProject.inputSchema.properties as Record<string, { enum?: string[] }>).gate;
  assert.deepEqual(gate?.enum, ["enforce", "warn", "off"], "set gate enum");
});

test("settings_set_project paths_hint is an array of strings", () => {
  const ph = (settingsSetProject.inputSchema.properties as Record<string, { type?: string; items?: { type?: string } }>).paths_hint;
  assert.equal(ph?.type, "array", "set paths_hint type");
  assert.equal(ph?.items?.type, "string", "set paths_hint items type");
});

test("settings_set_project requires section + fields + paths_hint", () => {
  assert.deepEqual(
    (settingsSetProject.inputSchema.required ?? []).sort(),
    ["fields", "paths_hint", "section"],
  );
});

test("settings_set_project section enum is the seven writable sections (no all)", () => {
  // 'all' is the read-only summary switch — it must NOT appear in the writable
  // section enum. An agent who sends section:"all" gets invalid_parameter from the
  // handler.
  const section = (settingsSetProject.inputSchema.properties as Record<string, { enum?: string[] }>).section;
  assert.deepEqual(section?.enum, [
    "rendering",
    "physics",
    "input",
    "layer_names",
    "autoload",
    "application",
    "display",
  ]);
});

test("settings_set_project fields is a non-empty array of {key, value?} patches", () => {
  const fields = (settingsSetProject.inputSchema.properties as Record<string, {
    type?: string;
    minItems?: number;
    items?: { type?: string; required?: string[]; properties?: Record<string, unknown> };
  }>).fields;
  assert.equal(fields?.type, "array", "fields type");
  assert.equal(fields?.minItems, 1, "fields minItems must be 1");
  assert.equal(fields?.items?.type, "object", "fields items type");
  assert.deepEqual(fields?.items?.required, ["key"], "each patch must require key");
  assert.ok(fields?.items?.properties?.key, "patch must declare key");
  assert.ok(fields?.items?.properties?.value, "patch must declare value");
});

test("settings_set_project field patch additionalProperties is false", () => {
  // A patch object must only carry key + value — no stray keys.
  const fields = (settingsSetProject.inputSchema.properties as Record<string, {
    items?: { additionalProperties?: boolean };
  }>).fields;
  assert.equal(fields?.items?.additionalProperties, false, "patch additionalProperties");
});

test("settings_set_project exposes the mutating property set", () => {
  const props = settingsSetProject.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["fields", "gate", "paths_hint", "section"]);
});
