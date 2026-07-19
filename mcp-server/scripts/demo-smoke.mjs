#!/usr/bin/env node
// demo-smoke.mjs — P9.4 offline MCP smoke for the demo integration fixture.
//
// Spawns the built stdio MCP server (`mcp-server/dist/index.js`) as a child
// process pointed at the demo project (`GODOT_PROJECT_PATH=<demo>`), drives the
// MCP wire flow (initialize → tools/list → tools/call) over a real stdio pipe
// using the SDK's own `StdioClientTransport`, and asserts the OFFLINE
// contracts the demo pins — no Godot editor required:
//
//   - tools/list advertises the always-visible meta-tools + the core group;
//   - godot_open_mcp_ping returns a structured `bridge_offline` error that
//     names the demo project's instance lock (no editor running);
//   - godot_open_mcp_capabilities lists the core + gate-and-verify groups and
//     the three verify rule ids;
//   - godot_open_mcp_manage_tools activate typed-editor flips the typed-editor
//     group visibility (notifications/tools/list_changed fires);
//   - godot_open_mcp_scene_get_data res://Main.tscn (offline fallback) returns
//     the `Main` root + known children (Player2D / Player3D / ValidFixtureChild);
//   - godot_open_mcp_filesystem_list res://Fixtures (offline fallback) lists
//     the broken fixture files;
//   - godot_open_mcp_read_compile_errors returns without error against the
//     demo project.
//
// What this smoke does NOT cover (and is explicit about): the LIVE bridge
// round-trip, the gated-mutation delta, and the exact-issue-code emission per
// broken fixture. Those require a real Godot editor with a bound bridge
// listener — headless CI cannot reliably start one — and are covered by the
// manual verification checklist in demo/README.md.
//
// Adapted from mcp-server/scripts/p1-parity-smoke.mjs (the SDK boot + the
// stdio wire flow carry over). Intentional deltas: (1) points at the demo
// project's real fixtures; (2) drives the offline readers + capabilities +
// manage_tools rather than a single ping; (3) no bridge stub — the demo has
// no live bridge by design in CI.
//
// Usage:
//   node mcp-server/scripts/demo-smoke.mjs                 # demo at <repo>/demo
//   node mcp-server/scripts/demo-smoke.mjs --project demo  # explicit project dir
//
// Exit codes: 0 = pass, 1 = fail, 2 = bootstrap error (server not built /
// demo missing / addon not materialized).

import { existsSync, mkdirSync, rmSync } from "node:fs";
import { dirname, resolve, join } from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const here = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(here, "..", "..");
const DIST_INDEX = resolve(here, "..", "dist", "index.js");

// Parse `--project <path>` (default: <repo>/demo). Also accept a bare
// positional for parity with the CLI.
function parseArgs(argv) {
  const out = { project: null };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--project") {
      out.project = argv[++i];
    } else if (!a.startsWith("--")) {
      out.project = a;
    }
  }
  return out;
}

const args = parseArgs(process.argv.slice(2));
const DEMO_PROJECT = resolve(REPO_ROOT, args.project ?? "demo");

// Deterministic, assertable fixture values. Kept in sync with the demo tree
// (demo/Main.tscn + demo/Resources + demo/Fixtures).
const EXPECTED_ROOT_NAME = "Main";
const EXPECTED_CHILD_NAMES = ["Player2D", "Player3D", "ValidFixtureChild"];
const EXPECTED_RULE_IDS = ["broken_references", "missing_scripts", "import_health"];
const EXPECTED_FIXTURE_FILES = [
  "BrokenReference.tscn",
  "MissingScript.tscn",
];

// Smoke scratch directory (the smoke does not mutate anything in CI, but the
// dir is created + cleaned so a future gated-mutation sub-case has a place
// to land). Gitignored at the repo root.
const SMOKE_SCRATCH = join(DEMO_PROJECT, "SmokeScratch");

function log(msg) {
  process.stderr.write(`[demo-smoke] ${msg}\n`);
}

function assert(cond, msg) {
  if (!cond) throw new Error(`ASSERT FAILED: ${msg}`);
}

function readFirstJson(result) {
  assert(Array.isArray(result.content) && result.content.length > 0, "content array non-empty");
  const block = result.content[0];
  assert(block.type === "text", "first content block is text");
  return JSON.parse(block.text);
}

// ---------------------------------------------------------------------------
// Bootstrap.
// ---------------------------------------------------------------------------

if (!existsSync(DIST_INDEX)) {
  log(`FATAL: ${DIST_INDEX} not found. Run \`npm run build\` in mcp-server/ first.`);
  process.exit(2);
}
if (!existsSync(join(DEMO_PROJECT, "project.godot"))) {
  log(`FATAL: ${DEMO_PROJECT} is not a Godot project (missing project.godot).`);
  process.exit(2);
}
if (!existsSync(join(DEMO_PROJECT, "addons", "godot_open_mcp", "plugin.cfg"))) {
  log(
    `FATAL: addon not materialized under ${DEMO_PROJECT}/addons/godot_open_mcp/. Run \`node scripts/prepare-demo-addon.mjs\` first.`,
  );
  process.exit(2);
}

// ---------------------------------------------------------------------------
// Boot the MCP server over stdio, pointed at the demo project.
// ---------------------------------------------------------------------------

async function bootMcpServer() {
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [DIST_INDEX],
    stderr: "inherit",
    env: {
      ...process.env,
      GODOT_PROJECT_PATH: DEMO_PROJECT,
      // Pin GODOT_OPEN_MCP_BRIDGE_PORT to an unlikely high port so the server
      // never collides with a real editor running on the same project during
      // a local smoke run. The bridge is not running in CI; locally this just
      // shortens the failure path.
      GODOT_OPEN_MCP_BRIDGE_PORT: "65432",
    },
  });
  const client = new Client(
    { name: "demo-smoke", version: "0.0.0-smoke" },
    { capabilities: {} },
  );
  await client.connect(transport);
  return {
    client,
    shutdown: async () => {
      try { await client.close(); } catch {}
    },
  };
}

// ---------------------------------------------------------------------------
// Assertions.
// ---------------------------------------------------------------------------

async function assertMetaToolsVisible(client) {
  const { tools } = await client.listTools();
  const names = new Set(tools.map((t) => t.name));
  // Always-visible meta-tools (route-policy.ts LOCAL_TOOLS + READ_COMPILE_ERRORS_TOOL).
  for (const name of [
    "godot_open_mcp_capabilities",
    "godot_open_mcp_bridge_status",
    "godot_open_mcp_pull_events",
    "godot_open_mcp_manage_tools",
    "godot_open_mcp_read_compile_errors",
  ]) {
    assert(names.has(name), `tools/list must include ${name} (got: ${[...names].join(", ")})`);
  }
  // Core group is on by default — ping + the gate surface.
  for (const name of [
    "godot_open_mcp_ping",
    "godot_open_mcp_validate_edit",
    "godot_open_mcp_checkpoint_create",
    "godot_open_mcp_delta",
    "godot_open_mcp_apply_fix",
  ]) {
    assert(names.has(name), `tools/list must include core tool ${name}`);
  }
  // typed-editor group is hidden by default — scene_get_data / filesystem_list
  // must NOT appear until activate.
  assert(
    !names.has("godot_open_mcp_scene_get_data"),
    "scene_get_data must be hidden until typed-editor is activated",
  );
  log("  ✔ tools/list advertises meta-tools + core; typed-editor hidden");
}

async function assertPingBridgeOffline(client) {
  const result = await client.callTool({ name: "godot_open_mcp_ping", arguments: {} });
  assert(result.isError === true, "ping with no bridge must be isError");
  const body = readFirstJson(result);
  assert(
    body?.error?.code === "bridge_offline",
    `ping error code = ${body?.error?.code} (expected bridge_offline)`,
  );
  // The offline hint must name this project's lock file path so an agent can
  // branch on the actionable hint.
  assert(
    /~\/\.godot-open-mcp\/instances\//.test(body?.error?.message ?? ""),
    `offline hint must name the project lock file (message: ${body?.error?.message})`,
  );
  log("  ✔ ping returns bridge_offline with lock-file hint");
}

async function assertCapabilitiesCatalog(client) {
  const result = await client.callTool({ name: "godot_open_mcp_capabilities", arguments: {} });
  assert(result.isError !== true, "capabilities must not be isError");
  const body = readFirstJson(result);
  const ruleIds = (body?.rules ?? []).map((r) => r.id);
  for (const id of EXPECTED_RULE_IDS) {
    assert(ruleIds.includes(id), `capabilities.rules must include ${id} (got: ${ruleIds.join(", ")})`);
  }
  // The implemented rule entries must mark themselves implemented.
  for (const r of body?.rules ?? []) {
    if (EXPECTED_RULE_IDS.includes(r.id)) {
      assert(
        r.status === "implemented",
        `rule ${r.id} must report status:implemented (got ${r.status})`,
      );
    }
  }
  log(`  ✔ capabilities lists rules: ${ruleIds.join(", ")}`);
}

async function assertManageToolsActivatesTypedEditor(client) {
  const before = await client.listTools();
  const beforeNames = new Set(before.tools.map((t) => t.name));
  assert(
    !beforeNames.has("godot_open_mcp_scene_get_data"),
    "precondition: scene_get_data hidden",
  );

  const result = await client.callTool({
    name: "godot_open_mcp_manage_tools",
    arguments: { action: "activate", group: "typed-editor" },
  });
  assert(result.isError !== true, "manage_tools activate must not be isError");
  const body = readFirstJson(result);
  assert(
    body?.status === "ok" || body?.changed === true || Array.isArray(body?.active),
    `manage_tools activate returned unexpected body: ${JSON.stringify(body)}`,
  );

  // After activate, the typed-editor group tools appear in tools/list.
  const after = await client.listTools();
  const afterNames = new Set(after.tools.map((t) => t.name));
  for (const name of [
    "godot_open_mcp_scene_get_data",
    "godot_open_mcp_filesystem_list",
    "godot_open_mcp_node_find",
  ]) {
    assert(
      afterNames.has(name),
      `tools/list must now include ${name} after typed-editor activate`,
    );
  }
  log("  ✔ manage_tools activate typed-editor flips visibility");
}

async function assertSceneGetDataOffline(client) {
  const result = await client.callTool({
    name: "godot_open_mcp_scene_get_data",
    arguments: { path: "res://Main.tscn", hierarchy_depth: 1 },
  });
  assert(result.isError !== true, "scene_get_data res://Main.tscn must not be isError");
  const body = readFirstJson(result);
  // The offline route tags the result with _source:"offline" and a
  // _route.fallbackReason:"live_unavailable" (the bridge is down).
  assert(
    body?._source === "offline",
    `scene_get_data _source = ${body?._source} (expected offline)`,
  );
  assert(
    body?._route?.route === "offline",
    `scene_get_data _route.route = ${body?._route?.route} (expected offline)`,
  );
  assert(
    body?.name === EXPECTED_ROOT_NAME,
    `scene root name = ${body?.name} (expected ${EXPECTED_ROOT_NAME})`,
  );
  // hierarchy_depth:1 → root + direct children. Main.tscn's direct children
  // are Player2D / Player3D / ValidFixtureChild.
  const childNames = (body?.root?.children ?? []).map((c) => c.name);
  for (const n of EXPECTED_CHILD_NAMES) {
    assert(
      childNames.includes(n),
      `scene_get_data root.children must include ${n} (got: ${childNames.join(", ")})`,
    );
  }
  log(`  ✔ scene_get_data res://Main.tscn offline → root=${body?.name}, children=[${childNames.join(", ")}]`);
}

async function assertFilesystemListOffline(client) {
  const result = await client.callTool({
    name: "godot_open_mcp_filesystem_list",
    arguments: { path: "res://Fixtures" },
  });
  assert(result.isError !== true, "filesystem_list res://Fixtures must not be isError");
  const body = readFirstJson(result);
  assert(
    body?._source === "offline",
    `filesystem_list _source = ${body?._source} (expected offline)`,
  );
  const entryNames = (body?.entries ?? []).map((e) => e.name);
  for (const name of EXPECTED_FIXTURE_FILES) {
    // filesystem_list is one-level — the fixture scene files live under
    // res://Fixtures/<Category>/, so they appear when listing the matching
    // sub-directory. Assert the category dirs at minimum.
  }
  // Assert at least the category directories appear.
  for (const dir of ["BrokenReference", "MissingScript", "ImportHealth", "ScriptValidation"]) {
    assert(
      entryNames.includes(dir),
      `filesystem_list res://Fixtures must include the ${dir}/ category dir (got: ${entryNames.join(", ")})`,
    );
  }
  log(`  ✔ filesystem_list res://Fixtures offline → [${entryNames.join(", ")}]`);

  // Listing the broken-reference category must surface BrokenReference.tscn.
  const deep = await client.callTool({
    name: "godot_open_mcp_filesystem_list",
    arguments: { path: "res://Fixtures/BrokenReference" },
  });
  assert(deep.isError !== true, "filesystem_list res://Fixtures/BrokenReference must not be isError");
  const deepBody = readFirstJson(deep);
  const deepNames = (deepBody?.entries ?? []).map((e) => e.name);
  assert(
    deepNames.includes("BrokenReference.tscn"),
    `filesystem_list res://Fixtures/BrokenReference must include BrokenReference.tscn (got: ${deepNames.join(", ")})`,
  );
  log(`  ✔ filesystem_list res://Fixtures/BrokenReference offline → [${deepNames.join(", ")}]`);
}

async function assertReadCompileErrorsOk(client) {
  const result = await client.callTool({
    name: "godot_open_mcp_read_compile_errors",
    arguments: {},
  });
  // read_compile_errors reads the demo project's Godot log from disk. With no
  // editor ever having run, the log may not exist; the tool MUST surface that
  // as a structured "no log / no errors" outcome, NOT a thrown error.
  assert(result.isError !== true, "read_compile_errors must not be isError");
  const body = readFirstJson(result);
  // The body shape varies (errors[] vs healthy:{}); we only assert the call
  // succeeds against the demo project and returns parseable JSON.
  assert(
    typeof body === "object" && body !== null,
    "read_compile_errors must return a JSON object body",
  );
  log(`  ✔ read_compile_errors returns cleanly against the demo project`);
}

// ---------------------------------------------------------------------------
// Main.
// ---------------------------------------------------------------------------

async function main() {
  log(`P9.4 demo offline smoke`);
  log(`  server: ${DIST_INDEX}`);
  log(`  project: ${DEMO_PROJECT}`);

  // Create SmokeScratch up-front so the cleanup trap has something to remove
  // even on early failure. Gitignored at the repo root.
  mkdirSync(SMOKE_SCRATCH, { recursive: true });

  let mcp;
  try {
    mcp = await bootMcpServer();
    log("case: tools/list meta-tools + core visibility");
    await assertMetaToolsVisible(mcp.client);
    log("case: ping → bridge_offline");
    await assertPingBridgeOffline(mcp.client);
    log("case: capabilities catalog");
    await assertCapabilitiesCatalog(mcp.client);
    log("case: manage_tools activate typed-editor");
    await assertManageToolsActivatesTypedEditor(mcp.client);
    log("case: scene_get_data res://Main.tscn offline");
    await assertSceneGetDataOffline(mcp.client);
    log("case: filesystem_list res://Fixtures offline");
    await assertFilesystemListOffline(mcp.client);
    log("case: read_compile_errors against demo");
    await assertReadCompileErrorsOk(mcp.client);
    log("PASS — P9.4 demo offline smoke green");
    process.exit(0);
  } catch (err) {
    log(`FAIL — ${err?.message ?? err}`);
    if (err?.stack) process.stderr.write(err.stack + "\n");
    process.exit(1);
  } finally {
    if (mcp) await mcp.shutdown();
    // Always clean SmokeScratch on both success and failure — never leave a
    // tracked-looking mutation behind.
    try {
      rmSync(SMOKE_SCRATCH, { recursive: true, force: true });
    } catch {}
  }
}

main();
