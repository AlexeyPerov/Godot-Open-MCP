// build-capabilities builder tests (P3.8; route policy + routing summary in P7.5; tool-group
// catalog block in P8.1). Pins the aggregation + filtering contract: every tool ships as
// implemented, the rule/fix catalog passes through, counts are accurate, the `kind` /
// `includePlanned` filters narrow the result, every tool carries a valid `routePolicy`, the
// routing summary is present and batch-free, and the `toolGroups` catalog block is advertised
// with the right shape. Adapted from Unity Open MCP's build-capabilities.test.ts (copy for the
// test shape; the assertions are Godot-specific).

import { test } from "node:test";
import assert from "node:assert/strict";
import { buildCapabilities, type BuildCapabilitiesDeps } from "./build-capabilities.js";
import { RULE_CATALOG, FIX_CATALOG } from "./rule-catalog.js";
import { ROUTE_POLICIES, SPECIAL_ROUTE_TOOLS, type RoutePolicy } from "./route-policy.js";
import { TOOL_GROUPS, DEFAULT_ENABLED_GROUPS } from "./tool-groups.js";
import { ALL_TOOLS } from "../tools/index.js";
import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// Fixture tools stand in for ALL_TOOLS without importing production modules that have cross-file
// runtime imports (strip-types safe). Includes one tool per override policy so per-tool route
// assertions can run against the fixture.
const FIXTURE_TOOLS: Tool[] = [
  {
    name: "godot_open_mcp_ping",
    description: "Bridge health check.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
  },
  {
    name: "godot_open_mcp_validate_edit",
    description: "Scoped verify pass.",
    inputSchema: { type: "object", required: ["paths"], properties: {} },
  },
  {
    name: "godot_open_mcp_apply_fix",
    description: "Apply a fix.",
    inputSchema: { type: "object", required: ["issue_id"], properties: {} },
  },
  {
    name: "godot_open_mcp_capabilities",
    description: "Discover the capability surface.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "godot_open_mcp_read_compile_errors",
    description: "Offline log diagnostics.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "godot_open_mcp_scene_get_data",
    description: "Read scene hierarchy (live-first).",
    inputSchema: { type: "object", properties: {} },
  },
];

const DEPS: BuildCapabilitiesDeps = {
  tools: FIXTURE_TOOLS,
  rules: RULE_CATALOG,
  fixes: FIX_CATALOG,
};

test("buildCapabilities returns every implemented tool as implemented", () => {
  const result = buildCapabilities(DEPS);
  assert.equal(result.tools.length, FIXTURE_TOOLS.length);
  for (const tool of result.tools) {
    assert.equal(tool.implemented, true);
    assert.equal(tool.status, "implemented");
    assert.ok(typeof tool.description === "string");
    // P7.5 — every tool must carry a valid route policy.
    assert.ok(
      (ROUTE_POLICIES as readonly string[]).includes(tool.routePolicy),
      `${tool.name} has invalid routePolicy '${tool.routePolicy}'`,
    );
  }
});

test("buildCapabilities passes the full rule + fix catalog through by default", () => {
  const result = buildCapabilities(DEPS);
  assert.equal(result.rules.length, RULE_CATALOG.length);
  assert.equal(result.fixes.length, FIX_CATALOG.length);
});

test("buildCapabilities counts implemented rules + fixes accurately", () => {
  const result = buildCapabilities(DEPS);
  assert.equal(result.counts.rulesImplemented, 3);
  assert.equal(result.counts.rulesPlanned, 0);
  assert.equal(result.counts.fixesImplemented, 1);
  assert.equal(result.counts.fixesPlanned, 0);
  assert.equal(result.counts.toolsImplemented, FIXTURE_TOOLS.length);
  assert.equal(result.counts.toolsPlanned, 0);
});

test("buildCapabilities kind:tools returns only tools", () => {
  const result = buildCapabilities(DEPS, { kind: "tools" });
  assert.equal(result.tools.length, FIXTURE_TOOLS.length);
  assert.equal(result.rules.length, 0);
  assert.equal(result.fixes.length, 0);
});

test("buildCapabilities kind:rules returns only rules", () => {
  const result = buildCapabilities(DEPS, { kind: "rules" });
  assert.equal(result.tools.length, 0);
  assert.equal(result.rules.length, RULE_CATALOG.length);
  assert.equal(result.fixes.length, 0);
});

test("buildCapabilities kind:fixes returns only fixes", () => {
  const result = buildCapabilities(DEPS, { kind: "fixes" });
  assert.equal(result.tools.length, 0);
  assert.equal(result.rules.length, 0);
  assert.equal(result.fixes.length, FIX_CATALOG.length);
});

test("buildCapabilities counts stay stable across kind filters", () => {
  // counts describe the WHOLE surface even when a kind filter narrows the returned arrays — so an agent
  // asking for `kind:rules` still learns how many tools/fixes exist.
  const rulesOnly = buildCapabilities(DEPS, { kind: "rules" });
  assert.equal(rulesOnly.counts.toolsImplemented, FIXTURE_TOOLS.length);
  assert.equal(rulesOnly.counts.fixesImplemented, 1);
});

// ---------------------------------------------------------------------------
// P7.5 — route policy + routing summary
// ---------------------------------------------------------------------------

test("buildCapabilities assigns route policy per tool (representative pins)", () => {
  const result = buildCapabilities(DEPS);
  const byName = new Map(result.tools.map((t) => [t.name, t.routePolicy]));
  assert.equal(byName.get("godot_open_mcp_capabilities"), "local");
  assert.equal(byName.get("godot_open_mcp_read_compile_errors"), "offline");
  assert.equal(byName.get("godot_open_mcp_scene_get_data"), "live-first");
  // Defaults to live when not in an override set.
  assert.equal(byName.get("godot_open_mcp_ping"), "live");
  assert.equal(byName.get("godot_open_mcp_apply_fix"), "live");
});

test("buildCapabilities no tool carries a batch policy", () => {
  const result = buildCapabilities(DEPS);
  for (const tool of result.tools) {
    // `RoutePolicy` has no `batch` member by construction — the cast keeps the
    // guard meaningful even though TS would otherwise narrow it away.
    const policy = tool.routePolicy as string;
    assert.ok(
      policy !== "batch" && policy !== "batchCapable",
      `${tool.name} must not carry a batch policy`,
    );
  }
});

test("buildCapabilities includes a top-level routing summary", () => {
  const result = buildCapabilities(DEPS);
  assert.ok(result.routing, "routing summary must be present");
  assert.equal(typeof result.routing.liveDefault, "boolean");
  assert.equal(result.routing.liveDefault, true);
  assert.ok(Array.isArray(result.routing.policies));
  assert.deepEqual(
    [...result.routing.policies],
    [...ROUTE_POLICIES],
  );
  // No batch policy may leak into the advertised roster.
  assert.ok(
    !(result.routing.policies as readonly string[]).includes("batch"),
    "routing summary must not advertise a batch policy",
  );
});

test("routing summary is returned even with a kind filter", () => {
  // Independent of the kind filter — an agent asking only for rules still
  // benefits from the routing narrative (same rationale as Unity's routing /
  // costHints / lifecycle blocks).
  const rulesOnly = buildCapabilities(DEPS, { kind: "rules" });
  assert.ok(rulesOnly.routing);
  assert.equal(rulesOnly.routing.liveDefault, true);
  assert.ok(rulesOnly.routing.policies.length > 0);
});

// ---------------------------------------------------------------------------
// P8.1 — tool-group catalog block
// ---------------------------------------------------------------------------

test("buildCapabilities advertises a toolGroups block matching the catalog", () => {
  const result = buildCapabilities(DEPS);
  assert.ok(Array.isArray(result.toolGroups));
  // Every catalog group appears, in catalog order.
  assert.deepEqual(
    result.toolGroups.map((g) => g.id),
    TOOL_GROUPS.map((g) => g.id),
  );
});

test("toolGroups core entry is default-on", () => {
  const result = buildCapabilities(DEPS);
  const core = result.toolGroups.find((g) => g.id === "core");
  assert.ok(core, "core group must appear in toolGroups");
  assert.equal(core!.defaultEnabled, true);
  assert.ok(DEFAULT_ENABLED_GROUPS.has("core"));
});

test("toolGroups domain pack groups appear with the right catalog flags", () => {
  // P12.1 filled the tilemap stub (six tools), P12.2 filled the navigation
  // stub (seven tools), P12.3 filled the particles stub (five tools), P12.4
  // filled the animation stub (seven tools), and P12.5 filled the csg stub
  // (seven tools — roster correctness is pinned in tool-groups.test.ts against
  // the real toolsInGroup; Phase 12 is now complete). This fixture-based builder
  // buckets only the injected tools, so every domain group surfaces an empty
  // roster here (the fixture has no domain tools); the catalog entry itself
  // (defaultEnabled, available) is what's pinned.
  const result = buildCapabilities(DEPS);
  const stubIds = ["tilemap", "navigation", "particles", "animation", "csg"];
  for (const id of stubIds) {
    const g = result.toolGroups.find((entry) => entry.id === id);
    assert.ok(g, `${id} group must appear in toolGroups`);
    assert.equal(g!.defaultEnabled, false);
    assert.equal(g!.available, true, `${id} must report available:true`);
  }
});

test("every toolGroups entry reports available:true (P8 — no compile inventory)", () => {
  // P8 has no bridge compile inventory for domain packs, so every group is
  // `available: true`. P12 will flip uninstalled packs without reshaping the
  // field — pin the P8 contract here.
  const result = buildCapabilities(DEPS);
  for (const g of result.toolGroups) {
    assert.equal(g.available, true, `${g.id} must be available:true in P8`);
  }
});

test("toolGroups tool rosters are sorted and exclude meta-tools", () => {
  const result = buildCapabilities(DEPS);
  for (const g of result.toolGroups) {
    const sorted = [...g.tools].sort();
    assert.deepEqual(g.tools, sorted, `${g.id} roster must be sorted`);
    // Meta-tools (groupFor → null) never appear in a group roster.
    for (const name of g.tools) {
      assert.notEqual(name, "godot_open_mcp_capabilities");
      assert.notEqual(name, "godot_open_mcp_bridge_status");
    }
  }
});

test("toolGroups is returned even with a kind filter", () => {
  // Independent of the kind filter — same rationale as `routing`. An agent
  // asking only for rules still benefits from group discovery.
  const rulesOnly = buildCapabilities(DEPS, { kind: "rules" });
  assert.ok(Array.isArray(rulesOnly.toolGroups));
  assert.equal(rulesOnly.toolGroups.length, TOOL_GROUPS.length);
});

// ---------------------------------------------------------------------------
// P7.5 — real-registry coverage. Builds capabilities over the actual
// ALL_TOOLS (not a fixture) so a newly registered tool with no policy entry is
// caught: it must still classify (defaulting to `live`) and must not duplicate
// an override.
// ---------------------------------------------------------------------------

test("every registered tool in ALL_TOOLS carries a valid route policy in capabilities", () => {
  const deps: BuildCapabilitiesDeps = {
    tools: ALL_TOOLS,
    rules: RULE_CATALOG,
    fixes: FIX_CATALOG,
  };
  const result = buildCapabilities(deps);
  assert.equal(result.tools.length, ALL_TOOLS.length);
  for (const tool of result.tools) {
    assert.ok(
      (ROUTE_POLICIES as readonly string[]).includes(tool.routePolicy),
      `${tool.name} routePolicy '${tool.routePolicy}' is not in the canonical vocabulary`,
    );
  }
});

test("capabilities route policy matches the override sets for every non-live tool", () => {
  // The catalog's `routePolicy` must agree with the override-set membership in
  // route-policy.ts. This is the catalog-side mirror of the disjointness test
  // in route-policy.test.ts — together they pin both the source-of-truth sets
  // and the capabilities transform.
  const deps: BuildCapabilitiesDeps = {
    tools: ALL_TOOLS,
    rules: RULE_CATALOG,
    fixes: FIX_CATALOG,
  };
  const result = buildCapabilities(deps);
  const byName = new Map(result.tools.map((t) => [t.name, t.routePolicy]));
  for (const name of SPECIAL_ROUTE_TOOLS.local) {
    assert.equal(byName.get(name), "local", `${name} must be local in capabilities`);
  }
  for (const name of SPECIAL_ROUTE_TOOLS.offline) {
    assert.equal(byName.get(name), "offline", `${name} must be offline in capabilities`);
  }
  for (const name of SPECIAL_ROUTE_TOOLS["live-first"]) {
    assert.equal(byName.get(name), "live-first", `${name} must be live-first in capabilities`);
  }
  // Every other tool must be `live`.
  const overrides = new Set<string>([
    ...SPECIAL_ROUTE_TOOLS.local,
    ...SPECIAL_ROUTE_TOOLS.offline,
    ...SPECIAL_ROUTE_TOOLS["live-first"],
  ]);
  for (const tool of result.tools) {
    if (!overrides.has(tool.name)) {
      assert.equal(
        tool.routePolicy,
        "live",
        `${tool.name} is not in any override set and must default to live`,
      );
    }
  }
});

test("capabilities built over ALL_TOOLS buckets the tilemap pack into its group roster", () => {
  // P12.1 filled the tilemap stub. When capabilities is built over the real
  // registry (not the fixture), the tilemap group must carry its six-tool roster
  // — this proves the groupFor → buildToolGroups wiring works for the new group.
  const deps: BuildCapabilitiesDeps = {
    tools: ALL_TOOLS,
    rules: RULE_CATALOG,
    fixes: FIX_CATALOG,
  };
  const result = buildCapabilities(deps);
  const tilemap = result.toolGroups.find((g) => g.id === "tilemap");
  assert.ok(tilemap, "tilemap group must appear in toolGroups");
  assert.equal(tilemap!.tools.length, 6, "tilemap must bucket all six pack tools");
  for (const name of tilemap!.tools) {
    assert.match(name, /^godot_open_mcp_tilemap_/);
  }
});

test("capabilities built over ALL_TOOLS buckets the navigation pack into its group roster", () => {
  // P12.2 filled the navigation stub. When capabilities is built over the real
  // registry (not the fixture), the navigation group must carry its seven-tool
  // roster — this proves the groupFor → buildToolGroups wiring works for the new
  // group, mirroring the P12.1 tilemap assertion above.
  const deps: BuildCapabilitiesDeps = {
    tools: ALL_TOOLS,
    rules: RULE_CATALOG,
    fixes: FIX_CATALOG,
  };
  const result = buildCapabilities(deps);
  const navigation = result.toolGroups.find((g) => g.id === "navigation");
  assert.ok(navigation, "navigation group must appear in toolGroups");
  assert.equal(navigation!.tools.length, 7, "navigation must bucket all seven pack tools");
  for (const name of navigation!.tools) {
    assert.match(name, /^godot_open_mcp_navigation_/);
  }
});
