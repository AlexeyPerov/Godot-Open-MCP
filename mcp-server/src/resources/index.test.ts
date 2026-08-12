// Catalog tests for the MCP resource URIs (P18.2).
//
// Pins the four-URI roster and the `godot-open-mcp://` scheme so an accidental
// rename, a stray extra URI, or a missing mimeType fails CI loudly. Adapted
// from Unity Open MCP's `resources/index.test.ts` (copy for the test shape;
// URIs carry the Godot scheme).

import test from "node:test";
import assert from "node:assert/strict";

import { ALL_RESOURCES } from "./index.js";

const ALLOWED_URIS = new Set([
  "godot-open-mcp://health/summary",
  "godot-open-mcp://health/baseline",
  "godot-open-mcp://bridge/status",
  "godot-open-mcp://tool-groups",
]);

test("ALL_RESOURCES has exactly four resources", () => {
  assert.equal(ALL_RESOURCES.length, 4);
});

test("every resource carries the godot-open-mcp:// scheme + json mimeType + name", () => {
  for (const r of ALL_RESOURCES) {
    assert.ok(
      r.uri.startsWith("godot-open-mcp://"),
      `URI must use the godot-open-mcp:// scheme: ${r.uri}`,
    );
    assert.equal(r.mimeType, "application/json");
    assert.ok(typeof r.name === "string" && r.name.length > 0);
    assert.ok(typeof r.description === "string" && r.description.length > 0);
  }
});

test("health/summary resource is registered with the correct URI", () => {
  const r = ALL_RESOURCES.find((x) => x.uri === "godot-open-mcp://health/summary");
  assert.ok(r, "health/summary must be in ALL_RESOURCES");
});

test("health/baseline resource is registered with the correct URI", () => {
  const r = ALL_RESOURCES.find((x) => x.uri === "godot-open-mcp://health/baseline");
  assert.ok(r, "health/baseline must be in ALL_RESOURCES");
});

test("bridge/status resource is registered with the correct URI", () => {
  const r = ALL_RESOURCES.find((x) => x.uri === "godot-open-mcp://bridge/status");
  assert.ok(r, "bridge/status must be in ALL_RESOURCES");
});

test("tool-groups resource is registered with the correct URI", () => {
  const r = ALL_RESOURCES.find((x) => x.uri === "godot-open-mcp://tool-groups");
  assert.ok(r, "tool-groups must be in ALL_RESOURCES");
});

test("resource URIs are stable and deterministic across reads", () => {
  const uris1 = ALL_RESOURCES.map((r) => r.uri).sort();
  const uris2 = ALL_RESOURCES.map((r) => r.uri).sort();
  assert.deepEqual(uris1, uris2);
  assert.deepEqual(uris1, [...ALLOWED_URIS].sort());
});

test("no unintended URIs are exposed", () => {
  for (const r of ALL_RESOURCES) {
    assert.ok(ALLOWED_URIS.has(r.uri), `unexpected URI: ${r.uri}`);
  }
});
