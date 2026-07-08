// `godot_open_mcp_node_modify` tool-definition tests (P2.4).
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// — name prefix per ADR-003, non-empty description, the Godot-adapted property
// set (node_path + node_paths + properties map + position/rotation/scale/name
// + forward-compat paths_hint / gate), and the `additionalProperties: false`
// guard. The live round-trip (POST /tools/godot_open_mcp_node_modify → bridge
// handler → NodeData[] + warnings envelope) is exercised against a local HTTP
// stub in live-client.test.ts; this file only asserts the contract advertised
// over stdio.
//
// Adapted from node-create.test.ts (copy fidelity for the catalog-metadata test
// shape), with property-set assertions specific to the node_modify schema and
// the Unity deltas documented in node-modify.ts.

import { test } from "node:test";
import assert from "node:assert/strict";
import { nodeModify } from "./node-modify.js";

test("node_modify tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(nodeModify.name, "godot_open_mcp_node_modify");
  assert.match(nodeModify.name, /^godot_open_mcp_/);
});

test("node_modify tool has a non-empty description", () => {
  assert.ok(typeof nodeModify.description === "string");
  assert.ok((nodeModify.description ?? "").length > 0);
});

test("node_modify tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(nodeModify.inputSchema.type, "object");
  assert.equal(nodeModify.inputSchema.additionalProperties, false);
});

test("node_modify tool exposes the Godot-adapted property set", () => {
  // The property set is the Godot adaptation of Unity's gameobject_modify:
  // node_path + node_paths (replace instance_id/path), properties map
  // (replaces Unity's component property paths + RFC 7396 three-surface form),
  // position/rotation/scale + name (kept as flat convenience fields), plus the
  // forward-compat mutating-tool fields paths_hint and gate. Unity's
  // local_space, gameObjectDiffs, pathPatchesPerGameObject,
  // jsonPatchesPerGameObject, tag, layer, active are dropped (Godot nodes are
  // not GameObjects with a component graph; transform is parent-local; Godot
  // has no Unity tags/layers).
  const props = nodeModify.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "name",
    "node_path",
    "node_paths",
    "paths_hint",
    "position",
    "properties",
    "rotation",
    "scale",
  ]);
});

test("node_modify tool does NOT expose Unity-only fields", () => {
  // Guards against an accidental copy-paste from Unity's gameobject_modify
  // schema bringing back the component-path / RFC 7396 three-surface form.
  const props = nodeModify.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.local_space, undefined, "Godot transforms are parent-local by default");
  assert.equal(props.gameObjectDiffs, undefined, "no Unity component graph");
  assert.equal(props.pathPatchesPerGameObject, undefined, "no Unity component graph");
  assert.equal(props.jsonPatchesPerGameObject, undefined, "no Unity component graph");
  assert.equal(props.tag, undefined, "Godot has no Unity tags");
  assert.equal(props.layer, undefined, "Godot has no Unity layers");
  assert.equal(props.active, undefined, "Godot uses 'visible' on CanvasItem/Node3D");
  assert.equal(props.instance_id, undefined, "Godot scenes are single-rooted; use node_path");
});

test("node_modify gate defaults to off", () => {
  // Default 'off' because the gate flow is not wired yet (P3.5). Pinned so a
  // future change does not silently flip the advertised default before the
  // gate actually lands.
  const props = nodeModify.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.equal(props.gate.default, "off");
  assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
});

test("node_modify has no required array (targets validated at runtime)", () => {
  // node_path OR node_paths is required at runtime, but we do not pin a
  // JSON-schema `required` array because either alone satisfies the constraint.
  // The handler fails with missing_parameter when neither is set.
  assert.equal(nodeModify.inputSchema.required, undefined);
});

test("node_modify node_paths is an array of strings", () => {
  const props = nodeModify.inputSchema.properties as Record<
    string,
    { type?: string; items?: { type?: string } }
  >;
  assert.equal(props.node_paths.type, "array");
  assert.equal(props.node_paths.items?.type, "string");
});

test("node_modify properties map has string additionalProperties", () => {
  // The properties map ferries everything as strings; the handler coerces per
  // key. Pinned so a future change does not accidentally widen this to a
  // free-form object that breaks the bridge's string→string parser.
  const props = nodeModify.inputSchema.properties as Record<
    string,
    { type?: string; additionalProperties?: { type?: string } }
  >;
  assert.equal(props.properties.type, "object");
  assert.equal(props.properties.additionalProperties?.type, "string");
});

test("node_modify paths_hint is an array of strings", () => {
  const props = nodeModify.inputSchema.properties as Record<
    string,
    { type?: string; items?: { type?: string } }
  >;
  assert.equal(props.paths_hint.type, "array");
  assert.equal(props.paths_hint.items?.type, "string");
});
