// route-policy catalog tests (P7.5).
//
// Pins the canonical route-policy vocabulary and the per-tool override sets so
// the advertised capability catalog and the shipped router cannot drift. Three
// concerns:
//   1. The override sets are disjoint (a tool is never in two policy sets).
//   2. Every override name is a real registered tool (`ALL_TOOLS`).
//   3. The router's named-handler classification matches the catalog's
//      non-`live` policies (the anti-drift contract).
//
// Adapted from Unity Open MCP's build-capabilities.test.ts route-coverage
// pattern (copy for the "every tool classifies" shape); Godot-specific deltas:
// the `live-first` policy, the no-batch assertion, and the router-parity test.

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  routePolicyFor,
  ROUTE_POLICIES,
  ROUTING_SUMMARY,
  SPECIAL_ROUTE_TOOLS,
  DEFAULT_ROUTE_POLICY,
  CAPABILITIES_TOOL,
  BRIDGE_STATUS_TOOL,
  PULL_EVENTS_TOOL,
  MANAGE_TOOLS_TOOL,
  SCENE_GET_DATA_TOOL,
  FILESYSTEM_LIST_TOOL,
  READ_COMPILE_ERRORS_TOOL,
  FIND_REFERENCES_TOOL,
  DEPENDENCIES_TOOL,
  BASELINE_CREATE_TOOL,
  REGRESSION_CHECK_TOOL,
  RESTART_EDITOR_TOOL,
  RESOURCE_PRESSURE_TOOL,
  type RoutePolicy,
} from "./route-policy.js";
import { ALL_TOOLS } from "../tools/index.js";

/**
 * The complete set of tool names the router dispatches via a named handler
 * (i.e. NOT the generic live fallback). This is the router-side anti-drift
 * roster: every name here MUST appear in a non-`live` override set, and every
 * non-`live` override name MUST appear here. The parity test below pins both
 * directions.
 */
const ROUTER_NAMED_HANDLERS: ReadonlySet<string> = new Set([
  CAPABILITIES_TOOL,
  BRIDGE_STATUS_TOOL,
  PULL_EVENTS_TOOL,
  MANAGE_TOOLS_TOOL,
  SCENE_GET_DATA_TOOL,
  FILESYSTEM_LIST_TOOL,
  READ_COMPILE_ERRORS_TOOL,
  FIND_REFERENCES_TOOL,
  DEPENDENCIES_TOOL,
  BASELINE_CREATE_TOOL,
  REGRESSION_CHECK_TOOL,
  RESTART_EDITOR_TOOL,
  RESOURCE_PRESSURE_TOOL,
]);

const REGISTERED = new Set(ALL_TOOLS.map((t) => t.name));

test("route-policy vocabulary is exactly live | local | offline | live-first (no batch)", () => {
  assert.deepEqual<RoutePolicy[]>([...ROUTE_POLICIES], [
    "live",
    "local",
    "offline",
    "live-first",
  ]);
  // The catalog must never advertise a `batch` policy — Godot has no headless
  // editor batch equivalent. Pinned explicitly so a future addition cannot
  // silently reintroduce it.
  assert.ok(
    !(ROUTE_POLICIES as readonly string[]).includes("batch"),
    "no `batch` route policy may appear in the catalog",
  );
  assert.ok(
    !(ROUTE_POLICIES as readonly string[]).includes("batchCapable"),
    "no `batchCapable` route policy may appear in the catalog",
  );
});

test("default route policy is live", () => {
  assert.equal(DEFAULT_ROUTE_POLICY, "live");
});

test("override sets are disjoint — no tool in two policy sets", () => {
  const sets = [
    { policy: "local", names: [...SPECIAL_ROUTE_TOOLS.local] },
    { policy: "offline", names: [...SPECIAL_ROUTE_TOOLS.offline] },
    { policy: "live-first", names: [...SPECIAL_ROUTE_TOOLS["live-first"]] },
  ];
  for (let i = 0; i < sets.length; i++) {
    for (let j = i + 1; j < sets.length; j++) {
      const a = new Set(sets[i].names);
      const overlap = sets[j].names.filter((n) => a.has(n));
      assert.deepEqual(
        overlap,
        [],
        `${sets[i].policy} and ${sets[j].policy} overlap on: ${overlap.join(", ")}`,
      );
    }
  }
  // Each override set is itself duplicate-free.
  for (const s of sets) {
    assert.equal(
      new Set(s.names).size,
      s.names.length,
      `${s.policy} set has duplicates`,
    );
  }
});

test("every override name is a registered tool", () => {
  for (const policy of ["local", "offline", "live-first"] as const) {
    const names = SPECIAL_ROUTE_TOOLS[policy];
    for (const name of names) {
      assert.ok(
        REGISTERED.has(name),
        `${policy} override lists '${name}' which is not in ALL_TOOLS`,
      );
    }
  }
});

test("every registered tool classifies under exactly one policy", () => {
  // No tool should be left unclassified — `routePolicyFor` always returns a
  // value (defaulting to `live`), but assert the return is a valid policy
  // member for every registered tool.
  for (const tool of ALL_TOOLS) {
    const policy = routePolicyFor(tool.name);
    assert.ok(
      (ROUTE_POLICIES as readonly string[]).includes(policy),
      `${tool.name} policy '${policy}' is not in the canonical vocabulary`,
    );
  }
});

test("representative pins: capabilities/bridge_status/pull_events/manage_tools → local", () => {
  assert.equal(routePolicyFor("godot_open_mcp_capabilities"), "local");
  assert.equal(routePolicyFor("godot_open_mcp_bridge_status"), "local");
  assert.equal(routePolicyFor("godot_open_mcp_pull_events"), "local");
  assert.equal(routePolicyFor("godot_open_mcp_manage_tools"), "local");
});

test("representative pin: read_compile_errors → offline", () => {
  assert.equal(routePolicyFor("godot_open_mcp_read_compile_errors"), "offline");
});

test("representative pin: restart_editor → local", () => {
  assert.equal(routePolicyFor("godot_open_mcp_restart_editor"), "local");
});

test("representative pin: resource_pressure → local", () => {
  assert.equal(routePolicyFor("godot_open_mcp_resource_pressure"), "local");
});

test("representative pins: scene_get_data / filesystem_list → live-first", () => {
  assert.equal(routePolicyFor("godot_open_mcp_scene_get_data"), "live-first");
  assert.equal(routePolicyFor("godot_open_mcp_filesystem_list"), "live-first");
});

test("representative pin: a mutator tool defaults to live", () => {
  // node_modify is a mutating live-only tool — it must default to `live`.
  assert.equal(routePolicyFor("godot_open_mcp_node_modify"), "live");
  assert.equal(routePolicyFor("godot_open_mcp_ping"), "live");
});

test("routing summary advertises liveDefault and the policy roster (no batch)", () => {
  assert.equal(ROUTING_SUMMARY.liveDefault, true);
  assert.deepEqual(
    [...ROUTING_SUMMARY.policies],
    [...ROUTE_POLICIES],
    "routing.policies must equal the canonical ROUTE_POLICIES roster",
  );
  assert.ok(
    !(ROUTING_SUMMARY.policies as readonly string[]).includes("batch"),
    "routing summary must not advertise a batch policy",
  );
});

// ---------------------------------------------------------------------------
// Router parity (P7.5 anti-drift). The router's named-handler roster MUST
// equal the union of the non-`live` override sets. If a tool is added to an
// override set without a matching router handler, the router would silently
// fall through to the generic live route and the catalog would lie. If a
// handler is added without an override entry, the catalog would hide its real
// policy. Both directions are pinned.
// ---------------------------------------------------------------------------

test("every non-live override tool has a matching router named handler", () => {
  const nonLiveOverrides = new Set<string>([
    ...SPECIAL_ROUTE_TOOLS.local,
    ...SPECIAL_ROUTE_TOOLS.offline,
    ...SPECIAL_ROUTE_TOOLS["live-first"],
  ]);
  for (const name of nonLiveOverrides) {
    assert.ok(
      ROUTER_NAMED_HANDLERS.has(name),
      `override tool '${name}' has no matching router named handler — the router would fall through to the generic live route and the catalog would lie`,
    );
  }
});

test("every router named handler appears in a non-live override set", () => {
  const nonLiveOverrides = new Set<string>([
    ...SPECIAL_ROUTE_TOOLS.local,
    ...SPECIAL_ROUTE_TOOLS.offline,
    ...SPECIAL_ROUTE_TOOLS["live-first"],
  ]);
  for (const name of ROUTER_NAMED_HANDLERS) {
    assert.ok(
      nonLiveOverrides.has(name),
      `router named handler '${name}' is not in any non-live override set — the catalog would advertise the wrong policy`,
    );
  }
});

test("the router named-handler constants are the expected tool names", () => {
  // Pins the literal names so a rename is caught here rather than silently
  // diverging between the catalog and the router.
  assert.equal(CAPABILITIES_TOOL, "godot_open_mcp_capabilities");
  assert.equal(BRIDGE_STATUS_TOOL, "godot_open_mcp_bridge_status");
  assert.equal(PULL_EVENTS_TOOL, "godot_open_mcp_pull_events");
  assert.equal(MANAGE_TOOLS_TOOL, "godot_open_mcp_manage_tools");
  assert.equal(SCENE_GET_DATA_TOOL, "godot_open_mcp_scene_get_data");
  assert.equal(FILESYSTEM_LIST_TOOL, "godot_open_mcp_filesystem_list");
  assert.equal(READ_COMPILE_ERRORS_TOOL, "godot_open_mcp_read_compile_errors");
  assert.equal(BASELINE_CREATE_TOOL, "godot_open_mcp_baseline_create");
  assert.equal(REGRESSION_CHECK_TOOL, "godot_open_mcp_regression_check");
  assert.equal(RESTART_EDITOR_TOOL, "godot_open_mcp_restart_editor");
  assert.equal(RESOURCE_PRESSURE_TOOL, "godot_open_mcp_resource_pressure");
});
