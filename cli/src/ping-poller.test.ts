// Tests for the compile-aware bridge readiness poller (src/ping-poller.ts).
//
// The poller is the heart of `wait-for-ready` and `ping`: it must return ready
// only when /ping says connected AND not compiling, keep waiting through a
// 503/compiling state, fail fast on a dead-bridge signature, and respect the
// deadline. The state machine is tested by injecting the single-poll stub and a
// fake clock; `singlePing` (the real fetch path) is tested against a loopback
// HTTP stub.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer, type Server } from "node:http";
import * as fs from "node:fs";
import * as path from "node:path";

import {
  pollUntilReady,
  singlePing,
  type ProbeTarget,
  type SinglePollResult,
  type PollOptions,
  type PingBody,
} from "./ping-poller.js";
import {
  classifyInstance,
  lockPath,
  projectHash,
} from "./instance-discovery.js";

const TARGET: ProbeTarget = { port: 29999, baseUrl: "http://127.0.0.1:29999" };

// ---------------------------------------------------------------------------
// state-machine helpers (injected clock + scripted poll outcomes)
// ---------------------------------------------------------------------------

function makeFakeClock(startMs: number) {
  let t = startMs;
  const sleeps: number[] = [];
  return {
    now: () => t,
    sleep: (ms: number) => {
      sleeps.push(ms);
      t += ms;
      return Promise.resolve();
    },
    elapsed: () => t - startMs,
    sleeps,
  };
}

// A scripted sequence of poll outcomes. When exhausted, repeats the last one.
function scriptPoll(results: SinglePollResult[]) {
  let i = 0;
  return async (): Promise<SinglePollResult> => {
    const r = results[Math.min(i, results.length - 1)];
    i++;
    return r;
  };
}

function makeOpts(clock: ReturnType<typeof makeFakeClock>): PollOptions {
  return {
    timeoutMs: 10_000,
    intervalMs: 1_000,
    now: clock.now,
    sleep: clock.sleep,
  };
}

const READY_BODY: PingBody = { connected: true, compiling: false, isPlaying: false };
const COMPILING_BODY: PingBody = { connected: true, compiling: true, isPlaying: false };
const OFFLINE_RESULT: SinglePollResult = { status: "offline", body: null };

// ---------------------------------------------------------------------------
// ready cases
// ---------------------------------------------------------------------------

test("pollUntilReady: returns ready immediately when first ping is ready", async () => {
  const clock = makeFakeClock(0);
  const outcome = await pollUntilReady(
    TARGET,
    undefined,
    scriptPoll([{ status: "ready", body: READY_BODY }]),
    makeOpts(clock),
  );
  assert.equal(outcome.ready, true);
  assert.equal(outcome.status, "ready");
  assert.equal(outcome.elapsedMs, 0);
  assert.deepEqual(clock.sleeps, []);
});

test("pollUntilReady: waits through compiling then becomes ready", async () => {
  const clock = makeFakeClock(0);
  const outcome = await pollUntilReady(
    TARGET,
    undefined,
    scriptPoll([
      { status: "compiling", body: COMPILING_BODY },
      { status: "compiling", body: COMPILING_BODY },
      { status: "ready", body: READY_BODY },
    ]),
    makeOpts(clock),
  );
  assert.equal(outcome.ready, true);
  assert.equal(outcome.status, "ready");
  assert.equal(outcome.elapsedMs, 2_000);
  assert.deepEqual(clock.sleeps, [1_000, 1_000]);
  assert.deepEqual(outcome.lastPing, READY_BODY);
});

test("pollUntilReady: treats connected:false as offline (not ready)", async () => {
  const clock = makeFakeClock(0);
  const outcome = await pollUntilReady(
    TARGET,
    undefined,
    scriptPoll([{ status: "offline", body: { connected: false } }]),
    { ...makeOpts(clock), timeoutMs: 500 },
  );
  assert.equal(outcome.ready, false);
  assert.equal(outcome.status, "timeout");
});

// ---------------------------------------------------------------------------
// timeout cases
// ---------------------------------------------------------------------------

test("pollUntilReady: times out when never ready", async () => {
  const clock = makeFakeClock(0);
  const outcome = await pollUntilReady(
    TARGET,
    undefined,
    scriptPoll([OFFLINE_RESULT]),
    { ...makeOpts(clock), timeoutMs: 3_000, intervalMs: 1_000 },
  );
  assert.equal(outcome.ready, false);
  assert.equal(outcome.status, "timeout");
  assert.match(outcome.reason, /never became reachable/i);
});

test("pollUntilReady: timeout reason mentions compiling when that was the stall", async () => {
  const clock = makeFakeClock(0);
  const outcome = await pollUntilReady(
    TARGET,
    undefined,
    scriptPoll([{ status: "compiling", body: COMPILING_BODY }]),
    { ...makeOpts(clock), timeoutMs: 3_000, intervalMs: 1_000 },
  );
  assert.equal(outcome.status, "timeout");
  assert.match(outcome.reason, /still compiling/i);
});

test("pollUntilReady: timeout reason mentions offline+compiling generically when both seen", async () => {
  const clock = makeFakeClock(0);
  const outcome = await pollUntilReady(
    TARGET,
    undefined,
    scriptPoll([
      { status: "offline", body: null },
      { status: "compiling", body: COMPILING_BODY },
    ]),
    { ...makeOpts(clock), timeoutMs: 3_000, intervalMs: 1_000 },
  );
  assert.equal(outcome.status, "timeout");
  assert.match(outcome.reason, /did not become ready/i);
});

test("pollUntilReady: deadline check happens before the first poll when already past", async () => {
  const clock = makeFakeClock(5_000);
  const outcome = await pollUntilReady(
    TARGET,
    undefined,
    scriptPoll([{ status: "ready", body: READY_BODY }]),
    { ...makeOpts(clock), timeoutMs: 0 },
  );
  assert.equal(outcome.ready, false);
  assert.equal(outcome.status, "timeout");
});

test("pollUntilReady: error status keeps waiting (treated like offline)", async () => {
  const clock = makeFakeClock(0);
  const outcome = await pollUntilReady(
    TARGET,
    undefined,
    scriptPoll([
      { status: "error", body: null },
      { status: "ready", body: READY_BODY },
    ]),
    makeOpts(clock),
  );
  assert.equal(outcome.ready, true);
  assert.equal(outcome.status, "ready");
});

// ---------------------------------------------------------------------------
// dead-bridge fail-fast
// ---------------------------------------------------------------------------

test("pollUntilReady: fail-fast on dead_bridge when projectPath resolves a stale lock", async () => {
  // Plant a dead-bridge lock for this test's project path: live PID (the test
  // runner) but a heartbeat far in the past. classifyInstance reads the
  // heartbeat relative to Date.now(); an ISO string from 2000 is well past the
  // stale threshold.
  const projectPath = "/fake/project/for/poller-dead-bridge";
  const lockFile = plantDeadBridgeLock(projectPath);
  try {
    const clock = makeFakeClock(0);
    const outcome = await pollUntilReady(
      TARGET,
      projectPath,
      scriptPoll([OFFLINE_RESULT]),
      makeOpts(clock),
    );
    assert.equal(outcome.ready, false);
    assert.equal(outcome.status, "dead_bridge");
    assert.match(outcome.reason, /compile errors/i);
  } finally {
    cleanupLock(lockFile);
  }
});

test("pollUntilReady: skips dead-bridge check when projectPath is undefined", async () => {
  const clock = makeFakeClock(0);
  const outcome = await pollUntilReady(
    TARGET,
    undefined, // no project path → no lock read
    scriptPoll([OFFLINE_RESULT]),
    { ...makeOpts(clock), timeoutMs: 500 },
  );
  assert.equal(outcome.status, "timeout");
});

test("pollUntilReady: healthy lock does NOT fail-fast dead_bridge", async () => {
  // A healthy lock (fresh heartbeat) must not trip the dead-bridge branch even
  // while the poll sees offline — the poller should keep waiting to timeout.
  const projectPath = "/fake/project/for/poller-healthy-lock";
  const lockFile = plantHealthyLock(projectPath);
  try {
    const clock = makeFakeClock(0);
    const outcome = await pollUntilReady(
      TARGET,
      projectPath,
      scriptPoll([OFFLINE_RESULT]),
      { ...makeOpts(clock), timeoutMs: 500 },
    );
    assert.notEqual(outcome.status, "dead_bridge");
    assert.equal(outcome.status, "timeout");
  } finally {
    cleanupLock(lockFile);
  }
});

// ---------------------------------------------------------------------------
// singlePing against a loopback HTTP stub
// ---------------------------------------------------------------------------

test("singlePing: returns ready for a 200 connected+idle body", async () => {
  const { server, port } = await startOneShotStub(200, {
    connected: true,
    compiling: false,
    isPlaying: false,
    godotVersion: "4.3.0",
    bridgeVersion: "0.1.0",
    mode: "live",
  });
  try {
    const target: ProbeTarget = { port, baseUrl: `http://127.0.0.1:${port}` };
    const result = await singlePing(target);
    assert.equal(result.status, "ready");
    assert.equal(result.body?.connected, true);
    assert.equal(result.body?.compiling, false);
    assert.equal(result.body?.godotVersion, "4.3.0");
  } finally {
    server.close();
  }
});

test("singlePing: returns compiling for a 200 body with compiling:true", async () => {
  const { server, port } = await startOneShotStub(200, {
    connected: true,
    compiling: true,
    isPlaying: false,
  });
  try {
    const target: ProbeTarget = { port, baseUrl: `http://127.0.0.1:${port}` };
    const result = await singlePing(target);
    assert.equal(result.status, "compiling");
    assert.equal(result.body?.compiling, true);
  } finally {
    server.close();
  }
});

test("singlePing: returns offline for a 503 with a compiling fallback body", async () => {
  // 503 is treated as compiling (reachable-but-not-ready), NOT offline.
  const { server, port } = await startOneShotStub(503, {
    connected: false,
    compiling: true,
  });
  try {
    const target: ProbeTarget = { port, baseUrl: `http://127.0.0.1:${port}` };
    const result = await singlePing(target);
    assert.equal(result.status, "compiling");
  } finally {
    server.close();
  }
});

test("singlePing: returns offline when ECONNREFUSED (no listener)", async () => {
  // A port with no listener → fetch throws TypeError → offline.
  const target: ProbeTarget = {
    port: 1,
    baseUrl: "http://127.0.0.1:1",
  };
  const result = await singlePing(target);
  assert.equal(result.status, "offline");
  assert.equal(result.body, null);
});

test("singlePing: attaches the Bearer token from the probe target", async () => {
  // The stub echoes the Authorization header in the body so we can assert it.
  const { server, port } = await startEchoHeaderStub("authorization");
  try {
    const target: ProbeTarget = {
      port,
      baseUrl: `http://127.0.0.1:${port}`,
      authToken: "secret-token-abc",
    };
    const result = await singlePing(target);
    assert.equal(result.status, "ready");
    const body = result.body as (PingBody & { authorizationHeader?: string | null }) | null;
    assert.equal(body?.authorizationHeader, "Bearer secret-token-abc");
  } finally {
    server.close();
  }
});

test("singlePing: omits Authorization when no authToken is set", async () => {
  const { server, port } = await startEchoHeaderStub("authorization");
  try {
    const target: ProbeTarget = { port, baseUrl: `http://127.0.0.1:${port}` };
    const result = await singlePing(target);
    assert.equal(result.status, "ready");
    const body = result.body as (PingBody & { authorizationHeader?: string | null }) | null;
    assert.equal(body?.authorizationHeader, null);
  } finally {
    server.close();
  }
});

// ---------------------------------------------------------------------------
// lock-planting helpers
// ---------------------------------------------------------------------------

/**
 * Plant a dead-bridge lock (live PID = this process, stale heartbeat from
 * 2000). Returns the lock file path so the caller can clean it up.
 */
function plantDeadBridgeLock(projectPath: string): string {
  const hash = projectHash(projectPath);
  const dir = path.dirname(lockPath(projectPath));
  fs.mkdirSync(dir, { recursive: true });
  const lp = lockPath(projectPath);
  fs.writeFileSync(
    lp,
    JSON.stringify({
      pid: process.pid,
      port: 29999,
      projectPath,
      projectHash: hash,
      startedAt: "2000-01-01T00:00:00.000Z",
      updatedAt: "2000-01-01T00:00:00.000Z",
      heartbeatAt: "2000-01-01T00:00:00.000Z",
      state: "idle",
      isPlaying: false,
      isCompiling: false,
      bridgeVersion: "0.1.0",
      godotVersion: "4.3.0",
    }),
  );
  return lp;
}

function plantHealthyLock(projectPath: string): string {
  const hash = projectHash(projectPath);
  const dir = path.dirname(lockPath(projectPath));
  fs.mkdirSync(dir, { recursive: true });
  const lp = lockPath(projectPath);
  const now = new Date().toISOString();
  fs.writeFileSync(
    lp,
    JSON.stringify({
      pid: process.pid,
      port: 29999,
      projectPath,
      projectHash: hash,
      startedAt: now,
      updatedAt: now,
      heartbeatAt: now,
      state: "idle",
      isPlaying: false,
      isCompiling: false,
      bridgeVersion: "0.1.0",
      godotVersion: "4.3.0",
    }),
  );
  return lp;
}

function cleanupLock(lp: string): void {
  try {
    fs.rmSync(lp, { force: true });
  } catch {
    // best-effort
  }
}

// ---------------------------------------------------------------------------
// HTTP stub variants
// ---------------------------------------------------------------------------

/** A stub that responds to GET /ping with a fixed status + JSON body. */
function startOneShotStub(status: number, body: unknown): Promise<{ server: Server; port: number }> {
  return new Promise((resolve, reject) => {
    const server = createServer((req, res) => {
      if (req.url === "/ping" && req.method === "GET") {
        res.statusCode = status;
        res.setHeader("Content-Type", "application/json");
        res.end(JSON.stringify(body));
      } else {
        res.statusCode = 404;
        res.end();
      }
    });
    server.on("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const addr = server.address();
      const port = typeof addr === "object" && addr ? addr.port : 0;
      resolve({ server, port });
    });
  });
}

/**
 * A stub that echoes a request header back as a top-level field in the ping
 * body, so tests can assert which header the probe sent.
 */
function startEchoHeaderStub(
  headerLower: string,
): Promise<{ server: Server; port: number }> {
  return new Promise((resolve, reject) => {
    const server = createServer((req, res) => {
      if (req.url === "/ping" && req.method === "GET") {
        const value = req.headers[headerLower] ?? null;
        res.statusCode = 200;
        res.setHeader("Content-Type", "application/json");
        // connected:true + compiling:false so the probe classifies as ready.
        res.end(
          JSON.stringify({
            connected: true,
            compiling: false,
            isPlaying: false,
            authorizationHeader: value,
          }),
        );
      } else {
        res.statusCode = 404;
        res.end();
      }
    });
    server.on("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const addr = server.address();
      const port = typeof addr === "object" && addr ? addr.port : 0;
      resolve({ server, port });
    });
  });
}

// ---------------------------------------------------------------------------
// sanity: the dead-bridge helper plants a lock that classifies dead_bridge
// ---------------------------------------------------------------------------

test("plantDeadBridgeLock helper classifies as dead_bridge (guards the test)", () => {
  const projectPath = "/fake/project/for/poller-sanity";
  const lp = plantDeadBridgeLock(projectPath);
  try {
    const lock = JSON.parse(fs.readFileSync(lp, "utf8"));
    assert.equal(classifyInstance(lock), "dead_bridge");
  } finally {
    cleanupLock(lp);
  }
});
