// build-capabilities builder tests (P3.8). Pins the aggregation + filtering contract: every tool ships
// as implemented, the rule/fix catalog passes through, counts are accurate, and the `kind` /
// `includePlanned` filters narrow the result. Adapted from Unity Open MCP's build-capabilities.test.ts
// (copy for the test shape; the assertions are Godot-specific).

import { test } from "node:test";
import assert from "node:assert/strict";
import { buildCapabilities, type BuildCapabilitiesDeps } from "./build-capabilities.js";
import { RULE_CATALOG, FIX_CATALOG } from "./rule-catalog.js";
import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// Fixture tools stand in for ALL_TOOLS without importing production modules that have cross-file
// runtime imports (strip-types safe).
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
