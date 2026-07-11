// `godot_open_mcp_resource_find` / `resource_get_data` tool-definition tests (P4.1).
// Pins the catalog metadata the MCP ListTools response advertises to AI clients for the resource
// finder and the bounded property reader — name prefix per ADR-003, non-empty descriptions, the
// Godot-adapted property sets, the `additionalProperties: false` guards, and the selector/limit
// invariants. The live round-trip (POST /tools/godot_open_mcp_resource_{find,get_data} → bridge
// handler → identity/property envelope) is exercised against a local HTTP stub; this file only
// asserts the contracts advertised over stdio.
//
// Adapted from scene-data.test.ts / node-find.test.ts (copy fidelity for the catalog-metadata test
// shape), with property-set assertions specific to each P4.1 schema and the Unity/Godot-MCP deltas
// documented in resource-find.ts / resource-get-data.ts.

import { test } from "node:test";
import assert from "node:assert/strict";
import { resourceFind } from "./resource-find.js";
import { resourceGetData } from "./resource-get-data.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// resource_find — read-only (no paths_hint / gate).
// ---------------------------------------------------------------------------

test("resource_find tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(resourceFind.name, "godot_open_mcp_resource_find");
  assert.match(resourceFind.name, /^godot_open_mcp_/);
});

test("resource_find tool has a non-empty description", () => {
  assert.ok(typeof resourceFind.description === "string");
  assert.ok((resourceFind.description ?? "").length > 0);
});

test("resource_find tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(resourceFind.inputSchema.type, "object");
  assert.equal(resourceFind.inputSchema.additionalProperties, false);
});

test("resource_find exposes the Godot-adapted property set", () => {
  // uid + resource_path (direct selectors) + type_filter (search selector) + directory + page_size +
  // cursor (paging). Unity's GUID-based identity becomes uid + resource_path; Unity's AssetDatabase
  // type search becomes the EditorFileSystem scan.
  const props = resourceFind.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "cursor",
    "directory",
    "page_size",
    "resource_path",
    "type_filter",
    "uid",
  ]);
});

test("resource_find does NOT expose Unity-only fields", () => {
  // Guards against an accidental copy-paste from Unity's search-assets schema bringing back
  // GUID/importer/max_results-only fields.
  const props = resourceFind.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.guid, undefined, "Unity GUID replaced by uid");
  assert.equal(props.name, undefined, "resource_find is path/type based, not name-search");
  assert.equal(props.paths_hint, undefined, "read-only tool — no gate surface");
  assert.equal(props.gate, undefined, "read-only tool — no gate surface");
});

test("resource_find page_size defaults to 50 with bounds", () => {
  const props = resourceFind.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number; maximum?: number }
  >;
  assert.equal(props.page_size.default, 50);
  assert.equal(props.page_size.minimum, 1);
  assert.equal(props.page_size.maximum, 200);
});

test("resource_find has no required array (selector validated at runtime)", () => {
  // At least one of uid/resource_path/type_filter is required, but the handler surfaces
  // invalid_request rather than relying on JSON-schema required validation (uniform with the family).
  assert.equal(resourceFind.inputSchema.required, undefined);
});

// ---------------------------------------------------------------------------
// resource_get_data — read-only (no paths_hint / gate).
// ---------------------------------------------------------------------------

test("resource_get_data tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(resourceGetData.name, "godot_open_mcp_resource_get_data");
  assert.match(resourceGetData.name, /^godot_open_mcp_/);
});

test("resource_get_data tool has a non-empty description", () => {
  assert.ok(typeof resourceGetData.description === "string");
  assert.ok((resourceGetData.description ?? "").length > 0);
});

test("resource_get_data tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(resourceGetData.inputSchema.type, "object");
  assert.equal(resourceGetData.inputSchema.additionalProperties, false);
});

test("resource_get_data exposes the Godot-adapted property set", () => {
  // resource_path (required identity) + profile + property_path + max_depth + collection_page_size +
  // cursor. Unity's read-asset profile/detail/paging surface is adapted to Godot Variant
  // serialization.
  const props = resourceGetData.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "collection_page_size",
    "cursor",
    "max_depth",
    "profile",
    "property_path",
    "resource_path",
  ]);
});

test("resource_get_data does NOT expose Unity-only fields", () => {
  const props = resourceGetData.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.guid, undefined, "Unity GUID replaced by res:// path / uid://");
  assert.equal(props.asset_path, undefined, "renamed to resource_path");
  assert.equal(props.detail, undefined, "replaced by profile");
  assert.equal(props.page_size, undefined, "renamed to collection_page_size");
  assert.equal(props.paths_hint, undefined, "read-only tool — no gate surface");
  assert.equal(props.gate, undefined, "read-only tool — no gate surface");
});

test("resource_get_data profile enum is compact/balanced/full with compact default", () => {
  const props = resourceGetData.inputSchema.properties as Record<
    string,
    { enum?: string[]; default?: string }
  >;
  assert.deepEqual(props.profile.enum, ["compact", "balanced", "full"]);
  assert.equal(props.profile.default, "compact");
});

test("resource_get_data max_depth has bounds [0, 6]", () => {
  const props = resourceGetData.inputSchema.properties as Record<
    string,
    { minimum?: number; maximum?: number }
  >;
  assert.equal(props.max_depth.minimum, 0);
  assert.equal(props.max_depth.maximum, 6);
});

test("resource_get_data collection_page_size defaults to 50 with bounds", () => {
  const props = resourceGetData.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number; maximum?: number }
  >;
  assert.equal(props.collection_page_size.default, 50);
  assert.equal(props.collection_page_size.minimum, 1);
  assert.equal(props.collection_page_size.maximum, 200);
});

test("resource_get_data has no required array (path validated at runtime)", () => {
  assert.equal(resourceGetData.inputSchema.required, undefined);
});

// ---------------------------------------------------------------------------
// Registration.
// ---------------------------------------------------------------------------

test("ALL_TOOLS registers both P4.1 resource tools", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(names.includes("godot_open_mcp_resource_find"), "resource_find missing from ALL_TOOLS");
  assert.ok(names.includes("godot_open_mcp_resource_get_data"), "resource_get_data missing from ALL_TOOLS");
});
