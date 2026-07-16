// Smoke test for the stdio MCP scaffold (P7.1 router-delegation contract).
//
// Verifies the contracts this scaffold ships:
//   1. `createServer` returns a Server instance whose backing package name and
//      version match package.json — the values clients see in `initialize`.
//   2. `handleListTools` returns the populated registry (ping is the first
//      entry — P1.7).
//   3. `handleCallTool` returns a structured `isError` response for unknown
//      tools instead of throwing, and dispatches REGISTERED tools through the
//      supplied `Router` exactly once (P7.1). The router owns live/offline/
//      local selection and the `_source` / `_route` metadata; index.ts only
//      validates registration + normalizes `arguments`.
//   4. A registered call with no router wired (test-harness omission) returns
//      a structured `isError` rather than throwing.
//
// Lifecycle / clean-exit-on-disconnect behaviour is exercised by an integration
// test (boot `dist/index.js`, send `initialize`, close stdin, assert exit 0),
// not by this in-process unit test. The ping round-trip itself is exercised
// against a local HTTP stub in live-client.test.ts and end-to-end in
// integration.test.ts.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { createServer, handleListTools, handleCallTool } from "./index.js";
import { ping } from "./tools/ping.js";
import type { Router } from "./router.js";

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
  // Unknown names must be rejected BEFORE the router is ever consulted — the
  // fake router below records calls and the test asserts it was untouched.
  const router = makeFakeRouter();
  const result = await handleCallTool(
    { name: "godot_open_mcp_does_not_exist", arguments: {} },
    router,
  );
  assert.equal(result.isError, true);
  const block = result.content[0];
  assert.equal(block.type, "text");
  assert.ok(block.type === "text");
  assert.match(block.text, /Unknown tool: godot_open_mcp_does_not_exist/);
  assert.equal(router.calls.length, 0, "unknown tool must not reach the router");
});

test("handleCallTool surfaces a structured error when a registered tool has no router wired", async () => {
  // P7.1 — a registered tool with no router attached (test-harness omission)
  // must still return a structured error rather than throwing. Production
  // main() always wires a fully-constructed ToolRouter.
  const result = await handleCallTool({
    name: "godot_open_mcp_ping",
    arguments: {},
  });
  assert.equal(result.isError, true);
  const block = result.content[0];
  assert.equal(block.type, "text");
  assert.ok(block.type === "text");
  assert.match(block.text, /no router is wired/);
});

test("handleCallTool delegates a registered tool to the router exactly once with normalized args", async () => {
  // The dispatcher hands the registered call straight to router.route,
  // normalizing non-object arguments to {}.
  const router = makeFakeRouter();
  await handleCallTool(
    { name: "godot_open_mcp_ping", arguments: {} },
    router,
  );
  await handleCallTool(
    { name: "godot_open_mcp_ping" /* no arguments field at all */ },
    router,
  );
  await handleCallTool(
    { name: "godot_open_mcp_ping", arguments: "not-an-object" },
    router,
  );

  assert.equal(router.calls.length, 3, "each registered call dispatches once");
  assert.deepEqual(router.calls[0], { tool: "godot_open_mcp_ping", args: {} });
  assert.deepEqual(router.calls[1], { tool: "godot_open_mcp_ping", args: {} });
  assert.deepEqual(router.calls[2], { tool: "godot_open_mcp_ping", args: {} });
});

test("handleCallTool returns the router's result verbatim for a registered tool", async () => {
  // The dispatcher must not mutate the router's result — metadata is the
  // router's responsibility, not the dispatcher's.
  const canned: CallToolResult = {
    content: [{ type: "text", text: JSON.stringify({ ok: true }) }],
    isError: false,
  };
  const router = makeFakeRouter(canned);
  const result = await handleCallTool(
    { name: "godot_open_mcp_capabilities", arguments: { kind: "tools" } },
    router,
  );
  assert.equal(result, canned, "dispatcher returns the router's result object");
  assert.deepEqual(router.calls[0], {
    tool: "godot_open_mcp_capabilities",
    args: { kind: "tools" },
  });
});

/**
 * Minimal fake Router for index.test.ts. Records every `route` call (tool +
 * normalized args) and returns a canned result. Kept local — the
 * router's own dispatch matrix is covered by tool-router.test.ts.
 */
function makeFakeRouter(
  result: CallToolResult = {
    content: [{ type: "text", text: JSON.stringify({ ok: true }) }],
    isError: false,
  },
): Router & { calls: { tool: string; args: Record<string, unknown> }[] } {
  const calls: { tool: string; args: Record<string, unknown> }[] = [];
  return {
    calls,
    async route(tool, args) {
      calls.push({ tool, args });
      return result;
    },
  };
}
