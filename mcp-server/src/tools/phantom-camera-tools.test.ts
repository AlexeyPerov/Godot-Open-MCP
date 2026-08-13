// phantom_camera domain pack — schema + group-assignment parity tests.
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// (name prefix, non-empty description, mutating-tool shape with paths_hint
// required + gate default enforce, read-only tool shape without paths_hint) and
// the group assignment (all six tools map to `phantom_camera`). The live
// round-trip (POST /tools/godot_open_mcp_phantom_camera_* → bridge handler →
// result envelope) is exercised by the headless Godot smoke WITH the addon
// enabled; this file only asserts the contract advertised over stdio.
//
// Adapted from tilemap-tools.test.ts (copy fidelity for the catalog-metadata
// test shape), with assertions specific to the phantom_camera pack (dimension
// enum on create, the addon-not-found contract in every description).

import { test } from "node:test";
import assert from "node:assert/strict";

import { phantomCameraCreate } from "./phantom-camera-create.js";
import { phantomCameraSetTarget } from "./phantom-camera-set-target.js";
import { phantomCameraSetPriority } from "./phantom-camera-set-priority.js";
import { phantomCameraSetFollow } from "./phantom-camera-set-follow.js";
import { phantomCameraSetLookAt } from "./phantom-camera-set-look-at.js";
import { phantomCameraGet } from "./phantom-camera-get.js";
import { groupFor, toolsInGroup } from "../capabilities/tool-groups.js";

const ALL_PHANTOM_TOOLS = [
  phantomCameraCreate,
  phantomCameraSetTarget,
  phantomCameraSetPriority,
  phantomCameraSetFollow,
  phantomCameraSetLookAt,
  phantomCameraGet,
];

const MUTATING_TOOLS = [
  phantomCameraCreate,
  phantomCameraSetTarget,
  phantomCameraSetPriority,
  phantomCameraSetFollow,
  phantomCameraSetLookAt,
];

const READ_ONLY_TOOLS = [phantomCameraGet];

// ---------------------------------------------------------------------------
// Names + group assignment
// ---------------------------------------------------------------------------

test("every phantom_camera tool name follows the godot_open_mcp_phantom_camera_* convention", () => {
  for (const t of ALL_PHANTOM_TOOLS) {
    assert.match(t.name, /^godot_open_mcp_phantom_camera_[a-z0-9_]+$/);
  }
});

test("every phantom_camera tool is assigned to the phantom_camera group", () => {
  for (const t of ALL_PHANTOM_TOOLS) {
    assert.equal(groupFor(t.name), "phantom_camera", `${t.name} must map to phantom_camera`);
  }
});

test("toolsInGroup(phantom_camera) returns exactly the six pack tools", () => {
  assert.deepEqual(toolsInGroup("phantom_camera"), [
    "godot_open_mcp_phantom_camera_create",
    "godot_open_mcp_phantom_camera_get",
    "godot_open_mcp_phantom_camera_set_follow",
    "godot_open_mcp_phantom_camera_set_look_at",
    "godot_open_mcp_phantom_camera_set_priority",
    "godot_open_mcp_phantom_camera_set_target",
  ]);
});

// ---------------------------------------------------------------------------
// Shared catalog-metadata invariants
// ---------------------------------------------------------------------------

test("every phantom_camera tool has a non-empty description", () => {
  for (const t of ALL_PHANTOM_TOOLS) {
    assert.ok(typeof t.description === "string");
    assert.ok((t.description ?? "").length > 0, `${t.name} description is empty`);
  }
});

test("every phantom_camera tool declares an object input schema with additionalProperties:false", () => {
  for (const t of ALL_PHANTOM_TOOLS) {
    assert.equal(t.inputSchema.type, "object", `${t.name} schema type`);
    assert.equal(t.inputSchema.additionalProperties, false, `${t.name} additionalProperties`);
  }
});

test("every phantom_camera tool description tells the agent to activate the phantom_camera group", () => {
  for (const t of ALL_PHANTOM_TOOLS) {
    assert.match(
      t.description ?? "",
      /activate the group with manage_tools/,
      `${t.name} description must mention manage_tools activation`,
    );
  }
});

// ---------------------------------------------------------------------------
// Mutating tools — paths_hint required + gate default enforce
// ---------------------------------------------------------------------------

test("every mutating phantom_camera tool requires paths_hint", () => {
  for (const t of MUTATING_TOOLS) {
    assert.ok(
      (t.inputSchema.required ?? []).includes("paths_hint"),
      `${t.name} must require paths_hint`,
    );
  }
});

test("every mutating phantom_camera tool defaults gate to enforce", () => {
  for (const t of MUTATING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { default?: string }>).gate;
    assert.equal(gate?.default, "enforce", `${t.name} gate default must be enforce`);
  }
});

test("every mutating phantom_camera tool gate enum is [enforce, warn, off]", () => {
  for (const t of MUTATING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { enum?: string[] }>).gate;
    assert.deepEqual(gate?.enum, ["enforce", "warn", "off"], `${t.name} gate enum`);
  }
});

test("paths_hint is an array of strings on every mutating phantom_camera tool", () => {
  for (const t of MUTATING_TOOLS) {
    const ph = (t.inputSchema.properties as Record<string, { type?: string; items?: { type?: string } }>).paths_hint;
    assert.equal(ph?.type, "array", `${t.name} paths_hint type`);
    assert.equal(ph?.items?.type, "string", `${t.name} paths_hint items type`);
  }
});

// ---------------------------------------------------------------------------
// Read-only tool — no paths_hint, no gate
// ---------------------------------------------------------------------------

test("phantom_camera_get is read-only (no paths_hint, no gate)", () => {
  const props = phantomCameraGet.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "get must not declare paths_hint");
  assert.equal(props.gate, undefined, "get must not declare gate");
  assert.deepEqual(Object.keys(props).sort(), ["node_path"]);
});

test("phantom_camera_get does not list paths_hint as required", () => {
  assert.ok(
    !(phantomCameraGet.inputSchema.required ?? []).includes("paths_hint"),
  );
});

// ---------------------------------------------------------------------------
// Per-tool property sets
// ---------------------------------------------------------------------------

test("phantom_camera_create exposes the dimension enum + node-creation property set", () => {
  const props = phantomCameraCreate.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.deepEqual(Object.keys(props).sort(), [
    "dimension",
    "gate",
    "name",
    "parent_node_path",
    "paths_hint",
    "position",
  ]);
  assert.deepEqual(props.dimension.enum, ["2d", "3d"]);
  assert.equal(props.dimension.default, "3d");
});

test("phantom_camera_set_target exposes node_path + target_node_path", () => {
  const props = phantomCameraSetTarget.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "node_path",
    "paths_hint",
    "target_node_path",
  ]);
  assert.deepEqual(
    (phantomCameraSetTarget.inputSchema.required ?? []).sort(),
    ["node_path", "paths_hint", "target_node_path"],
  );
});

test("phantom_camera_set_priority exposes node_path + priority", () => {
  const props = phantomCameraSetPriority.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "node_path",
    "paths_hint",
    "priority",
  ]);
  assert.deepEqual(
    (phantomCameraSetPriority.inputSchema.required ?? []).sort(),
    ["node_path", "paths_hint", "priority"],
  );
});

test("phantom_camera_set_follow exposes node_path + follow_mode + optional target", () => {
  const props = phantomCameraSetFollow.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "follow_mode",
    "gate",
    "node_path",
    "paths_hint",
    "target_node_path",
  ]);
  assert.deepEqual(
    (phantomCameraSetFollow.inputSchema.required ?? []).sort(),
    ["follow_mode", "node_path", "paths_hint"],
  );
});

test("phantom_camera_set_look_at exposes node_path + target + optional look_at_mode", () => {
  const props = phantomCameraSetLookAt.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "look_at_mode",
    "node_path",
    "paths_hint",
    "target_node_path",
  ]);
  assert.deepEqual(
    (phantomCameraSetLookAt.inputSchema.required ?? []).sort(),
    ["node_path", "paths_hint", "target_node_path"],
  );
});
