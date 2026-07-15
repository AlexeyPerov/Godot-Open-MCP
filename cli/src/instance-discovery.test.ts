// Tests for the CLI's per-project instance discovery (src/instance-discovery.ts).
//
// This module is a byte-for-byte copy of mcp-server/src/instance-discovery.ts;
// the tests here are the CLI-side parity guard for the port-formula + lock +
// dead-bridge classifier. They MUST agree with the bridge C#
// (InstancePortResolver.cs) and the mcp-server copy so the CLI probes the same
// port the bridge listens on.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

import {
  computePort,
  normalizePath,
  projectHash,
  classifyInstance,
  heartbeatAgeMs,
  isPidAlive,
  resolvePort,
  resolveAuthToken,
  lockPath,
  readInstanceLock,
  PORT_OVERRIDE_ENV_VAR,
  HEARTBEAT_STALE_MS,
  type InstanceLock,
} from "./instance-discovery.js";

// ---------------------------------------------------------------------------
// path normalization + hashing
// ---------------------------------------------------------------------------

test("normalizePath: trims trailing slashes (not the root)", () => {
  assert.equal(normalizePath("/a/b/"), "/a/b");
  assert.equal(normalizePath("/a/b///"), "/a/b");
  assert.equal(normalizePath("/"), "/");
  assert.equal(normalizePath(""), "");
});

test("normalizePath: converts backslashes to forward slashes", () => {
  assert.equal(normalizePath("C:\\Users\\me\\proj"), "C:/Users/me/proj");
});

test("projectHash: matches createHash(normalizePath) for a sample path", () => {
  const projectPath = "/Users/me/MyGame";
  const norm = normalizePath(projectPath);
  const expected = createHash("sha256").update(norm, "utf8").digest("hex");
  assert.equal(projectHash(projectPath), expected);
});

test("projectHash: backslash variant of a path hashes the same as forward-slash", () => {
  // Windows and macOS/Linux resolve the same logical project to the same hash.
  assert.equal(
    projectHash("C:\\Users\\me\\proj"),
    projectHash("C:/Users/me/proj"),
  );
});

// ---------------------------------------------------------------------------
// deterministic port
// ---------------------------------------------------------------------------

test("computePort: lands inside the deterministic range [20000, 29999]", () => {
  const port = computePort("/Users/me/MyGame");
  assert.ok(port >= 20000 && port <= 29999, `port=${port} out of range`);
  assert.ok(Number.isInteger(port));
});

test("computePort: backslash and forward-slash variants of a path resolve the same port", () => {
  assert.equal(
    computePort("C:\\Users\\me\\proj"),
    computePort("C:/Users/me/proj"),
  );
});

test("computePort: stable for the same input across calls", () => {
  assert.equal(computePort("/x"), computePort("/x"));
});

// ---------------------------------------------------------------------------
// PID liveness
// ---------------------------------------------------------------------------

test("isPidAlive: current process is alive", () => {
  assert.equal(isPidAlive(process.pid), true);
});

test("isPidAlive: pid 0 and negative are dead", () => {
  assert.equal(isPidAlive(0), false);
  assert.equal(isPidAlive(-1), false);
});

test("isPidAlive: a very high pid is dead (ESRCH)", () => {
  // 2_000_000 is safely above any real PID.
  assert.equal(isPidAlive(2_000_000), false);
});

// ---------------------------------------------------------------------------
// resolvePort precedence
// ---------------------------------------------------------------------------

test("resolvePort: explicit env port wins", () => {
  assert.equal(resolvePort("/any/project", 23456), 23456);
});

test("resolvePort: invalid env port falls through to lock/hash", () => {
  // Non-integer / out-of-range envPort is ignored → deterministic hash.
  const expected = computePort("/any/project");
  assert.equal(resolvePort("/any/project", 1.5), expected);
  assert.equal(resolvePort("/any/project", 0), expected);
  assert.equal(resolvePort("/any/project", 70_000), expected);
});

test("resolvePort: no env + no lock → deterministic hash", () => {
  const projectPath = "/no/such/project/for/resolvePort";
  assert.equal(resolvePort(projectPath), computePort(projectPath));
});

test("resolvePort: live lock port wins over hash when envPort absent", () => {
  // Plant a lock with a live PID (this process) at a synthetic port.
  const projectPath = "/fake/project/for/resolvePort-live-lock";
  const dir = path.dirname(lockPath(projectPath));
  fs.mkdirSync(dir, { recursive: true });
  const lockFile = lockPath(projectPath);
  const existedBefore = fs.existsSync(lockFile);
  const fakePort = 29876;
  const lock: InstanceLock = makeLock(projectPath, { port: fakePort, pid: process.pid });
  fs.writeFileSync(lockFile, JSON.stringify(lock));
  try {
    assert.equal(resolvePort(projectPath), fakePort);
  } finally {
    if (!existedBefore) fs.rmSync(lockFile, { force: true });
  }
});

test("resolvePort: stale lock (dead pid) falls through to hash", () => {
  const projectPath = "/fake/project/for/resolvePort-stale-lock";
  const dir = path.dirname(lockPath(projectPath));
  fs.mkdirSync(dir, { recursive: true });
  const lockFile = lockPath(projectPath);
  const existedBefore = fs.existsSync(lockFile);
  // pid 2_000_000 is guaranteed dead.
  const lock: InstanceLock = makeLock(projectPath, { port: 29876, pid: 2_000_000 });
  fs.writeFileSync(lockFile, JSON.stringify(lock));
  try {
    assert.equal(resolvePort(projectPath), computePort(projectPath));
  } finally {
    if (!existedBefore) fs.rmSync(lockFile, { force: true });
  }
});

// ---------------------------------------------------------------------------
// resolveAuthToken
// ---------------------------------------------------------------------------

test("resolveAuthToken: undefined when envPort is set", () => {
  assert.equal(resolveAuthToken("/any", 23456), undefined);
});

test("resolveAuthToken: undefined when no lock exists", () => {
  assert.equal(resolveAuthToken("/no/such/project/for/authToken"), undefined);
});

test("resolveAuthToken: returns token from a live lock", () => {
  const projectPath = "/fake/project/for/authToken-live";
  const dir = path.dirname(lockPath(projectPath));
  fs.mkdirSync(dir, { recursive: true });
  const lockFile = lockPath(projectPath);
  const existedBefore = fs.existsSync(lockFile);
  const lock: InstanceLock = makeLock(projectPath, {
    pid: process.pid,
    authToken: "secret-token",
  });
  fs.writeFileSync(lockFile, JSON.stringify(lock));
  try {
    assert.equal(resolveAuthToken(projectPath), "secret-token");
  } finally {
    if (!existedBefore) fs.rmSync(lockFile, { force: true });
  }
});

test("resolveAuthToken: undefined when lock has no authToken field", () => {
  const projectPath = "/fake/project/for/authToken-missing-field";
  const dir = path.dirname(lockPath(projectPath));
  fs.mkdirSync(dir, { recursive: true });
  const lockFile = lockPath(projectPath);
  const existedBefore = fs.existsSync(lockFile);
  const lock: InstanceLock = makeLock(projectPath, { pid: process.pid });
  delete (lock as Partial<InstanceLock>).authToken;
  fs.writeFileSync(lockFile, JSON.stringify(lock));
  try {
    assert.equal(resolveAuthToken(projectPath), undefined);
  } finally {
    if (!existedBefore) fs.rmSync(lockFile, { force: true });
  }
});

// ---------------------------------------------------------------------------
// readInstanceLock
// ---------------------------------------------------------------------------

test("readInstanceLock: null when the file is absent", () => {
  assert.equal(readInstanceLock("/no/such/project/for/readLock"), null);
});

test("readInstanceLock: null when the file is unparseable JSON", () => {
  const projectPath = "/fake/project/for/readLock-bad-json";
  const dir = path.dirname(lockPath(projectPath));
  fs.mkdirSync(dir, { recursive: true });
  const lockFile = lockPath(projectPath);
  const existedBefore = fs.existsSync(lockFile);
  fs.writeFileSync(lockFile, "{ not valid json");
  try {
    assert.equal(readInstanceLock(projectPath), null);
  } finally {
    if (!existedBefore) fs.rmSync(lockFile, { force: true });
  }
});

// ---------------------------------------------------------------------------
// classifyInstance
// ---------------------------------------------------------------------------

test("classifyInstance: null lock → gone", () => {
  assert.equal(classifyInstance(null), "gone");
});

test("classifyInstance: dead pid → gone", () => {
  const lock = makeLock("/p", { pid: 2_000_000 });
  assert.equal(classifyInstance(lock), "gone");
});

test("classifyInstance: live pid + fresh heartbeat + idle → healthy", () => {
  const now = Date.now();
  const lock = makeLock("/p", {
    pid: process.pid,
    heartbeatAt: new Date(now - 1_000).toISOString(),
    state: "idle",
  });
  assert.equal(classifyInstance(lock, now), "healthy");
});

test("classifyInstance: live pid + fresh heartbeat + reloading → reloading", () => {
  const now = Date.now();
  const lock = makeLock("/p", {
    pid: process.pid,
    heartbeatAt: new Date(now - 1_000).toISOString(),
    state: "reloading",
  });
  assert.equal(classifyInstance(lock, now), "reloading");
});

test("classifyInstance: live pid + compiling state → reloading", () => {
  const now = Date.now();
  const lock = makeLock("/p", {
    pid: process.pid,
    heartbeatAt: new Date(now - 1_000).toISOString(),
    state: "compiling",
  });
  assert.equal(classifyInstance(lock, now), "reloading");
});

test("classifyInstance: live pid + stale heartbeat → dead_bridge", () => {
  const now = Date.now();
  const lock = makeLock("/p", {
    pid: process.pid,
    heartbeatAt: new Date(now - (HEARTBEAT_STALE_MS + 5_000)).toISOString(),
    state: "idle",
  });
  assert.equal(classifyInstance(lock, now), "dead_bridge");
});

test("heartbeatAgeMs: Infinity when heartbeatAt is empty (falsy)", () => {
  // heartbeatAt is a required field on InstanceLock, so we test the falsy branch
  // with an empty string rather than deleting the field.
  const lock = makeLock("/p", { pid: process.pid, heartbeatAt: "" });
  assert.equal(heartbeatAgeMs(lock, Date.now()), Infinity);
});

test("heartbeatAgeMs: Infinity when heartbeatAt unparseable", () => {
  const lock = makeLock("/p", { pid: process.pid, heartbeatAt: "not-a-date" });
  assert.equal(heartbeatAgeMs(lock, Date.now()), Infinity);
});

// ---------------------------------------------------------------------------
// env-var name parity
// ---------------------------------------------------------------------------

test("PORT_OVERRIDE_ENV_VAR matches the shared name", () => {
  assert.equal(PORT_OVERRIDE_ENV_VAR, "GODOT_OPEN_MCP_BRIDGE_PORT");
});

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

/** Build a complete InstanceLock with sensible defaults; callers override fields. */
function makeLock(
  projectPath: string,
  overrides: Partial<InstanceLock> & { pid: number },
): InstanceLock {
  const now = new Date().toISOString();
  return {
    pid: overrides.pid,
    port: overrides.port ?? 23456,
    projectPath,
    projectHash: projectHash(projectPath),
    startedAt: overrides.startedAt ?? now,
    updatedAt: overrides.updatedAt ?? now,
    heartbeatAt: overrides.heartbeatAt ?? now,
    state: overrides.state ?? "idle",
    isPlaying: overrides.isPlaying ?? false,
    isCompiling: overrides.isCompiling ?? false,
    bridgeVersion: overrides.bridgeVersion ?? "0.1.0",
    godotVersion: overrides.godotVersion ?? "4.3.0",
    authToken: overrides.authToken,
  };
}

// Guard: the temp-dir helper for the lock-planting tests must match the
// discovery's home-dir convention. If statusDir() ever moves, these tests would
// plant locks in the wrong place and silently no-op.
test("lockPath lives under ~/.godot-open-mcp/instances", () => {
  const lp = lockPath("/some/project");
  const expectedDir = path.join(os.homedir(), ".godot-open-mcp", "instances");
  assert.ok(lp.startsWith(expectedDir + path.sep) || lp === expectedDir);
  assert.ok(lp.endsWith(".json"));
});
