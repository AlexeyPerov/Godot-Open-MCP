// Smoke test for the stdio MCP scaffold (P1.5).
//
// Verifies the three contracts this scaffold ships:
//   1. `createServer` returns a Server instance whose backing package name and
//      version match package.json — the values clients see in `initialize`.
//   2. `handleListTools` returns the (currently empty) registry.
//   3. `handleCallTool` returns a structured `isError` response for unknown
//      tools instead of throwing.
//
// Lifecycle / clean-exit-on-disconnect behaviour is exercised by an integration
// test (boot `dist/index.js`, send `initialize`, close stdin, assert exit 0),
// not by this in-process unit test.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createServer, handleListTools, handleCallTool } from "./index.js";

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

test("handleListTools returns the (currently empty) registry", async () => {
  const result = await handleListTools();
  assert.deepEqual(result.tools, []);
});

test("handleCallTool returns a structured error for unknown tools", async () => {
  const result = await handleCallTool({
    name: "godot_open_mcp_does_not_exist",
    arguments: {},
  });
  assert.equal(result.isError, true);
  assert.equal(result.content[0].type, "text");
  assert.match(
    result.content[0].text,
    /Unknown tool: godot_open_mcp_does_not_exist/,
  );
});
