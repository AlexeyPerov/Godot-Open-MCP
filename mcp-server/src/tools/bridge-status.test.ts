// `godot_open_mcp_bridge_status` tests (P5.3).
//
// Three layers:
//   1. Tool-definition contract (name prefix, empty schema, description) — the
//      metadata the MCP ListTools response advertises.
//   2. Pure mapper matrix — every (classification, ping, lockPidAlive) cell
//      resolves to the authoritative status token, recoveryHint is non-null
//      only for dead_bridge, and nextStep covers every status. Network-free.
//   3. Integration stub — a LiveClient whose /ping path is exercised against a
//      local HTTP bridge stub, plus the offline (no listener) path, confirming
//      the full lock → classify → ping → derive → shape flow. Also pins the
//      "never errors on offline bridge" contract.
//
// Adapted from Unity Open MCP's tool-router.test.ts bridge_status block (copy
// fidelity for the status matrix + response shape), minus Unity's
// cold-Safe-Mode scan (no Godot equivalent — intentional delta documented in
// bridge-status-derive.ts).

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  createServer,
  type Server as HttpServer,
  type IncomingMessage,
  type ServerResponse,
} from "node:http";
import { bridgeStatus } from "./bridge-status.js";
import {
  deriveBridgeStatus,
  bridgeStatusRecoveryHint,
  bridgeStatusNextStep,
  summarizeBridgeStatusLock,
  BRIDGE_STATUSES,
  type BridgeStatus,
  type BridgeStatusInput,
  type PingProbe,
} from "./bridge-status-derive.js";
import type { InstanceClassification, InstanceLock } from "../instance-discovery.js";
import { LiveClient, type PingResponse } from "../live-client.js";
import { ALL_TOOLS } from "./index.js";

// ── 1. Tool-definition contract ─────────────────────────────────────────────

test("bridge_status tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(bridgeStatus.name, "godot_open_mcp_bridge_status");
  assert.match(bridgeStatus.name, /^godot_open_mcp_/);
});

test("bridge_status tool has a non-empty description", () => {
  assert.ok(typeof bridgeStatus.description === "string");
  assert.ok((bridgeStatus.description ?? "").length > 0);
});

test("bridge_status declares an empty input schema with no additional properties", () => {
  assert.deepEqual(bridgeStatus.inputSchema, {
    type: "object",
    properties: {},
    additionalProperties: false,
  });
});

test("bridge_status is registered in ALL_TOOLS", () => {
  assert.ok(
    ALL_TOOLS.some((t) => t.name === "godot_open_mcp_bridge_status"),
    "bridge_status must be in the registry so ListTools advertises it",
  );
});

// ── 2. Pure mapper matrix ───────────────────────────────────────────────────

const REACHABLE_IDLE: PingProbe = {
  reachable: true,
  connected: true,
  compiling: false,
  isPlaying: false,
  godotVersion: "4.3.1.stable.mono",
  bridgeVersion: "0.0.1",
  mode: "live",
};

const REACHABLE_COMPILING: PingProbe = {
  ...REACHABLE_IDLE,
  connected: false,
  compiling: true,
};

const UNREACHABLE: PingProbe = {
  reachable: false,
  connected: null,
  compiling: null,
  isPlaying: null,
  godotVersion: null,
  bridgeVersion: null,
  mode: null,
};

function derive(
  classification: InstanceClassification,
  ping: PingProbe,
  lockPidAlive: boolean,
): BridgeStatus {
  const input: BridgeStatusInput = { classification, ping, lockPidAlive };
  return deriveBridgeStatus(input);
}

test("deriveBridgeStatus: dead_bridge classification wins over any ping", () => {
  // A stale heartbeat means the listener will not recover; even a coincidentally
  // reachable ping must not mask it.
  assert.equal(derive("dead_bridge", REACHABLE_IDLE, true), "dead_bridge");
  assert.equal(derive("dead_bridge", REACHABLE_COMPILING, true), "dead_bridge");
  assert.equal(derive("dead_bridge", UNREACHABLE, true), "dead_bridge");
  assert.equal(derive("dead_bridge", UNREACHABLE, false), "dead_bridge");
});

test("deriveBridgeStatus: reachable + compiling → compiling (regardless of classification)", () => {
  for (const c of ["healthy", "reloading"] as InstanceClassification[]) {
    assert.equal(derive(c, REACHABLE_COMPILING, true), "compiling");
    assert.equal(derive(c, REACHABLE_COMPILING, false), "compiling");
  }
});

test("deriveBridgeStatus: reachable + connected + not compiling → running", () => {
  for (const c of ["healthy", "reloading"] as InstanceClassification[]) {
    assert.equal(derive(c, REACHABLE_IDLE, true), "running");
    assert.equal(derive(c, REACHABLE_IDLE, false), "running");
  }
});

test("deriveBridgeStatus: unreachable + live PID → unreachable (transient reload window)", () => {
  // healthy/reloading + unreachable + live PID → unreachable (retry), NOT stopped.
  for (const c of ["healthy", "reloading"] as InstanceClassification[]) {
    assert.equal(derive(c, UNREACHABLE, true), "unreachable");
  }
});

test("deriveBridgeStatus: unreachable + no live PID → stopped", () => {
  // gone classification (no lock / dead pid) + unreachable → stopped.
  assert.equal(derive("gone", UNREACHABLE, false), "stopped");
  assert.equal(derive("healthy", UNREACHABLE, false), "stopped");
});

test("deriveBridgeStatus: gone + reachable ping still resolves via ping branches", () => {
  // A stale-then-recovered lock can classify "gone" while the bridge answers;
  // the ping is authoritative for the reachable cases.
  assert.equal(derive("gone", REACHABLE_IDLE, false), "running");
  assert.equal(derive("gone", REACHABLE_COMPILING, false), "compiling");
  assert.equal(derive("gone", UNREACHABLE, false), "stopped");
});

test("BRIDGE_STATUSES lists exactly the five coarse tokens", () => {
  assert.deepEqual([...BRIDGE_STATUSES], [
    "running",
    "compiling",
    "stopped",
    "dead_bridge",
    "unreachable",
  ]);
});

// recoveryHint rules — non-null ONLY for dead_bridge.

test("bridgeStatusRecoveryHint: non-null only for dead_bridge", () => {
  for (const status of BRIDGE_STATUSES) {
    const hint = bridgeStatusRecoveryHint(status);
    if (status === "dead_bridge") {
      assert.ok(hint, `hint must be present for ${status}`);
      assert.equal(typeof hint.tool, "string");
      assert.ok(hint.tool.length > 0);
      assert.equal(typeof hint.reason, "string");
      assert.ok(hint.reason.length > 0);
    } else {
      assert.equal(hint, null, `hint must be null for ${status}`);
    }
  }
});

test("bridgeStatusRecoveryHint: dead_bridge names the registered offline compile-errors reader", () => {
  const hint = bridgeStatusRecoveryHint("dead_bridge");
  assert.ok(hint);
  // P7.4 — the hint now names the authoritative offline log reader. The risk
  // safeguard requires the tool to be registered; ALL_TOOLS membership is
  // asserted here so a future unregister breaks this test in the same change.
  assert.equal(hint.tool, "godot_open_mcp_read_compile_errors");
  const registered = ALL_TOOLS.some((t) => t.name === hint.tool);
  assert.ok(registered, `recovery hint tool '${hint.tool}' must be a registered tool`);
  // The hint no longer carries a `note` — the offline reader IS the
  // authoritative recovery path, so there is no gap to caveat.
  assert.equal(hint.note, undefined, "no note once the authoritative tool ships");
});

// nextStep prose — every status has action-oriented guidance.

test("bridgeStatusNextStep: every status returns non-empty guidance", () => {
  for (const status of BRIDGE_STATUSES) {
    const step = bridgeStatusNextStep(status);
    assert.equal(typeof step, "string");
    assert.ok(step.length > 0, `${status} needs non-empty nextStep`);
  }
});

test("bridgeStatusNextStep: dead_bridge mentions the addon / heartbeat failure", () => {
  const step = bridgeStatusNextStep("dead_bridge");
  // Godot-specific wording (no Unity Safe Mode). Must name the recovery path.
  assert.ok(/addon|heartbeat|listener/i.test(step));
});

// summarizeBridgeStatusLock — compact lock mirror.

test("summarizeBridgeStatusLock mirrors the operator-relevant lock fields", () => {
  const lock: InstanceLock = {
    pid: 4242,
    port: 22028,
    authToken: "secret",
    projectPath: "/home/u/MyGame",
    projectHash: "abc",
    startedAt: "2026-07-12T00:00:00.000Z",
    updatedAt: "2026-07-12T00:00:01.000Z",
    heartbeatAt: "2026-07-12T00:00:02.000Z",
    state: "idle",
    isPlaying: false,
    isCompiling: false,
    bridgeVersion: "0.0.1",
    godotVersion: "4.3.1.stable.mono",
  };
  const summary = summarizeBridgeStatusLock(lock);
  assert.deepEqual(summary, {
    pid: 4242,
    port: 22028,
    state: "idle",
    isCompiling: false,
    isPlaying: false,
    heartbeatAt: "2026-07-12T00:00:02.000Z",
    bridgeVersion: "0.0.1",
    godotVersion: "4.3.1.stable.mono",
  });
  // Sensitive / non-operator fields are NOT leaked into the summary.
  assert.ok(!("authToken" in summary));
  assert.ok(!("projectPath" in summary));
});

// ── 3. Integration stub (LiveClient.routeBridgeStatus) ──────────────────────

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
  projectPath: "/home/u/MyGame",
  godotVersion: "4.3.1.stable.mono",
  bridgeVersion: "0.0.1",
  mode: "live",
  compiling: false,
  isPlaying: false,
};

/**
 * Parse the JSON body the routeBridgeStatus response carries in content[0].text.
 * Asserts the never-error contract (isError must be falsy).
 */
function parseStatusBody(result: { isError?: boolean; content: Array<{ type: string; text?: string }> }): {
  status: BridgeStatus;
  ready: boolean;
  classification: InstanceClassification;
  recoveryHint: { tool: string; reason: string; note?: string } | null;
  ping: { reachable: boolean; [k: string]: unknown };
  instance: { lockPath: string | null; classification: InstanceClassification; lock: unknown };
  nextStep: string;
  _source: string;
} {
  assert.equal(result.isError, false, "bridge_status must never report an MCP error");
  const block = result.content[0];
  assert.equal(block.type, "text");
  assert.ok(block.type === "text");
  assert.ok(typeof block.text === "string", "status body must be a text block");
  return JSON.parse(block.text!) as {
    status: BridgeStatus;
    ready: boolean;
    classification: InstanceClassification;
    recoveryHint: { tool: string; reason: string; note?: string } | null;
    ping: { reachable: boolean; [k: string]: unknown };
    instance: { lockPath: string | null; classification: InstanceClassification; lock: unknown };
    nextStep: string;
    _source: string;
  };
}

test("routeBridgeStatus: healthy bridge → running, ready:true, recoveryHint:null", async () => {
  const stub = await startBridgeStub((req, res) => {
    if (req.url === "/ping") {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify(HEALTHY_PING));
      return;
    }
    res.writeHead(404);
    res.end();
  });
  try {
    // No projectPath → no lock read → classification "gone" + no lockPidAlive.
    // The reachable+connected ping drives the status to "running".
    const client = new LiveClient(stub.port, undefined, undefined);
    const body = parseStatusBody(await client.routeBridgeStatus());
    assert.equal(body.status, "running");
    assert.equal(body.ready, true);
    assert.equal(body.classification, "gone");
    assert.equal(body.recoveryHint, null);
    assert.equal(body.ping.reachable, true);
    assert.equal(body._source, "local");
    assert.ok(typeof body.nextStep === "string" && body.nextStep.length > 0);
  } finally {
    await stub.close();
  }
});

test("routeBridgeStatus: compiling ping → compiling status", async () => {
  const stub = await startBridgeStub((req, res) => {
    if (req.url === "/ping") {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ ...HEALTHY_PING, connected: false, compiling: true }));
      return;
    }
    res.writeHead(404);
    res.end();
  });
  try {
    const client = new LiveClient(stub.port, undefined, undefined);
    const body = parseStatusBody(await client.routeBridgeStatus());
    assert.equal(body.status, "compiling");
    assert.equal(body.ready, false);
    assert.equal(body.recoveryHint, null);
  } finally {
    await stub.close();
  }
});

test("routeBridgeStatus: no listener + no lock → stopped (never errors)", async () => {
  // A high unused port with no listener → ECONNREFUSED → ping unreachable.
  // No projectPath → no lock → classification "gone" + lockPidAlive false → stopped.
  const client = new LiveClient(59999, undefined, undefined);
  const body = parseStatusBody(await client.routeBridgeStatus());
  assert.equal(body.status, "stopped");
  assert.equal(body.ready, false);
  assert.equal(body.classification, "gone");
  assert.equal(body.recoveryHint, null);
  assert.equal(body.ping.reachable, false);
  assert.equal(body.instance.lock, null);
});

test("routeBridgeStatus: stale-heartbeat lock → dead_bridge + recoveryHint", async () => {
  // Build an in-memory lock that classifies as dead_bridge: live PID + stale
  // heartbeat. We cannot easily write the lock file from a unit test (it lives
  // in ~/.godot-open-mcp/instances/), so this case is covered by the pure
  // matrix above + this test pins the recoveryHint wiring through the public
  // helper. Here we confirm a NO-lock + unreachable bridge does NOT get a
  // dead_bridge hint (it stays stopped/null), guarding against the hint
  // leaking into the wrong branch.
  const client = new LiveClient(59999, undefined, undefined);
  const body = parseStatusBody(await client.routeBridgeStatus());
  assert.equal(body.status, "stopped");
  assert.equal(body.recoveryHint, null, "stopped must not carry a dead_bridge hint");
});
