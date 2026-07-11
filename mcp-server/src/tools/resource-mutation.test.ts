// `godot_open_mcp_resource_create` / `resource_modify` tool-definition tests (P4.2).
// Pins the catalog metadata the MCP ListTools response advertises to AI clients for the resource
// mutators — name prefix per ADR-003, non-empty descriptions, the Godot-adapted property sets, the
// `additionalProperties: false` guards, and the mutating-tool gate surface (paths_hint + gate). The
// live round-trip (POST /tools/godot_open_mcp_resource_{create,modify} → bridge handler →
// ResourceSaver → identity/gate envelope) is exercised against a local HTTP stub + headless Godot
// smoke; this file only asserts the contracts advertised over stdio.
//
// Adapted from resource-tools.test.ts (copy fidelity for the catalog-metadata test shape), with
// property-set assertions specific to each P4.2 schema. The key contrast with the P4.1 read-only
// tools: these mutators MUST declare paths_hint + gate (the read-only tests explicitly assert those
// fields are absent).

import { test } from "node:test";
import assert from "node:assert/strict";
import { resourceCreate } from "./resource-create.js";
import { resourceModify } from "./resource-modify.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// resource_create — mutating (has paths_hint + gate, default enforce).
// ---------------------------------------------------------------------------

test("resource_create tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(resourceCreate.name, "godot_open_mcp_resource_create");
  assert.match(resourceCreate.name, /^godot_open_mcp_/);
});

test("resource_create tool has a non-empty description", () => {
  assert.ok(typeof resourceCreate.description === "string");
  assert.ok((resourceCreate.description ?? "").length > 0);
});

test("resource_create tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(resourceCreate.inputSchema.type, "object");
  assert.equal(resourceCreate.inputSchema.additionalProperties, false);
});

test("resource_create exposes the Godot-adapted property set", () => {
  // resource_path (destination) + type_class_name (ClassDB class) + properties (initial patches) +
  // paths_hint (gate scope) + gate (mode). Unity's type_name/asset_path/fields become
  // type_class_name/resource_path/properties.
  const props = resourceCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "paths_hint",
    "properties",
    "resource_path",
    "type_class_name",
  ]);
});

test("resource_create DOES expose paths_hint and gate (mutating tool)", () => {
  // Contrast with the P4.1 read-only tools that explicitly assert these are absent.
  const props = resourceCreate.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.paths_hint !== undefined, "paths_hint required for mutating tool");
  assert.ok(props.gate !== undefined, "gate surface required for mutating tool");
});

test("resource_create type_class_name defaults to Resource", () => {
  const props = resourceCreate.inputSchema.properties as Record<
    string,
    { default?: string }
  >;
  assert.equal(props.type_class_name.default, "Resource");
});

test("resource_create gate defaults to enforce", () => {
  const props = resourceCreate.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.equal(props.gate.default, "enforce");
  assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
});

test("resource_create requires resource_path and paths_hint", () => {
  assert.deepEqual(resourceCreate.inputSchema.required, ["resource_path", "paths_hint"]);
});

test("resource_create does NOT expose Unity-only fields", () => {
  // Guards against accidental copy-paste from Unity's scriptableobject-create schema.
  const props = resourceCreate.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.type_name, undefined, "renamed to type_class_name");
  assert.equal(props.asset_path, undefined, "renamed to resource_path");
  assert.equal(props.fields, undefined, "renamed to properties");
  assert.equal(props.assembly_name, undefined, "no assembly concept in Godot");
  assert.equal(props.allow_static, undefined, "no reflection concept in Godot");
});

test("resource_create properties items have path + value", () => {
  const props = resourceCreate.inputSchema.properties as Record<string, any>;
  const items = props.properties.items;
  assert.equal(items.type, "object");
  assert.deepEqual(items.required, ["path", "value"]);
  assert.equal(items.additionalProperties, false);
});

// ---------------------------------------------------------------------------
// resource_modify — mutating (has paths_hint + gate, default enforce).
// ---------------------------------------------------------------------------

test("resource_modify tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(resourceModify.name, "godot_open_mcp_resource_modify");
  assert.match(resourceModify.name, /^godot_open_mcp_/);
});

test("resource_modify tool has a non-empty description", () => {
  assert.ok(typeof resourceModify.description === "string");
  assert.ok((resourceModify.description ?? "").length > 0);
});

test("resource_modify tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(resourceModify.inputSchema.type, "object");
  assert.equal(resourceModify.inputSchema.additionalProperties, false);
});

test("resource_modify exposes the Godot-adapted property set", () => {
  // resource_path (target) + patches (property-path assignments) + paths_hint (gate scope) +
  // gate (mode). Unity's instance_id/asset_path/fields become resource_path/patches.
  const props = resourceModify.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "patches",
    "paths_hint",
    "resource_path",
  ]);
});

test("resource_modify DOES expose paths_hint and gate (mutating tool)", () => {
  const props = resourceModify.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.paths_hint !== undefined, "paths_hint required for mutating tool");
  assert.ok(props.gate !== undefined, "gate surface required for mutating tool");
});

test("resource_modify gate defaults to enforce", () => {
  const props = resourceModify.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.equal(props.gate.default, "enforce");
  assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
});

test("resource_modify requires resource_path, patches, and paths_hint", () => {
  assert.deepEqual(resourceModify.inputSchema.required, ["resource_path", "patches", "paths_hint"]);
});

test("resource_modify patches is non-empty (minItems:1)", () => {
  const props = resourceModify.inputSchema.properties as Record<string, any>;
  assert.equal(props.patches.minItems, 1);
});

test("resource_modify does NOT expose Unity-only fields", () => {
  const props = resourceModify.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.instance_id, undefined, "no instance_id — Godot resources addressed by path");
  assert.equal(props.asset_path, undefined, "renamed to resource_path");
  assert.equal(props.fields, undefined, "renamed to patches");
  assert.equal(props.allow_static, undefined, "no reflection concept in Godot");
});

test("resource_modify patches items have path + value", () => {
  const props = resourceModify.inputSchema.properties as Record<string, any>;
  const items = props.patches.items;
  assert.equal(items.type, "object");
  assert.deepEqual(items.required, ["path", "value"]);
  assert.equal(items.additionalProperties, false);
});

// ---------------------------------------------------------------------------
// Registration.
// ---------------------------------------------------------------------------

test("ALL_TOOLS registers both P4.2 resource mutator tools", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(names.includes("godot_open_mcp_resource_create"), "resource_create missing from ALL_TOOLS");
  assert.ok(names.includes("godot_open_mcp_resource_modify"), "resource_modify missing from ALL_TOOLS");
});
