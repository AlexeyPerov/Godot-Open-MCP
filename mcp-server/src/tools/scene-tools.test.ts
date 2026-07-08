// `godot_open_mcp_scene_open` / `scene_save` / `scene_list_opened` tool-
// definition tests (P2.6). Pins the catalog metadata the MCP ListTools response
// advertises to AI clients for the three scene lifecycle tools — name prefix
// per ADR-003, non-empty descriptions, the Godot-adapted property sets, and the
// `additionalProperties: false` guards. The live round-trip (POST
// /tools/godot_open_mcp_scene_{open,save,list_opened} → bridge handler →
// SceneSummary / saved[] envelope) is exercised against a local HTTP stub in
// live-client.test.ts; this file only asserts the contracts advertised over
// stdio.
//
// Adapted from node-tree-ops.test.ts (copy fidelity for the catalog-metadata
// test shape), with property-set assertions specific to each scene schema and
// the Unity deltas documented in scene-open.ts / scene-save.ts /
// scene-list-opened.ts.

import { test } from "node:test";
import assert from "node:assert/strict";
import { sceneOpen } from "./scene-open.js";
import { sceneSave } from "./scene-save.js";
import { sceneListOpened } from "./scene-list-opened.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// Mutator shape checks (scene_open + scene_save share the mutating-tool
// forward-compat surface: paths_hint array + gate enum defaulting off).
// ---------------------------------------------------------------------------

const mutatorNames = [
  ["godot_open_mcp_scene_open", sceneOpen],
  ["godot_open_mcp_scene_save", sceneSave],
] as const;

for (const [expectedName, tool] of mutatorNames) {
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

  test(`${expectedName} gate defaults to off`, () => {
    // Default 'off' because the gate flow is not wired yet (P3.5). Pinned so a
    // future change does not silently flip the advertised default before the
    // gate actually lands.
    const props = tool.inputSchema.properties as Record<
      string,
      { default?: string; enum?: string[] }
    >;
    assert.equal(props.gate.default, "off");
    assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
  });

  test(`${expectedName} paths_hint is an array of strings`, () => {
    const props = tool.inputSchema.properties as Record<
      string,
      { type?: string; items?: { type?: string } }
    >;
    assert.equal(props.paths_hint.type, "array");
    assert.equal(props.paths_hint.items?.type, "string");
  });

  test(`${expectedName} does NOT expose Unity-only fields`, () => {
    // Guards against an accidental copy-paste from Unity's scene-open / scene-save
    // schemas bringing back Unity-specific resolvers.
    const props = tool.inputSchema.properties as Record<string, unknown>;
    assert.equal(props.mode, undefined, "Godot has no additive/single mode in P2.6");
  });
}

// ---------------------------------------------------------------------------
// scene_open — property set + ignore_dirty.
// ---------------------------------------------------------------------------

test("scene_open tool exposes the Godot-adapted property set", () => {
  // path replaces Unity's path (same name, res:// not a Unity asset path).
  // ignore_dirty replaces Unity's ignore_scene_dirty. paths_hint + gate are the
  // forward-compat mutating-tool fields. Unity's mode (Single/Additive) is
  // absent — Godot opens scenes in tabs.
  const props = sceneOpen.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "ignore_dirty",
    "path",
    "paths_hint",
  ]);
});

test("scene_open does NOT expose Unity-only fields", () => {
  const props = sceneOpen.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.ignore_scene_dirty, undefined, "renamed to ignore_dirty");
  assert.equal(props.mode, undefined, "Godot has no additive/single mode in P2.6");
});

test("scene_open ignore_dirty defaults to false", () => {
  // Default false so the bridge refuses a dirty open by default (scene_dirty)
  // rather than discarding edits.
  const props = sceneOpen.inputSchema.properties as Record<
    string,
    { type?: string; default?: boolean }
  >;
  assert.equal(props.ignore_dirty.type, "boolean");
  assert.equal(props.ignore_dirty.default, false);
});

test("scene_open has no required array (path validated at runtime)", () => {
  // path is required at runtime, but the handler surfaces missing_parameter
  // with a specific message rather than relying on JSON-schema required
  // validation (keeps the error contract uniform with the rest of the tool
  // families).
  assert.equal(sceneOpen.inputSchema.required, undefined);
});

// ---------------------------------------------------------------------------
// scene_save — property set + save_all + save-as path.
// ---------------------------------------------------------------------------

test("scene_save tool exposes the Godot-adapted property set", () => {
  // path (optional save-as target) + save_all replace Unity's path + save_all.
  // paths_hint + gate are the forward-compat mutating-tool fields. Unity's
  // discard (close-without-save) is absent — P2.6 is save-only.
  const props = sceneSave.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "path",
    "paths_hint",
    "save_all",
  ]);
});

test("scene_save does NOT expose Unity-only fields", () => {
  const props = sceneSave.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.discard, undefined, "P2.6 is save-only; no discard");
});

test("scene_save save_all defaults to false", () => {
  const props = sceneSave.inputSchema.properties as Record<
    string,
    { type?: string; default?: boolean }
  >;
  assert.equal(props.save_all.type, "boolean");
  assert.equal(props.save_all.default, false);
});

test("scene_save has no required array (modes resolved at runtime)", () => {
  assert.equal(sceneSave.inputSchema.required, undefined);
});

// ---------------------------------------------------------------------------
// scene_list_opened — read-only (no paths_hint / gate).
// ---------------------------------------------------------------------------

test("scene_list_opened tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(sceneListOpened.name, "godot_open_mcp_scene_list_opened");
  assert.match(sceneListOpened.name, /^godot_open_mcp_/);
});

test("scene_list_opened tool has a non-empty description", () => {
  assert.ok(typeof sceneListOpened.description === "string");
  assert.ok((sceneListOpened.description ?? "").length > 0);
});

test("scene_list_opened tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(sceneListOpened.inputSchema.type, "object");
  assert.equal(sceneListOpened.inputSchema.additionalProperties, false);
});

test("scene_list_opened has no properties (read-only, no args)", () => {
  // Read-only tools carry no forward-compat paths_hint/gate fields — they are
  // gate-free by definition. The schema is an empty object.
  assert.deepEqual(sceneListOpened.inputSchema.properties, {});
});

test("scene_list_opened does NOT expose Unity-only fields", () => {
  // Guards against an accidental copy-paste from Unity's scene-list-opened
  // schema bringing back build index / isLoaded (Godot has neither for editor
  // scenes).
  const props = sceneListOpened.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.build_index, undefined, "Godot has no build index for editor scenes");
  assert.equal(props.is_loaded, undefined, "Godot has no isLoaded flag for editor scenes");
  assert.equal(props.paths_hint, undefined, "read-only tool — no gate surface");
  assert.equal(props.gate, undefined, "read-only tool — no gate surface");
});

test("ALL_TOOLS registers all three scene lifecycle tools", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(names.includes("godot_open_mcp_scene_open"), "scene_open missing from ALL_TOOLS");
  assert.ok(names.includes("godot_open_mcp_scene_save"), "scene_save missing from ALL_TOOLS");
  assert.ok(
    names.includes("godot_open_mcp_scene_list_opened"),
    "scene_list_opened missing from ALL_TOOLS",
  );
});
