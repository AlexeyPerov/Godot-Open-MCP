// `godot_open_mcp_pull_events` tool-definition tests (P5.4). Pins the catalog metadata the MCP
// ListTools response advertises to AI clients — name prefix per ADR-003, non-empty description, the
// property set, the `additionalProperties:false` guard, the absence of gate surface (read-only),
// and registration in ALL_TOOLS. The live round-trip (SSE drain → BridgeEventStream.pull) is
// exercised by event-stream.test.ts; this file only asserts the contract advertised over stdio.
//
// Adapted from bridge-status.test.ts / console-tools.test.ts (copy fidelity for the catalog-metadata
// test shape).

import { test } from "node:test";
import assert from "node:assert/strict";
import { pullEvents } from "./pull-events.js";
import { ALL_TOOLS } from "./index.js";

test("pull_events tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(pullEvents.name, "godot_open_mcp_pull_events");
  assert.match(pullEvents.name, /^godot_open_mcp_/);
});

test("pull_events tool has a non-empty description", () => {
  assert.ok(typeof pullEvents.description === "string");
  assert.ok((pullEvents.description ?? "").length > 0);
});

test("pull_events tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(pullEvents.inputSchema.type, "object");
  assert.equal(pullEvents.inputSchema.additionalProperties, false);
});

test("pull_events exposes exactly the max_events property", () => {
  const props = pullEvents.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["max_events"]);
});

test("pull_events does NOT advertise a subscriber param the router ignores", () => {
  // The schema used to declare `subscriber`, but routePullEvents reads only max_events and
  // BridgeEventStream fixes its subscriberId at construction — so the value was silently dropped
  // and a different id echoed back. Advertising an unimplemented resume knob is worse than omitting
  // it: agents retry against it. Re-add only alongside a real re-subscribe path on the stream.
  const props = pullEvents.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.subscriber, undefined);
});

test("pull_events does NOT expose gate surface (read-only)", () => {
  const props = pullEvents.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "read-only tool — no gate surface");
  assert.equal(props.gate, undefined, "read-only tool — no gate surface");
});

test("pull_events max_events defaults to 50 with bounds", () => {
  const props = pullEvents.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number; maximum?: number }
  >;
  assert.equal(props.max_events.default, 50);
  assert.equal(props.max_events.minimum, 1);
  assert.equal(props.max_events.maximum, 1000);
});

test("pull_events has no required array", () => {
  assert.equal(pullEvents.inputSchema.required, undefined);
});

test("ALL_TOOLS registers godot_open_mcp_pull_events", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(
    names.includes("godot_open_mcp_pull_events"),
    "pull_events missing from ALL_TOOLS",
  );
});
