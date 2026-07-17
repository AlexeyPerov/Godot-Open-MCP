// build-capabilities builder tests (P3.8; route policy + routing summary in P7.5). Pins the
// aggregation + filtering contract: every tool ships as implemented, the rule/fix catalog passes
// through, counts are accurate, the `kind` / `includePlanned` filters narrow the result, every
// tool carries a valid `routePolicy`, and the routing summary is present and batch-free. Adapted
// from Unity Open MCP's build-capabilities.test.ts (copy for the test shape; the assertions are
// Godot-specific).

import { test } from "node:test";
import assert from "node:assert/strict";
import { buildCapabilities, type BuildCapabilitiesDeps } from "./build-capabilities.js";
import { RULE_CATALOG, FIX_CATALOG } from "./rule-catalog.js";
import { ROUTE_POLICIES, SPECIAL_ROUTE_TOOLS, type RoutePolicy } from "./route-policy.js";
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
