// `godot_open_mcp_node_find` tool-definition tests (P2.2).
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// — name prefix per ADR-003, non-empty description, the Godot-adapted property
// set (node_path / name / type / name_contains / hierarchy_depth / max_results),
// and the `additionalProperties: false` guard. The live round-trip (POST
// /tools/godot_open_mcp_node_find → bridge handler → NodeData envelope) is
// exercised against a local HTTP stub in live-client.test.ts; this file only
// asserts the contract advertised over stdio.
//
// Adapted from ping.test.ts (copy fidelity for the catalog-metadata test
// shape), with property-set assertions specific to the node_find schema.

import { test } from "node:test";
import assert from "node:assert/strict";
import { nodeFind } from "./node-find.js";

test("node_find tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(nodeFind.name, "godot_open_mcp_node_find");
  assert.match(nodeFind.name, /^godot_open_mcp_/);
});

test("node_find tool has a non-empty description", () => {
  assert.ok(typeof nodeFind.description === "string");
  assert.ok((nodeFind.description ?? "").length > 0);
});

test("node_find tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(nodeFind.inputSchema.type, "object");
  assert.equal(nodeFind.inputSchema.additionalProperties, false);
});

test("node_find tool exposes the Godot-adapted property set", () => {
  // The property set is the Godot adaptation of Unity's gameobject_find:
  // node_path (priority-1 targeted resolver), name (priority-2 fallback),
  // type (Godot class filter, replaces Unity's `component`), name_contains
  // (substring filter), hierarchy_depth (NodeData subtree depth), max_results
  // (list cap). Unity's `tag`, `layer`, `root_only`, `instance_id`, and `path`
  // are intentionally absent — see the node-find.ts header comment.
  const props = nodeFind.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "hierarchy_depth",
    "max_results",
    "name",
    "name_contains",
    "node_path",
    "type",
  ]);
});

test("node_find tool does NOT expose Unity-only filters", () => {
  // Guards against an accidental copy-paste from Unity's gameobject_find schema
  // bringing back tag / layer / root_only / instance_id / path.
  const props = nodeFind.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.tag, undefined, "Godot has no Unity-style tags");
  assert.equal(props.layer, undefined, "Godot has no Unity-style layers");
  assert.equal(props.root_only, undefined, "use hierarchy_depth: 0 instead");
  assert.equal(props.instance_id, undefined, "deferred — see node-find.ts header");
  assert.equal(props.path, undefined, "renamed to node_path");
  assert.equal(props.component, undefined, "renamed to type");
});

test("node_find node_path property is a string", () => {
  const props = nodeFind.inputSchema.properties as Record<
    string,
    { type?: string | string[] }
  >;
  assert.equal(props.node_path.type, "string");
});

test("node_find hierarchy_depth defaults to 0 and has minimum 0", () => {
  const props = nodeFind.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number }
  >;
  assert.equal(props.hierarchy_depth.default, 0);
  assert.equal(props.hierarchy_depth.minimum, 0);
});

test("node_find max_results defaults to 50 and has minimum 1", () => {
  const props = nodeFind.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number }
  >;
  assert.equal(props.max_results.default, 50);
  assert.equal(props.max_results.minimum, 1);
});
