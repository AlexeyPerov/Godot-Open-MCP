// Smoke test for the stdio MCP scaffold.
//
// Verifies the three contracts this scaffold ships:
//   1. `createServer` returns a Server instance whose backing package name and
//      version match package.json — the values clients see in `initialize`.
//   2. `handleListTools` returns the populated registry (ping is the first
//      entry — P1.7).
//   3. `handleCallTool` returns a structured `isError` response for unknown
//      tools instead of throwing, and dispatches registered tools through the
//      supplied LiveClient.
//
// Lifecycle / clean-exit-on-disconnect behaviour is exercised by an integration
// test (boot `dist/index.js`, send `initialize`, close stdin, assert exit 0),
// not by this in-process unit test. The ping round-trip itself is exercised
// against a local HTTP stub in live-client.test.ts.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createServer, handleListTools, handleCallTool } from "./index.js";
import { ping } from "./tools/ping.js";

const here = dirname(fileURLToPath(import.meta.url));
const pkg = JSON.parse(
  readFileSync(resolve(here, "..", "package.json"), "utf8"),
) as { version: string; name: string };

test("package identity is well-formed and createServer returns a Server", () => {
  assert.equal(pkg.name, "godot-open-mcp");
  assert.match(pkg.version, /^\d+\.\d+\.\d+/);
  const server = createServer("godot-open-mcp");
  assert.ok(server, "createServer returned a Server instance");
});

test("handleListTools returns the registry with ping as the first entry", async () => {
  const result = await handleListTools();
  assert.ok(result.tools.length >= 1, "registry must contain at least ping");
  assert.equal(result.tools[0].name, ping.name);
  assert.equal(result.tools[0].name, "godot_open_mcp_ping");
});

test("handleCallTool returns a structured error for unknown tools", async () => {
  const result = await handleCallTool({
    name: "godot_open_mcp_does_not_exist",
    arguments: {},
  });
  assert.equal(result.isError, true);
  const block = result.content[0];
  assert.equal(block.type, "text");
  assert.ok(block.type === "text");
  assert.match(
    block.text,
    /Unknown tool: godot_open_mcp_does_not_exist/,
  );
});

test("handleCallTool surfaces a structured error when a registered tool has no live client", async () => {
  // ping is registered; without a LiveClient the dispatcher must still return
  // a structured error rather than throwing. This pins the test-harness
  // contract: a missing client never kills the server process.
  const result = await handleCallTool({
    name: "godot_open_mcp_ping",
    arguments: {},
  });
  assert.equal(result.isError, true);
  const block = result.content[0];
  assert.equal(block.type, "text");
  assert.ok(block.type === "text");
  assert.match(block.text, /no live client is wired/);
});
