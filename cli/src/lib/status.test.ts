// Tests for the pure status derivation (src/lib/status.ts).
//
// The full `assembleStatus` is I/O-bound (lock read + ping) and is exercised
// end-to-end by the dispatcher tests in cli.test.ts (no bridge → stopped). This
// file pins the pure mapper (`deriveStatus`) so it cannot drift from the MCP
// `deriveBridgeStatus` source of truth, and covers the addon-presence check.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";

import {
  deriveStatus,
  inspectAddon,
  type DeriveStatusInput,
  type BridgeStatus,
} from "./status.js";

// ---------------------------------------------------------------------------
// deriveStatus — mirrors mcp-server deriveBridgeStatus
// ---------------------------------------------------------------------------

/**
 * Drive `deriveStatus` across the full status matrix and assert it matches the
 * MCP source-of-truth mapping table (see the status.ts header). Every row of
 * the table is exercised so the two cannot drift silently.
 */
function derive(
  overrides: Partial<DeriveStatusInput> = {},
): BridgeStatus {
  return deriveStatus({
    classification: "healthy",
    pingReachable: false,
    pingCompiling: false,
    pingConnected: false,
    lockPidAlive: false,
    ...overrides,
  });
}

test("deriveStatus: dead_bridge wins outright regardless of ping", () => {
  // Even a coincidentally-reachable ping must not mask a dead_bridge (stale
  // heartbeat → listener will not recover).
  assert.equal(
    derive({ classification: "dead_bridge", pingReachable: true, pingConnected: true, lockPidAlive: true }),
    "dead_bridge",
  );
  assert.equal(
    derive({ classification: "dead_bridge", pingReachable: false, lockPidAlive: false }),
    "dead_bridge",
  );
});

test("deriveStatus: reachable + compiling → compiling", () => {
  assert.equal(
    derive({ classification: "healthy", pingReachable: true, pingCompiling: true }),
    "compiling",
  );
});

test("deriveStatus: reachable + connected → running", () => {
  assert.equal(
    derive({ classification: "healthy", pingReachable: true, pingConnected: true }),
    "running",
  );
});

test("deriveStatus: not reachable + lock pid alive → unreachable", () => {
  assert.equal(
    derive({ classification: "healthy", pingReachable: false, lockPidAlive: true }),
    "unreachable",
  );
});

test("deriveStatus: not reachable + no live pid → stopped", () => {
  assert.equal(
    derive({ classification: "gone", pingReachable: false, lockPidAlive: false }),
    "stopped",
  );
});

test("deriveStatus: reachable but neither compiling nor connected falls to stopped", () => {
  // reachable + connected:false + compiling:false — the residual case. Matches
  // the MCP mapper: only reachable+compiling → compiling and reachable+connected
  // → running are reachable-pinned branches; everything else with no live pid
  // lands on stopped.
  assert.equal(
    derive({ classification: "healthy", pingReachable: true, pingConnected: false, pingCompiling: false, lockPidAlive: false }),
    "stopped",
  );
});

test("deriveStatus: reloading classification does not override a reachable+connected ping", () => {
  // reloading (editor mid-reload) is not dead_bridge; a healthy ping still wins.
  assert.equal(
    derive({ classification: "reloading", pingReachable: true, pingConnected: true }),
    "running",
  );
});

// ---------------------------------------------------------------------------
// inspectAddon — addon presence + plugin-enabled check
// ---------------------------------------------------------------------------

function tempProject(): { dir: string; cleanup: () => void } {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "godot-open-mcp-status-"));
  return { dir, cleanup: () => fs.rmSync(dir, { recursive: true, force: true }) };
}

test("inspectAddon: missing addon reports present:false enabled:false", () => {
  const fx = tempProject();
  try {
    const a = inspectAddon(fx.dir);
    assert.equal(a.present, false);
    assert.equal(a.enabled, false);
    assert.equal(a.pluginPath, "res://addons/godot_open_mcp/plugin.cfg");
  } finally {
    fx.cleanup();
  }
});

test("inspectAddon: addon present but not enabled in project.godot", () => {
  const fx = tempProject();
  try {
    fs.mkdirSync(path.join(fx.dir, "addons", "godot_open_mcp"), { recursive: true });
    fs.writeFileSync(path.join(fx.dir, "addons", "godot_open_mcp", "plugin.cfg"), "[plugin]\n");
    fs.writeFileSync(path.join(fx.dir, "project.godot"), "[application]\n\nname=\"T\"\n");
    const a = inspectAddon(fx.dir);
    assert.equal(a.present, true);
    assert.equal(a.enabled, false);
  } finally {
    fx.cleanup();
  }
});

test("inspectAddon: addon present and enabled", () => {
  const fx = tempProject();
  try {
    fs.mkdirSync(path.join(fx.dir, "addons", "godot_open_mcp"), { recursive: true });
    fs.writeFileSync(path.join(fx.dir, "addons", "godot_open_mcp", "plugin.cfg"), "[plugin]\n");
    fs.writeFileSync(
      path.join(fx.dir, "project.godot"),
      '[application]\n\nname="T"\n\n[editor_plugins]\n\nenabled=PackedStringArray("res://addons/godot_open_mcp/plugin.cfg")\n',
    );
    const a = inspectAddon(fx.dir);
    assert.equal(a.present, true);
    assert.equal(a.enabled, true);
  } finally {
    fx.cleanup();
  }
});
