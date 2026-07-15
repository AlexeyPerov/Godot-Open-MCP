// Per-project bridge instance discovery for the CLI.
//
// This is a byte-for-byte copy of `mcp-server/src/instance-discovery.ts`, kept
// in `cli/` deliberately: the CLI is a separate package with ZERO runtime
// dependencies (cli/AGENTS.md), and importing the MCP server would pull in the
// MCP SDK + tool registry. Both copies share only node builtins (crypto, fs,
// os, path), and the formula + lock shape MUST agree byte-for-byte with the
// bridge's `InstancePortResolver.cs`. If you change this file, change the
// mcp-server copy and the bridge C# in lockstep.
//
// Fidelity: copy (from mcp-server/src/instance-discovery.ts, which is itself a
// copy of Unity's). Only the doc-comments are trimmed; the logic is identical.

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

/** Shape of `~/.godot-open-mcp/instances/<hash>.json` (bridge lock file). */
export interface InstanceLock {
  pid: number;
  port: number;
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

/** Path normalization applied BEFORE hashing (mirrors the bridge C#). */
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
 * C# `UInt64` computation exactly.
 */
export function computePort(projectPath: string): number {
  const hash = projectHash(projectPath);
  const prefix = BigInt("0x" + hash.slice(0, 16));
  return PORT_RANGE_START + Number(prefix % BigInt(PORT_RANGE_SIZE));
}

/** Base scratch directory shared by the bridge and the MCP server. */
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

/** Read this project's instance lock from disk. Never throws. */
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

/** `kill -0` equivalent — true if a process with the given pid exists. */
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
 * Resolve the bridge's per-session bearer token for a project. Returns
 * undefined when an explicit env port override is in use, the lock is
 * missing/stale, or the token field is absent.
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
// ---------------------------------------------------------------------------

/** A heartbeat older than this is considered stale. */
export const HEARTBEAT_STALE_MS = 10_000;

export type InstanceClassification =
  | "healthy"
  | "reloading"
  | "dead_bridge"
  | "gone";

/** Age of the lock's heartbeat in milliseconds; Infinity when unparseable. */
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
 * Classify a bridge instance from its on-disk lock. Pure over the lock snapshot
 * + current time + PID liveness. `null` lock → "gone".
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
