// P1.9 phase-gate parity smoke (`godot_open_mcp_ping` end-to-end).
//
// This is the canonical Phase 1 smoke path the roadmap exit gate keys off. It
// drives the FULL in-process route a real AI client would, using the MCP SDK's
// own `Client` over an `InMemoryTransport` pair linked to our `createServer`:
//
//   MCP Client ──(in-memory MCP)──▶ createServer
//                                        │
//                                        ▼ handleCallTool
//                                   LiveClient.route
//                                        │
//                                        ▼ fetch GET /ping
//                                   bridge stub (real loopback HttpListener)
//
// That exercises every contract P1.7 + P1.9 call out in one test process:
//   - tool registry advertises `godot_open_mcp_ping`,
//   - `tools/call` dispatches a registered tool through the LiveClient,
//   - the LiveClient fetches the resolved bridge port,
//   - the PingResponse body is returned verbatim with `isError:false`,
//   - failure cases (bridge down) surface actionable structured errors.
//
// The bridge stub is a real `node:http` server on an OS-assigned loopback port
// — the same harness `live-client.test.ts` uses — so the ping fetch goes over a
// real socket. The bridge stub's `200 /ping` body mirrors the bridge's
// `BridgeJson.BuildPingJson` field set, pinned by `BridgePingJsonTests.cs` and
// `live-client.test.ts`.
//
// The scripted companion (`scripts/p1-parity-smoke.sh`) drives the same route
// over a real stdio pipe against `dist/index.js`; this in-process test is the
// deterministic, fast-path CI gate that runs on every `npm test`.

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  createServer,
  type Server as HttpServer,
  type IncomingMessage,
  type ServerResponse,
} from "node:http";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";

import { createServer as createMcpServer } from "./index.js";
import { LiveClient, type PingResponse } from "./live-client.js";
import { ToolRouter } from "./tool-router.js";
import { BridgeEventStream } from "./event-stream.js";
import { ToolSessionState } from "./tool-session-state.js";

/**
 * The client-side return shape of `Client.callTool`. The SDK infers this from
 * `CallToolResultSchema` (output), which — under the latest MCP spec — keeps
 * `content` optional because a result may alternatively carry a `toolResult`
 * reference. We only care about the text-content shape for our assertions, so
 * this structural alias is the narrowest type that lets the test helpers accept
 * both the client's return and the server-side `CallToolResult`.
 */
type ClientToolResult = Awaited<ReturnType<Client["callTool"]>>;

// ---------------------------------------------------------------------------
// Bridge stub harness — mirrors the `healthyHandler` in live-client.test.ts.
// ---------------------------------------------------------------------------

interface BridgeStub {
  port: number;
  close(): Promise<void>;
}

function startBridgeStub(
  handler: (req: IncomingMessage, res: ServerResponse) => void,
): Promise<BridgeStub> {
  return new Promise((resolve) => {
    const server = createServer((req, res) => handler(req, res));
    server.listen(0, "127.0.0.1", () => {
      const addr = server.address();
      const port = typeof addr === "object" && addr ? addr.port : 0;
      resolve({
        port,
        close: () => new Promise<void>((r) => server.close(() => r())),
      });
    });
  });
}

const HEALTHY_PING: PingResponse = {
  connected: true,
  projectPath: "/home/user/MyGame",
  godotVersion: "4.3.1.stable.mono",
  bridgeVersion: "0.0.1",
  mode: "live",
  compiling: false,
  isPlaying: false,
};

function healthyHandler(req: IncomingMessage, res: ServerResponse): void {
  if (req.url === "/ping") {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify(HEALTHY_PING));
    return;
  }
  res.writeHead(404);
  res.end();
}

/**
 * Pull the first text content block's text out of a client callTool result.
 * The SDK types content as a discriminated union (text | image | audio | ...),
 * so a direct `.text` access trips TS; this helper narrows on `type: "text"`.
 * Asserts the result actually carries `content` (the spec allows an alternate
 * `toolResult` reference shape; we never emit it from the ping path, so any
 * such shape here would be a wire-level surprise worth failing loudly on).
 */
function textOf(result: ClientToolResult): string {
  assert.ok(
    Array.isArray(result.content) && result.content.length > 0,
    "expected a content array with at least one block",
  );
  const block = result.content[0];
  assert.equal(block.type, "text", "first content block must be text");
  assert.ok(block.type === "text");
  return block.text;
}

// ---------------------------------------------------------------------------
// MCP client + server wiring over InMemoryTransport.
//
// `createMcpServer` is the production factory used by `main()`; we pass a real
// `LiveClient` aimed at the bridge stub's port. The `InMemoryTransport` pair is
// the SDK's in-process equivalent of a stdio pipe — every wire-level message a
// real client would send flows through it.
// ---------------------------------------------------------------------------

interface Harness {
  client: Client;
  bridge: BridgeStub;
  cleanup: () => Promise<void>;
}

async function setupHarness(
  bridge: BridgeStub,
  projectPath?: string,
): Promise<Harness> {
  const liveClient = new LiveClient(bridge.port, undefined, projectPath);
  // P7.1 — every registered call dispatches through ToolRouter. The router
  // wires the live client + the shared event stream (the SSE reader is never
  // started in these tests because no pull_events call is made, so the stream
  // stays inert). Tagging of `_source` / `_route` happens inside the router.
  const eventStream = new BridgeEventStream(
    `http://127.0.0.1:${bridge.port}`,
    undefined,
    undefined,
  );
  // P8.3 — the session store is shared between the ListTools handler (via
  // createMcpServer) and the manage_tools router (via ToolRouter). Constructing
  // one instance here and passing it to both is the single-source-of-truth
  // contract.
  const sessionState = new ToolSessionState();
  const router = new ToolRouter(
    liveClient,
    projectPath ?? "/home/user/MyGame",
    eventStream,
    sessionState,
  );
  const { server } = createMcpServer("godot-open-mcp", router, {
    sessionState,
  });
  const [clientTransport, serverTransport] =
    InMemoryTransport.createLinkedPair();
  await server.connect(serverTransport);

  const client = new Client(
    { name: "p1-parity-smoke-client", version: "0.0.0-test" },
    { capabilities: {} },
  );
  await client.connect(clientTransport);

  return {
    client,
    bridge,
    cleanup: async () => {
      await client.close();
      await server.close();
    },
  };
}

// ---------------------------------------------------------------------------
// P1.9 acceptance: smoke passes on clean checkout with documented steps.
// ---------------------------------------------------------------------------

test("P1.9 smoke: tools/list advertises godot_open_mcp_ping", async () => {
  const bridge = await startBridgeStub(healthyHandler);
  try {
    const { client, cleanup } = await setupHarness(bridge);
    try {
      const { tools } = await client.listTools();
      const names = tools.map((t) => t.name);
      assert.ok(
        names.includes("godot_open_mcp_ping"),
        `tools/list must advertise godot_open_mcp_ping, got ${names.join(", ")}`,
      );
    } finally {
      await cleanup();
    }
  } finally {
    await bridge.close();
  }
});

test("P1.9 smoke: tools/call godot_open_mcp_ping returns the live PingResponse body with live route metadata", async () => {
  // The canonical Phase 1 exit-gate step: an MCP client calls the registered
  // ping tool and receives the bridge's live health payload verbatim, now
  // tagged with `_source: "live"` + `_route.route: "live"` by the P7.1 router.
  // If this breaks, Phase 2 must NOT start — the wire route between the MCP
  // server and the bridge has drifted (port formula, envelope, tool registry,
  // or router metadata).
  const bridge = await startBridgeStub(healthyHandler);
  try {
    const { client, cleanup } = await setupHarness(bridge);
    try {
      const result = await client.callTool({
        name: "godot_open_mcp_ping",
        arguments: {},
      });
      assert.equal(result.isError, false, "healthy ping must not be an error");
      const body = JSON.parse(textOf(result)) as PingResponse & {
        _source?: string;
        _route?: { route?: string };
      };
      // Every PingResponse field is preserved verbatim alongside the metadata.
      const { _source, _route, ...pingFields } = body;
      assert.deepEqual(pingFields, HEALTHY_PING);
      // P7.1 — live route metadata is attached by the router.
      assert.equal(_source, "live");
      assert.equal(_route?.route, "live");
    } finally {
      await cleanup();
    }
  } finally {
    await bridge.close();
  }
});

// ---------------------------------------------------------------------------
// P1.9 acceptance: failure output is actionable and points to owner area.
// ---------------------------------------------------------------------------

test("P1.9 smoke: bridge down surfaces bridge_offline with the per-project lock hint", async () => {
  // Pointing the LiveClient at a port with no listener produces ECONNREFUSED;
  // the failure must be a structured bridge_offline that names THIS project's
  // lock file, so an agent (or a human reading the smoke output) knows exactly
  // where to look. This is the actionable-failure criterion.
  const projectPath = "/home/user/MyGame";
  const { client, cleanup } = await setupHarness(
    { port: 1, close: async () => {} },
    projectPath,
  );
  try {
    const result = await client.callTool({
      name: "godot_open_mcp_ping",
      arguments: {},
    });
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    assert.equal(body.error.code, "bridge_offline");
    // The hint must name the per-project lock file (the owner area for a
    // "bridge down" failure is instance discovery / lock management).
    assert.match(
      body.error.message,
      /~\/\.godot-open-mcp\/instances\//,
      "offline hint must point at the project's lock file",
    );
  } finally {
    await cleanup();
  }
});

test("P1.9 smoke: bridge_http_error names the failing HTTP status", async () => {
  // A bridge that returns an unexpected status (not 200, not 503) must surface
  // bridge_http_error with the status code in the message — the owner area is
  // the bridge HTTP layer, and the status is the single most useful diagnostic.
  const bridge = await startBridgeStub((_req, res) => {
    res.writeHead(500);
    res.end("server error");
  });
  try {
    const { client, cleanup } = await setupHarness(bridge);
    try {
      const result = await client.callTool({
        name: "godot_open_mcp_ping",
        arguments: {},
      });
      assert.equal(result.isError, true);
      const body = JSON.parse(textOf(result));
      assert.equal(body.error.code, "bridge_http_error");
      assert.match(body.error.message, /HTTP 500/);
    } finally {
      await cleanup();
    }
  } finally {
    await bridge.close();
  }
});

test("P1.9 smoke: 503 (bridge loading) returns a structured fallback body, not an error", async () => {
  // The bridge listener is up but the session is not yet initialized. The
  // smoke must treat this as a reachable-but-not-ready bridge — isError:false
  // — so an agent can branch on body.connected / body.compiling rather than
  // misclassifying a slow Godot startup as "bridge down".
  const fallbackBody: PingResponse = {
    connected: false,
    projectPath: null,
    godotVersion: null,
    bridgeVersion: "0.0.1",
    mode: "live",
    compiling: true,
    isPlaying: false,
  };
  const bridge = await startBridgeStub((_req, res) => {
    res.writeHead(503, { "Content-Type": "application/json" });
    res.end(JSON.stringify(fallbackBody));
  });
  try {
    const { client, cleanup } = await setupHarness(bridge);
    try {
      const result = await client.callTool({
        name: "godot_open_mcp_ping",
        arguments: {},
      });
      assert.equal(result.isError, false, "503 is reachable, not an error");
      const body = JSON.parse(textOf(result));
      assert.equal(body.connected, false);
      assert.equal(body.compiling, true);
    } finally {
      await cleanup();
    }
  } finally {
    await bridge.close();
  }
});
