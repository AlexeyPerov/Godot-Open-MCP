// `godot_open_mcp_editor_selection_get` / `editor_selection_set` tool-definition tests (P4.6). Pins
// the catalog metadata the MCP ListTools response advertises to AI clients for the editor selection
// tools — name prefix per ADR-003, non-empty descriptions, the Godot-adapted property sets, the
// `additionalProperties: false` guards, and the gate surface (paths_hint + gate for the mutator). The
// live round-trip (POST /tools/godot_open_mcp_editor_selection_{get,set} → bridge handler →
// EditorInterface → selection envelope) is exercised against a local HTTP stub + headless Godot
// smoke; this file only asserts the contracts advertised over stdio.
//
// Adapted from editor-application-state.test.ts (copy fidelity for the catalog-metadata test shape),
// with property-set assertions specific to each P4.6 schema. The key contrast: editor_selection_get
// is read-only (no paths_hint/gate), editor_selection_set is mutating.

import { test } from "node:test";
import assert from "node:assert/strict";
import { editorSelectionGet } from "./editor-selection-get.js";
import { editorSelectionSet } from "./editor-selection-set.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// editor_selection_get — read-only (no paths_hint / gate).
// ---------------------------------------------------------------------------

test("editor_selection_get tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(editorSelectionGet.name, "godot_open_mcp_editor_selection_get");
  assert.match(editorSelectionGet.name, /^godot_open_mcp_/);
});

test("editor_selection_get tool has a non-empty description", () => {
  assert.ok(typeof editorSelectionGet.description === "string");
  assert.ok((editorSelectionGet.description ?? "").length > 0);
});

test("editor_selection_get tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(editorSelectionGet.inputSchema.type, "object");
  assert.equal(editorSelectionGet.inputSchema.additionalProperties, false);
});

test("editor_selection_get has an empty property set (no inputs)", () => {
  const props = editorSelectionGet.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props), []);
});

test("editor_selection_get does NOT expose Unity-only fields or gate surface", () => {
  // Guards against an accidental copy-paste from Unity's selection-get schema. The Godot DTO is
  // node-only — no asset GUIDs, component refs, or global object ids.
  const props = editorSelectionGet.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "read-only tool — no gate surface");
  assert.equal(props.gate, undefined, "read-only tool — no gate surface");
  assert.equal(props.asset_guid, undefined, "Godot selection is node-only");
  assert.equal(props.targets, undefined, "Unity targets[] replaced by select[] on the set tool");
  assert.equal(props.clear, undefined, "Unity clear flag replaced by empty select[] on the set tool");
});

test("editor_selection_get has no required array", () => {
  assert.equal(editorSelectionGet.inputSchema.required, undefined);
});

// ---------------------------------------------------------------------------
// editor_selection_set — mutating (has paths_hint + gate, default enforce).
// ---------------------------------------------------------------------------

test("editor_selection_set tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(editorSelectionSet.name, "godot_open_mcp_editor_selection_set");
  assert.match(editorSelectionSet.name, /^godot_open_mcp_/);
});

test("editor_selection_set tool has a non-empty description", () => {
  assert.ok(typeof editorSelectionSet.description === "string");
  assert.ok((editorSelectionSet.description ?? "").length > 0);
});

test("editor_selection_set tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(editorSelectionSet.inputSchema.type, "object");
  assert.equal(editorSelectionSet.inputSchema.additionalProperties, false);
});

test("editor_selection_set exposes the Godot-adapted property set", () => {
  // select (node refs) + paths_hint (gate scope) + gate (mode).
  const props = editorSelectionSet.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["gate", "paths_hint", "select"]);
});

test("editor_selection_set DOES expose paths_hint and gate (mutating tool)", () => {
  const props = editorSelectionSet.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.paths_hint !== undefined, "paths_hint required for mutating tool");
  assert.ok(props.gate !== undefined, "gate surface required for mutating tool");
});

test("editor_selection_set gate defaults to enforce", () => {
  const props = editorSelectionSet.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.equal(props.gate.default, "enforce");
  assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
});

test("editor_selection_set requires paths_hint", () => {
  // select is optional (default clear); paths_hint is always mandatory.
  assert.deepEqual(editorSelectionSet.inputSchema.required, ["paths_hint"]);
});

test("editor_selection_set select is an array of node refs with additionalProperties:false items", () => {
  const props = editorSelectionSet.inputSchema.properties as Record<
    string,
    { type?: string; items?: { type?: string; properties?: Record<string, unknown>; additionalProperties?: boolean } }
  >;
  assert.equal(props.select.type, "array");
  assert.equal(props.select.items?.type, "object");
  assert.equal(props.select.items?.additionalProperties, false);
  const itemProps = props.select.items?.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(itemProps).sort(), ["instance_id", "node_path"]);
});

test("editor_selection_set does NOT expose Unity-only fields", () => {
  // Guards against accidental copy-paste from Unity's selection-set schema.
  const props = editorSelectionSet.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.targets, undefined, "Unity targets[] renamed to select[]");
  assert.equal(props.clear, undefined, "Unity clear flag replaced by empty select[]");
  assert.equal(props.asset_guid, undefined, "Godot selection is node-only");
  assert.equal(props.component, undefined, "Godot selection is node-only");
  assert.equal(props.instance_id, undefined, "top-level instance_id replaced by per-ref field");
});

// ---------------------------------------------------------------------------
// Registration.
// ---------------------------------------------------------------------------

test("ALL_TOOLS registers both P4.6 editor selection tools", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(
    names.includes("godot_open_mcp_editor_selection_get"),
    "editor_selection_get missing from ALL_TOOLS",
  );
  assert.ok(
    names.includes("godot_open_mcp_editor_selection_set"),
    "editor_selection_set missing from ALL_TOOLS",
  );
});
