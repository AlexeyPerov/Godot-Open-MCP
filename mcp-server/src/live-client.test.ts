// LiveClient /ping round-trip tests (P1.7).
//
// Drives a real local HTTP "bridge" stub through the three states the ping
// tool's acceptance criteria call out:
//   1. healthy   — bridge up, returns 200 + the deterministic PingResponse;
//                  the tool returns the body verbatim with isError:false.
//   2. timeout   — bridge up but never answers; the client's AbortController
//                  fires and the tool surfaces `bridge_timeout`.
//   3. disconnected (bridge_offline) — no listener at the resolved port;
//                  ECONNREFUSED surfaces as `bridge_offline` with the
//                  per-project lock-file hint.
//
// The 503 path (bridge listener up, session not yet initialized) is covered
// as a fourth case — it returns a structured fallback body, not an error,
// because the bridge IS reachable, just not ready yet.
//
// Adapted from Unity Open MCP's mcp-server/src/live-client.test.ts (copy
// fidelity for the bridge-stub harness; the dialog-dismiss / dead-bridge /
// refresh / envelope-shape tests there cover later-phase features that the
// Godot minimal LiveClient does not yet implement and so are deferred).

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  createServer,
  type Server as HttpServer,
  type IncomingMessage,
  type ServerResponse,
} from "node:http";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { LiveClient, PING_TOOL_NAME, type PingResponse } from "./live-client.js";

/**
 * Pull the first text content block's text out of a CallToolResult. The SDK
 * types content as a discriminated union (text | image | audio | ...), so a
 * direct `.text` access trips TS; this helper narrows on `type: "text"` and
 * asserts the block is the shape every test in this file expects.
 */
function textOf(result: CallToolResult): string {
  const block = result.content[0];
  assert.equal(block.type, "text", "first content block must be text");
  assert.ok(block.type === "text");
  return block.text;
}

interface BridgeStub {
  server: HttpServer;
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
        server,
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

/** 200 handler that serves the deterministic idle PingResponse on /ping. */
function healthyHandler(req: IncomingMessage, res: ServerResponse): void {
  if (req.url === "/ping") {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify(HEALTHY_PING));
    return;
  }
  res.writeHead(404);
  res.end();
}

test("ping: healthy bridge returns the PingResponse body verbatim with isError:false", async () => {
  const bridge = await startBridgeStub(healthyHandler);
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route(PING_TOOL_NAME, {});
    assert.equal(result.isError, false, "healthy ping is not an error");
    assert.equal(result.content.length, 1);
    assert.equal(result.content[0].type, "text");
    const body = JSON.parse(textOf(result)) as PingResponse;
    assert.deepEqual(body, HEALTHY_PING);
  } finally {
    await bridge.close();
  }
});

test("ping: 503 (bridge not ready) returns a structured fallback body, not an error", async () => {
  // The bridge listener is up but BridgeSession is not initialized. The
  // handler returns 503 + the fallback body shape (mirrors BridgeJson.
  // BuildPingFallbackJson). LiveClient must surface this as a reachable-
  // but-not-ready bridge — isError:false so an agent can branch on
  // body.connected / body.compiling rather than treating it as offline.
  const fallbackBody = {
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
    const client = new LiveClient(bridge.port);
    const result = await client.route(PING_TOOL_NAME, {});
    assert.equal(result.isError, false, "503 is reachable, not an error");
    const body = JSON.parse(textOf(result));
    assert.equal(body.connected, false);
    assert.equal(body.compiling, true);
  } finally {
    await bridge.close();
  }
});

test("ping: bridge down (ECONNREFUSED) surfaces bridge_offline", async () => {
  // No stub — pointing at a port with no listener produces ECONNREFUSED on
  // connect. The client's fetch throws a TypeError ("fetch failed"); the
  // classifier must surface bridge_offline with the per-project hint.
  const client = new LiveClient(1, undefined, "/home/user/MyGame");
  const result = await client.route(PING_TOOL_NAME, {});
  assert.equal(result.isError, true);
  const body = JSON.parse(textOf(result));
  assert.equal(body.error.code, "bridge_offline");
  // The offline hint names this project's lock file so an agent knows where
  // to look. Pinned because it is the most actionable field in the payload.
  assert.match(
    body.error.message,
    /~\/\.godot-open-mcp\/instances\//,
    "offline hint must point at the project's lock file",
  );
});

test("ping: bridge_offline hint falls back to the generic message when projectPath is unknown", async () => {
  // Without projectPath threaded in, the lock-file-specific line is omitted;
  // the generic hint (which still mentions the lock path pattern) is shown.
  const client = new LiveClient(1);
  const result = await client.route(PING_TOOL_NAME, {});
  assert.equal(result.isError, true);
  const body = JSON.parse(textOf(result));
  assert.equal(body.error.code, "bridge_offline");
  assert.ok(
    body.error.message.includes("GODOT_OPEN_MCP_BRIDGE_PORT"),
    "generic hint must mention the env override",
  );
});

test("ping: bridge too slow to answer surfaces bridge_timeout", async () => {
  // The handler accepts the request but never responds. The client's 5s
  // AbortController fires (DOMException name "AbortError"); the classifier
  // must surface bridge_timeout — distinct from bridge_offline so an agent
  // can branch: a timeout suggests retry, offline suggests configuration.
  const bridge = await startBridgeStub((_req, _res) => {
    // Intentionally never calls res.end(). The request will hang until the
    // client's AbortController fires.
  });
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route(PING_TOOL_NAME, {});
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    assert.equal(body.error.code, "bridge_timeout");
    assert.match(
      body.error.message,
      /did not respond within/,
      "timeout message must explain the wait",
    );
  } finally {
    await bridge.close();
  }
});

test("ping: malformed 200 body surfaces as a fetch-side error (caught, not thrown)", async () => {
  // A bridge that returns 200 with non-JSON body makes res.json() reject.
  // That rejection bubbles into the catch block and is classified by err
  // type — it is NOT an AbortError, so the agent sees bridge_offline (the
  // safest framing when the body is unparseable; the bridge is effectively
  // unusable). The point of this test is that the tool returns a structured
  // result rather than throwing.
  const bridge = await startBridgeStub((_req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end("<<<not json>>>");
  });
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route(PING_TOOL_NAME, {});
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    assert.ok(
      body.error.code === "bridge_offline" || body.error.code === "bridge_timeout",
      `expected a structured error code, got ${body.error.code}`,
    );
  } finally {
    await bridge.close();
  }
});

test("ping: HTTP 500 surfaces as bridge_http_error", async () => {
  const bridge = await startBridgeStub((_req, res) => {
    res.writeHead(500);
    res.end("server error");
  });
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route(PING_TOOL_NAME, {});
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    assert.equal(body.error.code, "bridge_http_error");
    assert.match(body.error.message, /HTTP 500/);
  } finally {
    await bridge.close();
  }
});

test("route: non-ping tool name surfaces a structured tool_not_routed error", async () => {
  // P1.7 wires only the ping path. Any other registered tool name must
  // surface a structured error rather than throwing — the CallTool
  // dispatcher must never kill the server process. Later phases replace
  // this with mutating tool dispatch.
  const bridge = await startBridgeStub(healthyHandler);
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_gameobject_find", {});
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    assert.equal(body.error.code, "tool_not_routed");
  } finally {
    await bridge.close();
  }
});

// ----- Auth header wiring -----
//
// The instance lock carries an optional authToken (P5.2). LiveClient attaches
// it as `Authorization: Bearer <token>` on every request when present; when
// absent no header is sent and the bridge must be in authMode "none" for the
// request to succeed. The bridge's /ping handler does not enforce auth in
// P1.x, so these tests only assert the header is present/absent as wired —
// they do not exercise an auth rejection path.

function headerCapturingHandler(
  seen: { auth?: string | null },
): (req: IncomingMessage, res: ServerResponse) => void {
  return (req, res) => {
    if (req.url === "/ping" && seen.auth === undefined) {
      seen.auth = req.headers["authorization"] ?? null;
    }
    healthyHandler(req, res);
  };
}

test("ping: sends Authorization: Bearer <token> when a token was provided", async () => {
  const seen: { auth?: string | null } = {};
  const bridge = await startBridgeStub(headerCapturingHandler(seen));
  try {
    const token = "deadbeef".repeat(8);
    const client = new LiveClient(bridge.port, token);
    await client.route(PING_TOOL_NAME, {});
    assert.equal(
      seen.auth,
      `Bearer ${token}`,
      "Authorization header must carry the discovered token",
    );
  } finally {
    await bridge.close();
  }
});

test("ping: omits Authorization header when no token was provided", async () => {
  const seen: { auth?: string | null } = {};
  const bridge = await startBridgeStub(headerCapturingHandler(seen));
  try {
    const client = new LiveClient(bridge.port);
    await client.route(PING_TOOL_NAME, {});
    assert.equal(
      seen.auth,
      null,
      "No Authorization header when the client has no token (authMode \"none\")",
    );
  } finally {
    await bridge.close();
  }
});
