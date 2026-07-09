// `godot_open_mcp_validate_edit` / `checkpoint_create` / `delta` tool-
// definition tests (P3.6). Pins the catalog metadata the MCP ListTools response
// advertises to AI clients for the three gate meta-tools — name prefix per
// ADR-003, non-empty descriptions, the property sets, and the
// `additionalProperties: false` guards. The live round-trip (POST
// /tools/godot_open_mcp_{validate_edit,checkpoint_create,delta} → bridge handler
// → verify / CheckpointStore) is exercised against a local HTTP stub in
// live-client.test.ts; this file only asserts the contracts advertised over
// stdio.
//
// Adapted from scene-tools.test.ts (copy fidelity for the catalog-metadata test
// shape), with property-set assertions specific to each gate-tool schema and
// the Unity deltas documented in validate-edit.ts / checkpoint-create.ts /
// delta.ts.

import { test } from "node:test";
import assert from "node:assert/strict";
import { validateEdit } from "./validate-edit.js";
import { checkpointCreate } from "./checkpoint-create.js";
import { delta } from "./delta.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// Shared catalog-metadata checks (all three are read-only: no paths_hint/gate).
// ---------------------------------------------------------------------------

const toolCases = [
  ["godot_open_mcp_validate_edit", validateEdit],
  ["godot_open_mcp_checkpoint_create", checkpointCreate],
  ["godot_open_mcp_delta", delta],
] as const;

for (const [expectedName, tool] of toolCases) {
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

  test(`${expectedName} is read-only (no gate / paths_hint surface)`, () => {
    // Gate meta-tools bypass the gate dispatch path. They carry no forward-compat
    // paths_hint/gate fields — those belong to mutating tools only.
    const props = tool.inputSchema.properties as Record<string, unknown>;
    assert.equal(props.paths_hint, undefined, "read-only tool — no gate surface");
    assert.equal(props.gate, undefined, "read-only tool — no gate surface");
  });
}

// ---------------------------------------------------------------------------
// validate_edit — required paths, optional categories, no Unity-only fields.
// ---------------------------------------------------------------------------

test("validate_edit exposes the Godot-adapted property set", () => {
  // paths (required) + categories (optional rule filter). Unity's
  // include_rules / exclude_rules / platform_profile / profile / page_size /
  // cursor / detail are all dropped for v1.
  const props = validateEdit.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["categories", "paths"]);
});

test("validate_edit requires paths", () => {
  assert.deepEqual(validateEdit.inputSchema.required, ["paths"]);
});

test("validate_edit paths is a non-empty array of strings", () => {
  const props = validateEdit.inputSchema.properties as Record<
    string,
    { type?: string; minItems?: number; items?: { type?: string } }
  >;
  assert.equal(props.paths.type, "array");
  assert.equal(props.paths.minItems, 1);
  assert.equal(props.paths.items?.type, "string");
});

test("validate_edit does NOT expose Unity-only filter/paging fields", () => {
  // Guards against an accidental copy-paste from Unity's validate-edit schema.
  const props = validateEdit.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.include_rules, undefined, "dropped — categories covers rule filtering");
  assert.equal(props.exclude_rules, undefined, "dropped — categories covers rule filtering");
  assert.equal(props.platform_profile, undefined, "dropped — no paging/profile in v1");
  assert.equal(props.profile, undefined, "dropped — no paging/profile in v1");
  assert.equal(props.page_size, undefined, "dropped — no paging/profile in v1");
  assert.equal(props.cursor, undefined, "dropped — no paging/profile in v1");
  assert.equal(props.detail, undefined, "dropped — no paging/profile in v1");
});

// ---------------------------------------------------------------------------
// checkpoint_create — optional paths + label, no required array.
// ---------------------------------------------------------------------------

test("checkpoint_create exposes the Godot-adapted property set", () => {
  const props = checkpointCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["label", "paths"]);
});

test("checkpoint_create has no required array (paths optional but recommended)", () => {
  assert.equal(checkpointCreate.inputSchema.required, undefined);
});

test("checkpoint_create paths is an array of strings", () => {
  const props = checkpointCreate.inputSchema.properties as Record<
    string,
    { type?: string; items?: { type?: string } }
  >;
  assert.equal(props.paths.type, "array");
  assert.equal(props.paths.items?.type, "string");
});

// ---------------------------------------------------------------------------
// delta — required checkpoint_id, optional paths override.
// ---------------------------------------------------------------------------

test("delta exposes the Godot-adapted property set", () => {
  const props = delta.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["checkpoint_id", "paths"]);
});

test("delta requires checkpoint_id", () => {
  assert.deepEqual(delta.inputSchema.required, ["checkpoint_id"]);
});

test("delta checkpoint_id is a string", () => {
  const props = delta.inputSchema.properties as Record<string, { type?: string }>;
  assert.equal(props.checkpoint_id.type, "string");
});

test("delta paths is an array of strings (optional override)", () => {
  const props = delta.inputSchema.properties as Record<
    string,
    { type?: string; items?: { type?: string } }
  >;
  assert.equal(props.paths.type, "array");
  assert.equal(props.paths.items?.type, "string");
});

// ---------------------------------------------------------------------------
// ALL_TOOLS registration.
// ---------------------------------------------------------------------------

test("ALL_TOOLS registers all three gate meta-tools", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(names.includes("godot_open_mcp_validate_edit"), "validate_edit missing from ALL_TOOLS");
  assert.ok(
    names.includes("godot_open_mcp_checkpoint_create"),
    "checkpoint_create missing from ALL_TOOLS",
  );
  assert.ok(names.includes("godot_open_mcp_delta"), "delta missing from ALL_TOOLS");
});
