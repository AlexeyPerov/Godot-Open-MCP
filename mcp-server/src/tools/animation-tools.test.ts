// P12.4 animation domain pack — schema + group-assignment parity tests.
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// (name prefix, non-empty description, mutating-tool shape with paths_hint
// required + gate default enforce, read-only tool shape without paths_hint) and
// the group assignment (all seven tools map to `animation`). The live round-trip
// (POST /tools/godot_open_mcp_animation_* → bridge handler → result envelope) is
// exercised by the headless Godot smoke; this file only asserts the contract
// advertised over stdio.
//
// Adapted from particles-tools.test.ts (copy fidelity for the catalog-metadata test
// shape), with assertions specific to the animation pack's mutating vs read-only
// split and the Godot animation authoring property sets.

import { test } from "node:test";
import assert from "node:assert/strict";

import { animationDefaults } from "./animation-defaults.js";
import { animationPlayerCreate } from "./animation-player-create.js";
import { animationLibraryAdd } from "./animation-library-add.js";
import { animationCreate } from "./animation-create.js";
import { animationAddTrack } from "./animation-add-track.js";
import { animationInsertKey } from "./animation-insert-key.js";
import { animationGet } from "./animation-get.js";
import { groupFor, toolsInGroup } from "../capabilities/tool-groups.js";

const ALL_ANIMATION_TOOLS = [
  animationDefaults,
  animationPlayerCreate,
  animationLibraryAdd,
  animationCreate,
  animationAddTrack,
  animationInsertKey,
  animationGet,
];

const MUTATING_TOOLS = [
  animationPlayerCreate,
  animationLibraryAdd,
  animationCreate,
  animationAddTrack,
  animationInsertKey,
];

const READ_ONLY_TOOLS = [animationDefaults, animationGet];

// ---------------------------------------------------------------------------
// Names + group assignment
// ---------------------------------------------------------------------------

test("every animation tool name follows the godot_open_mcp_animation_* convention", () => {
  for (const t of ALL_ANIMATION_TOOLS) {
    assert.match(t.name, /^godot_open_mcp_animation_[a-z0-9_]+$/);
  }
});

test("every animation tool is assigned to the animation group", () => {
  for (const t of ALL_ANIMATION_TOOLS) {
    assert.equal(groupFor(t.name), "animation", `${t.name} must map to animation`);
  }
});

test("toolsInGroup(animation) returns exactly the seven pack tools", () => {
  assert.deepEqual(toolsInGroup("animation"), [
    "godot_open_mcp_animation_add_track",
    "godot_open_mcp_animation_create",
    "godot_open_mcp_animation_defaults",
    "godot_open_mcp_animation_get",
    "godot_open_mcp_animation_insert_key",
    "godot_open_mcp_animation_library_add",
    "godot_open_mcp_animation_player_create",
  ]);
});

test("toolsInGroup(animation).length === 7 (acceptance criterion)", () => {
  assert.equal(toolsInGroup("animation").length, 7);
});

// ---------------------------------------------------------------------------
// Shared catalog-metadata invariants
// ---------------------------------------------------------------------------

test("every animation tool has a non-empty description", () => {
  for (const t of ALL_ANIMATION_TOOLS) {
    assert.ok(typeof t.description === "string");
    assert.ok((t.description ?? "").length > 0, `${t.name} description is empty`);
  }
});

test("every animation tool declares an object input schema with additionalProperties:false", () => {
  for (const t of ALL_ANIMATION_TOOLS) {
    assert.equal(t.inputSchema.type, "object", `${t.name} schema type`);
    assert.equal(t.inputSchema.additionalProperties, false, `${t.name} additionalProperties`);
  }
});

test("every animation tool description tells the agent to activate the animation group", () => {
  // The group is default-off; the description must surface the activation step so an
  // agent does not call a hidden tool blind. defaults and get are also in the group —
  // they must mention activation too (same contract as the particles read-only tools).
  for (const t of ALL_ANIMATION_TOOLS) {
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

test("every mutating animation tool requires paths_hint", () => {
  for (const t of MUTATING_TOOLS) {
    assert.ok(
      (t.inputSchema.required ?? []).includes("paths_hint"),
      `${t.name} must require paths_hint`,
    );
  }
});

test("every mutating animation tool defaults gate to enforce", () => {
  for (const t of MUTATING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { default?: string }>).gate;
    assert.equal(gate?.default, "enforce", `${t.name} gate default must be enforce`);
  }
});

test("every mutating animation tool gate enum is [enforce, warn, off]", () => {
  for (const t of MUTATING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { enum?: string[] }>).gate;
    assert.deepEqual(gate?.enum, ["enforce", "warn", "off"], `${t.name} gate enum`);
  }
});

test("paths_hint is an array of strings on every mutating animation tool", () => {
  for (const t of MUTATING_TOOLS) {
    const ph = (t.inputSchema.properties as Record<string, { type?: string; items?: { type?: string } }>).paths_hint;
    assert.equal(ph?.type, "array", `${t.name} paths_hint type`);
    assert.equal(ph?.items?.type, "string", `${t.name} paths_hint items type`);
  }
});

// ---------------------------------------------------------------------------
// Read-only tools — no paths_hint, no gate
// ---------------------------------------------------------------------------

test("animation_defaults is read-only (no paths_hint, no gate)", () => {
  const props = animationDefaults.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "defaults must not declare paths_hint");
  assert.equal(props.gate, undefined, "defaults must not declare gate");
  assert.deepEqual(Object.keys(props), []);
});

test("animation_get is read-only (no paths_hint, no gate)", () => {
  const props = animationGet.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "get must not declare paths_hint");
  assert.equal(props.gate, undefined, "get must not declare gate");
});

test("read-only animation tools do not list paths_hint as required", () => {
  for (const t of READ_ONLY_TOOLS) {
    assert.ok(
      !(t.inputSchema.required ?? []).includes("paths_hint"),
      `${t.name} must not require paths_hint`,
    );
  }
});

// ---------------------------------------------------------------------------
// Per-tool property sets
// ---------------------------------------------------------------------------

test("animation_defaults exposes no properties (pure helper)", () => {
  const props = animationDefaults.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props), []);
  assert.deepEqual(animationDefaults.inputSchema.required ?? [], []);
});

test("animation_player_create exposes the create property set", () => {
  const props = animationPlayerCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "name",
    "parent_node_path",
    "paths_hint",
    "position",
  ]);
  assert.deepEqual(
    (animationPlayerCreate.inputSchema.required ?? []).sort(),
    ["paths_hint"],
  );
});

test("animation_library_add exposes node_path + library", () => {
  const props = animationLibraryAdd.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["gate", "library", "node_path", "paths_hint"]);
  assert.deepEqual(
    (animationLibraryAdd.inputSchema.required ?? []).sort(),
    ["node_path", "paths_hint"],
  );
});

test("animation_create exposes the clip-create property set", () => {
  const props = animationCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "animation",
    "gate",
    "length",
    "library",
    "loop_mode",
    "node_path",
    "paths_hint",
  ]);
  assert.deepEqual(
    (animationCreate.inputSchema.required ?? []).sort(),
    ["animation", "node_path", "paths_hint"],
  );
});

test("animation_create loop_mode enum is [none, linear, pingpong]", () => {
  const lm = (animationCreate.inputSchema.properties as Record<string, { enum?: string[] }>).loop_mode;
  assert.deepEqual(lm?.enum, ["none", "linear", "pingpong"]);
});

test("animation_create length is strictly positive", () => {
  const length = (animationCreate.inputSchema.properties as Record<
    string,
    { exclusiveMinimum?: number }
  >).length;
  assert.equal(length?.exclusiveMinimum, 0, "length must be strictly positive");
});

test("animation_add_track exposes the track-add property set", () => {
  const props = animationAddTrack.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "animation",
    "gate",
    "library",
    "node_path",
    "paths_hint",
    "track_path",
    "track_type",
    "update_mode",
    "value_type",
  ]);
  assert.deepEqual(
    (animationAddTrack.inputSchema.required ?? []).sort(),
    ["animation", "node_path", "paths_hint", "track_path", "track_type"],
  );
});

test("animation_add_track track_type enum is the v1 surface", () => {
  const tt = (animationAddTrack.inputSchema.properties as Record<string, { enum?: string[] }>).track_type;
  // Blend-shape / method / bezier / audio / animation are deliberately NOT here.
  assert.deepEqual(tt?.enum, ["value", "position_3d", "rotation_3d", "scale_3d"]);
});

test("animation_add_track update_mode enum is [continuous, discrete, capture]", () => {
  const um = (animationAddTrack.inputSchema.properties as Record<string, { enum?: string[] }>).update_mode;
  assert.deepEqual(um?.enum, ["continuous", "discrete", "capture"]);
});

test("animation_insert_key exposes the key-insert property set", () => {
  const props = animationInsertKey.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "animation",
    "gate",
    "interpolation",
    "library",
    "node_path",
    "paths_hint",
    "time",
    "track_index",
    "transition",
    "value",
  ]);
  assert.deepEqual(
    (animationInsertKey.inputSchema.required ?? []).sort(),
    ["animation", "node_path", "paths_hint", "time", "track_index", "value"],
  );
});

test("animation_insert_key track_index minimum is non-negative", () => {
  const ti = (animationInsertKey.inputSchema.properties as Record<
    string,
    { minimum?: number }
  >).track_index;
  assert.equal(ti?.minimum, 0, "track_index minimum must be 0");
});

test("animation_insert_key interpolation enum is [nearest, linear, cubic]", () => {
  const interp = (animationInsertKey.inputSchema.properties as Record<
    string,
    { enum?: string[] }
  >).interpolation;
  // Angle-variant interpolations are deferred from v1.
  assert.deepEqual(interp?.enum, ["nearest", "linear", "cubic"]);
});

test("animation_insert_key value is a multi-type (number/boolean/string/object)", () => {
  const v = (animationInsertKey.inputSchema.properties as Record<
    string,
    { type?: string[] }
  >).value;
  assert.deepEqual(v?.type, ["number", "boolean", "string", "object"]);
});

test("animation_get exposes the get property set", () => {
  const props = animationGet.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "animation",
    "include_keys",
    "library",
    "max_keys",
    "node_path",
  ]);
  assert.deepEqual(
    (animationGet.inputSchema.required ?? []).sort(),
    ["node_path"],
  );
});

test("animation_get include_keys defaults to false", () => {
  const ik = (animationGet.inputSchema.properties as Record<string, { default?: boolean }>).include_keys;
  assert.equal(ik?.default, false);
});

test("animation_get max_keys is bounded to [0, 256]", () => {
  const mk = (animationGet.inputSchema.properties as Record<
    string,
    { minimum?: number; maximum?: number }
  >).max_keys;
  assert.equal(mk?.minimum, 0, "max_keys minimum must be 0");
  assert.equal(mk?.maximum, 256, "max_keys maximum must be 256 (hard cap)");
});
