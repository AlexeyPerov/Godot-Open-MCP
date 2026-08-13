// gridmap domain pack — schema + group-assignment parity tests.
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// (name prefix, non-empty description, mutating-tool shape with paths_hint
// required + gate default enforce, read-only tool shape without paths_hint) and
// the group assignment (all six tools map to `gridmap`). The live round-trip
// (POST /tools/godot_open_mcp_gridmap_* → bridge handler → result envelope) is
// exercised by the headless Godot smoke; this file only asserts the contract
// advertised over stdio.
//
// Adapted from tilemap-tools.test.ts (copy fidelity for the catalog-metadata
// test shape), with assertions specific to the gridmap pack's 3D cell addressing
// (x, y, z + item + orientation).

import { test } from "node:test";
import assert from "node:assert/strict";

import { gridmapCreate } from "./gridmap-create.js";
import { gridmapSetMeshLibrary } from "./gridmap-set-mesh-library.js";
import { gridmapSetCell } from "./gridmap-set-cell.js";
import { gridmapEraseCell } from "./gridmap-erase-cell.js";
import { gridmapGetUsedCells } from "./gridmap-get-used-cells.js";
import { gridmapClear } from "./gridmap-clear.js";
import { groupFor, toolsInGroup } from "../capabilities/tool-groups.js";

const ALL_GRIDMAP_TOOLS = [
  gridmapCreate,
  gridmapSetMeshLibrary,
  gridmapSetCell,
  gridmapEraseCell,
  gridmapGetUsedCells,
  gridmapClear,
];

const MUTATING_TOOLS = [
  gridmapCreate,
  gridmapSetMeshLibrary,
  gridmapSetCell,
  gridmapEraseCell,
  gridmapClear,
];

const READ_ONLY_TOOLS = [gridmapGetUsedCells];

// ---------------------------------------------------------------------------
// Names + group assignment
// ---------------------------------------------------------------------------

test("every gridmap tool name follows the godot_open_mcp_gridmap_* convention", () => {
  for (const t of ALL_GRIDMAP_TOOLS) {
    assert.match(t.name, /^godot_open_mcp_gridmap_[a-z0-9_]+$/);
  }
});

test("every gridmap tool is assigned to the gridmap group", () => {
  for (const t of ALL_GRIDMAP_TOOLS) {
    assert.equal(groupFor(t.name), "gridmap", `${t.name} must map to gridmap`);
  }
});

test("toolsInGroup(gridmap) returns exactly the six pack tools", () => {
  assert.deepEqual(toolsInGroup("gridmap"), [
    "godot_open_mcp_gridmap_clear",
    "godot_open_mcp_gridmap_create",
    "godot_open_mcp_gridmap_erase_cell",
    "godot_open_mcp_gridmap_get_used_cells",
    "godot_open_mcp_gridmap_set_cell",
    "godot_open_mcp_gridmap_set_mesh_library",
  ]);
});

// ---------------------------------------------------------------------------
// Shared catalog-metadata invariants
// ---------------------------------------------------------------------------

test("every gridmap tool has a non-empty description", () => {
  for (const t of ALL_GRIDMAP_TOOLS) {
    assert.ok(typeof t.description === "string");
    assert.ok((t.description ?? "").length > 0, `${t.name} description is empty`);
  }
});

test("every gridmap tool declares an object input schema with additionalProperties:false", () => {
  for (const t of ALL_GRIDMAP_TOOLS) {
    assert.equal(t.inputSchema.type, "object", `${t.name} schema type`);
    assert.equal(t.inputSchema.additionalProperties, false, `${t.name} additionalProperties`);
  }
});

test("every gridmap tool description tells the agent to activate the gridmap group", () => {
  for (const t of ALL_GRIDMAP_TOOLS) {
    assert.match(
      t.description ?? "",
      /activate the group with manage_tools/,
      `${t.name} description must mention manage_tools activation`,
    );
  }
});

// ---------------------------------------------------------------------------
// Mutating tools — paths_hint required + gate default enforce
// ---------------------------------------------------------------------------

test("every mutating gridmap tool requires paths_hint", () => {
  for (const t of MUTATING_TOOLS) {
    assert.ok(
      (t.inputSchema.required ?? []).includes("paths_hint"),
      `${t.name} must require paths_hint`,
    );
  }
});

test("every mutating gridmap tool defaults gate to enforce", () => {
  for (const t of MUTATING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { default?: string }>).gate;
    assert.equal(gate?.default, "enforce", `${t.name} gate default must be enforce`);
  }
});

test("every mutating gridmap tool gate enum is [enforce, warn, off]", () => {
  for (const t of MUTATING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { enum?: string[] }>).gate;
    assert.deepEqual(gate?.enum, ["enforce", "warn", "off"], `${t.name} gate enum`);
  }
});

test("paths_hint is an array of strings on every mutating gridmap tool", () => {
  for (const t of MUTATING_TOOLS) {
    const ph = (t.inputSchema.properties as Record<string, { type?: string; items?: { type?: string } }>).paths_hint;
    assert.equal(ph?.type, "array", `${t.name} paths_hint type`);
    assert.equal(ph?.items?.type, "string", `${t.name} paths_hint items type`);
  }
});

// ---------------------------------------------------------------------------
// Read-only tool — no paths_hint, no gate
// ---------------------------------------------------------------------------

test("gridmap_get_used_cells is read-only (no paths_hint, no gate)", () => {
  const props = gridmapGetUsedCells.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "get_used_cells must not declare paths_hint");
  assert.equal(props.gate, undefined, "get_used_cells must not declare gate");
  assert.deepEqual(Object.keys(props).sort(), ["max_results", "node_path"]);
});

test("gridmap_get_used_cells does not list paths_hint as required", () => {
  assert.ok(
    !(gridmapGetUsedCells.inputSchema.required ?? []).includes("paths_hint"),
  );
});

// ---------------------------------------------------------------------------
// Per-tool property sets
// ---------------------------------------------------------------------------

test("gridmap_create exposes the node-creation property set", () => {
  const props = gridmapCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "name",
    "parent_node_path",
    "paths_hint",
    "position",
  ]);
});

test("gridmap_set_mesh_library exposes node_path + mesh_library_path", () => {
  const props = gridmapSetMeshLibrary.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "mesh_library_path",
    "node_path",
    "paths_hint",
  ]);
  assert.deepEqual(
    (gridmapSetMeshLibrary.inputSchema.required ?? []).sort(),
    ["mesh_library_path", "node_path", "paths_hint"],
  );
});

test("gridmap_set_cell exposes the 3D cell tuple", () => {
  const props = gridmapSetCell.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "item",
    "node_path",
    "orientation",
    "paths_hint",
    "x",
    "y",
    "z",
  ]);
  assert.deepEqual(
    (gridmapSetCell.inputSchema.required ?? []).sort(),
    ["item", "node_path", "paths_hint", "x", "y", "z"],
  );
});

test("gridmap_set_cell orientation defaults to zero", () => {
  const props = gridmapSetCell.inputSchema.properties as Record<string, { default?: number }>;
  assert.equal(props.orientation.default, 0);
});

test("gridmap_erase_cell exposes node_path + x + y + z only", () => {
  const props = gridmapEraseCell.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "node_path",
    "paths_hint",
    "x",
    "y",
    "z",
  ]);
});

test("gridmap_clear exposes node_path only (plus gate control)", () => {
  const props = gridmapClear.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["gate", "node_path", "paths_hint"]);
});

test("gridmap_get_used_cells max_results defaults to 256, hard-capped at 2000", () => {
  const max = (gridmapGetUsedCells.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number; maximum?: number }
  >).max_results;
  assert.equal(max.default, 256);
  assert.equal(max.minimum, undefined);
  assert.equal(max.maximum, 2000);
});
