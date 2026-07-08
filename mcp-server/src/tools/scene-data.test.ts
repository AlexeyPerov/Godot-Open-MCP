// `godot_open_mcp_scene_get_data` / `scene_create` tool-definition tests (P2.7).
// Pins the catalog metadata the MCP ListTools response advertises to AI clients for
// the scene hierarchy read and the scene file creator — name prefix per ADR-003,
// non-empty descriptions, the Godot-adapted property sets, and the
// `additionalProperties: false` guards. The live round-trip (POST
// /tools/godot_open_mcp_scene_{get_data,create} → bridge handler → root NodeData /
// created envelope) is exercised against a local HTTP stub in live-client.test.ts;
// this file only asserts the contracts advertised over stdio.
//
// Adapted from scene-tools.test.ts (copy fidelity for the catalog-metadata test
// shape), with property-set assertions specific to each P2.7 schema and the Unity
// deltas documented in scene-get-data.ts / scene-create.ts.

import { test } from "node:test";
import assert from "node:assert/strict";
import { sceneGetData } from "./scene-get-data.js";
import { sceneCreate } from "./scene-create.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// scene_get_data — read-only (no paths_hint / gate).
// ---------------------------------------------------------------------------

test("scene_get_data tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(sceneGetData.name, "godot_open_mcp_scene_get_data");
  assert.match(sceneGetData.name, /^godot_open_mcp_/);
});

test("scene_get_data tool has a non-empty description", () => {
  assert.ok(typeof sceneGetData.description === "string");
  assert.ok((sceneGetData.description ?? "").length > 0);
});

test("scene_get_data tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(sceneGetData.inputSchema.type, "object");
  assert.equal(sceneGetData.inputSchema.additionalProperties, false);
});

test("scene_get_data exposes the Godot-adapted property set", () => {
  // path (optional assert) + hierarchy_depth (depth bound). Unity's profile/detail/max_nodes/
  // paging surface collapses to a single depth axis for the P2.7 live read.
  const props = sceneGetData.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["hierarchy_depth", "path"]);
});

test("scene_get_data does NOT expose Unity-only fields", () => {
  // Guards against an accidental copy-paste from Unity's scene-get-data schema bringing back the
  // profile/detail/paging surface (deferred — not in P2.7).
  const props = sceneGetData.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.profile, undefined, "P2.7 uses hierarchy_depth, not profile");
  assert.equal(props.detail, undefined, "P2.7 uses hierarchy_depth, not detail");
  assert.equal(props.depth, undefined, "renamed to hierarchy_depth for parity with node_find");
  assert.equal(props.max_nodes, undefined, "token budget is bounded by hierarchy_depth cap");
  assert.equal(props.page_size, undefined, "paging deferred to a later phase");
  assert.equal(props.cursor, undefined, "paging deferred to a later phase");
  assert.equal(props.paths_hint, undefined, "read-only tool — no gate surface");
  assert.equal(props.gate, undefined, "read-only tool — no gate surface");
});

test("scene_get_data hierarchy_depth defaults to 1", () => {
  // Default 1 = root + direct children — a cheap, useful default that lets an agent see the scene's
  // top-level structure without pulling the whole tree.
  const props = sceneGetData.inputSchema.properties as Record<
    string,
    { type?: string; default?: number; minimum?: number }
  >;
  assert.equal(props.hierarchy_depth.type, "integer");
  assert.equal(props.hierarchy_depth.default, 1);
  assert.equal(props.hierarchy_depth.minimum, -1);
});

test("scene_get_data has no required array (path optional)", () => {
  // path is optional — when omitted the handler reads the edited scene.
  assert.equal(sceneGetData.inputSchema.required, undefined);
});

// ---------------------------------------------------------------------------
// scene_create — mutating (paths_hint + gate forward-compat).
// ---------------------------------------------------------------------------

test("scene_create tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(sceneCreate.name, "godot_open_mcp_scene_create");
  assert.match(sceneCreate.name, /^godot_open_mcp_/);
});

test("scene_create tool has a non-empty description", () => {
  assert.ok(typeof sceneCreate.description === "string");
  assert.ok((sceneCreate.description ?? "").length > 0);
});

test("scene_create tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(sceneCreate.inputSchema.type, "object");
  assert.equal(sceneCreate.inputSchema.additionalProperties, false);
});

test("scene_create gate defaults to off", () => {
  // Default 'off' because the gate flow is not wired yet (P3.5).
  const props = sceneCreate.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.equal(props.gate.default, "off");
  assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
});

test("scene_create paths_hint is an array of strings", () => {
  const props = sceneCreate.inputSchema.properties as Record<
    string,
    { type?: string; items?: { type?: string } }
  >;
  assert.equal(props.paths_hint.type, "array");
  assert.equal(props.paths_hint.items?.type, "string");
});

test("scene_create exposes the Godot-adapted property set", () => {
  // path + root_type + root_name + overwrite + open replace Unity's setup + mode. paths_hint + gate
  // are the forward-compat mutating-tool fields.
  const props = sceneCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "open",
    "overwrite",
    "path",
    "paths_hint",
    "root_name",
    "root_type",
  ]);
});

test("scene_create does NOT expose Unity-only fields", () => {
  // Guards against an accidental copy-paste from Unity's scene-create schema bringing back Unity's
  // setup (empty/default) and mode (single/additive) — Godot scenes are single-rooted.
  const props = sceneCreate.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.setup, undefined, "replaced by root_type (Godot scenes are single-rooted)");
  assert.equal(props.mode, undefined, "Godot opens scenes in tabs; use open boolean instead");
});

test("scene_create overwrite defaults to false", () => {
  // Default false so the handler refuses an existing path (path_exists) rather than clobbering it.
  const props = sceneCreate.inputSchema.properties as Record<
    string,
    { type?: string; default?: boolean }
  >;
  assert.equal(props.overwrite.type, "boolean");
  assert.equal(props.overwrite.default, false);
});

test("scene_create open defaults to true", () => {
  // Default true so the typical create-then-edit workflow makes the new scene active.
  const props = sceneCreate.inputSchema.properties as Record<
    string,
    { type?: string; default?: boolean }
  >;
  assert.equal(props.open.type, "boolean");
  assert.equal(props.open.default, true);
});

test("scene_create has no required array (path validated at runtime)", () => {
  // path is required at runtime, but the handler surfaces missing_parameter with a specific message
  // rather than relying on JSON-schema required validation (uniform with the rest of the tool family).
  assert.equal(sceneCreate.inputSchema.required, undefined);
});

// ---------------------------------------------------------------------------
// Registration.
// ---------------------------------------------------------------------------

test("ALL_TOOLS registers both P2.7 scene tools", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(names.includes("godot_open_mcp_scene_get_data"), "scene_get_data missing from ALL_TOOLS");
  assert.ok(names.includes("godot_open_mcp_scene_create"), "scene_create missing from ALL_TOOLS");
});
