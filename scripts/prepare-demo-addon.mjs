#!/usr/bin/env node
// prepare-demo-addon.mjs — materialize the Godot Open MCP addon into demo/.
//
// P9.4 demo integration fixture: a single canonical script that wraps the CLI
// installer so the demo's `demo/addons/godot_open_mcp/` tree is regenerated
// deterministically from `packages/bridge/` + `packages/verify/` (no stale
// vendored copy, no symlink that breaks on checkout). The CLI installer is the
// single source of truth — it stages into a temp sibling and swaps atomically,
// bundles the verify source, excludes Tests/obj/bin/.godot, and toggles the
// plugin into project.godot idempotently. See demo/AGENTS.md rule #3.
//
// Modes:
//   node scripts/prepare-demo-addon.mjs            # materialize (default)
//   node scripts/prepare-demo-addon.mjs --check    # materialize, then assert
//                                                  # tree shape, idempotent
//                                                  # re-run, and broken-fixture
//                                                  # patterns; exit 1 on drift
//
// The `--check` mode is the deterministic CI gate (the `demo-build` job). It
// proves that:
//   1. the materialized addon tree contains plugin.cfg + Editor/ + Runtime/ +
//      Verify/ (so the bridge's `using GodotOpenMcp.Verify.*` will resolve);
//   2. a second materialize is a no-op (`changed:false`) — so a clean tree is
//      byte-identical to a fresh install (no drift);
//   3. the broken .tscn / .tres / .import fixtures under demo/Fixtures/
//      exhibit the exact patterns the live verify rules flag —
//      `broken_scene_reference`, `missing_script`, `orphan_import`,
//      `duplicate_uid`, `project_broken_asset`, `project_empty_scene` — by
//      parsing the fixture text straight from disk.
//
// Exit codes: 0 = success / check passed, 1 = install failure / check drift,
// 2 = bootstrap error (CLI not built / demo missing).
//
// Requires Node 18+ (no runtime dependencies, only node: builtins). Requires
// the CLI to be built (`cli/dist/index.js`) — run `npm run build` in cli/
// first. The CI job does this before invoking the script.

import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, resolve, join } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const DEMO_DIR = join(REPO_ROOT, "demo");
const CLI_INDEX = join(REPO_ROOT, "cli", "dist", "index.js");
const ADDON_DIR = join(DEMO_DIR, "addons", "godot_open_mcp");

const args = new Set(process.argv.slice(2));
const CHECK = args.has("--check");

function log(msg) {
  process.stderr.write(`[prepare-demo-addon] ${msg}\n`);
}

function fail(msg, code = 1) {
  process.stderr.write(`[prepare-demo-addon] FAIL: ${msg}\n`);
  process.exit(code);
}

// ---------------------------------------------------------------------------
// Bootstrap
// ---------------------------------------------------------------------------

if (!existsSync(DEMO_DIR) || !existsSync(join(DEMO_DIR, "project.godot"))) {
  fail(
    `demo/ is not a Godot project (missing demo/project.godot). Run from the repository root.`,
    2,
  );
}
if (!existsSync(CLI_INDEX)) {
  fail(
    `CLI not built — ${CLI_INDEX} is missing. Run \`cd cli && npm ci && npm run build\` first.`,
    2,
  );
}

// ---------------------------------------------------------------------------
// 1. Materialize via the CLI installer (single canonical path).
// ---------------------------------------------------------------------------

function runCli(...cliArgs) {
  const result = spawnSync(process.execPath, [CLI_INDEX, ...cliArgs], {
    cwd: REPO_ROOT,
    encoding: "utf-8",
    env: { ...process.env },
  });
  if (result.error) {
    throw new Error(
      `Failed to spawn CLI: ${result.error.message}`,
    );
  }
  return result;
}

function installPluginOnce() {
  const result = runCli(
    "install-plugin",
    DEMO_DIR,
    "--source",
    join(REPO_ROOT, "packages", "bridge"),
    "--json",
  );
  // The CLI's exit code is the source of truth (0 success, 1 errors). stdout
  // carries the JSON envelope `{ command, changed, projectPath, addonDir,
  // pluginPath, enabledPlugins, source, warnings }` on success, and
  // `{ command, changed:false, error:{code,message}, warnings }` on failure.
  if (result.status !== 0) {
    log(`install-plugin stdout:\n${result.stdout}`);
    log(`install-plugin stderr:\n${result.stderr}`);
    let label = `exit ${result.status}`;
    try {
      const p = JSON.parse(result.stdout);
      if (p?.error?.code) label = `${p.error.code}: ${p.error.message ?? ""}`;
    } catch {}
    fail(`install-plugin failed (${label}).`);
  }
  let payload;
  try {
    payload = JSON.parse(result.stdout);
  } catch (err) {
    log(`install-plugin stdout:\n${result.stdout}`);
    fail(`install-plugin produced non-JSON output: ${err.message}`);
  }
  return payload;
}

log(`materializing addon from packages/bridge/ into ${ADDON_DIR} …`);
const first = installPluginOnce();
log(
  `first run: changed=${first.changed} source=${first.source?.kind ?? "?"} pluginEnabled=${first.enabledPlugins?.includes("res://addons/godot_open_mcp/plugin.cfg")}`,
);
if (first.warnings?.length) {
  for (const w of first.warnings) log(`  warning: ${w}`);
}

// ---------------------------------------------------------------------------
// 2. --check: tree shape + idempotent re-run + broken-fixture patterns.
// ---------------------------------------------------------------------------

if (CHECK) {
  // 2a. Tree shape.
  log("--check: asserting materialized tree shape …");
  const requiredFiles = [
    "plugin.cfg",
    join("Editor", "GodotOpenMcpPlugin.cs"),
    join("Editor", "Bridge", "BridgeHttpServer.cs"),
    join("Editor", "Gate", "GatePolicy.cs"),
    join("Runtime", "MainThread", "MainThreadDispatcher.cs"),
  ];
  for (const rel of requiredFiles) {
    if (!existsSync(join(ADDON_DIR, rel))) {
      fail(`materialized addon is missing ${rel} — verify bundling is intact.`);
    }
  }
  // Verify the bundled verify source landed under Verify/ — the bridge's
  // `using GodotOpenMcp.Verify.*` will not resolve without it.
  const verifyDir = join(ADDON_DIR, "Verify");
  if (!existsSync(verifyDir)) {
    fail(
      `materialized addon is missing Verify/ — the bridge addon will not compile (run the CLI installer with --source packages/bridge so the verify source is bundled).`,
    );
  }
  // Spot-check at least one rule file landed.
  const verifyRulesDir = join(verifyDir, "Rules");
  if (!existsSync(verifyRulesDir) || readdirSync(verifyRulesDir).length === 0) {
    fail(
      `materialized addon's Verify/Rules/ is empty — verify bundling copied the rule source.`,
    );
  }
  log("  ✔ tree shape ok (plugin.cfg + Editor/ + Runtime/ + Verify/ present)");

  // 2b. Idempotent re-run.
  log("--check: asserting second materialize is a no-op …");
  const second = installPluginOnce();
  if (second.changed !== false) {
    fail(
      `second materialize reported changed=${second.changed} (expected false). The install is not idempotent — staging/swap drift.`,
    );
  }
  log("  ✔ second run is a no-op (idempotent)");

  // 2c. Broken-fixture patterns. Parse the fixture text straight from disk and
  //     assert each fixture exhibits the exact pattern the live verify rule
  //     keys on. This is the offline, milliseconds-fast equivalent of
  //     validating each fixture's expected issue code.
  log("--check: asserting broken-fixture patterns …");
  checkBrokenReferenceFixture();
  checkMissingScriptFixture();
  checkOrphanImportFixture();
  checkDuplicateUidFixture();
  checkBrokenAssetFixture();
  checkEmptySceneFixture();
  log("  ✔ all broken fixtures exhibit expected patterns");

  log("PASS — materialization + fixture-shape check green");
}

process.exit(0);

// ---------------------------------------------------------------------------
// Fixture-shape checks (inline — kept small and intentional).
// ---------------------------------------------------------------------------

function checkBrokenReferenceFixture() {
  const path = join(DEMO_DIR, "Fixtures", "BrokenReference", "BrokenReference.tscn");
  const text = readOrDie(path);
  // Expect a real `[ext_resource type="Script" ...]` HEADER (a line that
  // starts with `[ext_resource`) whose path points at a file that does NOT
  // exist under demo/, and a node attaching it. Comment lines that mention
  // `[ext_resource ...]` are ignored by anchoring on line-start.
  const ext = text.match(/^[ \t]*\[ext_resource[^\]]*\btype="Script"[^\]]*\]/m);
  if (!ext) fail(`broken-reference fixture ${path} is missing a Script ext_resource header.`);
  const pathMatch = ext[0].match(/\bpath="(res:\/\/[^"]+)"/);
  if (!pathMatch) fail(`broken-reference fixture ${path} is missing a path= attribute.`);
  const resPath = pathMatch[1];
  const fsPath = join(DEMO_DIR, resPath.replace(/^res:\/\//, ""));
  if (existsSync(fsPath)) {
    fail(
      `broken-reference fixture's script path ${resPath} resolves to an existing file — the fixture is no longer "broken".`,
    );
  }
  if (!/script\s*=\s*ExtResource\(/.test(text)) {
    fail(`broken-reference fixture ${path} has no \`script = ExtResource(...)\` attachment.`);
  }
}

function checkMissingScriptFixture() {
  const path = join(DEMO_DIR, "Fixtures", "MissingScript", "MissingScript.tscn");
  const text = readOrDie(path);
  const ext = text.match(/^[ \t]*\[ext_resource[^\]]*\btype="Script"[^\]]*\]/m);
  if (!ext) fail(`missing-script fixture ${path} is missing a Script ext_resource header.`);
  const pathMatch = ext[0].match(/\bpath="(res:\/\/[^"]+)"/);
  if (!pathMatch) fail(`missing-script fixture ${path} is missing a path= attribute.`);
  const resPath = pathMatch[1];
  const fsPath = join(DEMO_DIR, resPath.replace(/^res:\/\//, ""));
  if (existsSync(fsPath)) {
    fail(
      `missing-script fixture's script path ${resPath} resolves to an existing file — the fixture is no longer "missing".`,
    );
  }
  if (!/script\s*=\s*ExtResource\(/.test(text)) {
    fail(`missing-script fixture ${path} has no \`script = ExtResource(...)\` attachment.`);
  }
}

function checkOrphanImportFixture() {
  const path = join(
    DEMO_DIR,
    "Fixtures",
    "ImportHealth",
    "OrphanTexture.png.import",
  );
  const text = readOrDie(path);
  const sourceMatch = text.match(/^source\s*=\s*"(res:\/\/[^"]+)"/m);
  if (!sourceMatch) {
    fail(`orphan-import fixture ${path} is missing a \`source=\` line.`);
  }
  const fsPath = join(DEMO_DIR, sourceMatch[1].replace(/^res:\/\//, ""));
  if (existsSync(fsPath)) {
    fail(
      `orphan-import fixture's source ${sourceMatch[1]} resolves to an existing file — the sidecar is no longer orphan.`,
    );
  }
}

function checkDuplicateUidFixture() {
  const dir = join(DEMO_DIR, "Fixtures", "ImportHealth");
  // Collect every uid= across every committed .import sidecar in this dir.
  const sidecars = readdirSync(dir)
    .filter((n) => n.endsWith(".import"))
    .map((n) => join(dir, n));
  if (sidecars.length < 2) {
    fail(
      `duplicate-uid fixture directory ${dir} has fewer than 2 .import sidecars — cannot demonstrate a collision.`,
    );
  }
  const uidsBySidecar = new Map();
  for (const s of sidecars) {
    const text = readOrDie(s);
    const m = text.match(/\buid="(uid:\/\/[^"]+)"/);
    if (m) uidsBySidecar.set(s, m[1]);
  }
  // Assert there exist at least two sidecars sharing the same uid.
  const byUid = new Map();
  for (const [s, u] of uidsBySidecar) {
    if (!byUid.has(u)) byUid.set(u, []);
    byUid.get(u).push(s);
  }
  let collision = null;
  for (const [u, ss] of byUid) {
    if (ss.length >= 2) {
      collision = { uid: u, sidecars: ss };
      break;
    }
  }
  if (!collision) {
    fail(
      `duplicate-uid fixture directory ${dir} has no shared uid= across its .import sidecars — the collision condition is gone.`,
    );
  }
  // The two colliding sidecars must each point at an EXISTING source so the
  // duplicate-uid finding isn't masked by orphan findings on the same files.
  for (const s of collision.sidecars) {
    const text = readOrDie(s);
    const sourceMatch = text.match(/^source\s*=\s*"(res:\/\/[^"]+)"/m);
    if (!sourceMatch) {
      fail(
        `duplicate-uid sidecar ${s} has no \`source=\` line — the duplicate-uid fixture must isolate the collision from orphan findings.`,
      );
    }
    const fsPath = join(DEMO_DIR, sourceMatch[1].replace(/^res:\/\//, ""));
    if (!existsSync(fsPath)) {
      fail(
        `duplicate-uid sidecar ${s} points at missing source ${sourceMatch[1]} — the fixture would also flag orphan_import, masking the duplicate_uid finding.`,
      );
    }
  }
}

function readOrDie(p) {
  if (!existsSync(p)) fail(`required fixture file missing: ${p}`);
  if (statSync(p).size > 256 * 1024) {
    fail(`fixture file ${p} is unexpectedly large (>256 KiB) — aborted read.`);
  }
  return readFileSync(p, "utf-8");
}

function checkBrokenAssetFixture() {
  // The broken-asset fixture is a .tscn that opens with a valid [gd_scene]
  // header but has NO [node] declaration — the project_health rule flags this
  // as project_broken_asset (a scene with no root node is structurally broken).
  const path = join(DEMO_DIR, "Fixtures", "ProjectHealth", "BrokenAsset.tscn");
  const text = readOrDie(path);
  const firstReal = text
    .split(/\r?\n/)
    .map((l) => l.trim())
    .find((l) => l.length > 0 && !l.startsWith(";"));
  if (!firstReal || !firstReal.startsWith("[gd_scene")) {
    fail(
      `broken-asset fixture ${path} must open with a [gd_scene] header (the parse must reach the node-count check, not fail on the header).`,
    );
  }
  // Count [node headers at column 0 — the same convention SceneRefParser and
  // ProjectAssetParser use.
  const nodeCount = text
    .split(/\r?\n/)
    .filter((l) => /^\[node/.test(l)).length;
  if (nodeCount !== 0) {
    fail(
      `broken-asset fixture ${path} must have zero [node] declarations (found ${nodeCount}) — the rule flags a nodeless scene as project_broken_asset.`,
    );
  }
}

function checkEmptySceneFixture() {
  // The empty-scene fixture is a .tscn that parses cleanly but has exactly one
  // [node] (the root) and no children — the project_health rule flags this as
  // project_empty_scene.
  const path = join(DEMO_DIR, "Fixtures", "ProjectHealth", "EmptyScene.tscn");
  const text = readOrDie(path);
  const firstReal = text
    .split(/\r?\n/)
    .map((l) => l.trim())
    .find((l) => l.length > 0 && !l.startsWith(";"));
  if (!firstReal || !firstReal.startsWith("[gd_scene")) {
    fail(
      `empty-scene fixture ${path} must open with a [gd_scene] header (it must parse as a valid scene, just an empty one).`,
    );
  }
  const nodeCount = text
    .split(/\r?\n/)
    .filter((l) => /^\[node/.test(l)).length;
  if (nodeCount !== 1) {
    fail(
      `empty-scene fixture ${path} must have exactly one [node] (the root, no children); found ${nodeCount}.`,
    );
  }
}
