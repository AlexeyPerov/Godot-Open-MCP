// `godot_open_mcp_node_set_parent` / `node_duplicate` / `node_delete` tool-
// definition tests (P2.5). Pins the catalog metadata the MCP ListTools response
// advertises to AI clients for the three tree-structure mutators — name prefix
// per ADR-003, non-empty descriptions, the Godot-adapted property sets, and the
// `additionalProperties: false` guards. The live round-trip (POST
// /tools/godot_open_mcp_node_{set_parent,duplicate,delete} → bridge handler →
// NodeData / deleted[] envelope) is exercised against a local HTTP stub in
// live-client.test.ts; this file only asserts the contracts advertised over
// stdio.
//
// Adapted from node-create.test.ts (copy fidelity for the catalog-metadata test
// shape), with property-set assertions specific to each tree-op schema and the
// Unity deltas documented in node-set-parent.ts / node-duplicate.ts /
// node-delete.ts.

import { test } from "node:test";
import assert from "node:assert/strict";
import { nodeSetParent } from "./node-set-parent.js";
import { nodeDuplicate } from "./node-duplicate.js";
import { nodeDelete } from "./node-delete.js";

// ---------------------------------------------------------------------------
// Shared shape checks (all three mutators share the mutating-tool forward-compat
// surface: paths_hint array + gate enum defaulting off).
// ---------------------------------------------------------------------------

const mutatorNames = [
  ["godot_open_mcp_node_set_parent", nodeSetParent],
  ["godot_open_mcp_node_duplicate", nodeDuplicate],
  ["godot_open_mcp_node_delete", nodeDelete],
] as const;

for (const [expectedName, tool] of mutatorNames) {
  test(`${expectedName} tool name follows the godot_open_mcp_* convention`, () => {
    assert.equal(tool.name, expectedName);
    assert.match(tool.name, /^godot_open_mcp_/);
  });

  test(`${expectedName} tool has a non-empty description`, () => {
    assert.ok(typeof tool.description === "string");
    assert.ok((tool.description ?? "").length > 0);
  });

  test(`${expectedName} tool declares an object input schema with additionalProperties:false`, () => {
    assert.equal(tool.inputSchema.type, "object");
    assert.equal(tool.inputSchema.additionalProperties, false);
  });

  test(`${expectedName} gate defaults to off`, () => {
    // Default 'off' because the gate flow is not wired yet (P3.5). Pinned so a
    // future change does not silently flip the advertised default before the
    // gate actually lands.
    const props = tool.inputSchema.properties as Record<
      string,
      { default?: string; enum?: string[] }
    >;
    assert.equal(props.gate.default, "off");
    assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
  });

  test(`${expectedName} paths_hint is an array of strings`, () => {
    const props = tool.inputSchema.properties as Record<
      string,
      { type?: string; items?: { type?: string } }
    >;
    assert.equal(props.paths_hint.type, "array");
    assert.equal(props.paths_hint.items?.type, "string");
  });

  test(`${expectedName} tool does NOT expose Unity instance_id/path resolvers`, () => {
    // Guards against an accidental copy-paste from Unity's gameobject_* schemas
    // bringing back the cross-scene instance_id resolver (Godot scenes are
    // single-rooted under the edited scene root).
    const props = tool.inputSchema.properties as Record<string, unknown>;
    assert.equal(props.instance_id, undefined, "Godot scenes are single-rooted; use node_path");
    // Unity's bare `path` resolver is also absent — the Godot vocabulary is
    // node_path, never path, for the node tool family.
    assert.equal(props.path, undefined, "renamed to node_path");
  });
}

// ---------------------------------------------------------------------------
// node_set_parent — property set + keep_global_transform.
// ---------------------------------------------------------------------------

test("node_set_parent tool exposes the Godot-adapted property set", () => {
  // node_path + parent_node_path replace Unity's instance_id/path +
  // parent_instance_id/parent_path. keep_global_transform replaces Unity's
  // world_position_stays. Unity's local_space is dropped (Godot Node.Reparent
  // is parent-local by default).
  const props = nodeSetParent.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "keep_global_transform",
    "node_path",
    "parent_node_path",
    "paths_hint",
  ]);
});

test("node_set_parent does NOT expose Unity-only fields", () => {
  const props = nodeSetParent.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.world_position_stays, undefined, "renamed to keep_global_transform");
  assert.equal(props.parent_instance_id, undefined, "use parent_node_path");
  assert.equal(props.parent_path, undefined, "renamed to parent_node_path");
  assert.equal(props.local_space, undefined, "Godot Node.Reparent is parent-local by default");
});

test("node_set_parent keep_global_transform defaults to true", () => {
  const props = nodeSetParent.inputSchema.properties as Record<
    string,
    { type?: string; default?: boolean }
  >;
  assert.equal(props.keep_global_transform.type, "boolean");
  assert.equal(props.keep_global_transform.default, true);
});

test("node_set_parent has no required array (node_path/parent_node_path validated at runtime)", () => {
  // Both node_path and parent_node_path are required at runtime, but the handler
  // surfaces missing_parameter with a specific message rather than relying on
  // JSON-schema required validation (keeps the error contract uniform with the
  // rest of the node tool family).
  assert.equal(nodeSetParent.inputSchema.required, undefined);
});

// ---------------------------------------------------------------------------
// node_duplicate — property set + new_name + parent_node_path.
// ---------------------------------------------------------------------------

test("node_duplicate tool exposes the Godot-adapted property set", () => {
  // node_path replaces Unity's instance_id/path. new_name (optional rename) is
  // added (Unity's duplicate has no rename arg). parent_node_path (optional
  // cross-parent placement) is added (Unity's duplicate is same-parent only).
  const props = nodeDuplicate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "new_name",
    "node_path",
    "parent_node_path",
    "paths_hint",
  ]);
});

test("node_duplicate has no required array (node_path validated at runtime)", () => {
  assert.equal(nodeDuplicate.inputSchema.required, undefined);
});

// ---------------------------------------------------------------------------
// node_delete — property set + node_paths batch + fail_if_has_children.
// ---------------------------------------------------------------------------

test("node_delete tool exposes the Godot-adapted property set", () => {
  // node_path (single) + node_paths (batch) replace Unity's instance_id/path.
  // fail_if_has_children (leaf guard) is added — a guard Unity's destroy does
  // not expose.
  const props = nodeDelete.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "fail_if_has_children",
    "gate",
    "node_path",
    "node_paths",
    "paths_hint",
  ]);
});

test("node_delete node_paths is an array of strings", () => {
  const props = nodeDelete.inputSchema.properties as Record<
    string,
    { type?: string; items?: { type?: string } }
  >;
  assert.equal(props.node_paths.type, "array");
  assert.equal(props.node_paths.items?.type, "string");
});

test("node_delete fail_if_has_children defaults to false", () => {
  // Default false so the tool deletes whole sub-trees by default (matches
  // Unity destroy semantics); opt-in true for leaf-only cleanup.
  const props = nodeDelete.inputSchema.properties as Record<
    string,
    { type?: string; default?: boolean }
  >;
  assert.equal(props.fail_if_has_children.type, "boolean");
  assert.equal(props.fail_if_has_children.default, false);
});

test("node_delete has no required array (targets validated at runtime)", () => {
  assert.equal(nodeDelete.inputSchema.required, undefined);
});
