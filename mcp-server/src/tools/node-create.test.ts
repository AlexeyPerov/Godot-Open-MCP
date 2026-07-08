// `godot_open_mcp_node_create` tool-definition tests (P2.3).
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// — name prefix per ADR-003, non-empty description, the Godot-adapted property
// set (name / type_class_name / instance_scene_path / parent_node_path /
// position / rotation / scale + forward-compat paths_hint / gate), and the
// `additionalProperties: false` guard. The live round-trip (POST
// /tools/godot_open_mcp_node_create → bridge handler → NodeData envelope) is
// exercised against a local HTTP stub in live-client.test.ts; this file only
// asserts the contract advertised over stdio.
//
// Adapted from node-find.test.ts (copy fidelity for the catalog-metadata test
// shape), with property-set assertions specific to the node_create schema and
// the Unity deltas documented in node-create.ts.

import { test } from "node:test";
import assert from "node:assert/strict";
import { nodeCreate } from "./node-create.js";

test("node_create tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(nodeCreate.name, "godot_open_mcp_node_create");
  assert.match(nodeCreate.name, /^godot_open_mcp_/);
});

test("node_create tool has a non-empty description", () => {
  assert.ok(typeof nodeCreate.description === "string");
  assert.ok((nodeCreate.description ?? "").length > 0);
});

test("node_create tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(nodeCreate.inputSchema.type, "object");
  assert.equal(nodeCreate.inputSchema.additionalProperties, false);
});

test("node_create tool exposes the Godot-adapted property set", () => {
  // The property set is the Godot adaptation of Unity's gameobject_create:
  // name (optional, unlike Unity), type_class_name + instance_scene_path
  // (replace Unity's primitive_type), parent_node_path (renamed from
  // parent_path), position/rotation/scale (kept), plus the forward-compat
  // mutating-tool fields paths_hint and gate. Unity's local_space is dropped
  // (Godot transforms are parent-local by default) and primitive_type is
  // absent (Godot has no Cube/Sphere primitives).
  const props = nodeCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "instance_scene_path",
    "name",
    "parent_node_path",
    "paths_hint",
    "position",
    "rotation",
    "scale",
    "type_class_name",
  ]);
});

test("node_create tool does NOT expose Unity-only fields", () => {
  // Guards against an accidental copy-paste from Unity's gameobject_create
  // schema bringing back primitive_type / parent_path / local_space.
  const props = nodeCreate.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.primitive_type, undefined, "Godot has no Unity primitives");
  assert.equal(props.parent_path, undefined, "renamed to parent_node_path");
  assert.equal(props.local_space, undefined, "Godot transforms are parent-local by default");
});

test("node_create gate defaults to off", () => {
  // Default 'off' because the gate flow is not wired yet (P3.5). Pinned so a
  // future change does not silently flip the advertised default before the
  // gate actually lands.
  const props = nodeCreate.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.equal(props.gate.default, "off");
  assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
});

test("node_create name field is optional (no required array)", () => {
  // Unlike Unity's gameobject_create (where name is required), Godot lets a
  // created node take the type's auto-name. Pinned: the schema must not list
  // any required field.
  assert.equal(nodeCreate.inputSchema.required, undefined);
});

test("node_create paths_hint is an array of strings", () => {
  // Forward-compat mutating-tool field; present so the call shape is stable
  // across phases even though the bridge is a no-op for it until P3.5.
  const props = nodeCreate.inputSchema.properties as Record<
    string,
    { type?: string; items?: { type?: string } }
  >;
  assert.equal(props.paths_hint.type, "array");
  assert.equal(props.paths_hint.items?.type, "string");
});
