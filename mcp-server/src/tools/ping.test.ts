// `godot_open_mcp_ping` tool-definition tests (P1.7).
//
// Pins the catalog metadata of the ping tool — name prefix per ADR-003, empty
// input schema, and a one-line description. The live round-trip is exercised
// against a local HTTP stub in live-client.test.ts; this file only asserts
// the contract the MCP ListTools response advertises to AI clients.

import { test } from "node:test";
import assert from "node:assert/strict";
import { ping } from "./ping.js";

test("ping tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(ping.name, "godot_open_mcp_ping");
  assert.match(ping.name, /^godot_open_mcp_/);
});

test("ping tool has a non-empty description", () => {
  assert.ok(typeof ping.description === "string");
  assert.ok((ping.description ?? "").length > 0);
});

test("ping tool declares an empty input schema with no additional properties", () => {
  assert.deepEqual(ping.inputSchema, {
    type: "object",
    properties: {},
    additionalProperties: false,
  });
});
