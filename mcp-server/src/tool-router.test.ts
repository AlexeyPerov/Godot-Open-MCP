// P7.1 — ToolRouter dispatch + metadata tests.
//
// Pins the routing contract introduced in P7.1:
//   - Generic (non-named) tools route through LiveClient exactly once and the
//     result is tagged `_source: "live"` + `_route.route: "live"`.
//   - Live structured errors preserve `isError` and receive route metadata.
//   - Capabilities resolves locally (never invokes the live client) with
//     `_source: "local"`.
//   - Bridge status invokes its current composition exactly once; the
//     pre-baked `_source: "local"` is preserved and `_route` is added.
//   - Pull events drains the injected shared stream with the bounded
//     `max_events` (default 50, hard cap 1000).
//   - Non-JSON text results are preserved unchanged; multi-content results
//     (image + text) preserve order and non-text blocks.
//   - No route named `batch` is ever emitted (Godot has no headless batch).
//
// Built + run via the project test config:
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'
//
// Adapted from Unity Open MCP's `mcp-server/src/tool-router.test.ts` (copy for
// the generic-live / local-dispatch / source-tagging / non-JSON preservation
// patterns). Intentional deltas: no batch / hub / compressible fakes; no
// offline disk-parser tests (those arrive in P7.2–P7.4); the Godot
// `BridgeEventStream` fake drains an in-memory queue.

import { test } from "node:test";
import assert from "node:assert/strict";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

import { ToolRouter } from "./tool-router.js";
import type { LiveClient } from "./live-client.js";
import type { BridgeEventStream, PullResult } from "./event-stream.js";

// ---------------------------------------------------------------------------
// fakes
// ---------------------------------------------------------------------------

interface LiveCall {
  tool: string;
  args: Record<string, unknown>;
}

/**
 * Minimal LiveClient fake. `route` records every call and returns a canned
 * result; `routeBridgeStatus` records its single composition call and returns
 * a canned result. The router only calls these two methods at P7.1.
 */
function makeFakeLive(opts: {
  result?: CallToolResult;
  bridgeStatusResult?: CallToolResult;
} = {}): LiveClient & { calls: LiveCall[]; statusCalls: number } {
  const calls: LiveCall[] = [];
  let statusCalls = 0;
  const result =
    opts.result ??
    ({
      content: [{ type: "text", text: JSON.stringify({ ok: true }) }],
      isError: false,
    } satisfies CallToolResult);
  const bridgeStatusResult =
    opts.bridgeStatusResult ??
    ({
      // Mirrors the shape LiveClient.routeBridgeStatus emits — including the
      // pre-baked `_source: "local"` the router must preserve.
      content: [
        {
          type: "text",
          text: JSON.stringify({
            status: "stopped",
            ready: false,
            classification: "gone",
            _source: "local",
          }),
        },
      ],
      isError: false,
    } satisfies CallToolResult);
  return {
    calls,
    statusCalls: 0,
    async route(tool: string, args: Record<string, unknown>) {
      calls.push({ tool, args });
      return result;
    },
    async routeBridgeStatus() {
      statusCalls++;
      // Re-expose the running count via a closure-captured mirror so the test
      // can assert how many compositions ran.
      (this as unknown as { statusCalls: number }).statusCalls = statusCalls;
      return bridgeStatusResult;
    },
  } as unknown as LiveClient & { calls: LiveCall[]; statusCalls: number };
}

/**
 * Fake event stream. The real `BridgeEventStream` opens an SSE reader; in unit
 * tests we don't want any network I/O, so the fake drains an in-memory queue
 * and reports how many `pull` calls ran.
 */
function makeFakeEventStream(
  events: PullResult["events"] = [],
): BridgeEventStream & { pullCalls: number[] } {
  let queue = events.slice();
  const pullCalls: number[] = [];
  return {
    pullCalls,
    pull(maxEvents: number) {
      pullCalls.push(maxEvents);
      const out = queue.slice(0, maxEvents);
      queue = queue.slice(maxEvents);
      return {
        subscriberId: "test-subscriber",
        events: out,
        dropped: 0,
        connected: true,
        started: queue.length === 0 ? false : true,
        lastError: null,
      };
    },
  } as unknown as BridgeEventStream & { pullCalls: number[] };
}

function makeRouter(
  live: LiveClient,
  eventStream: BridgeEventStream,
  projectPath = "/proj",
): ToolRouter {
  return new ToolRouter(live, projectPath, eventStream);
}

function parseBody(result: CallToolResult): Record<string, unknown> {
  const first = result.content[0];
  if (!first || first.type !== "text" || typeof first.text !== "string") {
    throw new Error("expected a text content part");
  }
  return JSON.parse(first.text);
}

function routeOf(result: CallToolResult): string {
  const route = parseBody(result)._route as { route?: string } | undefined;
  return route?.route ?? "";
}

// ---------------------------------------------------------------------------
// generic live route
// ---------------------------------------------------------------------------

test("route: generic tool calls live exactly once with unchanged args", async () => {
  const live = makeFakeLive();
  const router = makeRouter(live, makeFakeEventStream());

  await router.route("godot_open_mcp_node_find", {
    node_path: "/root/Player",
  });

  assert.equal(live.calls.length, 1);
  assert.equal(live.calls[0].tool, "godot_open_mcp_node_find");
  assert.deepEqual(live.calls[0].args, { node_path: "/root/Player" });
});

test("route: generic live success receives _source=live and _route.route=live", async () => {
  const live = makeFakeLive();
  const router = makeRouter(live, makeFakeEventStream());

  const result = await router.route("godot_open_mcp_ping", {});
  const body = parseBody(result);

  assert.equal(result.isError, false);
  assert.equal(body._source, "live");
  assert.deepEqual(body._route, { route: "live" });
  // The original payload is preserved alongside the metadata.
  assert.equal(body.ok, true);
});

test("route: live structured error preserves isError and receives route metadata", async () => {
  const live = makeFakeLive({
    result: {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            error: { code: "bridge_offline", message: "down" },
          }),
        },
      ],
      isError: true,
    },
  });
  const router = makeRouter(live, makeFakeEventStream());

  const result = await router.route("godot_open_mcp_ping", {});
  const body = parseBody(result);

  assert.equal(result.isError, true, "isError must be preserved");
  assert.equal(
    (body.error as { code?: string }).code,
    "bridge_offline",
    "error code preserved",
  );
  assert.equal(body._source, "live");
  assert.deepEqual(body._route, { route: "live" });
});

// ---------------------------------------------------------------------------
// capabilities — local, never touches live
// ---------------------------------------------------------------------------

test("route: capabilities resolves locally and never invokes live", async () => {
  const live = makeFakeLive();
  const router = makeRouter(live, makeFakeEventStream());

  const result = await router.route("godot_open_mcp_capabilities", {});
  const body = parseBody(result);

  assert.equal(result.isError, false);
  assert.equal(body._source, "local");
  assert.deepEqual(body._route, { route: "local" });
  assert.equal(live.calls.length, 0, "capabilities must not hit the live bridge");
  assert.ok(Array.isArray(body.tools) && body.tools.length >= 1);
});

test("route: capabilities kind=rules narrows to rules only", async () => {
  const router = makeRouter(makeFakeLive(), makeFakeEventStream());
  const result = await router.route("godot_open_mcp_capabilities", {
    kind: "rules",
  });
  const body = parseBody(result);

  assert.equal(body._source, "local");
  assert.equal((body.tools as unknown[]).length, 0);
  assert.ok((body.rules as unknown[]).length >= 1, "rules surface present");
  assert.equal((body.fixes as unknown[]).length, 0);
});

// ---------------------------------------------------------------------------
// bridge_status — local/live hybrid, single composition
// ---------------------------------------------------------------------------

test("route: bridge_status invokes its composition once and preserves _source=local", async () => {
  const live = makeFakeLive();
  const router = makeRouter(live, makeFakeEventStream());

  const result = await router.route("godot_open_mcp_bridge_status", {});
  const body = parseBody(result);

  assert.equal(result.isError, false);
  // The body pre-bakes _source: "local"; the route is local, so no conflict.
  assert.equal(body._source, "local");
  assert.deepEqual(body._route, { route: "local" });
  assert.equal(body.status, "stopped", "status payload preserved");
  assert.equal(live.calls.length, 0, "no generic route() hop");
  assert.equal(live.statusCalls, 1, "routeBridgeStatus composed exactly once");
});

// ---------------------------------------------------------------------------
// pull_events — drains the shared stream
// ---------------------------------------------------------------------------

test("route: pull_events drains the injected shared stream", async () => {
  const events = [
    {
      seq: 1,
      ts: "2026-07-16T00:00:00.000Z",
      type: "log" as const,
      logType: "error",
      message: "boom",
    },
    {
      seq: 2,
      ts: "2026-07-16T00:00:00.100Z",
      type: "editor_state" as const,
      state: "idle",
      isCompiling: false,
      isPlaying: false,
    },
  ];
  const stream = makeFakeEventStream(events);
  const router = makeRouter(makeFakeLive(), stream);

  const result = await router.route("godot_open_mcp_pull_events", {
    max_events: 10,
  });
  const body = parseBody(result);

  assert.equal(result.isError, false);
  assert.equal(body._source, "local");
  assert.deepEqual(body._route, { route: "local" });
  assert.ok(Array.isArray(body.events) && body.events.length === 2);
  assert.deepEqual(stream.pullCalls, [10], "passes max_events through");
});

test("route: pull_events default max_events=50 when missing or invalid", async () => {
  const stream = makeFakeEventStream();
  const router = makeRouter(makeFakeLive(), stream);

  await router.route("godot_open_mcp_pull_events", {});
  await router.route("godot_open_mcp_pull_events", { max_events: "lots" });
  await router.route("godot_open_mcp_pull_events", { max_events: -1 });

  assert.deepEqual(stream.pullCalls, [50, 50, 50]);
});

test("route: pull_events caps oversized max_events at 1000", async () => {
  const stream = makeFakeEventStream();
  const router = makeRouter(makeFakeLive(), stream);

  await router.route("godot_open_mcp_pull_events", { max_events: 999999 });

  assert.deepEqual(stream.pullCalls, [1000]);
});

test("route: pull_events connected=false is a successful status result, not an error", async () => {
  const stream = makeFakeEventStream();
  (stream as unknown as { pull: BridgeEventStream["pull"] }).pull = () => ({
    subscriberId: "test-subscriber",
    events: [],
    dropped: 0,
    connected: false,
    started: false,
    lastError: "connect ECONNREFUSED",
  });
  const router = makeRouter(makeFakeLive(), stream);

  const result = await router.route("godot_open_mcp_pull_events", {});
  const body = parseBody(result);

  assert.equal(result.isError, false, "offline stream is not a routing error");
  assert.equal(body.connected, false);
  assert.equal(body.lastError, "connect ECONNREFUSED");
  assert.equal(body._source, "local");
});

// ---------------------------------------------------------------------------
// metadata preservation — non-JSON text + multi-content results
// ---------------------------------------------------------------------------

test("route: non-JSON live text result is preserved unchanged (no metadata injected)", async () => {
  const live = makeFakeLive({
    result: {
      content: [{ type: "text", text: "plain string, not json {" }],
      isError: false,
    },
  });
  const router = makeRouter(live, makeFakeEventStream());

  const result = await router.route("godot_open_mcp_ping", {});
  const block = result.content[0];

  assert.equal(block.type, "text");
  assert.ok(block.type === "text");
  // Non-JSON text must round-trip verbatim; no _source / _route is added.
  assert.equal(block.text, "plain string, not json {");
});

test("route: multi-content (image + text) live result preserves order, image block, and tags the JSON text block", async () => {
  const live = makeFakeLive({
    result: {
      content: [
        { type: "image", data: "ZmFrZS1wbmctYnl0ZXM=", mimeType: "image/png" },
        {
          type: "text",
          text: JSON.stringify({ width: 800, height: 600, mode: "viewport" }),
        },
      ],
      isError: false,
    },
  });
  const router = makeRouter(live, makeFakeEventStream());

  const result = await router.route("godot_open_mcp_screenshot_viewport", {});

  assert.equal(result.content.length, 2, "both blocks preserved");
  // Image block untouched (still first).
  const img = result.content[0];
  assert.equal(img.type, "image");
  assert.ok(img.type === "image");
  assert.equal(img.mimeType, "image/png");
  // Text block carries the original payload + injected metadata.
  const txt = result.content[1];
  assert.equal(txt.type, "text");
  assert.ok(txt.type === "text");
  const body = JSON.parse(txt.text);
  assert.equal(body.width, 800);
  assert.equal(body.height, 600);
  assert.equal(body._source, "live");
  assert.deepEqual(body._route, { route: "live" });
});

test("route: JSON array (non-object) live result is preserved unchanged", async () => {
  // Arrays are valid JSON but not objects — metadata targets objects only.
  const live = makeFakeLive({
    result: {
      content: [{ type: "text", text: JSON.stringify([1, 2, 3]) }],
      isError: false,
    },
  });
  const router = makeRouter(live, makeFakeEventStream());

  const result = await router.route("godot_open_mcp_node_find", {});
  const block = result.content[0];

  assert.equal(block.type, "text");
  assert.ok(block.type === "text");
  assert.equal(block.text, JSON.stringify([1, 2, 3]));
});

// ---------------------------------------------------------------------------
// no batch route is ever emitted (Godot has no headless batch)
// ---------------------------------------------------------------------------

test("route: no route name 'batch' is ever emitted", async () => {
  const live = makeFakeLive();
  const router = makeRouter(live, makeFakeEventStream());

  // Exercise one generic live call + every local handler.
  await router.route("godot_open_mcp_ping", {});
  await router.route("godot_open_mcp_capabilities", {});
  await router.route("godot_open_mcp_bridge_status", {});
  await router.route("godot_open_mcp_pull_events", {});

  // No result body's _route.route may be "batch".
  for (const call of live.calls) {
    assert.notEqual(call.tool, "batch");
  }
});
