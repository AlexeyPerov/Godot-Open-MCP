// P16.3 lighting pack — schema + group-assignment parity tests.
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// (name prefix, non-empty description, mutating-tool shape with paths_hint
// required + gate default enforce + enum vocabularies) and the group assignment
// (all four tools map to `lighting`). The live round-trip (POST
// /tools/godot_open_mcp_light_* → bridge handler → result envelope) is exercised
// by the headless Godot smoke; this file only asserts the contract advertised
// over stdio.
//
// Adapted from materials-tools.test.ts (copy fidelity for the catalog-metadata
// test shape), with assertions specific to the lighting pack's mutating-only
// surface (no read-only tools in this family) and the light-kind /
// environment-set input shapes.

import { test } from "node:test";
import assert from "node:assert/strict";

import { lightCreate } from "./light-create.js";
import { lightSet } from "./light-set.js";
import { lightModify } from "./light-modify.js";
import { environmentSet } from "./environment-set.js";
import { groupFor, toolsInGroup } from "../capabilities/tool-groups.js";

const ALL_LIGHTING_TOOLS = [lightCreate, lightSet, lightModify, environmentSet];

// ---------------------------------------------------------------------------
// Names + group assignment
// ---------------------------------------------------------------------------

test("every lighting tool name follows the godot_open_mcp_light_* / environment_* convention", () => {
  for (const t of ALL_LIGHTING_TOOLS) {
    assert.match(t.name, /^godot_open_mcp_(light|environment)_[a-z0-9_]+$/);
  }
});

test("every lighting tool is assigned to the lighting group", () => {
  for (const t of ALL_LIGHTING_TOOLS) {
    assert.equal(groupFor(t.name), "lighting", `${t.name} must map to lighting`);
  }
});

test("toolsInGroup(lighting) returns exactly the four pack tools", () => {
  assert.deepEqual(toolsInGroup("lighting"), [
    "godot_open_mcp_environment_set",
    "godot_open_mcp_light_create",
    "godot_open_mcp_light_modify",
    "godot_open_mcp_light_set",
  ]);
});

// ---------------------------------------------------------------------------
// Shared catalog-metadata invariants
// ---------------------------------------------------------------------------

test("every lighting tool has a non-empty description", () => {
  for (const t of ALL_LIGHTING_TOOLS) {
    assert.ok(typeof t.description === "string");
    assert.ok((t.description ?? "").length > 0, `${t.name} description is empty`);
  }
});

test("every lighting tool declares an object input schema with additionalProperties:false", () => {
  for (const t of ALL_LIGHTING_TOOLS) {
    assert.equal(t.inputSchema.type, "object", `${t.name} schema type`);
    assert.equal(t.inputSchema.additionalProperties, false, `${t.name} additionalProperties`);
  }
});

test("every lighting tool description tells the agent to activate the lighting group", () => {
  // The group is default-off; the description must surface the activation step so an
  // agent does not call a hidden tool blind. All four tools are in the group — they
  // must mention activation.
  for (const t of ALL_LIGHTING_TOOLS) {
    assert.match(
      t.description ?? "",
      /activate the group with manage_tools/,
      `${t.name} description must mention manage_tools activation`,
    );
  }
});

// ---------------------------------------------------------------------------
// Shared mutating shape — every lighting tool is mutating
// ---------------------------------------------------------------------------

test("every lighting tool requires paths_hint", () => {
  for (const t of ALL_LIGHTING_TOOLS) {
    assert.ok(
      (t.inputSchema.required ?? []).includes("paths_hint"),
      `${t.name} must require paths_hint`,
    );
  }
});

test("every lighting tool gate default is enforce", () => {
  for (const t of ALL_LIGHTING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { default?: string }>).gate;
    assert.equal(gate?.default, "enforce", `${t.name} gate default must be enforce`);
  }
});

test("every lighting tool gate enum is [enforce, warn, off]", () => {
  for (const t of ALL_LIGHTING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { enum?: string[] }>).gate;
    assert.deepEqual(gate?.enum, ["enforce", "warn", "off"], `${t.name} gate enum`);
  }
});

test("every lighting tool paths_hint is an array of strings", () => {
  for (const t of ALL_LIGHTING_TOOLS) {
    const ph = (t.inputSchema.properties as Record<string, { type?: string; items?: { type?: string } }>).paths_hint;
    assert.equal(ph?.type, "array", `${t.name} paths_hint type`);
    assert.equal(ph?.items?.type, "string", `${t.name} paths_hint items type`);
  }
});

// ---------------------------------------------------------------------------
// light_create — mutating shape
// ---------------------------------------------------------------------------

test("light_create requires kind + paths_hint", () => {
  assert.deepEqual(
    (lightCreate.inputSchema.required ?? []).sort(),
    ["kind", "paths_hint"],
  );
});

test("light_create kind enum is exactly the five creatable light families", () => {
  const kind = (lightCreate.inputSchema.properties as Record<string, { enum?: string[] }>).kind;
  assert.deepEqual(
    kind?.enum,
    ["directional3d", "omni3d", "spot3d", "directional2d", "point2d"],
    "create kind enum",
  );
});

test("light_create exposes the mutating property set", () => {
  const props = lightCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(
    Object.keys(props).sort(),
    [
      "attenuation",
      "color",
      "energy",
      "gate",
      "kind",
      "name",
      "parent_node_path",
      "paths_hint",
      "position",
      "range",
      "shadow_enabled",
      "spot_angle",
    ],
  );
});

// ---------------------------------------------------------------------------
// light_set — mutating shape
// ---------------------------------------------------------------------------

test("light_set requires node_path + field + value + paths_hint", () => {
  assert.deepEqual(
    (lightSet.inputSchema.required ?? []).sort(),
    ["field", "node_path", "paths_hint", "value"],
  );
});

test("light_set field enum is exactly the six allow-listed light scalars", () => {
  const field = (lightSet.inputSchema.properties as Record<string, { enum?: string[] }>).field;
  assert.deepEqual(
    field?.enum,
    ["color", "energy", "range", "spot_angle", "attenuation", "shadow_enabled"],
    "set field enum",
  );
});

test("light_set value is an untyped (any JSON type) property", () => {
  // value is any JSON type re-parsed into the field's Godot type — no type
  // constraint, just a description. The schema omits `type` so any JSON value is
  // accepted.
  const value = (lightSet.inputSchema.properties as Record<string, { type?: string }>).value;
  assert.equal(value?.type, undefined, "set value must be untyped (any JSON)");
});

test("light_set exposes the mutating property set", () => {
  const props = lightSet.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(
    Object.keys(props).sort(),
    ["field", "gate", "node_path", "paths_hint", "value"],
  );
});

// ---------------------------------------------------------------------------
// light_modify — mutating shape
// ---------------------------------------------------------------------------

test("light_modify requires node_path + fields + paths_hint", () => {
  assert.deepEqual(
    (lightModify.inputSchema.required ?? []).sort(),
    ["fields", "node_path", "paths_hint"],
  );
});

test("light_modify fields is an object with additionalProperties:true (free-form map)", () => {
  // fields is a free-form {field → value} map; the handler validates each key
  // against the allow-list. additionalProperties:true so any field name is
  // accepted at the schema layer (the handler surfaces unsupported_field).
  const fields = (lightModify.inputSchema.properties as Record<string, { type?: string; additionalProperties?: boolean }>).fields;
  assert.equal(fields?.type, "object", "modify fields type");
  assert.equal(fields?.additionalProperties, true, "modify fields additionalProperties");
});

test("light_modify exposes the mutating property set", () => {
  const props = lightModify.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(
    Object.keys(props).sort(),
    ["fields", "gate", "node_path", "paths_hint"],
  );
});

// ---------------------------------------------------------------------------
// environment_set — mutating shape
// ---------------------------------------------------------------------------

test("environment_set requires environment_path + paths_hint", () => {
  assert.deepEqual(
    (environmentSet.inputSchema.required ?? []).sort(),
    ["environment_path", "paths_hint"],
  );
});

test("environment_set create_if_missing defaults to false", () => {
  const cim = (environmentSet.inputSchema.properties as Record<string, { default?: boolean }>).create_if_missing;
  assert.equal(cim?.default, false, "environment_set create_if_missing default");
});

test("environment_set exposes the mutating property set", () => {
  const props = environmentSet.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(
    Object.keys(props).sort(),
    ["create_if_missing", "environment_path", "gate", "node_path", "paths_hint"],
  );
});
