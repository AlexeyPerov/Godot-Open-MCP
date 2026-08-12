// Smoke test for the stdio MCP scaffold (P7.1 router-delegation contract,
// P8.2 ListTools filter, P8.4 listChanged capability advertisement).
//
// Verifies the contracts this scaffold ships:
//   1. `createServer` returns a Server instance whose backing package name and
//      version match package.json — the values clients see in `initialize`.
//   2. `createServer` advertises `tools.listChanged: true` so the
//      `notifications/tools/list_changed` emitter (P8.4) is honored without a
//      capability renegotiation.
//   3. `handleListTools` filters `ALL_TOOLS` through the supplied session
//      state: a fresh state advertises `core` (incl. ping) plus always-visible
//      meta-tools and omits `typed-editor` tools like `node_find` (P8.2).
//   4. `handleCallTool` returns a structured `isError` response for unknown
//      tools instead of throwing, and dispatches REGISTERED tools through the
//      supplied `Router` exactly once (P7.1). The router owns live/offline/
//      local selection and the `_source` / `_route` metadata; index.ts only
//      validates registration + normalizes `arguments`.
//   5. A registered call with no router wired (test-harness omission) returns
//      a structured `isError` rather than throwing.
//   6. P8.2 — CallTool does NOT filter by group: a hidden-but-registered tool
//      name still reaches the router (regression guard against accidentally
//      gating the call path).
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
import { ToolSessionState } from "./tool-session-state.js";
import { ResourceRouter } from "./resources/resource-router.js";
import type { Router } from "./router.js";
import type { LiveClient } from "./live-client.js";

const here = dirname(fileURLToPath(import.meta.url));
const pkg = JSON.parse(
  readFileSync(resolve(here, "..", "package.json"), "utf8"),
) as { version: string; name: string };

test("package identity is well-formed and createServer returns a Server", () => {
  assert.equal(pkg.name, "godot-open-mcp");
  assert.match(pkg.version, /^\d+\.\d+\.\d+/);
  const { server, sessionState } = createServer("godot-open-mcp");
  assert.ok(server, "createServer returned a Server instance");
  assert.ok(sessionState, "createServer returned a ToolSessionState");
});

test("createServer injects the supplied sessionState instead of constructing one", () => {
  // The injected store must be the SAME instance — the stdio MCP server has
  // exactly one client per process and one store; a second store would
  // desync ListTools and (P8.3) manage_tools.
  const injected = new ToolSessionState();
  injected.activate("typed-editor");
  const { sessionState } = createServer("godot-open-mcp", undefined, {
    sessionState: injected,
  });
  assert.equal(
    sessionState,
    injected,
    "createServer must use the injected sessionState",
  );
  assert.deepEqual(sessionState.activeGroups(), ["core", "typed-editor"]);
});

test("P8.4 — createServer advertises tools.listChanged:true in server capabilities", () => {
  // The SDK's Server class exposes `getCapabilities()` (protected, used during
  // the initialize handshake) which surfaces the capabilities object passed at
  // construction. The listChanged flag MUST stay true so P8.4's
  // notifications/tools/list_changed emitter is honored without a capability
  // renegotiation. Pin the contract so a future edit that drops the flag (or
  // drops the `tools` capability block) fails this test loudly.
  const { server } = createServer("godot-open-mcp");
  // Cast to access the protected member from the test side — same field the
  // SDK sends in the `initialize` response.
  const caps = (
    server as unknown as {
      getCapabilities(): { tools?: { listChanged?: boolean } };
    }
  ).getCapabilities();
  assert.ok(caps.tools, "capabilities must advertise a tools block");
  assert.equal(
    caps.tools?.listChanged,
    true,
    "tools.listChanged MUST be true (P8.4 notifications/tools/list_changed)",
  );
});

test("P18.2 — resources capability is advertised only when a ResourceRouter is wired", () => {
  // The resources capability + handlers are optional: tests that only exercise
  // tools omit the router, and the server MUST NOT advertise a resources
  // capability with no handlers behind it. When a router IS supplied (stdio
  // main()), the server declares `resources: {}` so clients probe resources/
  // list + resources/read. Pin both sides so a future edit that drops the
  // guard (or forgets to declare the capability) fails loudly.
  const capsOf = (server: unknown): { resources?: unknown } =>
    (server as unknown as { getCapabilities(): { resources?: unknown } }).getCapabilities();

  // Omitted router → no resources capability.
  const bare = createServer("godot-open-mcp");
  assert.equal(
    capsOf(bare.server).resources,
    undefined,
    "resources capability must NOT be advertised without a ResourceRouter",
  );

  // Supplied router → resources capability present.
  const stubLive = {} as unknown as LiveClient;
  const resourceRouter = new ResourceRouter({
    live: stubLive,
    projectPath: "/fake/project",
    port: 22028,
  });
  const wired = createServer("godot-open-mcp", undefined, { resourceRouter });
  assert.ok(
    capsOf(wired.server).resources !== undefined,
    "resources capability MUST be advertised when a ResourceRouter is wired",
  );
});

test("handleListTools with a fresh state includes ping + capabilities + omits node_find", async () => {
  // P8.2 acceptance — a fresh session advertises only `core` plus the
  // always-visible meta-tools. node_find is a typed-editor tool and must be
  // hidden until the group is activated.
  const state = new ToolSessionState();
  const result = await handleListTools(state);
  const names = result.tools.map((t) => t.name);
  // Always-visible meta-tools + core (incl. ping as the first core entry).
  assert.ok(
    names.includes("godot_open_mcp_ping"),
    "ping (core + always-visible) must be advertised",
  );
  assert.ok(
    names.includes("godot_open_mcp_capabilities"),
    "capabilities (always-visible) must be advertised",
  );
  // P8.3 — manage_tools is registered and always-visible so an agent can
  // reach the visibility surface before any opt-in group is active.
  assert.ok(
    names.includes("godot_open_mcp_manage_tools"),
    "manage_tools (always-visible) must be advertised",
  );
  assert.ok(
    !names.includes("godot_open_mcp_node_find"),
    "node_find (typed-editor) must NOT be advertised on a fresh state",
  );
  // ping is the first entry in ALL_TOOLS and survives the filter, so it stays
  // first.
  assert.equal(result.tools[0].name, ping.name);
  assert.equal(result.tools[0].name, "godot_open_mcp_ping");
});

test("handleListTools after activate('typed-editor') advertises node_find", async () => {
  // Activating an opt-in group adds its tools to subsequent ListTools
  // responses — the contract manage_tools (P8.3) will surface to agents.
  const state = new ToolSessionState();
  state.activate("typed-editor");
  const result = await handleListTools(state);
  const names = result.tools.map((t) => t.name);
  assert.ok(
    names.includes("godot_open_mcp_node_find"),
    "node_find must be advertised after typed-editor is activated",
  );
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

test("P8.2 — handleCallTool reaches the router for a hidden-but-registered typed-editor tool", async () => {
  // The P8.2 non-goal: CallTool is NOT filtered by group. node_find is hidden
  // from ListTools on a fresh state, but calling it by name still dispatches
  // to the router. This is the regression guard against accidentally gating
  // the call path on session visibility — hiding is a prompt-size control,
  // not an authorization boundary.
  const router = makeFakeRouter();
  const state = new ToolSessionState();
  // Sanity: node_find is currently hidden on this state.
  const listed = await handleListTools(state);
  assert.ok(
    !listed.tools.map((t) => t.name).includes("godot_open_mcp_node_find"),
    "precondition: node_find is hidden on a fresh state",
  );
  // The hidden tool still reaches the router.
  await handleCallTool(
    { name: "godot_open_mcp_node_find", arguments: { name: "Foo" } },
    router,
  );
  assert.equal(router.calls.length, 1, "hidden-but-registered tool must route");
  assert.deepEqual(router.calls[0], {
    tool: "godot_open_mcp_node_find",
    args: { name: "Foo" },
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
