// P12.5 CSG domain pack — schema + group-assignment parity tests.
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// (name prefix, non-empty description, mutating-tool shape with paths_hint
// required + gate default enforce, read-only tool shape without paths_hint) and
// the group assignment (all seven tools map to `csg`). The live round-trip
// (POST /tools/godot_open_mcp_csg_* → bridge handler → result envelope) is
// exercised by the headless Godot smoke; this file only asserts the contract
// advertised over stdio.
//
// Adapted from particles-tools.test.ts (copy fidelity for the catalog-metadata
// test shape), with assertions specific to the CSG pack's mutating vs read-only
// split, the kind-agnostic defaults tool, and the kind-specific scalar allow-list
// per create tool.

import { test } from "node:test";
import assert from "node:assert/strict";

import { csgDefaults } from "./csg-defaults.js";
import { csgBoxCreate } from "./csg-box-create.js";
import { csgSphereCreate } from "./csg-sphere-create.js";
import { csgCylinderCreate } from "./csg-cylinder-create.js";
import { csgCombinerCreate } from "./csg-combiner-create.js";
import { csgSetOperation } from "./csg-set-operation.js";
import { csgGet } from "./csg-get.js";
import { groupFor, toolsInGroup } from "../capabilities/tool-groups.js";

const ALL_CSG_TOOLS = [
  csgDefaults,
  csgBoxCreate,
  csgSphereCreate,
  csgCylinderCreate,
  csgCombinerCreate,
  csgSetOperation,
  csgGet,
];

const MUTATING_TOOLS = [
  csgBoxCreate,
  csgSphereCreate,
  csgCylinderCreate,
  csgCombinerCreate,
  csgSetOperation,
];

const READ_ONLY_TOOLS = [csgDefaults, csgGet];

// ---------------------------------------------------------------------------
// Names + group assignment
// ---------------------------------------------------------------------------

test("every csg tool name follows the godot_open_mcp_csg_* convention", () => {
  for (const t of ALL_CSG_TOOLS) {
    assert.match(t.name, /^godot_open_mcp_csg_[a-z0-9_]+$/);
  }
});

test("every csg tool is assigned to the csg group", () => {
  for (const t of ALL_CSG_TOOLS) {
    assert.equal(groupFor(t.name), "csg", `${t.name} must map to csg`);
  }
});

test("toolsInGroup(csg) returns exactly the seven pack tools", () => {
  assert.deepEqual(toolsInGroup("csg"), [
    "godot_open_mcp_csg_box_create",
    "godot_open_mcp_csg_combiner_create",
    "godot_open_mcp_csg_cylinder_create",
    "godot_open_mcp_csg_defaults",
    "godot_open_mcp_csg_get",
    "godot_open_mcp_csg_set_operation",
    "godot_open_mcp_csg_sphere_create",
  ]);
});

// ---------------------------------------------------------------------------
// Shared catalog-metadata invariants
// ---------------------------------------------------------------------------

test("every csg tool has a non-empty description", () => {
  for (const t of ALL_CSG_TOOLS) {
    assert.ok(typeof t.description === "string");
    assert.ok((t.description ?? "").length > 0, `${t.name} description is empty`);
  }
});

test("every csg tool declares an object input schema with additionalProperties:false", () => {
  for (const t of ALL_CSG_TOOLS) {
    assert.equal(t.inputSchema.type, "object", `${t.name} schema type`);
    assert.equal(t.inputSchema.additionalProperties, false, `${t.name} additionalProperties`);
  }
});

test("every csg tool description tells the agent to activate the csg group", () => {
  // The group is default-off; the description must surface the activation step so an
  // agent does not call a hidden tool blind. defaults and get are also in the group —
  // they must mention activation too (same contract as the particles read-only tools).
  for (const t of ALL_CSG_TOOLS) {
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

test("every mutating csg tool requires paths_hint", () => {
  for (const t of MUTATING_TOOLS) {
    assert.ok(
      (t.inputSchema.required ?? []).includes("paths_hint"),
      `${t.name} must require paths_hint`,
    );
  }
});

test("every mutating csg tool defaults gate to enforce", () => {
  for (const t of MUTATING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { default?: string }>).gate;
    assert.equal(gate?.default, "enforce", `${t.name} gate default must be enforce`);
  }
});

test("every mutating csg tool gate enum is [enforce, warn, off]", () => {
  for (const t of MUTATING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { enum?: string[] }>).gate;
    assert.deepEqual(gate?.enum, ["enforce", "warn", "off"], `${t.name} gate enum`);
  }
});

test("paths_hint is an array of strings on every mutating csg tool", () => {
  for (const t of MUTATING_TOOLS) {
    const ph = (t.inputSchema.properties as Record<string, { type?: string; items?: { type?: string } }>).paths_hint;
    assert.equal(ph?.type, "array", `${t.name} paths_hint type`);
    assert.equal(ph?.items?.type, "string", `${t.name} paths_hint items type`);
  }
});

// ---------------------------------------------------------------------------
// Read-only tools — no paths_hint, no gate
// ---------------------------------------------------------------------------

test("csg_defaults is read-only (no paths_hint, no gate)", () => {
  const props = csgDefaults.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "defaults must not declare paths_hint");
  assert.equal(props.gate, undefined, "defaults must not declare gate");
  assert.deepEqual(Object.keys(props).sort(), ["kind"]);
});

test("csg_get is read-only (no paths_hint, no gate)", () => {
  const props = csgGet.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "get must not declare paths_hint");
  assert.equal(props.gate, undefined, "get must not declare gate");
  assert.deepEqual(Object.keys(props).sort(), ["node_path"]);
});

test("read-only csg tools do not list paths_hint as required", () => {
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

test("csg_defaults exposes only kind", () => {
  const props = csgDefaults.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["kind"]);
  assert.deepEqual(
    (csgDefaults.inputSchema.required ?? []).sort(),
    ["kind"],
  );
});

test("csg_defaults kind enum is [box, sphere, cylinder, combiner]", () => {
  const kind = (csgDefaults.inputSchema.properties as Record<string, { enum?: string[] }>).kind;
  assert.deepEqual(kind?.enum, ["box", "sphere", "cylinder", "combiner"]);
});

test("csg_box_create exposes the box create property set", () => {
  const props = csgBoxCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "name",
    "operation",
    "parent_node_path",
    "paths_hint",
    "position",
    "size",
  ]);
  assert.deepEqual(
    (csgBoxCreate.inputSchema.required ?? []).sort(),
    ["paths_hint"],
  );
});

test("csg_sphere_create exposes the sphere create property set", () => {
  const props = csgSphereCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "name",
    "operation",
    "parent_node_path",
    "paths_hint",
    "position",
    "radial_segments",
    "radius",
    "rings",
    "smooth_faces",
  ]);
  assert.deepEqual(
    (csgSphereCreate.inputSchema.required ?? []).sort(),
    ["paths_hint"],
  );
});

test("csg_cylinder_create exposes the cylinder create property set", () => {
  const props = csgCylinderCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "cone",
    "gate",
    "height",
    "name",
    "operation",
    "parent_node_path",
    "paths_hint",
    "position",
    "radius",
    "sides",
    "smooth_faces",
  ]);
  assert.deepEqual(
    (csgCylinderCreate.inputSchema.required ?? []).sort(),
    ["paths_hint"],
  );
});

test("csg_combiner_create exposes the combiner create property set (no primitive scalars)", () => {
  const props = csgCombinerCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "name",
    "operation",
    "parent_node_path",
    "paths_hint",
    "position",
  ]);
  assert.deepEqual(
    (csgCombinerCreate.inputSchema.required ?? []).sort(),
    ["paths_hint"],
  );
});

test("csg_set_operation exposes node_path + operation only (+ gate/paths_hint)", () => {
  const props = csgSetOperation.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "node_path",
    "operation",
    "paths_hint",
  ]);
  assert.deepEqual(
    (csgSetOperation.inputSchema.required ?? []).sort(),
    ["node_path", "operation", "paths_hint"],
  );
});

test("csg_get exposes node_path only", () => {
  const props = csgGet.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["node_path"]);
  assert.deepEqual(
    (csgGet.inputSchema.required ?? []).sort(),
    ["node_path"],
  );
});

// ---------------------------------------------------------------------------
// Operation enum is consistent across every operation-accepting tool
// ---------------------------------------------------------------------------

test("every operation-accepting tool pins the [union, intersection, subtraction] enum", () => {
  for (const t of [csgBoxCreate, csgSphereCreate, csgCylinderCreate, csgCombinerCreate, csgSetOperation]) {
    const op = (t.inputSchema.properties as Record<string, { enum?: string[] }>).operation;
    assert.deepEqual(op?.enum, ["union", "intersection", "subtraction"], `${t.name} operation enum`);
  }
});

// ---------------------------------------------------------------------------
// Positive-range clamping surface (mirror of particles' clamp-bound tests)
// ---------------------------------------------------------------------------

test("csg_box_create size is a string (x,y,z) — clamped per component in the handler", () => {
  const size = (csgBoxCreate.inputSchema.properties as Record<string, { type?: string }>).size;
  // Size is an 'x,y,z' string (same vector style as node_create / navigation positions), not an
  // object — the handler parses it via TryParseVector3 and clamps each component in the handler.
  assert.equal(size?.type, "string");
});

test("csg_sphere_create + csg_cylinder_create radius / height are strictly positive", () => {
  const sphere = (csgSphereCreate.inputSchema.properties as Record<string, { exclusiveMinimum?: number }>).radius;
  const cylProps = csgCylinderCreate.inputSchema.properties as Record<string, { exclusiveMinimum?: number }>;
  const cylRadius = cylProps.radius;
  const cylHeight = cylProps.height;
  assert.equal(sphere.exclusiveMinimum, 0, "sphere radius must be strictly positive");
  assert.equal(cylRadius.exclusiveMinimum, 0, "cylinder radius must be strictly positive");
  assert.equal(cylHeight.exclusiveMinimum, 0, "cylinder height must be strictly positive");
});

test("csg sphere/cylinder segment counts (radial_segments / rings / sides) minimum is 3", () => {
  const sphere = csgSphereCreate.inputSchema.properties as Record<string, { minimum?: number }>;
  const cyl = csgCylinderCreate.inputSchema.properties as Record<string, { minimum?: number }>;
  assert.equal(sphere.radial_segments.minimum, 3, "sphere radial_segments minimum must be 3");
  assert.equal(sphere.rings.minimum, 3, "sphere rings minimum must be 3");
  assert.equal(cyl.sides.minimum, 3, "cylinder sides minimum must be 3");
});
