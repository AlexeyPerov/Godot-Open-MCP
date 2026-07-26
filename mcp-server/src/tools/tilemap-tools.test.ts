// P12.1 tilemap domain pack — schema + group-assignment parity tests.
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// (name prefix, non-empty description, mutating-tool shape with paths_hint
// required + gate default enforce, read-only tool shape without paths_hint) and
// the group assignment (all six tools map to `tilemap`). The live round-trip
// (POST /tools/godot_open_mcp_tilemap_* → bridge handler → result envelope) is
// exercised by the headless Godot smoke; this file only asserts the contract
// advertised over stdio.
//
// Adapted from node-create.test.ts (copy fidelity for the catalog-metadata test
// shape), with assertions specific to the tilemap pack's mutating vs read-only
// split and the Godot atlas-addressing property set.

import { test } from "node:test";
import assert from "node:assert/strict";

import { tilemapCreate } from "./tilemap-create.js";
import { tilemapSetTileset } from "./tilemap-set-tileset.js";
import { tilemapSetCell } from "./tilemap-set-cell.js";
import { tilemapEraseCell } from "./tilemap-erase-cell.js";
import { tilemapGetUsedCells } from "./tilemap-get-used-cells.js";
import { tilemapClear } from "./tilemap-clear.js";
import { groupFor, toolsInGroup } from "../capabilities/tool-groups.js";

const ALL_TILEMAP_TOOLS = [
  tilemapCreate,
  tilemapSetTileset,
  tilemapSetCell,
  tilemapEraseCell,
  tilemapGetUsedCells,
  tilemapClear,
];

const MUTATING_TOOLS = [
  tilemapCreate,
  tilemapSetTileset,
  tilemapSetCell,
  tilemapEraseCell,
  tilemapClear,
];

const READ_ONLY_TOOLS = [tilemapGetUsedCells];

// ---------------------------------------------------------------------------
// Names + group assignment
// ---------------------------------------------------------------------------

test("every tilemap tool name follows the godot_open_mcp_tilemap_* convention", () => {
  for (const t of ALL_TILEMAP_TOOLS) {
    assert.match(t.name, /^godot_open_mcp_tilemap_[a-z0-9_]+$/);
  }
});

test("every tilemap tool is assigned to the tilemap group", () => {
  for (const t of ALL_TILEMAP_TOOLS) {
    assert.equal(groupFor(t.name), "tilemap", `${t.name} must map to tilemap`);
  }
});

test("toolsInGroup(tilemap) returns exactly the six pack tools", () => {
  assert.deepEqual(toolsInGroup("tilemap"), [
    "godot_open_mcp_tilemap_clear",
    "godot_open_mcp_tilemap_create",
    "godot_open_mcp_tilemap_erase_cell",
    "godot_open_mcp_tilemap_get_used_cells",
    "godot_open_mcp_tilemap_set_cell",
    "godot_open_mcp_tilemap_set_tileset",
  ]);
});

// ---------------------------------------------------------------------------
// Shared catalog-metadata invariants
// ---------------------------------------------------------------------------

test("every tilemap tool has a non-empty description", () => {
  for (const t of ALL_TILEMAP_TOOLS) {
    assert.ok(typeof t.description === "string");
    assert.ok((t.description ?? "").length > 0, `${t.name} description is empty`);
  }
});

test("every tilemap tool declares an object input schema with additionalProperties:false", () => {
  for (const t of ALL_TILEMAP_TOOLS) {
    assert.equal(t.inputSchema.type, "object", `${t.name} schema type`);
    assert.equal(t.inputSchema.additionalProperties, false, `${t.name} additionalProperties`);
  }
});

test("every tilemap tool description tells the agent to activate the tilemap group", () => {
  // The group is default-off; the description must surface the activation step so an
  // agent does not call a hidden tool blind.
  for (const t of ALL_TILEMAP_TOOLS) {
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

test("every mutating tilemap tool requires paths_hint", () => {
  for (const t of MUTATING_TOOLS) {
    assert.ok(
      (t.inputSchema.required ?? []).includes("paths_hint"),
      `${t.name} must require paths_hint`,
    );
  }
});

test("every mutating tilemap tool defaults gate to enforce", () => {
  for (const t of MUTATING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { default?: string }>).gate;
    assert.equal(gate?.default, "enforce", `${t.name} gate default must be enforce`);
  }
});

test("every mutating tilemap tool gate enum is [enforce, warn, off]", () => {
  for (const t of MUTATING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { enum?: string[] }>).gate;
    assert.deepEqual(gate?.enum, ["enforce", "warn", "off"], `${t.name} gate enum`);
  }
});

test("paths_hint is an array of strings on every mutating tilemap tool", () => {
  for (const t of MUTATING_TOOLS) {
    const ph = (t.inputSchema.properties as Record<string, { type?: string; items?: { type?: string } }>).paths_hint;
    assert.equal(ph?.type, "array", `${t.name} paths_hint type`);
    assert.equal(ph?.items?.type, "string", `${t.name} paths_hint items type`);
  }
});

// ---------------------------------------------------------------------------
// Read-only tool — no paths_hint, no gate
// ---------------------------------------------------------------------------

test("tilemap_get_used_cells is read-only (no paths_hint, no gate)", () => {
  const props = tilemapGetUsedCells.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "get_used_cells must not declare paths_hint");
  assert.equal(props.gate, undefined, "get_used_cells must not declare gate");
  // Only node_path + max_results.
  assert.deepEqual(Object.keys(props).sort(), ["max_results", "node_path"]);
});

test("tilemap_get_used_cells does not list paths_hint as required", () => {
  assert.ok(
    !(tilemapGetUsedCells.inputSchema.required ?? []).includes("paths_hint"),
  );
});

// ---------------------------------------------------------------------------
// Per-tool property sets
// ---------------------------------------------------------------------------

test("tilemap_create exposes the Godot-adapted property set", () => {
  const props = tilemapCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "name",
    "parent_node_path",
    "paths_hint",
    "position",
  ]);
});

test("tilemap_set_tileset exposes node_path + tileset_path", () => {
  const props = tilemapSetTileset.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "node_path",
    "paths_hint",
    "tileset_path",
  ]);
  assert.deepEqual(
    (tilemapSetTileset.inputSchema.required ?? []).sort(),
    ["node_path", "paths_hint", "tileset_path"],
  );
});

test("tilemap_set_cell exposes the atlas addressing quadruple", () => {
  const props = tilemapSetCell.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "alternative_tile",
    "atlas_x",
    "atlas_y",
    "gate",
    "node_path",
    "paths_hint",
    "source_id",
    "x",
    "y",
  ]);
  assert.deepEqual(
    (tilemapSetCell.inputSchema.required ?? []).sort(),
    ["node_path", "paths_hint", "x", "y"],
  );
});

test("tilemap_set_cell atlas quadruple defaults to zero", () => {
  // The atlas quadruple (source_id / atlas_x / atlas_y / alternative_tile) defaults to
  // (0, 0, 0, 0) so a single-source single-tile TileSet can omit every optional field.
  const props = tilemapSetCell.inputSchema.properties as Record<
    string,
    { default?: number }
  >;
  assert.equal(props.source_id.default, 0);
  assert.equal(props.atlas_x.default, 0);
  assert.equal(props.atlas_y.default, 0);
  assert.equal(props.alternative_tile.default, 0);
});

test("tilemap_erase_cell exposes node_path + x + y only", () => {
  const props = tilemapEraseCell.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["gate", "node_path", "paths_hint", "x", "y"]);
});

test("tilemap_clear exposes node_path only (plus gate control)", () => {
  const props = tilemapClear.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["gate", "node_path", "paths_hint"]);
});

test("tilemap_get_used_cells max_results defaults to 256, hard-capped at 2000", () => {
  // No `minimum`: the bridge's EffectiveMaxResults falls back to the default for non-positive
  // values (unit-tested in TilemapBodiesTests). The schema declares the hard cap (maximum: 2000)
  // so a validating client knows where the silent clamp kicks in.
  const max = (tilemapGetUsedCells.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number; maximum?: number }
  >).max_results;
  assert.equal(max.default, 256);
  assert.equal(max.minimum, undefined);
  assert.equal(max.maximum, 2000);
});
