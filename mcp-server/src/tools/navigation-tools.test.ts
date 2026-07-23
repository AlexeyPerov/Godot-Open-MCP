// P12.2 navigation domain pack — schema + group-assignment parity tests.
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// (name prefix, non-empty description, mutating-tool shape with paths_hint
// required + gate default enforce, read-only tool shape without paths_hint) and
// the group assignment (all seven tools map to `navigation`). The live round-trip
// (POST /tools/godot_open_mcp_navigation_* → bridge handler → result envelope)
// is exercised by the headless Godot smoke; this file only asserts the contract
// advertised over stdio.
//
// Adapted from tilemap-tools.test.ts (copy fidelity for the catalog-metadata test
// shape), with assertions specific to the navigation pack's mutating vs read-only
// split and the Godot dimension-selection property set.

import { test } from "node:test";
import assert from "node:assert/strict";

import { navigationDefaults } from "./navigation-defaults.js";
import { navigationRegionCreate } from "./navigation-region-create.js";
import { navigationRegionSetMesh } from "./navigation-region-set-mesh.js";
import { navigationAgentCreate } from "./navigation-agent-create.js";
import { navigationAgentConfigure } from "./navigation-agent-configure.js";
import { navigationLinkCreate } from "./navigation-link-create.js";
import { navigationGet } from "./navigation-get.js";
import { groupFor, toolsInGroup } from "../capabilities/tool-groups.js";

const ALL_NAVIGATION_TOOLS = [
  navigationDefaults,
  navigationRegionCreate,
  navigationRegionSetMesh,
  navigationAgentCreate,
  navigationAgentConfigure,
  navigationLinkCreate,
  navigationGet,
];

const MUTATING_TOOLS = [
  navigationRegionCreate,
  navigationRegionSetMesh,
  navigationAgentCreate,
  navigationAgentConfigure,
  navigationLinkCreate,
];

const READ_ONLY_TOOLS = [navigationDefaults, navigationGet];

// ---------------------------------------------------------------------------
// Names + group assignment
// ---------------------------------------------------------------------------

test("every navigation tool name follows the godot_open_mcp_navigation_* convention", () => {
  for (const t of ALL_NAVIGATION_TOOLS) {
    assert.match(t.name, /^godot_open_mcp_navigation_[a-z0-9_]+$/);
  }
});

test("every navigation tool is assigned to the navigation group", () => {
  for (const t of ALL_NAVIGATION_TOOLS) {
    assert.equal(groupFor(t.name), "navigation", `${t.name} must map to navigation`);
  }
});

test("toolsInGroup(navigation) returns exactly the seven pack tools", () => {
  assert.deepEqual(toolsInGroup("navigation"), [
    "godot_open_mcp_navigation_agent_configure",
    "godot_open_mcp_navigation_agent_create",
    "godot_open_mcp_navigation_defaults",
    "godot_open_mcp_navigation_get",
    "godot_open_mcp_navigation_link_create",
    "godot_open_mcp_navigation_region_create",
    "godot_open_mcp_navigation_region_set_mesh",
  ]);
});

// ---------------------------------------------------------------------------
// Shared catalog-metadata invariants
// ---------------------------------------------------------------------------

test("every navigation tool has a non-empty description", () => {
  for (const t of ALL_NAVIGATION_TOOLS) {
    assert.ok(typeof t.description === "string");
    assert.ok((t.description ?? "").length > 0, `${t.name} description is empty`);
  }
});

test("every navigation tool declares an object input schema with additionalProperties:false", () => {
  for (const t of ALL_NAVIGATION_TOOLS) {
    assert.equal(t.inputSchema.type, "object", `${t.name} schema type`);
    assert.equal(t.inputSchema.additionalProperties, false, `${t.name} additionalProperties`);
  }
});

test("every navigation tool description tells the agent to activate the navigation group", () => {
  // The group is default-off; the description must surface the activation step so an
  // agent does not call a hidden tool blind. defaults and get are also in the group —
  // they must mention activation too (same contract as the tilemap read-only tool).
  for (const t of ALL_NAVIGATION_TOOLS) {
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

test("every mutating navigation tool requires paths_hint", () => {
  for (const t of MUTATING_TOOLS) {
    assert.ok(
      (t.inputSchema.required ?? []).includes("paths_hint"),
      `${t.name} must require paths_hint`,
    );
  }
});

test("every mutating navigation tool defaults gate to enforce", () => {
  for (const t of MUTATING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { default?: string }>).gate;
    assert.equal(gate?.default, "enforce", `${t.name} gate default must be enforce`);
  }
});

test("every mutating navigation tool gate enum is [enforce, warn, off]", () => {
  for (const t of MUTATING_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { enum?: string[] }>).gate;
    assert.deepEqual(gate?.enum, ["enforce", "warn", "off"], `${t.name} gate enum`);
  }
});

test("paths_hint is an array of strings on every mutating navigation tool", () => {
  for (const t of MUTATING_TOOLS) {
    const ph = (t.inputSchema.properties as Record<string, { type?: string; items?: { type?: string } }>).paths_hint;
    assert.equal(ph?.type, "array", `${t.name} paths_hint type`);
    assert.equal(ph?.items?.type, "string", `${t.name} paths_hint items type`);
  }
});

// ---------------------------------------------------------------------------
// Read-only tools — no paths_hint, no gate
// ---------------------------------------------------------------------------

test("navigation_defaults is read-only (no paths_hint, no gate)", () => {
  const props = navigationDefaults.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "defaults must not declare paths_hint");
  assert.equal(props.gate, undefined, "defaults must not declare gate");
  assert.deepEqual(Object.keys(props).sort(), ["dimension"]);
});

test("navigation_get is read-only (no paths_hint, no gate)", () => {
  const props = navigationGet.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "get must not declare paths_hint");
  assert.equal(props.gate, undefined, "get must not declare gate");
  assert.deepEqual(Object.keys(props).sort(), ["node_path"]);
});

test("read-only navigation tools do not list paths_hint as required", () => {
  for (const t of READ_ONLY_TOOLS) {
    assert.ok(
      !(t.inputSchema.required ?? []).includes("paths_hint"),
      `${t.name} must not require paths_hint`,
    );
  }
});

// ---------------------------------------------------------------------------
// Per-tool property sets
// ---------------------------------------------------------------------------

test("navigation_defaults exposes only dimension", () => {
  const props = navigationDefaults.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["dimension"]);
  assert.deepEqual(
    (navigationDefaults.inputSchema.required ?? []).sort(),
    ["dimension"],
  );
});

test("navigation_defaults dimension enum is [2d, 3d]", () => {
  const dim = (navigationDefaults.inputSchema.properties as Record<string, { enum?: string[] }>).dimension;
  assert.deepEqual(dim?.enum, ["2d", "3d"]);
});

test("navigation_region_create exposes the dimension + create property set", () => {
  const props = navigationRegionCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "dimension",
    "gate",
    "name",
    "parent_node_path",
    "paths_hint",
    "position",
  ]);
  assert.deepEqual(
    (navigationRegionCreate.inputSchema.required ?? []).sort(),
    ["dimension", "paths_hint"],
  );
});

test("navigation_region_set_mesh exposes node_path + mesh_path", () => {
  const props = navigationRegionSetMesh.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "gate",
    "mesh_path",
    "node_path",
    "paths_hint",
  ]);
  assert.deepEqual(
    (navigationRegionSetMesh.inputSchema.required ?? []).sort(),
    ["mesh_path", "node_path", "paths_hint"],
  );
});

test("navigation_agent_create exposes the dimension + create property set", () => {
  const props = navigationAgentCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "dimension",
    "gate",
    "name",
    "parent_node_path",
    "paths_hint",
    "position",
  ]);
  assert.deepEqual(
    (navigationAgentCreate.inputSchema.required ?? []).sort(),
    ["dimension", "paths_hint"],
  );
});

test("navigation_agent_configure exposes the clamped-scalar property set", () => {
  const props = navigationAgentConfigure.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "avoidance_enabled",
    "gate",
    "height",
    "max_speed",
    "node_path",
    "path_desired_distance",
    "paths_hint",
    "radius",
    "target_desired_distance",
  ]);
  assert.deepEqual(
    (navigationAgentConfigure.inputSchema.required ?? []).sort(),
    ["node_path", "paths_hint"],
  );
});

test("navigation_agent_configure radius + distances are strictly positive", () => {
  const props = navigationAgentConfigure.inputSchema.properties as Record<
    string,
    { exclusiveMinimum?: number; minimum?: number }
  >;
  assert.equal(props.radius.exclusiveMinimum, 0, "radius must be strictly positive");
  assert.equal(props.path_desired_distance.exclusiveMinimum, 0, "path_desired_distance must be strictly positive");
  assert.equal(props.target_desired_distance.exclusiveMinimum, 0, "target_desired_distance must be strictly positive");
});

test("navigation_agent_configure height + max_speed are non-negative", () => {
  const props = navigationAgentConfigure.inputSchema.properties as Record<
    string,
    { exclusiveMinimum?: number; minimum?: number }
  >;
  assert.equal(props.height.minimum, 0, "height must be non-negative");
  assert.equal(props.max_speed.minimum, 0, "max_speed must be non-negative");
});

test("navigation_link_create exposes dimension + start/end + bidirectional", () => {
  const props = navigationLinkCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "bidirectional",
    "dimension",
    "end_position",
    "gate",
    "name",
    "parent_node_path",
    "paths_hint",
    "position",
    "start_position",
  ]);
  assert.deepEqual(
    (navigationLinkCreate.inputSchema.required ?? []).sort(),
    ["dimension", "end_position", "paths_hint", "start_position"],
  );
});

test("navigation_link_create bidirectional defaults to false", () => {
  const bi = (navigationLinkCreate.inputSchema.properties as Record<string, { default?: boolean }>).bidirectional;
  assert.equal(bi?.default, false);
});

test("navigation_get exposes node_path only", () => {
  const props = navigationGet.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["node_path"]);
  assert.deepEqual(
    (navigationGet.inputSchema.required ?? []).sort(),
    ["node_path"],
  );
});

// ---------------------------------------------------------------------------
// Dimension enum is consistent across every create tool + defaults
// ---------------------------------------------------------------------------

test("every dimension-accepting tool pins the [2d, 3d] enum", () => {
  for (const t of [navigationDefaults, navigationRegionCreate, navigationAgentCreate, navigationLinkCreate]) {
    const dim = (t.inputSchema.properties as Record<string, { enum?: string[] }>).dimension;
    assert.deepEqual(dim?.enum, ["2d", "3d"], `${t.name} dimension enum`);
  }
});
