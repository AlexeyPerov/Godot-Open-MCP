// Router tests for the MCP resource URIs (P18.2).
//
// Each URI is exercised through `ResourceRouter.read`: health/summary runs the
// offline scan against a tmpdir project, health/baseline reads / missing /
// corrupt baseline files, bridge/status delegates to a mocked LiveClient and
// passes the body through, tool-groups returns the static catalog, and an
// unknown URI returns a no_data payload instead of throwing.
//
// Adapted from Unity Open MCP's `resource-router.test.ts` (copy for the test
// shape + the makeDeps / parseContent helpers). Intentional deltas: health/
// summary asserts an offline-scan payload (no bridge-cached summary in Godot),
// and bridge/status asserts the tool body passes through verbatim (no ping
// cache — each read probes).

import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { ReadResourceResult, CallToolResult } from "@modelcontextprotocol/sdk/types.js";

import {
  ResourceRouter,
  handleHealthSummary,
  handleHealthBaseline,
  handleBridgeStatus,
  handleToolGroups,
} from "./resource-router.js";
import type { ResourceHandlerDeps } from "./resource-router.js";
import type { LiveClient } from "../live-client.js";
import { buildBaseline, saveBaseline } from "../baseline/baseline-schema.js";

/** Parse the first text content block of a resource read as JSON. */
function parseContent(result: ReadResourceResult): Record<string, unknown> {
  const first = result.contents[0];
  if (!first || !("text" in first) || typeof first.text !== "string") {
    throw new Error("expected a text content part");
  }
  return JSON.parse(first.text) as Record<string, unknown>;
}

interface MockLive {
  routeBridgeStatusCalls: number;
  result: CallToolResult;
}

/** Build ResourceHandlerDeps with a mocked LiveClient that returns a fixed
 *  bridge_status result, plus optional project path / port overrides. */
function makeDeps(
  overrides: {
    projectPath?: string;
    port?: number;
    mockLive?: MockLive;
  } = {},
): { deps: ResourceHandlerDeps; mockLive: MockLive } {
  const mockLive: MockLive = overrides.mockLive ?? {
    routeBridgeStatusCalls: 0,
    result: {
      isError: false,
      content: [
        {
          type: "text",
          text: JSON.stringify({
            status: "running",
            ready: true,
            classification: "healthy",
            projectPath: overrides.projectPath ?? "/fake/project",
            _source: "local",
          }),
        },
      ],
    },
  };

  const live = {
    routeBridgeStatus: async (): Promise<CallToolResult> => {
      mockLive.routeBridgeStatusCalls++;
      return mockLive.result;
    },
  } as unknown as LiveClient;

  return {
    deps: {
      live,
      projectPath: overrides.projectPath ?? "/fake/project",
      port: overrides.port ?? 22028,
    },
    mockLive,
  };
}

// ---------------------------------------------------------------------------
// tool-groups (static — no I/O)
// ---------------------------------------------------------------------------

test("tool-groups returns the static catalog with default-enabled groups", () => {
  const result = handleToolGroups();
  const body = parseContent(result);
  assert.equal(body.status, "ok");
  assert.ok(Array.isArray(body.groups));
  assert.ok((body.groups as unknown[]).length > 0);
  // `core` is the only default-on group in Godot.
  assert.deepEqual(body.defaultEnabledGroups, ["core"]);
  for (const g of body.groups as Array<Record<string, unknown>>) {
    assert.equal(typeof g.id, "string");
    assert.equal(typeof g.description, "string");
    assert.equal(typeof g.defaultEnabled, "boolean");
    // Godot omits Unity's domainDefine / unityPackage fields.
    assert.ok(!("domainDefine" in g), "domainDefine must not leak into Godot catalog");
    assert.ok(!("unityPackage" in g), "unityPackage must not leak into Godot catalog");
  }
  assert.equal(result.contents[0] && result.contents[0].uri, "godot-open-mcp://tool-groups");
});

// ---------------------------------------------------------------------------
// health/summary — offline whole-project scan
// ---------------------------------------------------------------------------

test("health/summary returns ok + zero counts for a clean empty project", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "mcp-res-health-"));
  try {
    const { deps } = makeDeps({ projectPath: tmp });
    const result = await handleHealthSummary(deps);
    const body = parseContent(result);
    assert.equal(body.status, "ok");
    assert.equal(body.source, "offline_scan");
    assert.equal(body.scanner, "offline");
    assert.deepEqual(body.summary, { error: 0, warn: 0, info: 0 });
    assert.equal(body.scannedFileCount, 0);
    assert.ok(typeof body.asOf === "string");
    assert.ok(Array.isArray(body.categoriesRun));
    assert.ok(Array.isArray(body.ciExcludedRules));
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("health/summary counts a broken ext_resource as an error", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "mcp-res-health-"));
  try {
    await writeFile(
      join(tmp, "Broken.tscn"),
      [
        "[gd_scene load_steps=1 format=3]",
        '[ext_resource type="Resource" path="res://missing.tres" id="1_x"]',
        '[node name="Root" type="Node"]',
        "",
      ].join("\n"),
    );
    const { deps } = makeDeps({ projectPath: tmp });
    const result = await handleHealthSummary(deps);
    const body = parseContent(result);
    assert.equal(body.status, "ok");
    assert.equal((body.summary as Record<string, number>).error, 1);
    assert.equal((body.summary as Record<string, number>).warn, 0);
    assert.equal(body.scannedFileCount, 1);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// health/baseline — disk file
// ---------------------------------------------------------------------------

test("health/baseline returns no_baseline when the file is absent", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "mcp-res-base-"));
  try {
    const { deps } = makeDeps({ projectPath: tmp });
    const router = new ResourceRouter(deps);
    const result = await router.read("godot-open-mcp://health/baseline");
    const body = parseContent(result);
    assert.equal(body.status, "no_baseline");
    assert.equal(body.asOf, null);
    assert.ok(typeof body.baselinePath === "string");
    assert.ok((body.nextStep as string).includes("baseline_create"));
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("health/baseline returns ok when a valid baseline exists", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "mcp-res-base-"));
  try {
    await mkdir(join(tmp, "CI"), { recursive: true });
    const baseline = buildBaseline(
      [],
      ["broken_references"],
      ["project_health"],
      "desktop",
    );
    await saveBaseline(baseline, join(tmp, "CI", "godot-open-mcp-baseline.json"));

    const { deps } = makeDeps({ projectPath: tmp });
    const router = new ResourceRouter(deps);
    const result = await router.read("godot-open-mcp://health/baseline");
    const body = parseContent(result);
    assert.equal(body.status, "ok");
    assert.ok(body.asOf, "asOf should be populated from generatedAt");
    assert.equal(body.schemaVersion, 1);
    assert.equal(body.platformProfile, "desktop");
    assert.deepEqual(body.summary, { error: 0, warn: 0, info: 0 });
    assert.ok(Array.isArray(body.ciExcludedRules));
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("health/baseline returns invalid_baseline on corrupt JSON", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "mcp-res-base-"));
  try {
    await mkdir(join(tmp, "CI"), { recursive: true });
    await writeFile(
      join(tmp, "CI", "godot-open-mcp-baseline.json"),
      "not valid json {{{",
    );
    const { deps } = makeDeps({ projectPath: tmp });
    const result = await handleHealthBaseline(deps);
    const body = parseContent(result);
    assert.equal(body.status, "invalid_baseline");
    assert.ok(typeof body.message === "string");
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// bridge/status — delegates to LiveClient.routeBridgeStatus
// ---------------------------------------------------------------------------

test("bridge/status passes the tool body through verbatim", async () => {
  const { deps, mockLive } = makeDeps();
  const result = await handleBridgeStatus(deps);
  const body = parseContent(result);
  assert.equal(mockLive.routeBridgeStatusCalls, 1);
  assert.equal(body.status, "running");
  assert.equal(body.ready, true);
  assert.equal(body.classification, "healthy");
  // The tool's _source tag travels with the verbatim body.
  assert.equal(body._source, "local");
  assert.equal(result.contents[0] && result.contents[0].uri, "godot-open-mcp://bridge/status");
});

test("bridge/status surfaces a no_data snapshot when the tool returns isError", async () => {
  const mockLive: MockLive = {
    routeBridgeStatusCalls: 0,
    result: {
      isError: true,
      content: [{ type: "text", text: "programmer mistake" }],
    },
  };
  const { deps } = makeDeps({ mockLive, port: 22028 });
  const result = await handleBridgeStatus(deps);
  const body = parseContent(result);
  assert.equal(body.status, "no_data");
  assert.equal(body.connected, false);
});

// ---------------------------------------------------------------------------
// router dispatch + unknown URI
// ---------------------------------------------------------------------------

test("router dispatches each URI to the right handler", async () => {
  const tmp = await mkdtemp(join(tmpdir(), "mcp-res-dispatch-"));
  try {
    const { deps } = makeDeps({ projectPath: tmp });
    const router = new ResourceRouter(deps);
    for (const uri of [
      "godot-open-mcp://health/summary",
      "godot-open-mcp://health/baseline",
      "godot-open-mcp://bridge/status",
      "godot-open-mcp://tool-groups",
    ]) {
      const result = await router.read(uri);
      assert.ok(result.contents.length > 0, `${uri} returned no content`);
      const first = result.contents[0];
      assert.equal(first && first.uri, uri, `${uri} content uri mismatch`);
    }
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
});

test("unknown URI returns a no_data payload, not an exception", async () => {
  const { deps } = makeDeps();
  const router = new ResourceRouter(deps);
  const result = await router.read("godot-open-mcp://unknown/uri");
  const body = parseContent(result);
  assert.equal(body.status, "no_data");
  assert.ok((body.error as string).includes("Unknown resource URI"));
});
