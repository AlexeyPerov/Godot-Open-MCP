// P5.4 — BridgeEventStream unit tests.
//
// Covers the SSE block parser contract surface (the wire boundary between bridge and MCP server),
// the client-side queue + drop accounting, the bearer-header behavior (P5.2), and the drain/pull
// lifecycle. The parser is the load-bearing piece — it must tolerate multi-line data blocks (log
// stacks split across `data:` lines) and the named event types the bridge emits
// (log / editor_state / ready / missed / close).
//
// Adapted from Unity Open MCP's mcp-server/src/event-stream.test.ts (copy fidelity): same parser
// cases, same bearer-header stub-server pattern. node:test + node:assert/strict, no external deps.

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  createServer,
  type Server as HttpServer,
  type IncomingMessage,
  type ServerResponse,
} from "node:http";
import { BridgeEventStream } from "./event-stream.js";

// --- parseSseBlock ---------------------------------------------------------------------

test("parseSseBlock: parses a log event with stack", () => {
  const block = [
    "event: log",
    'data: {"seq":42,"ts":"2026-07-12T00:00:00.000Z","type":"log","logType":"error","message":"boom","stack":"Frame1\\nFrame2"}',
    "",
  ].join("\n");
  const evt = BridgeEventStream.parseSseBlock(block);
  assert.ok(evt);
  assert.equal(evt?.type, "log");
  assert.equal(evt?.seq, 42);
  assert.equal(evt?.logType, "error");
  assert.equal(evt?.message, "boom");
  assert.equal(evt?.stack, "Frame1\nFrame2");
});

test("parseSseBlock: parses an editor_state event", () => {
  const block = [
    "event: editor_state",
    'data: {"seq":7,"ts":"2026-07-12T00:00:01.000Z","type":"editor_state","state":"compiling","isCompiling":true,"isPlaying":false}',
    "",
  ].join("\n");
  const evt = BridgeEventStream.parseSseBlock(block);
  assert.ok(evt);
  assert.equal(evt?.type, "editor_state");
  assert.equal(evt?.state, "compiling");
  assert.equal(evt?.isCompiling, true);
  assert.equal(evt?.isPlaying, false);
});

test("parseSseBlock: reassembles multi-line data blocks (stacks split across data: lines)", () => {
  // SSE splits long payloads across multiple `data:` lines; the parser must reassemble them with
  // embedded newlines before JSON.parse.
  const block = [
    "event: log",
    'data: {"seq":1,"ts":"2026-07-12T00:00:00.000Z","type":"log","logType":"log","message":"hi",',
    'data: "stack":"Line1\\nLine2"}',
    "",
  ].join("\n");
  const evt = BridgeEventStream.parseSseBlock(block);
  assert.ok(evt);
  assert.equal(evt?.type, "log");
  assert.equal(evt?.message, "hi");
  assert.equal(evt?.stack, "Line1\nLine2");
});

test("parseSseBlock: handles the 'ready' control event", () => {
  const block = [
    "event: ready",
    'data: {"subscriber":"abc123"}',
    "",
  ].join("\n");
  const evt = BridgeEventStream.parseSseBlock(block);
  assert.ok(evt);
  assert.equal(evt?.type, "ready");
});

test("parseSseBlock: handles the 'missed' control event", () => {
  const block = [
    "event: missed",
    'data: {"missed":3}',
    "",
  ].join("\n");
  const evt = BridgeEventStream.parseSseBlock(block);
  assert.ok(evt);
  assert.equal(evt?.type, "missed");
  assert.match(evt?.message ?? "", /missed=3/);
});

test("parseSseBlock: returns null for empty blocks", () => {
  assert.equal(BridgeEventStream.parseSseBlock(""), null);
  assert.equal(BridgeEventStream.parseSseBlock("\n\n"), null);
});

test("parseSseBlock: defaults seq/ts when bridge omits them", () => {
  const block = [
    "event: log",
    'data: {"type":"log","logType":"log","message":"hi"}',
    "",
  ].join("\n");
  const evt = BridgeEventStream.parseSseBlock(block);
  assert.ok(evt);
  assert.equal(typeof evt?.seq, "number");
  assert.equal(typeof evt?.ts, "string");
});

test("parseSseBlock: strips a single leading space after data: (SSE spec)", () => {
  // The bridge writer emits "data: <json>" with one space; the parser must strip exactly one.
  const block = [
    "event: log",
    'data: {"seq":1,"ts":"t","type":"log","logType":"log","message":" spaced"}',
    "",
  ].join("\n");
  const evt = BridgeEventStream.parseSseBlock(block);
  assert.ok(evt);
  assert.equal(evt?.message, " spaced");
});

// --- drain / stop lifecycle -------------------------------------------------------------

test("drain: returns at most maxEvents, leaves the rest buffered", () => {
  // Construct without connecting; the queue starts empty.
  const stream = new BridgeEventStream("http://127.0.0.1:1");
  const out = stream.drain(10);
  assert.ok(Array.isArray(out));
  assert.equal(out.length, 0);
});

test("drain: clamps maxEvents to (0, 1000] defaulting to 100", () => {
  const stream = new BridgeEventStream("http://127.0.0.1:1");
  // 0, negative, and >1000 all clamp to the default cap (100). No throw.
  assert.equal(stream.drain(0).length, 0);
  assert.equal(stream.drain(-5).length, 0);
  assert.equal(stream.drain(5000).length, 0);
});

test("stop: is idempotent and clears connection state", () => {
  const stream = new BridgeEventStream("http://127.0.0.1:1");
  stream.stop();
  stream.stop();
  assert.equal(stream.isConnected, false);
});

// --- P5.2 bearer token header on the SSE connection -----------------------------------

interface StubHandle {
  server: HttpServer;
  port: number;
  close(): Promise<void>;
}

function startSseStub(capture: { auth?: string | null }): Promise<StubHandle> {
  return new Promise((resolve) => {
    const server = createServer((req: IncomingMessage, res: ServerResponse) => {
      if (capture.auth === undefined) {
        capture.auth = req.headers["authorization"] ?? null;
      }
      // Minimal valid SSE response so connect()'s res.ok check passes and the reader enters its
      // loop; we close immediately after to end the test.
      res.writeHead(200, {
        "Content-Type": "text/event-stream",
        "Cache-Control": "no-cache",
        Connection: "keep-alive",
      });
      res.write("event: ready\ndata: {}\n\n");
      res.end();
    });
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

test("BridgeEventStream: sends Authorization: Bearer when a token was provided", async () => {
  const seen: { auth?: string | null } = {};
  const stub = await startSseStub(seen);
  try {
    const token = "deadbeef".repeat(8);
    const stream = new BridgeEventStream(
      `http://127.0.0.1:${stub.port}`,
      "test-sub",
      token,
    );
    stream.ensureSubscription();
    // Give the fetch + handler a tick to land.
    await new Promise((r) => setTimeout(r, 50));
    stream.stop();
    assert.equal(
      seen.auth,
      `Bearer ${token}`,
      "SSE connection must carry the discovered bearer token",
    );
  } finally {
    await stub.close();
  }
});

test("BridgeEventStream: omits Authorization when no token was provided", async () => {
  const seen: { auth?: string | null } = {};
  const stub = await startSseStub(seen);
  try {
    const stream = new BridgeEventStream(
      `http://127.0.0.1:${stub.port}`,
      "test-sub",
    );
    stream.ensureSubscription();
    await new Promise((r) => setTimeout(r, 50));
    stream.stop();
    assert.equal(
      seen.auth,
      null,
      'No Authorization header when the stream has no token (authMode "none")',
    );
  } finally {
    await stub.close();
  }
});

// --- pull() envelope shape --------------------------------------------------------------

test("pull: returns the PullResult envelope with subscriberId + connected fields", () => {
  // No network — connect() will fail asynchronously; pull() returns synchronously with started:true
  // (it just kicked off the subscription) and connected:false.
  const stream = new BridgeEventStream("http://127.0.0.1:1", "my-sub");
  const result = stream.pull(50);
  assert.equal(result.subscriberId, "my-sub");
  assert.equal(result.started, true);
  assert.ok(Array.isArray(result.events));
  assert.equal(result.events.length, 0);
  assert.equal(typeof result.dropped, "number");
  assert.equal(typeof result.connected, "boolean");
  stream.stop();
});

test("pull: second call reports started:false (subscription already running)", () => {
  const stream = new BridgeEventStream("http://127.0.0.1:1", "my-sub");
  stream.pull(50);
  const result = stream.pull(50);
  assert.equal(result.started, false);
  stream.stop();
});

// ---------------------------------------------------------------------------
// stop() must be final (regression)
// ---------------------------------------------------------------------------
//
// stop() aborts the controller, which makes any in-flight fetch/pump SETTLE — and both the connect
// `.catch` and pump's `finally` call scheduleReconnect(). Without a stop latch, stop() therefore
// armed a fresh 2s reconnect timer, whose connect() failed, whose .catch armed another, forever:
// an unbounded reconnect loop plus a live loopback fetch that kept the process alive. That is why
// `node --test` on this file used to hang and why the CLI's shutdown stop() had no effect.

/** Active libuv Timeout handles — a leaked reconnect timer shows up here. */
function timeoutHandles(): number {
  return process.getActiveResourcesInfo().filter((h) => h === "Timeout").length;
}

test("stop: no reconnect timer survives past the reconnect interval", async () => {
  const before = timeoutHandles();
  const stream = new BridgeEventStream("http://127.0.0.1:1");
  stream.pull(10); // starts a subscription against a dead port -> connect fails -> reconnect armed
  await new Promise((r) => setTimeout(r, 150));
  stream.stop();
  // Wait comfortably longer than RECONNECT_MS (2000) so a resurrected timer would have fired.
  await new Promise((r) => setTimeout(r, 2600));
  assert.equal(
    timeoutHandles(),
    before,
    "stop() must not leave (or re-arm) a reconnect timer",
  );
});

test("stop: connected/lastError stay quiet and the stream does not resurrect", async () => {
  const stream = new BridgeEventStream("http://127.0.0.1:1");
  stream.pull(10);
  await new Promise((r) => setTimeout(r, 150));
  stream.stop();
  await new Promise((r) => setTimeout(r, 2600));
  assert.equal(stream.isConnected, false, "a stopped stream must never report connected");
  // A resurrected loop would have replaced the abort-time state with a fresh "fetch failed".
  const result = stream.pull(10);
  assert.equal(result.started, true, "pull after stop starts a genuinely new subscription");
  stream.stop();
});

test("stop: is idempotent", () => {
  const stream = new BridgeEventStream("http://127.0.0.1:1");
  stream.pull(10);
  stream.stop();
  assert.doesNotThrow(() => stream.stop());
  assert.doesNotThrow(() => stream.stop());
});
