// P12.3 particles domain pack — schema + group-assignment parity tests.
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// (name prefix, non-empty description, mutating-tool shape with paths_hint
// required + gate default enforce, read-only tool shape without paths_hint) and
// the group assignment (all five tools map to `particles`). The live round-trip
// (POST /tools/godot_open_mcp_particles_* → bridge handler → result envelope)
// is exercised by the headless Godot smoke; this file only asserts the contract
// advertised over stdio.
//
// Adapted from navigation-tools.test.ts (copy fidelity for the catalog-metadata
// test shape), with assertions specific to the particles pack's mutating vs
// read-only split and the Godot scalar allow-list property set.

import { test } from "node:test";
import assert from "node:assert/strict";

import { particlesDefaults } from "./particles-defaults.js";
import { particlesCreate } from "./particles-create.js";
import { particlesConfigure } from "./particles-configure.js";
import { particlesSetEmitting } from "./particles-set-emitting.js";
import { particlesGet } from "./particles-get.js";
import { groupFor, toolsInGroup } from "../capabilities/tool-groups.js";

const ALL_PARTICLES_TOOLS = [
  particlesDefaults,
  particlesCreate,
  particlesConfigure,
  particlesSetEmitting,
  particlesGet,
];

const MUTATING_TOOLS = [particlesCreate, particlesConfigure, particlesSetEmitting];

const READ_ONLY_TOOLS = [particlesDefaults, particlesGet];

// ---------------------------------------------------------------------------
// Names + group assignment
// ---------------------------------------------------------------------------

test("every particles tool name follows the godot_open_mcp_particles_* convention", () => {
  for (const t of ALL_PARTICLES_TOOLS) {
    assert.match(t.name, /^godot_open_mcp_particles_[a-z0-9_]+$/);
  }
});

test("every particles tool is assigned to the particles group", () => {
  for (const t of ALL_PARTICLES_TOOLS) {
    assert.equal(groupFor(t.name), "particles", `${t.name} must map to particles`);
  }
});

test("toolsInGroup(particles) returns exactly the five pack tools", () => {
  assert.deepEqual(toolsInGroup("particles"), [
    "godot_open_mcp_particles_configure",
    "godot_open_mcp_particles_create",
    "godot_open_mcp_particles_defaults",
    "godot_open_mcp_particles_get",
    "godot_open_mcp_particles_set_emitting",
  ]);
});

// ---------------------------------------------------------------------------
// Shared catalog-metadata invariants
// ---------------------------------------------------------------------------

test("every particles tool has a non-empty description", () => {
  for (const t of ALL_PARTICLES_TOOLS) {
    assert.ok(typeof t.description === "string");
    assert.ok((t.description ?? "").length > 0, `${t.name} description is empty`);
  }
});

test("every particles tool declares an object input schema with additionalProperties:false", () => {
  for (const t of ALL_PARTICLES_TOOLS) {
    assert.equal(t.inputSchema.type, "object", `${t.name} schema type`);
    assert.equal(t.inputSchema.additionalProperties, false, `${t.name} additionalProperties`);
  }
});

test("every particles tool description tells the agent to activate the particles group", () => {
  // The group is default-off; the description must surface the activation step so an
  // agent does not call a hidden tool blind. defaults and get are also in the group —
  // they must mention activation too (same contract as the navigation read-only tools).
  for (const t of ALL_PARTICLES_TOOLS) {
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

test("every mutating particles tool requires paths_hint", () => {
  for (const t of MUTATING_TOOLS) {
    assert.ok(
      (t.inputSchema.required ?? []).includes("paths_hint"),
      `${t.name} must require paths_hint`,
    );
  }
});

test("every mutating particles tool defaults gate to enforce", () => {
  for (const t of MUTATING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { default?: string }>).gate;
    assert.equal(gate?.default, "enforce", `${t.name} gate default must be enforce`);
  }
});

test("every mutating particles tool gate enum is [enforce, warn, off]", () => {
  for (const t of MUTATING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { enum?: string[] }>).gate;
    assert.deepEqual(gate?.enum, ["enforce", "warn", "off"], `${t.name} gate enum`);
  }
});

test("paths_hint is an array of strings on every mutating particles tool", () => {
  for (const t of MUTATING_TOOLS) {
    const ph = (t.inputSchema.properties as Record<string, { type?: string; items?: { type?: string } }>).paths_hint;
    assert.equal(ph?.type, "array", `${t.name} paths_hint type`);
    assert.equal(ph?.items?.type, "string", `${t.name} paths_hint items type`);
  }
});

// ---------------------------------------------------------------------------
// Read-only tools — no paths_hint, no gate
// ---------------------------------------------------------------------------

test("particles_defaults is read-only (no paths_hint, no gate)", () => {
  const props = particlesDefaults.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "defaults must not declare paths_hint");
  assert.equal(props.gate, undefined, "defaults must not declare gate");
  assert.deepEqual(Object.keys(props).sort(), ["dimension"]);
});

test("particles_get is read-only (no paths_hint, no gate)", () => {
  const props = particlesGet.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "get must not declare paths_hint");
  assert.equal(props.gate, undefined, "get must not declare gate");
  assert.deepEqual(Object.keys(props).sort(), ["node_path"]);
});

test("read-only particles tools do not list paths_hint as required", () => {
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

test("particles_defaults exposes only dimension", () => {
  const props = particlesDefaults.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["dimension"]);
  assert.deepEqual(
    (particlesDefaults.inputSchema.required ?? []).sort(),
    ["dimension"],
  );
});

test("particles_defaults dimension enum is [2d, 3d]", () => {
  const dim = (particlesDefaults.inputSchema.properties as Record<string, { enum?: string[] }>).dimension;
  assert.deepEqual(dim?.enum, ["2d", "3d"]);
});

test("particles_create exposes the dimension + create property set", () => {
  const props = particlesCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "dimension",
    "gate",
    "name",
    "parent_node_path",
    "paths_hint",
    "position",
    "process_material_path",
    "properties",
  ]);
  assert.deepEqual(
    (particlesCreate.inputSchema.required ?? []).sort(),
    ["dimension", "paths_hint"],
  );
});

test("particles_create dimension enum is [2d, 3d]", () => {
  const dim = (particlesCreate.inputSchema.properties as Record<string, { enum?: string[] }>).dimension;
  assert.deepEqual(dim?.enum, ["2d", "3d"]);
});

test("particles_configure exposes the clamped-scalar allow-list property set", () => {
  const props = particlesConfigure.inputSchema.properties as Record<string, unknown>;
  // emitting is intentionally NOT here — use particles_set_emitting.
  assert.deepEqual(Object.keys(props).sort(), [
    "amount",
    "explosiveness",
    "fixed_fps",
    "fract_delta",
    "gate",
    "interpolate",
    "lifetime",
    "local_coords",
    "node_path",
    "one_shot",
    "paths_hint",
    "preprocess",
    "randomness",
    "speed_scale",
  ]);
  assert.deepEqual(
    (particlesConfigure.inputSchema.required ?? []).sort(),
    ["node_path", "paths_hint"],
  );
});

test("particles_configure does NOT expose emitting (use set_emitting)", () => {
  // The P12.3 design decision §3: emitting is intentionally excluded from the configure
  // allow-list so set_emitting stays the single toggle path.
  const props = particlesConfigure.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.emitting, undefined, "configure must not expose emitting");
});

test("particles_configure amount + fixed_fps minimums are non-negative", () => {
  const props = particlesConfigure.inputSchema.properties as Record<
    string,
    { minimum?: number; exclusiveMinimum?: number }
  >;
  assert.equal(props.amount.minimum, 1, "amount minimum must be 1");
  assert.equal(props.fixed_fps.minimum, 0, "fixed_fps minimum must be 0");
});

test("particles_configure lifetime is strictly positive", () => {
  const props = particlesConfigure.inputSchema.properties as Record<
    string,
    { exclusiveMinimum?: number }
  >;
  assert.equal(props.lifetime.exclusiveMinimum, 0, "lifetime must be strictly positive");
});

test("particles_configure preprocess + speed_scale are non-negative", () => {
  const props = particlesConfigure.inputSchema.properties as Record<
    string,
    { minimum?: number }
  >;
  assert.equal(props.preprocess.minimum, 0, "preprocess must be non-negative");
  assert.equal(props.speed_scale.minimum, 0, "speed_scale must be non-negative");
});

test("particles_configure explosiveness + randomness are clamped to [0, 1]", () => {
  const props = particlesConfigure.inputSchema.properties as Record<
    string,
    { minimum?: number; maximum?: number }
  >;
  assert.equal(props.explosiveness.minimum, 0, "explosiveness minimum");
  assert.equal(props.explosiveness.maximum, 1, "explosiveness maximum");
  assert.equal(props.randomness.minimum, 0, "randomness minimum");
  assert.equal(props.randomness.maximum, 1, "randomness maximum");
});

test("particles_set_emitting exposes node_path + emitting + restart", () => {
  const props = particlesSetEmitting.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "emitting",
    "gate",
    "node_path",
    "paths_hint",
    "restart",
  ]);
  assert.deepEqual(
    (particlesSetEmitting.inputSchema.required ?? []).sort(),
    ["emitting", "node_path", "paths_hint"],
  );
});

test("particles_set_emitting restart defaults to false", () => {
  const restart = (particlesSetEmitting.inputSchema.properties as Record<string, { default?: boolean }>).restart;
  assert.equal(restart?.default, false);
});

test("particles_get exposes node_path only", () => {
  const props = particlesGet.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["node_path"]);
  assert.deepEqual(
    (particlesGet.inputSchema.required ?? []).sort(),
    ["node_path"],
  );
});

// ---------------------------------------------------------------------------
// Dimension enum is consistent across every dimension-accepting tool
// ---------------------------------------------------------------------------

test("every dimension-accepting tool pins the [2d, 3d] enum", () => {
  for (const t of [particlesDefaults, particlesCreate]) {
    const dim = (t.inputSchema.properties as Record<string, { enum?: string[] }>).dimension;
    assert.deepEqual(dim?.enum, ["2d", "3d"], `${t.name} dimension enum`);
  }
});
