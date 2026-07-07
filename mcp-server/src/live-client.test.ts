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

test("route: non-ping tool name dispatches via POST /tools/{name} (P2.1)", async () => {
  // P2.1 replaces the P1.7 tool_not_routed fallback with real tool dispatch:
  // every non-ping registered tool name now POSTs to /tools/{name} and
  // unwraps the canonical { ok, result, error } envelope. A bridge stub that
  // does not serve /tools/{name} returns 404; the client must surface the
  // HTTP-level error code from the body (or bridge_http_error as the fallback)
  // rather than throwing.
  const bridge = await startBridgeStub(healthyHandler);
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_find", {});
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    // healthyHandler returns a bare 404 with no JSON body, so the client falls
    // back to bridge_http_error naming the HTTP status.
    assert.equal(body.error.code, "bridge_http_error");
    assert.match(body.error.message, /HTTP 404/);
  } finally {
    await bridge.close();
  }
});

// ----- P2.1: POST /tools/{name} dispatch + envelope unwrapping -----
//
// The postTool path sends a non-ping tool name to POST /tools/{name} and
// unwraps the bridge's canonical { ok, result, error } envelope. These tests
// pin the envelope contract C# ↔ TS: success shape, failure shape, HTTP-level
// routing errors, and the unparsable-body defensive cases. Adapted from the
// Unity live-client postTool tests, simplified to the P2.1 canonical envelope
// (no mutation/gate envelope, no compile-wait 503 retry).

/** Bridge stub that serves the canonical success envelope for one tool name. */
function successEnvelopeHandler(
  toolName: string,
  result: unknown,
): (req: IncomingMessage, res: ServerResponse) => void {
  return (req, res) => {
    if (req.url === `/tools/${toolName}` && req.method === "POST") {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ ok: true, result }));
      return;
    }
    res.writeHead(404);
    res.end();
  };
}

/** Bridge stub that serves the canonical failure envelope for one tool name. */
function failureEnvelopeHandler(
  toolName: string,
  code: string,
  message: string,
): (req: IncomingMessage, res: ServerResponse) => void {
  return (req, res) => {
    if (req.url === `/tools/${toolName}` && req.method === "POST") {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ ok: false, error: { code, message } }));
      return;
    }
    res.writeHead(404);
    res.end();
  };
}

test("postTool: success envelope unwraps result verbatim with isError:false", async () => {
  // The happy path: bridge returns { ok: true, result: {...} }. The client
  // must serialize `result` verbatim as the text block and set isError:false.
  const resultPayload = { name: "Player", instanceId: 12345, children: 3 };
  const bridge = await startBridgeStub(
    successEnvelopeHandler("godot_open_mcp_node_find", resultPayload),
  );
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_find", {
      node_path: "/root/Player",
    });
    assert.equal(result.isError, false, "success envelope is not an error");
    assert.equal(result.content.length, 1);
    assert.equal(result.content[0].type, "text");
    // The result payload is serialized verbatim — no ok/result wrapper leaks
    // through to the agent.
    assert.deepEqual(JSON.parse(textOf(result)), resultPayload);
  } finally {
    await bridge.close();
  }
});

test("postTool: success envelope with null result returns 'null' text", async () => {
  // A handler that returns no payload (result: null) must still produce a
  // well-formed success — the agent sees the literal string "null", not an
  // empty content block or a fake error.
  const bridge = await startBridgeStub(
    successEnvelopeHandler("godot_open_mcp_node_find", null),
  );
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_find", {});
    assert.equal(result.isError, false);
    assert.equal(textOf(result), "null");
  } finally {
    await bridge.close();
  }
});

test("postTool: success envelope with scalar result returns the scalar", async () => {
  // result may be any JSON value — a scalar (number, string, bool) is valid.
  const bridge = await startBridgeStub(
    successEnvelopeHandler("godot_open_mcp_console_clear", 42),
  );
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_console_clear", {});
    assert.equal(result.isError, false);
    assert.equal(textOf(result), "42");
  } finally {
    await bridge.close();
  }
});

test("postTool: failure envelope surfaces error code + message with isError:true", async () => {
  // A handler that returns ok:false must surface the bridge's error.code and
  // error.message verbatim — the agent branches on the code (invalid_request,
  // main_thread_blocked, timeout, execution_error, ...).
  const bridge = await startBridgeStub(
    failureEnvelopeHandler(
      "godot_open_mcp_node_modify",
      "invalid_request",
      "missing required field 'node_path'",
    ),
  );
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_modify", {});
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    assert.equal(body.error.code, "invalid_request");
    assert.equal(body.error.message, "missing required field 'node_path'");
  } finally {
    await bridge.close();
  }
});

test("postTool: main_thread_blocked failure code passes through", async () => {
  // The bridge surfaces a modal-blocked main thread as ok:false +
  // main_thread_blocked. The client must NOT reframe this as a timeout or
  // bridge_offline — the code is the agent's signal to dismiss a dialog /
  // scene_save rather than retry.
  const bridge = await startBridgeStub(
    failureEnvelopeHandler(
      "godot_open_mcp_node_create",
      "main_thread_blocked",
      "Godot main thread blocked by a modal dialog",
    ),
  );
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_create", {});
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    assert.equal(body.error.code, "main_thread_blocked");
  } finally {
    await bridge.close();
  }
});

test("postTool: HTTP 404 tool_not_found surfaces the body's error code", async () => {
  // When the bridge returns 404 with { error: { code: 'tool_not_found', ... } }
  // (the tool is not registered), the client must surface the body's code
  // rather than the generic bridge_http_error.
  const bridge = await startBridgeStub((req, res) => {
    if (req.url === "/tools/godot_open_mcp_missing" && req.method === "POST") {
      res.writeHead(404, { "Content-Type": "application/json" });
      res.end(
        JSON.stringify({
          error: { code: "tool_not_found", message: "Unknown tool: godot_open_mcp_missing" },
        }),
      );
      return;
    }
    res.writeHead(404);
    res.end();
  });
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_missing", {});
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    assert.equal(body.error.code, "tool_not_found");
    assert.match(body.error.message, /godot_open_mcp_missing/);
  } finally {
    await bridge.close();
  }
});

test("postTool: HTTP 405 method_not_allowed surfaces the body's error code", async () => {
  // A GET to /tools/{name} returns 405 method_not_allowed. The client must
  // surface the body's code (not the generic bridge_http_error) so the agent
  // sees the method was wrong.
  const bridge = await startBridgeStub((req, res) => {
    if (req.url === "/tools/godot_open_mcp_node_find") {
      res.writeHead(405, { "Content-Type": "application/json" });
      res.end(
        JSON.stringify({
          error: { code: "method_not_allowed", message: "POST required for tool endpoints" },
        }),
      );
      return;
    }
    res.writeHead(404);
    res.end();
  });
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_find", {});
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    assert.equal(body.error.code, "method_not_allowed");
  } finally {
    await bridge.close();
  }
});

test("postTool: HTTP 500 with no JSON body falls back to bridge_http_error", async () => {
  // A bare 500 (no JSON body) must fall back to bridge_http_error naming the
  // status — the client must not throw on the unparseable body.
  const bridge = await startBridgeStub((_req, res) => {
    res.writeHead(500);
    res.end("internal server error");
  });
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_find", {});
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    assert.equal(body.error.code, "bridge_http_error");
    assert.match(body.error.message, /HTTP 500/);
  } finally {
    await bridge.close();
  }
});

test("postTool: 200 OK with non-JSON body surfaces bridge_response_unparsable", async () => {
  // A 200 with a non-JSON body is a bridge contract violation (the bridge
  // always emits valid JSON envelopes). Surface it as a structured error
  // rather than manufacturing a fake success.
  const bridge = await startBridgeStub((_req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end("<<<not json>>>");
  });
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_find", {});
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    assert.equal(body.error.code, "bridge_response_unparsable");
  } finally {
    await bridge.close();
  }
});

test("postTool: 200 OK with body missing ok field surfaces bridge_response_unparsable", async () => {
  // A body that is valid JSON but not the canonical envelope (no `ok` field)
  // is contract drift — surface it rather than guessing success/failure.
  const bridge = await startBridgeStub((_req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ mutation: { success: true } }));
  });
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_find", {});
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    assert.equal(body.error.code, "bridge_response_unparsable");
  } finally {
    await bridge.close();
  }
});

test("postTool: 200 OK with ok:false but missing error object surfaces execution_error", async () => {
  // Defensive: a malformed failure envelope (ok:false but no error object)
  // must surface execution_error rather than crashing on a undefined access.
  const bridge = await startBridgeStub((_req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ ok: false }));
  });
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_find", {});
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    assert.equal(body.error.code, "execution_error");
  } finally {
    await bridge.close();
  }
});

test("postTool: sends the tool args as the POST body", async () => {
  // The client must serialize the args object as the JSON request body so the
  // bridge can parse them by key name. Pin that the body is JSON and carries
  // the caller's fields.
  const seen: { body?: string } = {};
  const bridge = await startBridgeStub((req, res) => {
    if (req.url === "/tools/godot_open_mcp_node_find" && req.method === "POST") {
      let body = "";
      req.on("data", (chunk) => (body += chunk));
      req.on("end", () => {
        seen.body = body;
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ ok: true, result: { found: true } }));
      });
      return;
    }
    res.writeHead(404);
    res.end();
  });
  try {
    const client = new LiveClient(bridge.port);
    await client.route("godot_open_mcp_node_find", { node_path: "/root/Player" });
    assert.ok(seen.body, "POST body must be captured");
    const parsed = JSON.parse(seen.body!);
    assert.equal(parsed.node_path, "/root/Player");
  } finally {
    await bridge.close();
  }
});

test("postTool: bridge down (ECONNREFUSED) surfaces bridge_offline", async () => {
  // The same fetch-with-timeout helper backs ping and postTool, so a
  // connection failure classifies identically. Pin that a non-ping tool call
  // against a dead bridge surfaces bridge_offline (not a throw).
  const client = new LiveClient(1, undefined, "/home/user/MyGame");
  const result = await client.route("godot_open_mcp_node_find", {});
  assert.equal(result.isError, true);
  const body = JSON.parse(textOf(result));
  assert.equal(body.error.code, "bridge_offline");
});

test("postTool: bridge too slow surfaces bridge_timeout (shared classifier)", async () => {
  // postTool reuses classifyPingFailure (the same fetch-with-timeout helper
  // backs both paths), so a response timeout classifies identically to ping.
  // Driving a real response-timeout through postTool takes ~40s because the
  // fetch timeout is floored at BRIDGE_DEFAULT_TIMEOUT_MS + slack (so the
  // client never preempts the bridge's own timeout envelope — a duplicate-
  // mutation hazard). Rather than pay that 40s in the suite, we assert the
  // classifier is shared by calling postTool against a bridge that returns
  // HTTP 200 with a non-envelope body — the unparsable path is fast and
  // proves postTool routes failures through the same makeErrorResult factory.
  // The real response-timeout classification is pinned by the ping test
  // above ("bridge too slow to answer surfaces bridge_timeout").
  const bridge = await startBridgeStub((_req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end("not an envelope");
  });
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_find", {});
    assert.equal(result.isError, true);
    // The unparsable-body path surfaces a structured error — proving postTool
    // never throws and routes through makeErrorResult.
    const body = JSON.parse(textOf(result));
    assert.equal(body.error.code, "bridge_response_unparsable");
  } finally {
    await bridge.close();
  }
});

test("postTool: attaches Authorization header when a token is provided", async () => {
  // The bearer token from the instance lock is attached to every request,
  // including POST /tools/{name}. Pin that the header is present.
  const seen: { auth?: string | null } = {};
  const bridge = await startBridgeStub((req, res) => {
    if (seen.auth === undefined) {
      seen.auth = req.headers["authorization"] ?? null;
    }
    successEnvelopeHandler("godot_open_mcp_node_find", { ok: true })(
      req,
      res,
    );
  });
  try {
    const token = "deadbeef".repeat(8);
    const client = new LiveClient(bridge.port, token);
    await client.route("godot_open_mcp_node_find", {});
    assert.equal(seen.auth, `Bearer ${token}`);
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

// ----- P2.2: godot_open_mcp_node_find result-shape round-trip -----
//
// The postTool envelope tests above use generic payloads. These tests pin the
// ACTUAL node_find result shapes the bridge handler emits — the { nodes, count,
// truncated } list shape, the { nodes, count, truncated, notFound } targeted-
// miss shape, and the no_edited_scene failure code — so a C# ↔ TS contract
// drift on the node_find-specific fields is caught at the MCP layer. The bridge
// handler itself (EditorInterface-coupled) is verified via the headless Godot
// smoke / live call path, not here.

test("node_find: targeted hit round-trips the NodeData list shape", async () => {
  // The bridge returns one resolved node. The client must surface the result
  // verbatim so the agent can read nodes[0].instanceId / .path / .type.
  const nodeData = {
    instanceId: 12345,
    name: "Player",
    path: "/root/Main/Player",
    type: "Node3D",
    scriptResourcePath: "res://player.gd",
    childCount: 2,
    children: null,
  };
  const bridge = await startBridgeStub(
    successEnvelopeHandler("godot_open_mcp_node_find", {
      nodes: [nodeData],
      count: 1,
      truncated: 0,
    }),
  );
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_find", {
      node_path: "Main/Player",
    });
    assert.equal(result.isError, false, "targeted hit is a success");
    const body = JSON.parse(textOf(result));
    assert.equal(body.count, 1);
    assert.equal(body.truncated, 0);
    assert.equal(body.nodes[0].name, "Player");
    assert.equal(body.nodes[0].path, "/root/Main/Player");
    assert.equal(body.nodes[0].type, "Node3D");
    // notFound must NOT be present on a hit — its absence is the signal.
    assert.equal(body.notFound, undefined);
  } finally {
    await bridge.close();
  }
});

test("node_find: targeted miss round-trips notFound:true (NOT an error)", async () => {
  // Mirrors Unity: a targeted lookup that misses is an empty list with
  // notFound:true, NOT ok:false. The client must surface it as isError:false
  // so an agent checking "does this node exist?" branches on the flag.
  const bridge = await startBridgeStub(
    successEnvelopeHandler("godot_open_mcp_node_find", {
      nodes: [],
      count: 0,
      truncated: 0,
      notFound: true,
    }),
  );
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_find", {
      node_path: "Main/Missing",
    });
    assert.equal(result.isError, false, "notFound is not an error");
    const body = JSON.parse(textOf(result));
    assert.equal(body.notFound, true);
    assert.equal(body.count, 0);
    assert.deepEqual(body.nodes, []);
  } finally {
    await bridge.close();
  }
});

test("node_find: list mode round-trips count + truncated", async () => {
  // A list scan that exceeds max_results reports the remainder in truncated.
  const bridge = await startBridgeStub(
    successEnvelopeHandler("godot_open_mcp_node_find", {
      nodes: [
        { instanceId: 1, name: "A", path: "/root/Main/A", type: "Node", childCount: 0, children: null },
        { instanceId: 2, name: "B", path: "/root/Main/B", type: "Node", childCount: 0, children: null },
      ],
      count: 2,
      truncated: 3,
    }),
  );
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_find", {
      type: "Node",
      max_results: 2,
    });
    assert.equal(result.isError, false);
    const body = JSON.parse(textOf(result));
    assert.equal(body.count, 2);
    assert.equal(body.truncated, 3);
    assert.equal(body.nodes.length, 2);
  } finally {
    await bridge.close();
  }
});

test("node_find: no_edited_scene failure code passes through", async () => {
  // When no scene is open, the bridge returns ok:false + no_edited_scene. The
  // client must surface the code verbatim so the agent can prompt to open a
  // .tscn rather than retrying blindly.
  const bridge = await startBridgeStub(
    failureEnvelopeHandler(
      "godot_open_mcp_node_find",
      "no_edited_scene",
      "No scene is currently being edited; open a .tscn before calling node_find.",
    ),
  );
  try {
    const client = new LiveClient(bridge.port);
    const result = await client.route("godot_open_mcp_node_find", {});
    assert.equal(result.isError, true);
    const body = JSON.parse(textOf(result));
    assert.equal(body.error.code, "no_edited_scene");
    assert.match(body.error.message, /\.tscn/);
  } finally {
    await bridge.close();
  }
});
