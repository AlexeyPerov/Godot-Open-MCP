// `godot_open_mcp_reflection_method_find` / `reflection_method_call` tool-definition tests (P5.1).
// Pins the catalog metadata the MCP ListTools response advertises to AI clients for the member
// discovery tool and the gated method invoker — name prefix per ADR-003, non-empty descriptions, the
// Godot-adapted property sets, the `additionalProperties: false` guards, and the required-field /
// max_results / gate invariants. The live round-trip (POST /tools/godot_open_mcp_reflection_method_*
// → bridge handler → members / invoke envelope) is exercised against a local HTTP stub; this file
// only asserts the contracts advertised over stdio.
//
// Adapted from resource-tools.test.ts / resource-mutation.test.ts (copy fidelity for the
// catalog-metadata test shape), with property-set assertions specific to each P5.1 schema and the
// Unity/Godot-MCP deltas documented in reflection-method-find.ts / reflection-method-call.ts.

import { test } from "node:test";
import assert from "node:assert/strict";
import { reflectionMethodFind } from "./reflection-method-find.js";
import { reflectionMethodCall } from "./reflection-method-call.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// reflection_method_find — read-only (no paths_hint / gate).
// ---------------------------------------------------------------------------

test("reflection_method_find tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(reflectionMethodFind.name, "godot_open_mcp_reflection_method_find");
  assert.match(reflectionMethodFind.name, /^godot_open_mcp_/);
});

test("reflection_method_find tool has a non-empty description", () => {
  assert.ok(typeof reflectionMethodFind.description === "string");
  assert.ok((reflectionMethodFind.description ?? "").length > 0);
});

test("reflection_method_find declares an object input schema with additionalProperties:false", () => {
  assert.equal(reflectionMethodFind.inputSchema.type, "object");
  assert.equal(reflectionMethodFind.inputSchema.additionalProperties, false);
});

test("reflection_method_find exposes the Godot-adapted property set", () => {
  // query + kind + assembly_filter + include_godot_editor + include_project + include_signatures +
  // type_name + max_results. Unity's include_unity_editor → include_godot_editor; type_name is the
  // single-type drill-down added for Godot.
  const props = reflectionMethodFind.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "assembly_filter",
    "include_godot_editor",
    "include_project",
    "include_signatures",
    "kind",
    "max_results",
    "query",
    "type_name",
  ]);
});

test("reflection_method_find does NOT expose Unity-only fields", () => {
  // Guards against an accidental copy-paste from Unity's find_members schema bringing back the
  // Unity-only include_unity_editor toggle.
  const props = reflectionMethodFind.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.include_unity_editor, undefined, "Unity toggle replaced by include_godot_editor");
  assert.equal(props.paths_hint, undefined, "read-only tool — no gate surface");
  assert.equal(props.gate, undefined, "read-only tool — no gate surface");
});

test("reflection_method_find kind enum is type/method/property/all with all default", () => {
  const props = reflectionMethodFind.inputSchema.properties as Record<
    string,
    { enum?: string[]; default?: string }
  >;
  assert.deepEqual(props.kind.enum, ["type", "method", "property", "all"]);
  assert.equal(props.kind.default, "all");
});

test("reflection_method_find include flags default true", () => {
  const props = reflectionMethodFind.inputSchema.properties as Record<
    string,
    { default?: boolean }
  >;
  assert.equal(props.include_godot_editor.default, true);
  assert.equal(props.include_project.default, true);
  assert.equal(props.include_signatures.default, true);
});

test("reflection_method_find max_results defaults to 50 with bounds", () => {
  const props = reflectionMethodFind.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number; maximum?: number }
  >;
  assert.equal(props.max_results.default, 50);
  assert.equal(props.max_results.minimum, 1);
  assert.equal(props.max_results.maximum, 200);
});

test("reflection_method_find has no required array (broad scan allowed)", () => {
  // Every field is optional — an empty body is a valid broad scan bounded by max_results.
  assert.equal(reflectionMethodFind.inputSchema.required, undefined);
});

// ---------------------------------------------------------------------------
// reflection_method_call — mutating (paths_hint + gate).
// ---------------------------------------------------------------------------

test("reflection_method_call tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(reflectionMethodCall.name, "godot_open_mcp_reflection_method_call");
  assert.match(reflectionMethodCall.name, /^godot_open_mcp_/);
});

test("reflection_method_call tool has a non-empty description", () => {
  assert.ok(typeof reflectionMethodCall.description === "string");
  assert.ok((reflectionMethodCall.description ?? "").length > 0);
});

test("reflection_method_call declares an object input schema with additionalProperties:false", () => {
  assert.equal(reflectionMethodCall.inputSchema.type, "object");
  assert.equal(reflectionMethodCall.inputSchema.additionalProperties, false);
});

test("reflection_method_call requires type_name, method_name, paths_hint", () => {
  assert.deepEqual(reflectionMethodCall.inputSchema.required, ["type_name", "method_name", "paths_hint"]);
});

test("reflection_method_call exposes the Godot-adapted property set", () => {
  // type_name + method_name (required) + args + arg_type_names + generic_arg_types + is_static +
  // assembly_name + node_path + object_id + execute_in_main_thread + max_depth + max_items +
  // paths_hint (required) + gate + timeout_ms. Unity's object_id-first targeting is replaced by
  // node_path (primary) + object_id (registry-only); execute_in_main_thread echoes the Godot-MCP
  // main-thread opt.
  const props = reflectionMethodCall.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "arg_type_names",
    "args",
    "assembly_name",
    "execute_in_main_thread",
    "gate",
    "generic_arg_types",
    "is_static",
    "max_depth",
    "max_items",
    "method_name",
    "node_path",
    "object_id",
    "paths_hint",
    "timeout_ms",
    "type_name",
  ]);
});

test("reflection_method_call does NOT expose Unity-only fields", () => {
  const props = reflectionMethodCall.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.object_id === undefined, false, "object_id present (registry-only target)");
  assert.equal(props.ignore_scene_dirty, undefined, "Unity scene-dirty guard not applicable in Godot");
});

test("reflection_method_call gate enum is enforce/warn/off with enforce default", () => {
  const props = reflectionMethodCall.inputSchema.properties as Record<
    string,
    { enum?: string[]; default?: string }
  >;
  assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
  assert.equal(props.gate.default, "enforce");
});

test("reflection_method_call is_static defaults false", () => {
  const props = reflectionMethodCall.inputSchema.properties as Record<
    string,
    { default?: boolean }
  >;
  assert.equal(props.is_static.default, false);
});

test("reflection_method_call execute_in_main_thread defaults true", () => {
  const props = reflectionMethodCall.inputSchema.properties as Record<
    string,
    { default?: boolean }
  >;
  assert.equal(props.execute_in_main_thread.default, true);
});

test("reflection_method_call max_depth defaults to 4 with minimum 0", () => {
  const props = reflectionMethodCall.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number }
  >;
  assert.equal(props.max_depth.default, 4);
  assert.equal(props.max_depth.minimum, 0);
});

test("reflection_method_call max_items defaults to 100 with minimum 0", () => {
  const props = reflectionMethodCall.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number }
  >;
  assert.equal(props.max_items.default, 100);
  assert.equal(props.max_items.minimum, 0);
});

test("reflection_method_call object_id defaults to 0", () => {
  const props = reflectionMethodCall.inputSchema.properties as Record<
    string,
    { default?: number }
  >;
  assert.equal(props.object_id.default, 0);
});

// ---------------------------------------------------------------------------
// Registration.
// ---------------------------------------------------------------------------

test("ALL_TOOLS registers both P5.1 reflection tools", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(
    names.includes("godot_open_mcp_reflection_method_find"),
    "reflection_method_find missing from ALL_TOOLS",
  );
  assert.ok(
    names.includes("godot_open_mcp_reflection_method_call"),
    "reflection_method_call missing from ALL_TOOLS",
  );
});
