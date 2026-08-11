// `godot_open_mcp_reserialize` tool-definition tests (P17.2).
// Pins the catalog metadata the MCP ListTools response advertises to AI clients for the
// reserialize mutator — name prefix per ADR-003, non-empty description, the Godot-adapted property
// set (paths + paths_hint + gate), the `additionalProperties: false` guard, and the mutating-tool
// gate surface (paths_hint + gate defaulting to enforce). The live round-trip
// (POST /tools/godot_open_mcp_reserialize → bridge handler → ResourceLoader/ResourceSaver) is
// exercised against a local HTTP stub + headless Godot smoke; this file only asserts the contracts
// advertised over stdio.
//
// Adapted from resource-mutation.test.ts (copy fidelity for the catalog-metadata test shape), with
// property-set assertions specific to the P17.2 schema. The key contrast with the P17.1 read-only
// asset-intelligence tools: this mutator MUST declare paths_hint + gate (those tests explicitly
// assert those fields are absent).

import { test } from "node:test";
import assert from "node:assert/strict";
import { reserialize } from "./reserialize.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// reserialize — mutating (has paths_hint + gate, default enforce).
// ---------------------------------------------------------------------------

test("reserialize tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(reserialize.name, "godot_open_mcp_reserialize");
  assert.match(reserialize.name, /^godot_open_mcp_/);
});

test("reserialize tool has a non-empty description", () => {
  assert.ok(typeof reserialize.description === "string");
  assert.ok((reserialize.description ?? "").length > 0);
});

test("reserialize tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(reserialize.inputSchema.type, "object");
  assert.equal(reserialize.inputSchema.additionalProperties, false);
});

test("reserialize exposes the Godot-adapted property set", () => {
  const props = reserialize.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["gate", "paths", "paths_hint"]);
});

test("reserialize DOES expose paths_hint and gate (mutating tool)", () => {
  const props = reserialize.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.paths_hint !== undefined, "paths_hint required for mutating tool");
  assert.ok(props.gate !== undefined, "gate surface required for mutating tool");
});

test("reserialize gate defaults to enforce", () => {
  const props = reserialize.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.equal(props.gate.default, "enforce");
  assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
});

test("reserialize requires paths and paths_hint", () => {
  assert.deepEqual(reserialize.inputSchema.required, ["paths", "paths_hint"]);
});

test("reserialize paths is a non-empty array (minItems:1) of strings", () => {
  const props = reserialize.inputSchema.properties as Record<string, any>;
  assert.equal(props.paths.type, "array");
  assert.equal(props.paths.minItems, 1);
  assert.equal(props.paths.items.type, "string");
});

test("reserialize paths_hint is an array of strings", () => {
  const props = reserialize.inputSchema.properties as Record<string, any>;
  assert.equal(props.paths_hint.type, "array");
  assert.equal(props.paths_hint.items.type, "string");
});

test("reserialize does NOT expose Unity-only fields", () => {
  const props = reserialize.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.include_meta, undefined, "no .meta concept in Godot");
  assert.equal(props.asset_paths, undefined, "renamed to paths");
  assert.equal(props.extensions, undefined, "extensions are fixed server-side");
});

// ---------------------------------------------------------------------------
// Registration.
// ---------------------------------------------------------------------------

test("ALL_TOOLS registers the P17.2 reserialize tool", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(
    names.includes("godot_open_mcp_reserialize"),
    "reserialize missing from ALL_TOOLS",
  );
});
