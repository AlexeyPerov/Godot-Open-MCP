// `godot_open_mcp_resource_move` / `resource_delete` tool-definition tests (P4.3).
// Pins the catalog metadata the MCP ListTools response advertises to AI clients for the resource
// file-lifecycle mutators — name prefix per ADR-003, non-empty descriptions, the Godot-adapted
// property sets, the `additionalProperties: false` guards, and the mutating-tool gate surface
// (paths_hint + gate). The live round-trip (POST /tools/godot_open_mcp_resource_{move,delete} →
// bridge handler → DirAccess → identity/gate envelope) is exercised against a local HTTP stub +
// headless Godot smoke; this file only asserts the contracts advertised over stdio.
//
// Adapted from resource-mutation.test.ts (copy fidelity for the catalog-metadata test shape), with
// property-set assertions specific to each P4.3 schema. The key contrast with the P4.1 read-only
// tools: these mutators MUST declare paths_hint + gate (the read-only tests explicitly assert those
// fields are absent).

import { test } from "node:test";
import assert from "node:assert/strict";
import { resourceMove } from "./resource-move.js";
import { resourceDelete } from "./resource-delete.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// resource_move — mutating (has paths_hint + gate, default enforce).
// ---------------------------------------------------------------------------

test("resource_move tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(resourceMove.name, "godot_open_mcp_resource_move");
  assert.match(resourceMove.name, /^godot_open_mcp_/);
});

test("resource_move tool has a non-empty description", () => {
  assert.ok(typeof resourceMove.description === "string");
  assert.ok((resourceMove.description ?? "").length > 0);
});

test("resource_move tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(resourceMove.inputSchema.type, "object");
  assert.equal(resourceMove.inputSchema.additionalProperties, false);
});

test("resource_move exposes the Godot-adapted property set", () => {
  // source_path (origin) + destination_path (new location) + paths_hint (gate scope, must contain
  // BOTH) + gate (mode). Unity's entries[]{source,destination} / paths[] becomes flat source_path /
  // destination_path since Godot's move is a single-file operation.
  const props = resourceMove.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "destination_path",
    "gate",
    "paths_hint",
    "source_path",
  ]);
});

test("resource_move DOES expose paths_hint and gate (mutating tool)", () => {
  // Contrast with the P4.1 read-only tools that explicitly assert these are absent.
  const props = resourceMove.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.paths_hint !== undefined, "paths_hint required for mutating tool");
  assert.ok(props.gate !== undefined, "gate surface required for mutating tool");
});

test("resource_move gate defaults to enforce", () => {
  const props = resourceMove.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.equal(props.gate.default, "enforce");
  assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
});

test("resource_move requires source_path, destination_path, and paths_hint", () => {
  assert.deepEqual(resourceMove.inputSchema.required, [
    "source_path",
    "destination_path",
    "paths_hint",
  ]);
});

test("resource_move does NOT expose Unity-only fields", () => {
  // Guards against accidental copy-paste from Unity's assets-move schema.
  const props = resourceMove.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.entries, undefined, "Unity's entries[] array is flattened to source_path/destination_path");
  assert.equal(props.paths, undefined, "no paths[] array — single-file move");
  assert.equal(props.asset_path, undefined, "renamed to source_path/destination_path");
  assert.equal(props.overwrite, undefined, "no overwrite flag — move rejects destination collisions");
});

// ---------------------------------------------------------------------------
// resource_delete — mutating (has paths_hint + gate, default enforce).
// ---------------------------------------------------------------------------

test("resource_delete tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(resourceDelete.name, "godot_open_mcp_resource_delete");
  assert.match(resourceDelete.name, /^godot_open_mcp_/);
});

test("resource_delete tool has a non-empty description", () => {
  assert.ok(typeof resourceDelete.description === "string");
  assert.ok((resourceDelete.description ?? "").length > 0);
});

test("resource_delete tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(resourceDelete.inputSchema.type, "object");
  assert.equal(resourceDelete.inputSchema.additionalProperties, false);
});

test("resource_delete exposes the Godot-adapted property set", () => {
  // resource_path (target) + paths_hint (gate scope) + gate (mode). Unity's paths[] (array delete)
  // becomes a single resource_path since Godot's delete is a single-file operation.
  const props = resourceDelete.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "paths_hint",
    "resource_path",
  ]);
});

test("resource_delete DOES expose paths_hint and gate (mutating tool)", () => {
  const props = resourceDelete.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.paths_hint !== undefined, "paths_hint required for mutating tool");
  assert.ok(props.gate !== undefined, "gate surface required for mutating tool");
});

test("resource_delete gate defaults to enforce", () => {
  const props = resourceDelete.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.equal(props.gate.default, "enforce");
  assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
});

test("resource_delete requires resource_path and paths_hint", () => {
  assert.deepEqual(resourceDelete.inputSchema.required, ["resource_path", "paths_hint"]);
});

test("resource_delete does NOT expose Unity-only fields", () => {
  // Guards against accidental copy-paste from Unity's assets-delete schema.
  const props = resourceDelete.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths, undefined, "Unity's paths[] array is flattened to resource_path");
  assert.equal(props.asset_path, undefined, "renamed to resource_path");
  assert.equal(props.recyle_bin, undefined, "no recycle bin — delete is explicit and immediate");
});

// ---------------------------------------------------------------------------
// Registration.
// ---------------------------------------------------------------------------

test("ALL_TOOLS registers both P4.3 resource file-lifecycle tools", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(names.includes("godot_open_mcp_resource_move"), "resource_move missing from ALL_TOOLS");
  assert.ok(names.includes("godot_open_mcp_resource_delete"), "resource_delete missing from ALL_TOOLS");
});
