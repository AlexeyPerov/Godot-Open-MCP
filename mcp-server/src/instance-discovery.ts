// Per-project instance discovery (P1.6).
//
// Mirror of the bridge-side InstancePortResolver (packages/bridge/Editor/Bridge/
// InstancePortResolver.cs) + BridgeInstanceLock. The MCP server uses these to
// find the right bridge instance for a given Godot project without any shared
// config:
//
//   1. GODOT_OPEN_MCP_BRIDGE_PORT env var (override wins)
//   2. ~/.godot-open-mcp/instances/<sha256(projectPath)>.json — the bridge's
//      instance lock / heartbeat file. We trust its `port` only when its
//      `pid` is still alive; a stale lock (crashed editor) falls through.
//   3. deterministic hash of the project path (20000 + sha256 % 10000).
//
// The hash formula MUST match the bridge exactly: first 8 bytes (16 hex chars)
// of SHA256(normalizedPath) as a big-endian 64-bit unsigned integer, mod 10000,
// + 20000. Cross-side consistency is pinned by tests in
// instance-discovery.test.ts and the bridge InstancePortResolverTests.cs.
//
// Adapted from Unity Open MCP's mcp-server/src/instance-discovery.ts (copy
// fidelity): the formula, lock shape, and dead-bridge classification are
// byte-identical; only the home-dir convention (`.godot-open-mcp`) and the
// version field name (`godotVersion`) change. The Unity TestRunner-specific
// `hasRecentPendingTestRun` helper is NOT ported — Godot has no equivalent
// scratch file signal (see porting-map.md P1.6, do-not-port list).
//
// No external deps (only node:crypto, node:fs, node:os, node:path) so the
// "no runtime deps beyond MCP SDK" rule (mcp-server/AGENTS.md) holds.

import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

export const PORT_RANGE_START = 20000;
export const PORT_RANGE_SIZE = 10000;

/**
 * The env-var name that overrides the deterministic port. Mirrors the bridge
 * `InstancePortResolver.PortOverrideEnvVar`. The caller reads the env and
 * passes the parsed int to {@link resolvePort} / {@link resolveAuthToken}.
 */
export const PORT_OVERRIDE_ENV_VAR = "GODOT_OPEN_MCP_BRIDGE_PORT";

/** Editor state values the bridge writes into its lock / heartbeat file. */
export type InstanceState =
  | "idle"
  | "compiling"
  | "reloading"
  | "entering_playmode"
  | "playing"
  | "exiting_playmode";

/**
 * Shape of `~/.godot-open-mcp/instances/<hash>.json` (bridge
 * {@link BridgeInstanceLock}). Field names mirror the C# `BuildJson` output
 * byte-for-byte.
 *
 * `authToken` is optional: P1.4 writes the structural lock without auth; the
 * bridge mints the per-session bearer token in P5.2
 * (`packages/bridge/AGENTS.md` §Auth). Keeping the field now means
 * {@link resolveAuthToken} works unchanged once the bridge starts writing it.
 */
export interface InstanceLock {
  pid: number;
  port: number;
  /** P5.2 — per-session bearer token. Optional for back-compat with locks
   *  written by older bridges; when absent the MCP client sends no header
   *  (authMode "none" accepts that). */
  authToken?: string;
  projectPath: string;
  projectHash: string;
  startedAt: string;
  updatedAt: string;
  heartbeatAt: string;
  state: InstanceState;
  isPlaying: boolean;
  isCompiling: boolean;
  bridgeVersion: string;
  godotVersion: string;
}

/**
 * Path normalization applied BEFORE hashing. Mirrors
 * `InstancePortResolver.NormalizePath` in the bridge:
 *   - backslashes -> forward slashes (Windows paths hash the same cross-platform)
 *   - trailing slashes trimmed
 * No lowercasing — macOS/Linux paths are case-sensitive.
 */
export function normalizePath(projectPath: string): string {
  if (!projectPath) return "";
  let norm = projectPath.replace(/\\/g, "/");
  while (norm.length > 1 && norm.endsWith("/")) {
    norm = norm.slice(0, -1);
  }
  return norm;
}

/** Lowercase hex SHA256 of the normalized project path. */
export function projectHash(projectPath: string): string {
  return createHash("sha256").update(normalizePath(projectPath), "utf8").digest("hex");
}

/**
 * Deterministic port for a project path: `20000 + (sha256(path) % 10000)`.
 * Uses the first 8 bytes of the hash as a BigInt so the modulo matches the
 * C# `UInt64` computation exactly. A full 256-bit modulo would diverge across
 * language BigInts; the 8-byte prefix keeps both sides in lockstep.
 */
export function computePort(projectPath: string): number {
  const hash = projectHash(projectPath);
  const prefix = BigInt("0x" + hash.slice(0, 16));
  return PORT_RANGE_START + Number(prefix % BigInt(PORT_RANGE_SIZE));
}

/**
 * Base scratch directory shared by the bridge and the MCP server
 * (`~/.godot-open-mcp`). Mirrors the C# `InstancePortResolver.InstancesDir`
 * parent. Holds the `instances/` subdirectory (per-project locks); future
 * phases may add further scratch files here.
 */
export function statusDir(): string {
  return join(homedir(), ".godot-open-mcp");
}

/** Directory holding one lock file per running bridge instance. */
export function instancesDir(): string {
  return join(statusDir(), "instances");
}

/** Path to this project's instance lock file. */
export function lockPath(projectPath: string): string {
  return join(instancesDir(), `${projectHash(projectPath)}.json`);
}

/**
 * Read this project's instance lock from disk. Returns null if the file is
 * missing, unreadable, or doesn't parse. Never throws — the caller falls
 * through to the deterministic hash on any failure.
 */
export function readInstanceLock(projectPath: string): InstanceLock | null {
  const path = lockPath(projectPath);
  if (!existsSync(path)) return null;
  let raw: string;
  try {
    raw = readFileSync(path, "utf8");
  } catch {
    return null;
  }
  try {
    return JSON.parse(raw) as InstanceLock;
  } catch {
    return null;
  }
}

/**
 * `kill -0` equivalent. Returns true if a process with the given pid exists.
 * Wrapped in try/catch: EPERM (exists but can't be probed) → true,
 * ESRCH (no such process) → false. Mirrors the C#
 * `BridgeInstanceLock.IsPidAlive` logic (Process.GetProcessById).
 */
export function isPidAlive(pid: number): boolean {
  if (!pid || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (err) {
    const code = (err as NodeJS.ErrnoException).code;
    if (code === "EPERM") return true; // exists but we can't probe it
    return false; // ESRCH or anything else → treat as dead
  }
}

/**
 * Resolve the bridge port for a project, with override precedence:
 *   1. explicit envPort (already parsed + validated by the caller)
 *   2. live instance lock's port (only when its pid is alive)
 *   3. deterministic hash
 *
 * @param projectPath absolute Godot project root (the dir containing
 *                    `project.godot`)
 * @param envPort     parsed `GODOT_OPEN_MCP_BRIDGE_PORT`, or undefined
 */
export function resolvePort(projectPath: string, envPort?: number): number {
  if (typeof envPort === "number" && Number.isInteger(envPort) && envPort >= 1 && envPort <= 65535) {
    return envPort;
  }

  const lock = readInstanceLock(projectPath);
  if (lock && typeof lock.port === "number" && isPidAlive(lock.pid)) {
    return lock.port;
  }

  return computePort(projectPath);
}

/**
 * Resolve the bridge's per-session bearer token for a project (P5.2 consumer).
 *
 * The token is only discoverable from a live instance lock (the same file we
 * read the port from). When an explicit env port override is in use there is
 * no lock file to read, so this returns undefined — in that case the MCP
 * client sends no `Authorization` header and the bridge must be in authMode
 * `"none"` for the request to succeed. Returns undefined when the lock is
 * missing, stale (dead pid), or predates the token field.
 *
 * @param projectPath absolute Godot project root
 * @param envPort     parsed `GODOT_OPEN_MCP_BRIDGE_PORT`, or undefined. When
 *                    set, skips the lock read (no token to discover).
 */
export function resolveAuthToken(projectPath: string, envPort?: number): string | undefined {
  if (typeof envPort === "number" && Number.isInteger(envPort) && envPort >= 1 && envPort <= 65535) {
    return undefined;
  }
  const lock = readInstanceLock(projectPath);
  if (!lock || !isPidAlive(lock.pid)) return undefined;
  const token = lock.authToken;
  return typeof token === "string" && token.length > 0 ? token : undefined;
}

// ---------------------------------------------------------------------------
// Dead-bridge detection
//
// When the bridge assembly itself fails to compile, the Godot editor reload
// never re-runs the plugin's `_EnterTree` (the HTTP listener never restarts
// and the heartbeat stops advancing). But the Godot process is still alive
// (sitting on compile errors). That stale-heartbeat + live-PID signature is
// the ONLY out-of-band signal the MCP server has to distinguish "normal
// reload, will recover" from "bridge assembly is dead, will never recover" —
// and it only exists because the bridge keeps the lock file on disk during
// a reload (releasing it only on graceful quit). See
// packages/bridge/AGENTS.md §Multi-instance "Lock retention on domain reload".

/**
 * A heartbeat is considered "stale" once it is this many milliseconds old.
 * The bridge heartbeat writer (a later phase) writes on a fixed tick, so
 * anything older than a few seconds means the heartbeat writer is no longer
 * running. The threshold is generous to absorb a slow Godot editor reload
 * without a false "dead" classification.
 */
export const HEARTBEAT_STALE_MS = 10_000;

/**
 * Coarse health classification of a bridge instance derived from its on-disk
 * lock. Used by the live-client (P1.7+) to decide whether a `/ping` failure
 * is recoverable (keep waiting) or fatal (fail fast).
 *
 *   - "healthy"     — lock fresh and PID alive; bridge is (or just was) up.
 *   - "reloading"   — lock fresh + state="reloading"/"compiling" + PID alive;
 *                     a normal editor reload in flight, expected to recover.
 *   - "dead_bridge" — PID alive BUT heartbeat is stale. The Godot process is
 *                     running but the bridge's heartbeat writer is gone — the
 *                     signature of a bridge assembly that failed to recompile.
 *                     `/ping` will not recover until the C# error is fixed.
 *   - "gone"        — no lock, or PID no longer alive. No live instance.
 */
export type InstanceClassification =
  | "healthy"
  | "reloading"
  | "dead_bridge"
  | "gone";

/**
 * Age of the lock's heartbeat in milliseconds, measured against `nowMs`.
 * Returns `Infinity` when the heartbeat timestamp is missing or unparseable,
 * so a malformed lock is treated as maximally stale (and thus, if the PID is
 * alive, "dead_bridge").
 */
export function heartbeatAgeMs(
  lock: InstanceLock | null,
  nowMs: number = Date.now(),
): number {
  if (!lock || !lock.heartbeatAt) return Infinity;
  const t = Date.parse(lock.heartbeatAt);
  if (!Number.isFinite(t)) return Infinity;
  return Math.max(0, nowMs - t);
}

/**
 * Classify a bridge instance from its on-disk lock. `lock` may be null (no
 * lock file / unreadable) — treated as "gone". This is a pure function over
 * the lock snapshot + current time + PID liveness; it does no I/O of its own.
 */
export function classifyInstance(
  lock: InstanceLock | null,
  nowMs: number = Date.now(),
): InstanceClassification {
  if (!lock) return "gone";
  if (!isPidAlive(lock.pid)) return "gone";

  const age = heartbeatAgeMs(lock, nowMs);
  if (age >= HEARTBEAT_STALE_MS) return "dead_bridge";
  if (lock.state === "reloading" || lock.state === "compiling") return "reloading";
  return "healthy";
}
