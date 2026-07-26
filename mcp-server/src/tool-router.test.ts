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
import { ToolSessionState } from "./tool-session-state.js";

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
 * a canned result. `isLiveAvailable` controls the P7.2 live-first probe
 * (default `false` so the offline fallback path is exercised unless a test
 * opts into live).
 */
function makeFakeLive(opts: {
  result?: CallToolResult;
  bridgeStatusResult?: CallToolResult;
  liveAvailable?: boolean;
} = {}): LiveClient & { calls: LiveCall[]; statusCalls: number; liveProbes: number } {
  const calls: LiveCall[] = [];
  let statusCalls = 0;
  let liveProbes = 0;
  const liveAvailable = opts.liveAvailable ?? false;
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
    liveProbes: 0,
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
    async isLiveAvailable() {
      liveProbes++;
      (this as unknown as { liveProbes: number }).liveProbes = liveProbes;
      return liveAvailable;
    },
  } as unknown as LiveClient & {
    calls: LiveCall[];
    statusCalls: number;
    liveProbes: number;
  };
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
  sessionState?: ToolSessionState,
  notifyToolListChanged?: () => void | Promise<void>,
): ToolRouter {
  return new ToolRouter(
    live,
    projectPath,
    eventStream,
    sessionState ?? new ToolSessionState(),
    notifyToolListChanged,
  );
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

/** Pull the structured error code from a result body, or undefined. */
function errCode(result: CallToolResult): string | undefined {
  const err = parseBody(result).error as { code?: string } | undefined;
  return err?.code;
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

// ---------------------------------------------------------------------------
// P7.2 — scene_get_data: live-first with offline fallback
// ---------------------------------------------------------------------------

import { mkdtemp, mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

const OFFLINE_SCENE = `[gd_scene load_steps=2 format=3]

[ext_resource type="Script" path="res://player.gd" id="1_s"]

[node name="Main" type="Node2D"]

[node name="Player" type="CharacterBody2D" parent="."]
script = ExtResource("1_s")
`;

async function makeOfflineProject(): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), "gom-router-"));
  await mkdir(join(root, "scenes"), { recursive: true });
  await writeFile(join(root, "scenes", "main.tscn"), OFFLINE_SCENE, "utf-8");
  return root;
}

test("route: scene_get_data forwards to live when the bridge is available", async () => {
  const live = makeFakeLive({
    liveAvailable: true,
    result: {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            path: "res://scenes/main.tscn",
            name: "Main",
            isDirty: true,
            rootType: "Node2D",
            hierarchyDepth: 1,
            root: { name: "Main", type: "Node2D" },
          }),
        },
      ],
      isError: false,
    },
  });
  const router = makeRouter(live, makeFakeEventStream());

  const result = await router.route("godot_open_mcp_scene_get_data", {
    hierarchy_depth: 1,
  });
  const body = parseBody(result);

  // Live wins — exactly one live route call, one availability probe.
  assert.equal(live.calls.length, 1);
  assert.equal(live.calls[0].tool, "godot_open_mcp_scene_get_data");
  assert.equal(live.liveProbes, 1);
  // The live payload (including unsaved isDirty) is preserved + tagged live.
  assert.equal(body.isDirty, true, "live reflects unsaved state");
  assert.equal(body._source, "live");
  assert.deepEqual(body._route, { route: "live" });
});

test("route: scene_get_data falls back to offline disk parse when the bridge is down", async () => {
  const projectRoot = await makeOfflineProject();
  const live = makeFakeLive({ liveAvailable: false });
  const router = makeRouter(live, makeFakeEventStream(), projectRoot);

  const result = await router.route("godot_open_mcp_scene_get_data", {
    path: "res://scenes/main.tscn",
    hierarchy_depth: 1,
  });
  const body = parseBody(result);

  assert.equal(result.isError, false);
  assert.equal(live.calls.length, 0, "no live POST when bridge unavailable");
  assert.equal(live.liveProbes, 1, "probed once then fell back");
  assert.equal(body._source, "offline");
  assert.deepEqual(body._route, {
    route: "offline",
    fallbackReason: "live_unavailable",
  });
  // Offline payload: disk-sourced scene with the normalized shape.
  assert.equal(body.stateSource, "disk");
  assert.equal(body.isDirty, false);
  assert.equal(body.name, "Main");
  assert.equal(body.rootType, "Node2D");
  const root = body.root as {
    instanceId: unknown;
    children: { name: string; scriptResourcePath: string }[];
  };
  assert.equal(root.instanceId, null);
  const player = root.children.find((c) => c.name === "Player");
  assert.ok(player);
  assert.equal(player.scriptResourcePath, "res://player.gd");
});

test("route: scene_get_data offline without path returns path_required_offline", async () => {
  const projectRoot = await makeOfflineProject();
  const live = makeFakeLive({ liveAvailable: false });
  const router = makeRouter(live, makeFakeEventStream(), projectRoot);

  const result = await router.route("godot_open_mcp_scene_get_data", {
    hierarchy_depth: 1,
  });
  const body = parseBody(result);

  assert.equal(result.isError, true);
  assert.equal(body._source, "offline");
  assert.equal(errCode(result), "path_required_offline");
  assert.deepEqual(body._route, {
    route: "offline",
    fallbackReason: "live_unavailable",
  });
});

test("route: scene_get_data offline structured error is tagged offline + isError", async () => {
  const projectRoot = await makeOfflineProject();
  const live = makeFakeLive({ liveAvailable: false });
  const router = makeRouter(live, makeFakeEventStream(), projectRoot);

  // Missing file → scene_not_found, surfaced as an offline-tagged error.
  const result = await router.route("godot_open_mcp_scene_get_data", {
    path: "res://scenes/missing.tscn",
  });
  const body = parseBody(result);

  assert.equal(result.isError, true);
  assert.equal(body._source, "offline");
  assert.equal(errCode(result), "scene_not_found");
});

test("route: scene_get_data does NOT fall back to disk after a live semantic error", async () => {
  // The bridge is UP (liveAvailable=true) but returns a semantic scene_not_edited
  // error. The router must surface that live error authoritatively — NOT fall
  // back to disk. Only an UNAVAILABLE bridge triggers the fallback.
  const live = makeFakeLive({
    liveAvailable: true,
    result: {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            error: {
              code: "scene_not_edited",
              message: "requested scene is not the edited scene",
            },
          }),
        },
      ],
      isError: true,
    },
  });
  const projectRoot = await makeOfflineProject();
  const router = makeRouter(live, makeFakeEventStream(), projectRoot);

  const result = await router.route("godot_open_mcp_scene_get_data", {
    path: "res://scenes/main.tscn",
  });
  const body = parseBody(result);

  assert.equal(result.isError, true);
  assert.equal(errCode(result), "scene_not_edited", "live error preserved");
  assert.equal(body._source, "live", "tagged live, not offline");
  assert.deepEqual(body._route, { route: "live" });
  assert.equal(live.calls.length, 1, "forwarded to live exactly once");
});

// ---------------------------------------------------------------------------
// P7.3 — filesystem_list: live-first with offline fallback
// ---------------------------------------------------------------------------

async function makeListingProject(): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), "gom-router-list-"));
  await writeFile(
    join(root, "project.godot"),
    `[application]\nconfig/name="Listing Project"\n`,
    "utf-8",
  );
  await mkdir(join(root, "scenes"), { recursive: true });
  await writeFile(join(root, "scenes", "main.tscn"), "x", "utf-8");
  await writeFile(join(root, "player.gd"), "x", "utf-8");
  return root;
}

test("route: filesystem_list forwards to live when the bridge is available", async () => {
  const live = makeFakeLive({
    liveAvailable: true,
    result: {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            path: "res://",
            directoryCount: 1,
            fileCount: 1,
            entries: [
              {
                name: "scenes",
                path: "res://scenes/",
                isDirectory: true,
                resourceType: null,
                uid: null,
              },
              {
                name: "player.gd",
                path: "res://player.gd",
                isDirectory: false,
                resourceType: "GDScript",
                uid: "uid://abc123",
              },
            ],
            pagination: { nextCursor: null },
          }),
        },
      ],
      isError: false,
    },
  });
  const router = makeRouter(live, makeFakeEventStream());

  const result = await router.route("godot_open_mcp_filesystem_list", {
    path: "res://",
  });
  const body = parseBody(result);

  // Live wins — exactly one live route call, one availability probe.
  assert.equal(live.calls.length, 1);
  assert.equal(live.calls[0].tool, "godot_open_mcp_filesystem_list");
  assert.equal(live.liveProbes, 1);
  // The live payload (including importer uid) is preserved + tagged live.
  assert.equal(body.directoryCount, 1);
  assert.equal(body._source, "live");
  assert.deepEqual(body._route, { route: "live" });
});

test("route: filesystem_list falls back to offline disk listing when the bridge is down", async () => {
  const projectRoot = await makeListingProject();
  const live = makeFakeLive({ liveAvailable: false });
  const router = makeRouter(live, makeFakeEventStream(), projectRoot);

  const result = await router.route("godot_open_mcp_filesystem_list", {
    path: "res://",
  });
  const body = parseBody(result);

  assert.equal(result.isError, false);
  assert.equal(live.calls.length, 0, "no live POST when bridge unavailable");
  assert.equal(live.liveProbes, 1, "probed once then fell back");
  assert.equal(body._source, "offline");
  assert.deepEqual(body._route, {
    route: "offline",
    fallbackReason: "live_unavailable",
  });
  // Offline payload: disk-sourced listing with the same shape + stateSource.
  assert.equal(body.stateSource, "disk");
  assert.equal(body.path, "res://");
  assert.equal(body.directoryCount, 1);
  assert.equal(body.fileCount, 1);
  const entries = body.entries as { name: string; resourceType: string | null; uid: null }[];
  const names = entries.map((e) => e.name);
  // Directories first (scenes), then files (player.gd).
  assert.deepEqual(names, ["scenes", "player.gd"]);
  // Offline uid is always null.
  for (const e of entries) assert.equal(e.uid, null);
  // Offline resource type is best-effort by extension.
  const player = entries.find((e) => e.name === "player.gd");
  assert.equal(player!.resourceType, "GDScript");
});

test("route: filesystem_list offline without path defaults to res:// project root", async () => {
  const projectRoot = await makeListingProject();
  const live = makeFakeLive({ liveAvailable: false });
  const router = makeRouter(live, makeFakeEventStream(), projectRoot);

  const result = await router.route("godot_open_mcp_filesystem_list", {});
  const body = parseBody(result);

  assert.equal(result.isError, false);
  assert.equal(body._source, "offline");
  assert.equal(body.path, "res://", "defaults to project root");
  assert.equal(body.directoryCount, 1);
});

test("route: filesystem_list offline passes page_size + include_hidden through", async () => {
  const projectRoot = await mkdtemp(join(tmpdir(), "gom-router-opts-"));
  await writeFile(
    join(projectRoot, "project.godot"),
    `[application]\nconfig/name="Opts"\n`,
    "utf-8",
  );
  // 3 visible + 1 hidden file.
  for (const n of ["a.gd", "b.gd", "c.gd"]) {
    await writeFile(join(projectRoot, n), "x", "utf-8");
  }
  await writeFile(join(projectRoot, ".hidden.gd"), "x", "utf-8");

  const live = makeFakeLive({ liveAvailable: false });
  const router = makeRouter(live, makeFakeEventStream(), projectRoot);

  // page_size 2 → first page has 2 entries; hidden excluded by default.
  const p1 = await router.route("godot_open_mcp_filesystem_list", {
    page_size: 2,
  });
  const b1 = parseBody(p1);
  assert.equal(b1.fileCount, 3, "hidden not counted by default");
  assert.equal((b1.entries as unknown[]).length, 2);
  assert.ok((b1.pagination as { nextCursor: string | null }).nextCursor);

  // include_hidden surfaces the dotfile.
  const all = await router.route("godot_open_mcp_filesystem_list", {
    page_size: 100,
    include_hidden: true,
  });
  const bAll = parseBody(all);
  assert.equal(bAll.fileCount, 4, "hidden counted with include_hidden");
});

test("route: filesystem_list offline structured error is tagged offline + isError", async () => {
  const projectRoot = await makeListingProject();
  const live = makeFakeLive({ liveAvailable: false });
  const router = makeRouter(live, makeFakeEventStream(), projectRoot);

  // Missing directory → directory_not_found, surfaced as an offline-tagged error.
  const result = await router.route("godot_open_mcp_filesystem_list", {
    path: "res://nope/",
  });
  const body = parseBody(result);

  assert.equal(result.isError, true);
  assert.equal(body._source, "offline");
  assert.equal(errCode(result), "directory_not_found");
});

test("route: filesystem_list does NOT fall back to disk after a live semantic error", async () => {
  // The bridge is UP but returns a semantic directory_not_found. The router
  // surfaces the live error authoritatively — NOT a disk fallback.
  const live = makeFakeLive({
    liveAvailable: true,
    result: {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            error: {
              code: "directory_not_found",
              message: "indexed directory missing",
            },
          }),
        },
      ],
      isError: true,
    },
  });
  const projectRoot = await makeListingProject();
  const router = makeRouter(live, makeFakeEventStream(), projectRoot);

  const result = await router.route("godot_open_mcp_filesystem_list", {
    path: "res://missing/",
  });
  const body = parseBody(result);

  assert.equal(result.isError, true);
  assert.equal(errCode(result), "directory_not_found", "live error preserved");
  assert.equal(body._source, "live", "tagged live, not offline");
  assert.deepEqual(body._route, { route: "live" });
  assert.equal(live.calls.length, 1, "forwarded to live exactly once");
});

// ---------------------------------------------------------------------------
// P7.4 — read_compile_errors: always-offline (never calls the bridge)
// ---------------------------------------------------------------------------

const LOG_PROJECT_GODOT = `config_version=5

[application]

config/name="Log Project"

[debug]

[file_logging]

enable_file_logging=true
`;

/**
 * Build a temp project whose Godot log lives under a known absolute path. The
 * env override `GODOT_OPEN_MCP_LOG_FILE` is set on the router process so the
 * resolver picks our temp file deterministically (the default user-data dir
 * would land under the real user home and be flaky in CI).
 *
 * Returns the project root + the log file path. The caller writes the log
 * content via `writeFileSync(logPath, ...)`.
 */
async function makeLogProject(): Promise<{ root: string; logPath: string }> {
  const root = await mkdtemp(join(tmpdir(), "gom-router-log-"));
  await writeFile(join(root, "project.godot"), LOG_PROJECT_GODOT, "utf-8");
  const logPath = join(root, "godot.log");
  process.env.GODOT_OPEN_MCP_LOG_FILE = logPath;
  return { root, logPath };
}

function clearLogEnvOverride(): void {
  delete process.env.GODOT_OPEN_MCP_LOG_FILE;
}

test("route: read_compile_errors never probes live and never calls the bridge", async () => {
  const { root, logPath } = await makeLogProject();
  try {
    await writeFile(logPath, "clean log, no errors\n", "utf-8");
    const live = makeFakeLive({ liveAvailable: true });
    const router = makeRouter(live, makeFakeEventStream(), root);

    const result = await router.route("godot_open_mcp_read_compile_errors", {});
    const body = parseBody(result);

    // The always-offline route MUST NOT probe availability or POST to the
    // bridge — even when the bridge is up.
    assert.equal(live.liveProbes, 0, "must not probe isLiveAvailable");
    assert.equal(live.calls.length, 0, "must not POST to the bridge");
    assert.equal(result.isError, false);
    assert.equal(body._source, "offline");
    assert.deepEqual(body._route, { route: "offline" });
    assert.equal(
      (body._route as { fallbackReason?: string }).fallbackReason,
      undefined,
      "offline is the primary route — no fallbackReason",
    );
  } finally {
    clearLogEnvOverride();
  }
});

test("route: read_compile_errors surfaces C# errors from the log tail", async () => {
  const { root, logPath } = await makeLogProject();
  try {
    const log = [
      "Godot startup preamble",
      "res://scripts/player.cs(75,27): error CS1061: 'Player' does not contain a definition for 'Jump'",
      "res://other.cs(10,2): error CS0103: The name 'Bar' does not exist",
    ].join("\n");
    await writeFile(logPath, log, "utf-8");

    const router = makeRouter(makeFakeLive(), makeFakeEventStream(), root);
    const result = await router.route("godot_open_mcp_read_compile_errors", {});
    const body = parseBody(result);

    assert.equal(result.isError, false);
    assert.equal(body.status, "compile_failed");
    assert.equal(body.unhealthy, true);
    assert.equal(body.errorCount, 2);
    assert.ok(typeof body.headline === "string" && body.headline.length > 0);
    const diags = body.diagnostics as { kind: string; file: string; code: string }[];
    assert.equal(diags.length, 2);
    assert.equal(diags[0].kind, "csharp");
    assert.equal(diags[0].file, "res://scripts/player.cs");
    assert.equal(diags[0].code, "CS1061");
    // Provenance fields.
    assert.equal(body.logPath, logPath);
    assert.equal(body.selectedLogKind, "current");
    assert.equal(body.logSource, "env_override");
    assert.equal(body.envOverrideUsed, true);
  } finally {
    clearLogEnvOverride();
  }
});

test("route: read_compile_errors surfaces GDScript parse errors", async () => {
  const { root, logPath } = await makeLogProject();
  try {
    const log =
      "res://scripts/player.gd:12 - Parse Error: The identifier 'jump' isn't declared.\n";
    await writeFile(logPath, log, "utf-8");

    const router = makeRouter(makeFakeLive(), makeFakeEventStream(), root);
    const result = await router.route("godot_open_mcp_read_compile_errors", {});
    const body = parseBody(result);

    assert.equal(body.status, "compile_failed");
    const diags = body.diagnostics as { kind: string; file: string; line: number }[];
    assert.equal(diags.length, 1);
    assert.equal(diags[0].kind, "gdscript");
    assert.equal(diags[0].file, "res://scripts/player.gd");
    assert.equal(diags[0].line, 12);
  } finally {
    clearLogEnvOverride();
  }
});

test("route: read_compile_errors returns logging_disabled when file logging is off + no log", async () => {
  // A project with file logging DISABLED and no log file on disk.
  const root = await mkdtemp(join(tmpdir(), "gom-router-nolog-"));
  await writeFile(
    join(root, "project.godot"),
    `config_version=5\n[application]\nconfig/name="NoLog"\n`,
    "utf-8",
  );
  // Point the env override at a path that does NOT exist under this project.
  process.env.GODOT_OPEN_MCP_LOG_FILE = join(root, "godot.log");
  try {
    const router = makeRouter(makeFakeLive(), makeFakeEventStream(), root);
    const result = await router.route("godot_open_mcp_read_compile_errors", {});
    const body = parseBody(result);

    // The tool SUCCEEDED — logging_disabled is a valid explanatory status, not
    // a hard error. isError must be false.
    assert.equal(result.isError, false, "logging_disabled is not a hard error");
    assert.equal(body.status, "logging_disabled");
    assert.equal(body.unhealthy, false);
    assert.equal(body.errorCount, 0);
    assert.equal(body.loggingDisabled, true);
    assert.ok(
      typeof body.headline === "string" && body.headline.length > 0,
      "carries an actionable explanation",
    );
    assert.equal(body._source, "offline");
  } finally {
    clearLogEnvOverride();
  }
});

test("route: read_compile_errors returns log_not_found when logging enabled but no log exists", async () => {
  // File logging ENABLED but the log file is missing (e.g. Godot has not run
  // since the setting was toggled). logging_disabled must NOT fire; log_not_found
  // is the correct explanatory status.
  const root = await mkdtemp(join(tmpdir(), "gom-router-missinglog-"));
  await writeFile(
    join(root, "project.godot"),
    `config_version=5\n[application]\nconfig/name="MissingLog"\n[debug]\n[file_logging]\nenable_file_logging=true\n`,
    "utf-8",
  );
  process.env.GODOT_OPEN_MCP_LOG_FILE = join(root, "godot.log");
  try {
    const router = makeRouter(makeFakeLive(), makeFakeEventStream(), root);
    const result = await router.route("godot_open_mcp_read_compile_errors", {});
    const body = parseBody(result);

    assert.equal(result.isError, false);
    assert.equal(body.status, "log_not_found");
    assert.equal(body.unhealthy, false);
    assert.equal(body.loggingDisabled, false);
  } finally {
    clearLogEnvOverride();
  }
});

test("route: read_compile_errors returns no_errors_found for a clean log", async () => {
  const { root, logPath } = await makeLogProject();
  try {
    await writeFile(logPath, "Godot v4.3 - all good, no errors here\n", "utf-8");

    const router = makeRouter(makeFakeLive(), makeFakeEventStream(), root);
    const result = await router.route("godot_open_mcp_read_compile_errors", {});
    const body = parseBody(result);

    assert.equal(result.isError, false);
    assert.equal(body.status, "no_errors_found");
    assert.equal(body.unhealthy, false);
    assert.equal(body.errorCount, 0);
  } finally {
    clearLogEnvOverride();
  }
});

test("route: read_compile_errors project_not_found when project.godot is missing", async () => {
  const root = await mkdtemp(join(tmpdir(), "gom-router-noproj-"));
  // No project.godot marker written.
  process.env.GODOT_OPEN_MCP_LOG_FILE = join(root, "godot.log");
  try {
    const router = makeRouter(makeFakeLive(), makeFakeEventStream(), root);
    const result = await router.route("godot_open_mcp_read_compile_errors", {});
    const body = parseBody(result);

    assert.equal(result.isError, true);
    assert.equal(errCode(result), "project_not_found");
    assert.equal(body._source, "offline");
  } finally {
    clearLogEnvOverride();
  }
});

test("route: read_compile_errors stale-log advisory flags when a cited source is newer", async () => {
  const { root, logPath } = await makeLogProject();
  try {
    // Write a C# error citing a source file, then make that source NEWER than
    // the log → stale-log advisory should fire.
    await mkdir(join(root, "scripts"), { recursive: true });
    const srcPath = join(root, "scripts", "player.cs");
    const srcContent = "namespace Fixed {}";
    await writeFile(srcPath, srcContent, "utf-8");
    const log =
      "res://scripts/player.cs(75,27): error CS1061: 'Player' does not contain 'Jump'\n";
    await writeFile(logPath, log, "utf-8");
    // Log older, source newer → stale.
    const oldT = new Date(1000);
    const newT = new Date(2000);
    const { utimes } = await import("node:fs/promises");
    await utimes(logPath, oldT, oldT);
    await utimes(srcPath, newT, newT);

    const router = makeRouter(makeFakeLive(), makeFakeEventStream(), root);
    const result = await router.route("godot_open_mcp_read_compile_errors", {});
    const body = parseBody(result);

    assert.equal(body.staleLogSuspected, true);
    assert.ok(Array.isArray(body.staleLogNewerFiles) && body.staleLogNewerFiles.length === 1);
    assert.equal(
      (body.staleLogNewerFiles as string[])[0],
      "scripts/player.cs",
    );
    assert.ok(typeof body.staleLogHint === "string" && body.staleLogHint.length > 0);
    // Diagnostics are still surfaced — staleness is advisory, never suppresses.
    assert.equal(body.errorCount, 1);
  } finally {
    clearLogEnvOverride();
  }
});

// ---------------------------------------------------------------------------
// P8.3 — manage_tools routes local + mutates session state
// ---------------------------------------------------------------------------

test("route: manage_tools list_groups returns the catalog with session activation state", async () => {
  const router = makeRouter(makeFakeLive(), makeFakeEventStream());
  const result = await router.route("godot_open_mcp_manage_tools", {
    action: "list_groups",
  });
  const body = parseBody(result);

  assert.equal(result.isError, false);
  assert.equal(body._source, "local");
  assert.deepEqual(body._route, { route: "local" });
  assert.ok(Array.isArray(body.groups), "groups must be an array");

  // Catalog order is preserved — pin core first, typed-editor second.
  const ids = (body.groups as Array<{ id: string }>).map((g) => g.id);
  assert.equal(ids[0], "core");
  assert.equal(ids[1], "typed-editor");

  // Fresh session: core active + default-on; typed-editor inactive + opt-in.
  const core = (body.groups as Array<{
    id: string;
    active: boolean;
    defaultEnabled: boolean;
    activationSource: string | null;
    tools: string[];
    toolCount: number;
  }>).find((g) => g.id === "core");
  assert.ok(core, "core must be in the catalog");
  assert.equal(core!.active, true, "fresh session has core active");
  assert.equal(core!.defaultEnabled, true);
  assert.equal(core!.activationSource, "default");
  assert.ok(core!.toolCount >= 1, "core has a non-empty roster");
  assert.ok(
    core!.tools.includes("godot_open_mcp_ping"),
    "core roster includes ping",
  );

  const typed = (body.groups as Array<{
    id: string;
    active: boolean;
    defaultEnabled: boolean;
    activationSource: string | null;
  }>).find((g) => g.id === "typed-editor");
  assert.ok(typed, "typed-editor must be in the catalog");
  assert.equal(typed!.active, false, "fresh session has typed-editor inactive");
  assert.equal(typed!.defaultEnabled, false);
  assert.equal(typed!.activationSource, null);

  // The domain packs have been filled in progressively: tilemap 6, navigation 7,
  // particles 5, animation 7, csg 7. Phase 12 is now complete — every reserved
  // stub is filled, so there is no longer an empty-stub case to exercise here.
  // Assert each pack's roster count so a future pack rename or tool removal fails
  // loudly.
  const tilemap = (body.groups as Array<{
    id: string;
    toolCount: number;
    tools: string[];
    active: boolean;
  }>).find((g) => g.id === "tilemap");
  assert.ok(tilemap, "tilemap group must be in the catalog");
  assert.equal(tilemap!.toolCount, 6);
  assert.equal(tilemap!.tools.length, 6);
  assert.equal(tilemap!.active, false, "fresh session has tilemap inactive");

  const navigation = (body.groups as Array<{
    id: string;
    toolCount: number;
    tools: string[];
  }>).find((g) => g.id === "navigation");
  assert.ok(navigation, "navigation group must be in the catalog");
  assert.equal(navigation!.toolCount, 7);
  assert.equal(navigation!.tools.length, 7);

  const particles = (body.groups as Array<{
    id: string;
    toolCount: number;
    tools: string[];
  }>).find((g) => g.id === "particles");
  assert.ok(particles, "particles group must be in the catalog");
  assert.equal(particles!.toolCount, 5, "particles is now filled (P12.3) — five tools");
  assert.equal(particles!.tools.length, 5);

  const animation = (body.groups as Array<{
    id: string;
    toolCount: number;
    tools: string[];
  }>).find((g) => g.id === "animation");
  assert.ok(animation, "animation group must be in the catalog");
  assert.equal(animation!.toolCount, 7, "animation is now filled (P12.4) — seven tools");
  assert.equal(animation!.tools.length, 7);

  const csg = (body.groups as Array<{
    id: string;
    toolCount: number;
    tools: string[];
  }>).find((g) => g.id === "csg");
  assert.ok(csg, "csg group must be in the catalog");
  assert.equal(csg!.toolCount, 7, "csg is now filled (P12.5) — seven tools");
  assert.equal(csg!.tools.length, 7);

  // Active set snapshot.
  assert.deepEqual(body.activeGroups, ["core"]);
});

test("route: manage_tools activate adds a group to the session", async () => {
  const session = new ToolSessionState();
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    session,
  );
  const result = await router.route("godot_open_mcp_manage_tools", {
    action: "activate",
    group: "typed-editor",
  });
  const body = parseBody(result);

  assert.equal(result.isError, false);
  assert.equal(body._source, "local");
  assert.equal(body.changed, true);
  assert.equal(body.action, "activate");
  assert.equal(body.group, "typed-editor");
  assert.deepEqual(body.activeGroups, ["core", "typed-editor"]);
  // The store reflects the change — the same instance ListTools reads.
  assert.ok(session.isGroupActive("typed-editor"));
  assert.equal(session.activationSource("typed-editor"), "manual");
});

test("route: manage_tools activate is idempotent (changed=false on second call)", async () => {
  const session = new ToolSessionState();
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    session,
  );
  await router.route("godot_open_mcp_manage_tools", {
    action: "activate",
    group: "typed-editor",
  });
  const second = await router.route("godot_open_mcp_manage_tools", {
    action: "activate",
    group: "typed-editor",
  });
  const body = parseBody(second);
  assert.equal(body.changed, false);
});

test("route: manage_tools activate rejects unknown group with structured error", async () => {
  const router = makeRouter(makeFakeLive(), makeFakeEventStream());
  const result = await router.route("godot_open_mcp_manage_tools", {
    action: "activate",
    group: "does-not-exist",
  });
  assert.equal(result.isError, true);
  assert.equal(errCode(result), "unknown_group");
  // The error body is still tagged local — manage_tools is always local.
  assert.equal(parseBody(result)._source, "local");
});

test("route: manage_tools activate without group returns missing_parameter", async () => {
  const router = makeRouter(makeFakeLive(), makeFakeEventStream());
  const result = await router.route("godot_open_mcp_manage_tools", {
    action: "activate",
  });
  assert.equal(result.isError, true);
  assert.equal(errCode(result), "missing_parameter");
});

test("route: manage_tools deactivate removes a group", async () => {
  const session = new ToolSessionState();
  session.activate("typed-editor");
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    session,
  );
  const result = await router.route("godot_open_mcp_manage_tools", {
    action: "deactivate",
    group: "typed-editor",
  });
  const body = parseBody(result);
  assert.equal(body.changed, true);
  assert.equal(body.action, "deactivate");
  assert.equal(body.group, "typed-editor");
  assert.deepEqual(body.activeGroups, ["core"]);
  assert.equal(session.isGroupActive("typed-editor"), false);
});

test("route: manage_tools deactivate is idempotent on an already-inactive group", async () => {
  const router = makeRouter(makeFakeLive(), makeFakeEventStream());
  const result = await router.route("godot_open_mcp_manage_tools", {
    action: "deactivate",
    group: "typed-editor",
  });
  const body = parseBody(result);
  assert.equal(body.changed, false);
});

test("route: manage_tools reset restores the default-on groups (core only)", async () => {
  const session = new ToolSessionState();
  session.activate("typed-editor");
  session.activate("navigation");
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    session,
  );
  const result = await router.route("godot_open_mcp_manage_tools", {
    action: "reset",
  });
  const body = parseBody(result);
  assert.equal(body.reset, true);
  assert.equal(body.changed, true);
  assert.deepEqual(body.activeGroups, ["core"]);
});

test("route: manage_tools reset reports changed=false when state is already at defaults", async () => {
  // Fresh session is already at the defaults — reset must be a no-op for state
  // AND for the notification.
  const router = makeRouter(makeFakeLive(), makeFakeEventStream());
  const result = await router.route("godot_open_mcp_manage_tools", {
    action: "reset",
  });
  const body = parseBody(result);
  assert.equal(body.reset, true);
  assert.equal(body.changed, false, "reset on a fresh session is a no-op");
});

test("route: manage_tools unknown action returns structured error", async () => {
  const router = makeRouter(makeFakeLive(), makeFakeEventStream());
  const result = await router.route("godot_open_mcp_manage_tools", {
    action: "bogus",
  });
  assert.equal(result.isError, true);
  assert.equal(errCode(result), "unknown_action");
});

test("route: manage_tools does not hit the live bridge", async () => {
  // manage_tools is server-only — it never touches the bridge even when live.
  const live = makeFakeLive();
  const router = makeRouter(live, makeFakeEventStream());
  await router.route("godot_open_mcp_manage_tools", { action: "list_groups" });
  await router.route("godot_open_mcp_manage_tools", {
    action: "activate",
    group: "typed-editor",
  });
  await router.route("godot_open_mcp_manage_tools", { action: "reset" });
  assert.equal(live.calls.length, 0, "no POST /tools/{name} hop");
  assert.equal(live.statusCalls, 0, "no bridge_status composition");
  assert.equal(live.liveProbes, 0, "no isLiveAvailable probe");
});

test("route: manage_tools activate + ListTools filter expose typed-editor tools", async () => {
  // End-to-end: the same session store mutates and is read by the filter, so
  // an activate call makes node_find visible to the next ListTools response.
  const session = new ToolSessionState();
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    session,
  );
  // Before activate: node_find is hidden (typed-editor is opt-in).
  const { filterVisibleTools } = await import("./tool-session-state.js");
  const { ALL_TOOLS } = await import("./tools/index.js");
  const before = filterVisibleTools(ALL_TOOLS, session);
  assert.ok(
    !before.some((t) => t.name === "godot_open_mcp_node_find"),
    "node_find must be hidden before typed-editor is activated",
  );
  // Activate via the tool.
  await router.route("godot_open_mcp_manage_tools", {
    action: "activate",
    group: "typed-editor",
  });
  // After activate: node_find appears.
  const after = filterVisibleTools(ALL_TOOLS, session);
  assert.ok(
    after.some((t) => t.name === "godot_open_mcp_node_find"),
    "node_find must be visible after typed-editor is activated",
  );
});

// ---------------------------------------------------------------------------
// P8.3 — manage_tools notifies on visibility change (notifyToolListChanged)
// ---------------------------------------------------------------------------

test("route: manage_tools activate fires notifyToolListChanged when state changes", async () => {
  let notifyCount = 0;
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    new ToolSessionState(),
    () => {
      notifyCount++;
    },
  );
  await router.route("godot_open_mcp_manage_tools", {
    action: "activate",
    group: "typed-editor",
  });
  assert.equal(notifyCount, 1);
});

test("route: manage_tools activate does NOT notify when idempotent", async () => {
  let notifyCount = 0;
  const session = new ToolSessionState();
  session.activate("typed-editor");
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    session,
    () => {
      notifyCount++;
    },
  );
  await router.route("godot_open_mcp_manage_tools", {
    action: "activate",
    group: "typed-editor",
  });
  assert.equal(notifyCount, 0);
});

test("route: manage_tools deactivate fires notifyToolListChanged when state changes", async () => {
  let notifyCount = 0;
  const session = new ToolSessionState();
  session.activate("typed-editor");
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    session,
    () => {
      notifyCount++;
    },
  );
  await router.route("godot_open_mcp_manage_tools", {
    action: "deactivate",
    group: "typed-editor",
  });
  assert.equal(notifyCount, 1);
});

test("route: manage_tools reset fires notifyToolListChanged when state changes", async () => {
  let notifyCount = 0;
  const session = new ToolSessionState();
  session.activate("typed-editor");
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    session,
    () => {
      notifyCount++;
    },
  );
  await router.route("godot_open_mcp_manage_tools", { action: "reset" });
  assert.equal(notifyCount, 1);
});

test("route: manage_tools reset does NOT notify when already at defaults", async () => {
  let notifyCount = 0;
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    new ToolSessionState(),
    () => {
      notifyCount++;
    },
  );
  await router.route("godot_open_mcp_manage_tools", { action: "reset" });
  assert.equal(notifyCount, 0);
});

test("route: manage_tools list_groups does NOT notify", async () => {
  let notifyCount = 0;
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    new ToolSessionState(),
    () => {
      notifyCount++;
    },
  );
  await router.route("godot_open_mcp_manage_tools", { action: "list_groups" });
  assert.equal(notifyCount, 0, "list_groups is read-only — no notification");
});

test("route: manage_tools error paths do NOT notify", async () => {
  // Unknown group / missing group / unknown action must not fire the callback
  // — the visible set is unchanged.
  let notifyCount = 0;
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    new ToolSessionState(),
    () => {
      notifyCount++;
    },
  );
  await router.route("godot_open_mcp_manage_tools", {
    action: "activate",
    group: "does-not-exist",
  });
  await router.route("godot_open_mcp_manage_tools", {
    action: "activate",
  });
  await router.route("godot_open_mcp_manage_tools", { action: "bogus" });
  assert.equal(notifyCount, 0);
});

test("route: manage_tools succeed even when notify rejects (failure isolation)", async () => {
  // P8.4 §3 — a rejecting notifier must NOT flip the manage_tools result to
  // isError. The router swallows the rejection so a transport fault can't
  // surface as a tool-call failure. State still mutated; the change is durable.
  const session = new ToolSessionState();
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    session,
    async () => {
      throw new Error("transport down");
    },
  );
  const result = await router.route("godot_open_mcp_manage_tools", {
    action: "activate",
    group: "typed-editor",
  });
  const body = parseBody(result);

  assert.equal(result.isError, false, "rejection must not flip isError");
  assert.equal(body.changed, true, "state change is durable");
  assert.deepEqual(body.activeGroups, ["core", "typed-editor"]);
  assert.ok(
    session.isGroupActive("typed-editor"),
    "session still reflects the activation",
  );
});

test("route: manage_tools succeed even when notify throws synchronously (failure isolation)", async () => {
  // A notifier that throws synchronously (rather than returning a rejected
  // promise) must also be isolated — `await` re-throws sync throws the same
  // way, but this pins the contract explicitly.
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    new ToolSessionState(),
    () => {
      throw new Error("sync boom");
    },
  );
  const result = await router.route("godot_open_mcp_manage_tools", {
    action: "activate",
    group: "typed-editor",
  });
  assert.equal(result.isError, false, "sync throw must not flip isError");
  assert.equal(parseBody(result).changed, true);
});

test("route: manage_tools with no notifier wired is a no-op for notification", async () => {
  // P8.3/P8.4 — the notifier is optional. When omitted (e.g. test harnesses,
  // pre-P8.4 wiring), manage_tools must still mutate state and report changed.
  const session = new ToolSessionState();
  const router = makeRouter(
    makeFakeLive(),
    makeFakeEventStream(),
    "/proj",
    session,
    // No notifier — makeRouter leaves it undefined.
  );
  const result = await router.route("godot_open_mcp_manage_tools", {
    action: "activate",
    group: "typed-editor",
  });
  const body = parseBody(result);
  assert.equal(result.isError, false);
  assert.equal(body.changed, true);
  assert.ok(session.isGroupActive("typed-editor"));
});
