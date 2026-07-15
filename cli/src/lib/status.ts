// Pure assembly of the CLI `status` JSON (P6.5).
//
// Gathers the signals a status probe needs — project validity, addon presence +
// plugin-enabled state, instance-lock classification, resolved port/base URL,
// and a single `/ping` probe — and folds them into a coarse `status` token
// aligned with the MCP tool `godot_open_mcp_bridge_status` (P5.3).
//
// ── Vocabulary alignment ────────────────────────────────────────────────────
// The MCP tool's pure mapper lives at
// `mcp-server/src/tools/bridge-status-derive.ts` (`deriveBridgeStatus`). The CLI
// is a separate package with ZERO runtime dependencies (cli/AGENTS.md), so it
// cannot import that module. Instead this file duplicates the mapping table and
// pins it with a comment pointing at the MCP source of truth — the approach the
// plan allows when sharing the helper is not practical.
//
// The duplicated mapping MUST agree with `deriveBridgeStatus` byte-for-byte:
//
//   | classification | ping               | lockPidAlive | status        |
//   |----------------|--------------------|--------------|---------------|
//   | dead_bridge    | any                | any          | dead_bridge   |
//   | any            | reachable+compiling| any          | compiling     |
//   | any            | reachable+connected| any          | running       |
//   | any            | NOT reachable      | true         | unreachable   |
//   | any            | NOT reachable      | false        | stopped       |
//
// `dead_bridge` wins outright (a stale heartbeat means the listener will not
// recover). If the two ever drift, the bridge-status tests on the MCP side and
// the status tests here should catch it.
//
// Adapted from Unity Open MCP's `runStatusCommand` (mcp-server/src/cli/commands.ts)
// + the Godot-MCP behavior reference (`cli/src/commands/status.ts`). Intentional
// deltas: (1) the CLI probes the bridge `/ping` directly (no MCP client in
// process); (2) status includes the Godot-specific addon-present + plugin-enabled
// check; (3) the vocabulary is aligned with the MCP `bridge_status` tool, not
// Godot-MCP's "MCP server" wording.

import * as fs from "fs";
import * as path from "path";

import {
  readInstanceLock,
  classifyInstance,
  isPidAlive,
  lockPath,
  heartbeatAgeMs,
  resolvePort,
  resolveAuthToken,
  type InstanceClassification,
  type InstanceLock,
} from "../instance-discovery.js";
import { singlePing, PING_FETCH_TIMEOUT_MS, type PingBody } from "../ping-poller.js";
import {
  GODOT_OPEN_MCP_PLUGIN_PATH,
  isGodotProjectRoot,
  parseEnabledPlugins,
  projectGodotPath,
} from "../utils/project-godot.js";

// ---------------------------------------------------------------------------
// Status vocabulary — MUST match mcp-server/src/tools/bridge-status-derive.ts
// ---------------------------------------------------------------------------

/**
 * The five coarse status tokens. Copied verbatim from the MCP `bridge_status`
 * tool so an operator branches on the same vocabulary across the CLI and MCP.
 *
 *   - `running`     — bridge connected and idle.
 *   - `compiling`   — bridge reachable but Godot reports a rebuild in flight.
 *   - `stopped`     — no live listener (Godot not running or addon disabled).
 *   - `unreachable` — Godot process alive (lock PID live) but `/ping` silent.
 *   - `dead_bridge` — Godot alive but bridge heartbeat stale; listener down.
 */
export type BridgeStatus =
  | "running"
  | "compiling"
  | "stopped"
  | "dead_bridge"
  | "unreachable";

// ---------------------------------------------------------------------------
// Input + output shapes
// ---------------------------------------------------------------------------

export interface StatusProbeInput {
  /** Absolute project root. */
  projectPath: string;
  /** Bridge port override (flag / env), else resolved from lock / hash. */
  portOverride?: number;
  /** Per-ping fetch timeout in ms (default PING_FETCH_TIMEOUT_MS). */
  fetchTimeoutMs?: number;
}

/** Addon presence + plugin-enabled check (Godot-specific). */
export interface StatusAddon {
  /** True when `addons/godot_open_mcp/plugin.cfg` exists on disk. */
  present: boolean;
  /** True when the plugin path is in `[editor_plugins] enabled`. */
  enabled: boolean;
  /** Canonical resource path. */
  pluginPath: string;
}

/** Instance-lock derived metadata (null when no lock exists). */
export interface StatusInstance {
  port: number;
  pid: number;
  classification: InstanceClassification;
  lockPath: string;
  heartbeatAgeMs: number;
  state: InstanceLock["state"] | null;
  isCompiling: boolean;
  isPlaying: boolean;
}

/** Normalized `/ping` probe result for the status JSON. */
export interface StatusPing {
  ok: boolean;
  /** Poller status token: ready | compiling | offline | error. */
  status: "ready" | "compiling" | "offline" | "error";
  body: PingBody | null;
}

/** The full status JSON payload the CLI emits. */
export interface StatusJson {
  command: "status";
  projectPath: string;
  isGodotProject: boolean;
  addon: StatusAddon;
  instance: StatusInstance | null;
  port: number;
  baseUrl: string;
  authTokenDiscovered: boolean;
  ping: StatusPing;
  /** Coarse status token aligned with `godot_open_mcp_bridge_status`. */
  status: BridgeStatus;
  /** True when `status === "running"`. */
  ready: boolean;
}

// ---------------------------------------------------------------------------
// Public entry point
// ---------------------------------------------------------------------------

/**
 * Assemble the status payload for a project. Performs the I/O (lock read, addon
 * check, single `/ping` probe) and folds the results into the coarse `status`
 * token. Never throws — every signal degrades gracefully so a status probe
 * always returns a payload (the `status` field carries the verdict).
 *
 * The caller (the command wrapper) maps this to a `CliCommandResult` with the
 * right exit code: `0` when `status === "running"`, `1` otherwise.
 */
export async function assembleStatus(
  input: StatusProbeInput,
): Promise<StatusJson> {
  const projectPath = path.resolve(input.projectPath);
  const isGodot = isGodotProjectRoot(projectPath);

  // Addon presence + plugin-enabled check.
  const addon = inspectAddon(projectPath);

  // Instance lock + classification.
  const lock = readInstanceLock(projectPath);
  const classification = classifyInstance(lock);
  const instance = lock
    ? {
        port: lock.port,
        pid: lock.pid,
        classification,
        lockPath: lockPath(projectPath),
        heartbeatAgeMs: heartbeatAgeMs(lock),
        state: lock.state,
        isCompiling: lock.isCompiling,
        isPlaying: lock.isPlaying,
      }
    : null;

  // Resolved port + auth token (env override > live lock > deterministic hash).
  const port = resolvePort(projectPath, input.portOverride);
  const baseUrl = `http://127.0.0.1:${port}`;
  const authToken = resolveAuthToken(projectPath, input.portOverride);

  // Single /ping probe. The CLI has no MCP client in process, so it hits the
  // bridge endpoint directly with the discovered bearer token attached.
  const probe = await singlePing(
    { port, baseUrl, authToken },
    fetch,
    input.fetchTimeoutMs ?? PING_FETCH_TIMEOUT_MS,
  );
  const ping: StatusPing = {
    ok: probe.status === "ready",
    status: probe.status,
    body: probe.body,
  };

  // Fold the signals into the coarse status token. The lockPidAlive flag is the
  // only disambiguator between `unreachable` (Godot up, listener momentarily
  // down — retry) and `stopped` (clean stop, nothing there).
  const lockPidAlive = lock ? isPidAlive(lock.pid) : false;
  const status = deriveStatus({
    classification,
    pingReachable: probe.status === "ready" || probe.status === "compiling",
    pingCompiling: probe.status === "compiling",
    pingConnected: probe.status === "ready",
    lockPidAlive,
  });

  return {
    command: "status",
    projectPath,
    isGodotProject: isGodot,
    addon,
    instance,
    port,
    baseUrl,
    authTokenDiscovered: authToken !== undefined,
    ping,
    status,
    ready: status === "running",
  };
}

// ---------------------------------------------------------------------------
// Pure status derivation — mirror of mcp-server deriveBridgeStatus
// ---------------------------------------------------------------------------

/** Inputs to {@link deriveStatus} (already-reduced signals; no I/O). */
export interface DeriveStatusInput {
  classification: InstanceClassification;
  pingReachable: boolean;
  pingCompiling: boolean;
  pingConnected: boolean;
  lockPidAlive: boolean;
}

/**
 * Derive the coarse status token. Pure — no I/O, no time reads (the
 * classification already absorbed the heartbeat-age check).
 *
 * Mirrors `deriveBridgeStatus` in `mcp-server/src/tools/bridge-status-derive.ts`
 * — keep the two in lockstep. See the file header for the mapping table.
 */
export function deriveStatus(input: DeriveStatusInput): BridgeStatus {
  const { classification, pingReachable, pingCompiling, pingConnected, lockPidAlive } = input;

  if (classification === "dead_bridge") {
    return "dead_bridge";
  }
  if (pingReachable && pingCompiling) {
    return "compiling";
  }
  if (pingReachable && pingConnected) {
    return "running";
  }
  if (!pingReachable && lockPidAlive) {
    return "unreachable";
  }
  return "stopped";
}

// ---------------------------------------------------------------------------
// Addon presence + plugin-enabled check
// ---------------------------------------------------------------------------

/**
 * Check whether the addon is installed and enabled in `project.godot`. Never
 * throws — a missing/unreadable `project.godot` reports `present/enabled:
 * false`. The `pluginPath` field is always populated with the canonical path so
 * the JSON shape is stable.
 */
export function inspectAddon(projectPath: string): StatusAddon {
  const addonCfg = path.join(projectPath, "addons", "godot_open_mcp", "plugin.cfg");
  const present = fs.existsSync(addonCfg);

  let enabled = false;
  if (present) {
    try {
      const manifest = projectGodotPath(projectPath);
      if (fs.existsSync(manifest)) {
        const text = fs.readFileSync(manifest, "utf8");
        enabled = parseEnabledPlugins(text).includes(GODOT_OPEN_MCP_PLUGIN_PATH);
      }
    } catch {
      enabled = false;
    }
  }

  return { present, enabled, pluginPath: GODOT_OPEN_MCP_PLUGIN_PATH };
}
