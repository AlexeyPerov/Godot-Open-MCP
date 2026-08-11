// P16.6 spatial query pack — schema + group-assignment parity tests.
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// (name prefix, non-empty description, read-only shape without paths_hint/gate,
// the query_type / dimension / shape enums, the max_results default) and the
// group assignment (the one tool maps to `spatial`). The live round-trip
// (POST /tools/godot_open_mcp_spatial_query → bridge handler → result envelope)
// is exercised by the headless Godot smoke; this file only asserts the contract
// advertised over stdio.
//
// Adapted from particles-tools.test.ts (copy fidelity for the catalog-metadata
// test shape), with assertions specific to the spatial pack's single read-only
// query tool and the ray / shape / point × 2D / 3D enum surface.

import { test } from "node:test";
import assert from "node:assert/strict";

import { spatialQuery } from "./spatial-query.js";
import { groupFor, toolsInGroup } from "../capabilities/tool-groups.js";

const ALL_SPATIAL_TOOLS = [spatialQuery];

// ---------------------------------------------------------------------------
// Names + group assignment
// ---------------------------------------------------------------------------

test("every spatial tool name follows the godot_open_mcp_spatial_* convention", () => {
  for (const t of ALL_SPATIAL_TOOLS) {
    assert.match(t.name, /^godot_open_mcp_spatial_[a-z0-9_]+$/);
  }
});

test("every spatial tool is assigned to the spatial group", () => {
  for (const t of ALL_SPATIAL_TOOLS) {
    assert.equal(groupFor(t.name), "spatial", `${t.name} must map to spatial`);
  }
});

test("toolsInGroup(spatial) returns exactly the one pack tool", () => {
  assert.deepEqual(toolsInGroup("spatial"), [
    "godot_open_mcp_spatial_query",
  ]);
});

// ---------------------------------------------------------------------------
// Shared catalog-metadata invariants
// ---------------------------------------------------------------------------

test("every spatial tool has a non-empty description", () => {
  for (const t of ALL_SPATIAL_TOOLS) {
    assert.ok(typeof t.description === "string");
    assert.ok((t.description ?? "").length > 0, `${t.name} description is empty`);
  }
});

test("every spatial tool declares an object input schema with additionalProperties:false", () => {
  for (const t of ALL_SPATIAL_TOOLS) {
    assert.equal(t.inputSchema.type, "object", `${t.name} schema type`);
    assert.equal(t.inputSchema.additionalProperties, false, `${t.name} additionalProperties`);
  }
});

test("every spatial tool description tells the agent to activate the spatial group", () => {
  // The group is default-off; the description must surface the activation step so an
  // agent does not call a hidden tool blind.
  for (const t of ALL_SPATIAL_TOOLS) {
    assert.match(
      t.description ?? "",
      /activate the group with manage_tools/,
      `${t.name} description must mention manage_tools activation`,
    );
  }
});

// ---------------------------------------------------------------------------
// Read-only tool — no paths_hint, no gate
// ---------------------------------------------------------------------------

test("spatial_query is read-only (no paths_hint, no gate)", () => {
  const props = spatialQuery.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "spatial_query must not declare paths_hint");
  assert.equal(props.gate, undefined, "spatial_query must not declare gate");
});

test("spatial_query does not list paths_hint as required", () => {
  assert.ok(
    !(spatialQuery.inputSchema.required ?? []).includes("paths_hint"),
    "spatial_query must not require paths_hint",
  );
});

// ---------------------------------------------------------------------------
// Required + enum surface
// ---------------------------------------------------------------------------

test("spatial_query requires exactly query_type + dimension", () => {
  assert.deepEqual(
    (spatialQuery.inputSchema.required ?? []).sort(),
    ["dimension", "query_type"],
  );
});

test("spatial_query query_type enum is [ray, shape, point]", () => {
  const qt = (spatialQuery.inputSchema.properties as Record<string, { enum?: string[] }>).query_type;
  assert.deepEqual(qt?.enum, ["ray", "shape", "point"]);
});

test("spatial_query dimension enum is [2d, 3d]", () => {
  const dim = (spatialQuery.inputSchema.properties as Record<string, { enum?: string[] }>).dimension;
  assert.deepEqual(dim?.enum, ["2d", "3d"]);
});

test("spatial_query shape enum is [circle, sphere, rectangle, box, capsule]", () => {
  const shape = (spatialQuery.inputSchema.properties as Record<string, { enum?: string[] }>).shape;
  assert.deepEqual(shape?.enum, ["circle", "sphere", "rectangle", "box", "capsule"]);
});

// ---------------------------------------------------------------------------
// Property set + bounds
// ---------------------------------------------------------------------------

test("spatial_query exposes the full query property set", () => {
  const props = spatialQuery.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "collide_with_areas",
    "collide_with_bodies",
    "dimension",
    "exclude",
    "from",
    "height",
    "mask",
    "max_results",
    "position",
    "query_type",
    "radius",
    "rotation",
    "shape",
    "size",
    "to",
  ]);
});

test("spatial_query max_results defaults to 32 with minimum 1", () => {
  const mr = (spatialQuery.inputSchema.properties as Record<string, { default?: number; minimum?: number }>).max_results;
  assert.equal(mr?.default, 32, "max_results default must be 32");
  assert.equal(mr?.minimum, 1, "max_results minimum must be 1");
});

test("spatial_query collide_with_bodies defaults true and collide_with_areas defaults false", () => {
  const props = spatialQuery.inputSchema.properties as Record<string, { default?: boolean }>;
  assert.equal(props.collide_with_bodies?.default, true);
  assert.equal(props.collide_with_areas?.default, false);
});

test("spatial_query radius + height are strictly positive (exclusiveMinimum 0)", () => {
  const props = spatialQuery.inputSchema.properties as Record<string, { exclusiveMinimum?: number }>;
  assert.equal(props.radius?.exclusiveMinimum, 0, "radius exclusiveMinimum");
  assert.equal(props.height?.exclusiveMinimum, 0, "height exclusiveMinimum");
});

test("spatial_query mask minimum is 0", () => {
  const mask = (spatialQuery.inputSchema.properties as Record<string, { minimum?: number }>).mask;
  assert.equal(mask?.minimum, 0);
});

test("spatial_query exclude is an array of strings", () => {
  const ex = (spatialQuery.inputSchema.properties as Record<string, { type?: string; items?: { type?: string } }>).exclude;
  assert.equal(ex?.type, "array", "exclude type");
  assert.equal(ex?.items?.type, "string", "exclude items type");
});
