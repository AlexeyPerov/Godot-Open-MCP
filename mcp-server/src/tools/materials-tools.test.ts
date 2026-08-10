// P16.2 materials/shaders pack — schema + group-assignment parity tests.
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// (name prefix, non-empty description, read-only tool shape without paths_hint,
// mutating-tool shape with paths_hint required + gate default enforce + enum
// vocabularies) and the group assignment (all five tools map to `materials`).
// The live round-trip (POST /tools/godot_open_mcp_material_* → bridge handler →
// result envelope) is exercised by the headless Godot smoke; this file only
// asserts the contract advertised over stdio.
//
// Adapted from settings-tools.test.ts (copy fidelity for the catalog-metadata
// test shape), with assertions specific to the materials pack's read-only vs
// mutating split, the material-kind enum, and the property value type-fidelity
// contract.

import { test } from "node:test";
import assert from "node:assert/strict";

import { materialCreate } from "./material-create.js";
import { materialGetProperties } from "./material-get-properties.js";
import { materialSetProperty } from "./material-set-property.js";
import { materialSetShader } from "./material-set-shader.js";
import { shaderGetData } from "./shader-get-data.js";
import { groupFor, toolsInGroup } from "../capabilities/tool-groups.js";

const ALL_MATERIALS_TOOLS = [
  materialCreate,
  materialGetProperties,
  materialSetProperty,
  materialSetShader,
  shaderGetData,
];

// ---------------------------------------------------------------------------
// Names + group assignment
// ---------------------------------------------------------------------------

test("every materials tool name follows the godot_open_mcp_material_* / shader_* convention", () => {
  for (const t of ALL_MATERIALS_TOOLS) {
    assert.match(t.name, /^godot_open_mcp_(material|shader)_[a-z0-9_]+$/);
  }
});

test("every materials tool is assigned to the materials group", () => {
  for (const t of ALL_MATERIALS_TOOLS) {
    assert.equal(groupFor(t.name), "materials", `${t.name} must map to materials`);
  }
});

test("toolsInGroup(materials) returns exactly the five pack tools", () => {
  assert.deepEqual(toolsInGroup("materials"), [
    "godot_open_mcp_material_create",
    "godot_open_mcp_material_get_properties",
    "godot_open_mcp_material_set_property",
    "godot_open_mcp_material_set_shader",
    "godot_open_mcp_shader_get_data",
  ]);
});

// ---------------------------------------------------------------------------
// Shared catalog-metadata invariants
// ---------------------------------------------------------------------------

test("every materials tool has a non-empty description", () => {
  for (const t of ALL_MATERIALS_TOOLS) {
    assert.ok(typeof t.description === "string");
    assert.ok((t.description ?? "").length > 0, `${t.name} description is empty`);
  }
});

test("every materials tool declares an object input schema with additionalProperties:false", () => {
  for (const t of ALL_MATERIALS_TOOLS) {
    assert.equal(t.inputSchema.type, "object", `${t.name} schema type`);
    assert.equal(t.inputSchema.additionalProperties, false, `${t.name} additionalProperties`);
  }
});

test("every materials tool description tells the agent to activate the materials group", () => {
  // The group is default-off; the description must surface the activation step so an
  // agent does not call a hidden tool blind. All five tools are in the group — they
  // must mention activation.
  for (const t of ALL_MATERIALS_TOOLS) {
    assert.match(
      t.description ?? "",
      /activate the group with manage_tools/,
      `${t.name} description must mention manage_tools activation`,
    );
  }
});

// ---------------------------------------------------------------------------
// material_create — mutating shape
// ---------------------------------------------------------------------------

test("material_create is mutating (paths_hint required, gate default enforce)", () => {
  assert.ok(
    (materialCreate.inputSchema.required ?? []).includes("paths_hint"),
    "create must require paths_hint",
  );
  const gate = (materialCreate.inputSchema.properties as Record<string, { default?: string }>).gate;
  assert.equal(gate?.default, "enforce", "create gate default must be enforce");
});

test("material_create gate enum is [enforce, warn, off]", () => {
  const gate = (materialCreate.inputSchema.properties as Record<string, { enum?: string[] }>).gate;
  assert.deepEqual(gate?.enum, ["enforce", "warn", "off"], "create gate enum");
});

test("material_create paths_hint is an array of strings", () => {
  const ph = (materialCreate.inputSchema.properties as Record<string, { type?: string; items?: { type?: string } }>).paths_hint;
  assert.equal(ph?.type, "array", "create paths_hint type");
  assert.equal(ph?.items?.type, "string", "create paths_hint items type");
});

test("material_create requires resource_path + kind + paths_hint", () => {
  assert.deepEqual(
    (materialCreate.inputSchema.required ?? []).sort(),
    ["kind", "paths_hint", "resource_path"],
  );
});

test("material_create kind enum is exactly the three creatable families", () => {
  const kind = (materialCreate.inputSchema.properties as Record<string, { enum?: string[] }>).kind;
  assert.deepEqual(kind?.enum, ["standard", "orm", "shader"], "create kind enum");
});

test("material_create exposes the mutating property set", () => {
  const props = materialCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["gate", "kind", "overwrite", "paths_hint", "resource_path", "shader_path"]);
});

// ---------------------------------------------------------------------------
// material_get_properties — read-only shape
// ---------------------------------------------------------------------------

test("material_get_properties is read-only (no paths_hint, no gate)", () => {
  const props = materialGetProperties.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "get_properties must not declare paths_hint");
  assert.equal(props.gate, undefined, "get_properties must not declare gate");
  assert.deepEqual(Object.keys(props).sort(), ["resource_path"]);
});

test("material_get_properties exposes only resource_path", () => {
  const props = materialGetProperties.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["resource_path"]);
  assert.deepEqual(
    (materialGetProperties.inputSchema.required ?? []).sort(),
    ["resource_path"],
  );
});

// ---------------------------------------------------------------------------
// material_set_property — mutating shape
// ---------------------------------------------------------------------------

test("material_set_property is mutating (paths_hint required, gate default enforce)", () => {
  assert.ok(
    (materialSetProperty.inputSchema.required ?? []).includes("paths_hint"),
    "set_property must require paths_hint",
  );
  const gate = (materialSetProperty.inputSchema.properties as Record<string, { default?: string }>).gate;
  assert.equal(gate?.default, "enforce", "set_property gate default must be enforce");
});

test("material_set_property requires resource_path + property + value + paths_hint", () => {
  assert.deepEqual(
    (materialSetProperty.inputSchema.required ?? []).sort(),
    ["paths_hint", "property", "resource_path", "value"],
  );
});

test("material_set_property value is an untyped (any JSON type) property", () => {
  // value is any JSON type re-parsed into the property's Variant type — no type
  // constraint, just a description. The schema omits `type` so any JSON value is
  // accepted.
  const value = (materialSetProperty.inputSchema.properties as Record<string, { type?: string }>).value;
  assert.equal(value?.type, undefined, "set_property value must be untyped (any JSON)");
});

test("material_set_property exposes the mutating property set", () => {
  const props = materialSetProperty.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["gate", "paths_hint", "property", "resource_path", "value"]);
});

// ---------------------------------------------------------------------------
// material_set_shader — mutating shape
// ---------------------------------------------------------------------------

test("material_set_shader is mutating (paths_hint required, gate default enforce)", () => {
  assert.ok(
    (materialSetShader.inputSchema.required ?? []).includes("paths_hint"),
    "set_shader must require paths_hint",
  );
  const gate = (materialSetShader.inputSchema.properties as Record<string, { default?: string }>).gate;
  assert.equal(gate?.default, "enforce", "set_shader gate default must be enforce");
});

test("material_set_shader requires resource_path + shader_path + paths_hint", () => {
  assert.deepEqual(
    (materialSetShader.inputSchema.required ?? []).sort(),
    ["paths_hint", "resource_path", "shader_path"],
  );
});

test("material_set_shader exposes the mutating property set", () => {
  const props = materialSetShader.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["gate", "paths_hint", "resource_path", "shader_path"]);
});

// ---------------------------------------------------------------------------
// shader_get_data — read-only shape
// ---------------------------------------------------------------------------

test("shader_get_data is read-only (no paths_hint, no gate)", () => {
  const props = shaderGetData.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "get_data must not declare paths_hint");
  assert.equal(props.gate, undefined, "get_data must not declare gate");
  assert.deepEqual(Object.keys(props).sort(), ["shader_path"]);
});

test("shader_get_data exposes only shader_path", () => {
  const props = shaderGetData.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["shader_path"]);
  assert.deepEqual(
    (shaderGetData.inputSchema.required ?? []).sort(),
    ["shader_path"],
  );
});
