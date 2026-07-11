// `godot_open_mcp_editor_application_get_state` / `editor_application_set_state` tool-definition
// tests (P4.5). Pins the catalog metadata the MCP ListTools response advertises to AI clients for
// the editor application-state tools — name prefix per ADR-003, non-empty descriptions, the
// Godot-adapted property sets, the `additionalProperties: false` guards, and the gate surface
// (paths_hint + gate for the mutator). The live round-trip (POST /tools/godot_open_mcp_editor_application_{get,set}_state
// → bridge handler → EditorInterface → state envelope) is exercised against a local HTTP stub +
// headless Godot smoke; this file only asserts the contracts advertised over stdio.
//
// Adapted from filesystem-tools.test.ts (copy fidelity for the catalog-metadata test shape), with
// property-set assertions specific to each P4.5 schema. The key contrast: editor_application_get_state
// is read-only (no paths_hint/gate), editor_application_set_state is mutating.

import { test } from "node:test";
import assert from "node:assert/strict";
import { editorApplicationGetState } from "./editor-application-get-state.js";
import { editorApplicationSetState } from "./editor-application-set-state.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// editor_application_get_state — read-only (no paths_hint / gate).
// ---------------------------------------------------------------------------

test("editor_application_get_state tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(editorApplicationGetState.name, "godot_open_mcp_editor_application_get_state");
  assert.match(editorApplicationGetState.name, /^godot_open_mcp_/);
});

test("editor_application_get_state tool has a non-empty description", () => {
  assert.ok(typeof editorApplicationGetState.description === "string");
  assert.ok((editorApplicationGetState.description ?? "").length > 0);
});

test("editor_application_get_state tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(editorApplicationGetState.inputSchema.type, "object");
  assert.equal(editorApplicationGetState.inputSchema.additionalProperties, false);
});

test("editor_application_get_state has an empty property set (no inputs)", () => {
  const props = editorApplicationGetState.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props), []);
});

test("editor_application_get_state does NOT expose Unity-only fields or gate surface", () => {
  // Guards against an accidental copy-paste from Unity's editor_status schema. The Godot DTO excludes
  // isPaused / isCompiling / editorType (no editor-side equivalent — separate OS process play model).
  const props = editorApplicationGetState.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "read-only tool — no gate surface");
  assert.equal(props.gate, undefined, "read-only tool — no gate surface");
  assert.equal(props.is_paused, undefined, "Godot has no editor-side pause");
  assert.equal(props.is_compiling, undefined, "Godot has no editor-side compile state");
  assert.equal(props.state, undefined, "Unity play/pause/stop verb replaced by set-state tool");
});

test("editor_application_get_state has no required array", () => {
  assert.equal(editorApplicationGetState.inputSchema.required, undefined);
});

// ---------------------------------------------------------------------------
// editor_application_set_state — mutating (has paths_hint + gate, default enforce).
// ---------------------------------------------------------------------------

test("editor_application_set_state tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(editorApplicationSetState.name, "godot_open_mcp_editor_application_set_state");
  assert.match(editorApplicationSetState.name, /^godot_open_mcp_/);
});

test("editor_application_set_state tool has a non-empty description", () => {
  assert.ok(typeof editorApplicationSetState.description === "string");
  assert.ok((editorApplicationSetState.description ?? "").length > 0);
});

test("editor_application_set_state tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(editorApplicationSetState.inputSchema.type, "object");
  assert.equal(editorApplicationSetState.inputSchema.additionalProperties, false);
});

test("editor_application_set_state exposes the Godot-adapted property set", () => {
  // is_playing (action) + scene (selector) + timeout_ms (settle bound) + paths_hint (gate scope) + gate (mode).
  const props = editorApplicationSetState.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "is_playing",
    "paths_hint",
    "scene",
    "timeout_ms",
  ]);
});

test("editor_application_set_state DOES expose paths_hint and gate (mutating tool)", () => {
  const props = editorApplicationSetState.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.paths_hint !== undefined, "paths_hint required for mutating tool");
  assert.ok(props.gate !== undefined, "gate surface required for mutating tool");
});

test("editor_application_set_state gate defaults to enforce", () => {
  const props = editorApplicationSetState.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.equal(props.gate.default, "enforce");
  assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
});

test("editor_application_set_state requires paths_hint", () => {
  // is_playing + scene are optional (default stop / main); paths_hint is always mandatory.
  assert.deepEqual(editorApplicationSetState.inputSchema.required, ["paths_hint"]);
});

test("editor_application_set_state is_playing defaults to false", () => {
  const props = editorApplicationSetState.inputSchema.properties as Record<
    string,
    { default?: boolean }
  >;
  assert.equal(props.is_playing.default, false);
});

test("editor_application_set_state timeout_ms defaults to 5000 with bounds", () => {
  const props = editorApplicationSetState.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number; maximum?: number }
  >;
  assert.equal(props.timeout_ms.default, 5000);
  assert.equal(props.timeout_ms.minimum, 1000);
  assert.equal(props.timeout_ms.maximum, 60000);
});

test("editor_application_set_state does NOT expose Unity-only fields", () => {
  // Guards against accidental copy-paste from Unity's editor_set_state schema.
  const props = editorApplicationSetState.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.state, undefined, "Unity play/pause/stop verb replaced by is_playing + scene");
  assert.equal(props.force, undefined, "Unity force flag dropped — use same-scene idempotence");
  assert.equal(props.ignore_scene_dirty, undefined, "Godot play does not trigger a native save modal");
  assert.equal(props.pause, undefined, "Godot has no editor-side pause");
});

// ---------------------------------------------------------------------------
// Registration.
// ---------------------------------------------------------------------------

test("ALL_TOOLS registers both P4.5 editor application-state tools", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(
    names.includes("godot_open_mcp_editor_application_get_state"),
    "editor_application_get_state missing from ALL_TOOLS",
  );
  assert.ok(
    names.includes("godot_open_mcp_editor_application_set_state"),
    "editor_application_set_state missing from ALL_TOOLS",
  );
});
