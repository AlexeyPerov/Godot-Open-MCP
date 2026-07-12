// Pure status mapping for `godot_open_mcp_bridge_status` (P5.3).
//
// This module holds the NETWORK-FREE part of the tool: the coarse `status`
// token derivation + the prose/recovery helpers. Everything here is a pure
// function over already-collected signals (instance-lock classification, a
// ping probe result, the lock snapshot) so the full status matrix can be
// unit-tested without a bridge stub or a Godot editor. The I/O composition
// (read the lock, run the /ping probe) lives in `LiveClient.routeBridgeStatus`.
//
// Adapted from Unity Open MCP's tool-router.ts helpers (copy fidelity):
//   - status vocabulary `running | compiling | stopped | unreachable | dead_bridge`
//   - `recoveryHint` shape `{ tool, reason } | null`, non-null only for
//     `dead_bridge`
//   - `nextStep` operator-facing prose per status
// Only the recovery *tool id* and the Godot-specific wording change (no Unity
// Safe Mode cold-start story; the offline compile-errors reader is a later
// phase — see INTENTIONAL_DELTAS below).
//
// ── INTENTIONAL_DELTAS (from Unity) ──────────────────────────────────────────
// 1. No `findUnityForProject` cold-Safe-Mode branch. Unity folds "no lock +
//    unreachable + a live Unity process for this project" into `dead_bridge`
//    (cold Safe Mode). Godot has no equivalent out-of-band process scan, and
//    the plan explicitly defers it. That case stays `stopped` here.
// 2. Recovery tool today is `godot_open_mcp_console_get_logs` (the closest
//    registered diagnostic), NOT Unity's offline
//    `unity_open_mcp_read_compile_errors`. A dedicated offline
//    `godot_open_mcp_read_compile_errors` arrives in a later phase; until then
//    the hint carries a `note` documenting the gap so it is never misleading.
// 3. No Unity Safe Mode language — `dead_bridge` reads as "Godot process
//    alive, bridge heartbeat stale / plugin failed to load".
// ─────────────────────────────────────────────────────────────────────────────

import type { InstanceClassification, InstanceLock } from "../instance-discovery.js";

/**
 * The five coarse status tokens `bridge_status` returns. Copied verbatim from
 * Unity so an operator (or the future Validation Suite) branches on the same
 * vocabulary across both projects.
 *
 *   - `running`     — bridge connected and idle; live-only tools are usable.
 *   - `compiling`   — bridge connected but the editor reports a rebuild in
 *                     flight; retry shortly.
 *   - `stopped`     — no live listener: Godot is not running, OR the bridge
 *                     addon is disabled / not started. (Folds two cases the
 *                     MCP server cannot distinguish without an out-of-band
 *                     process scan — see the `instance` sub-object to
 *                     disambiguate: `lock === null` → Godot likely down.)
 *   - `unreachable` — Godot process alive (lock PID live) but `/ping` did not
 *                     respond. Usually a transient editor reload window; retry.
 *   - `dead_bridge` — Godot process alive BUT the bridge heartbeat is stale.
 *                     The bridge addon is not running its listener (failed to
 *                     load / plugin disabled mid-session); `/ping` will not
 *                     recover on its own. See `recoveryHint`.
 */
export type BridgeStatus = "running" | "compiling" | "stopped" | "dead_bridge" | "unreachable";

/** Ordered list of every status token (used by tests / validation). */
export const BRIDGE_STATUSES: readonly BridgeStatus[] = [
  "running",
  "compiling",
  "stopped",
  "dead_bridge",
  "unreachable",
];

/**
 * The normalized `/ping` probe result fed into the mapper. The composition
 * layer (`LiveClient.routeBridgeStatus`) reduces the raw CallToolResult to
 * this shape so the mapper stays network-free.
 */
export interface PingProbe {
  /** True when `/ping` returned a parseable body (HTTP 200 or the 503 fallback). */
  reachable: boolean;
  /** Body fields when reachable; all `null` when not. */
  connected: boolean | null;
  compiling: boolean | null;
  isPlaying: boolean | null;
  godotVersion: string | null;
  bridgeVersion: string | null;
  mode: string | null;
}

/**
 * Inputs to {@link deriveBridgeStatus}. The composition layer collects these
 * from the instance lock + one `/ping` probe; the mapper is a pure function
 * over them.
 */
export interface BridgeStatusInput {
  /** `classifyInstance(lock, now)` — the lock-derived health classification. */
  classification: InstanceClassification;
  /** The normalized `/ping` probe (reachable + body fields). */
  ping: PingProbe;
  /**
   * True when a live lock was read AND its `pid` is still alive
   * (`isPidAlive(lock.pid)`). Used to distinguish a transient
   * `unreachable` (Godot up, listener momentarily down) from a clean
   * `stopped` (nothing there).
   */
  lockPidAlive: boolean;
}

/** Structured recovery hint — non-null only for `dead_bridge`. */
export interface BridgeRecoveryHint {
  /** The tool an agent/operator should call next to diagnose/recover. */
  tool: string;
  /** Why that tool — one short sentence. */
  reason: string;
  /**
   * Optional caveat when the named tool is not the ideal one (e.g. a planned
   * offline reader is not shipped yet). Omitted when the tool is the
   * authoritative recovery path.
   */
  note?: string;
}

/**
 * Derive the coarse `status` token from the collected signals. Pure — no I/O,
 * no time reads (the classification already absorbed the heartbeat-age check).
 *
 * Mapping (authoritative for P5.3):
 *
 * | classification | ping              | lockPidAlive | status        |
 * |---|---|---|---|
 * | `dead_bridge`  | any               | any          | `dead_bridge` |
 * | any            | reachable+compiling | any        | `compiling`   |
 * | any            | reachable+connected | any        | `running`     |
 * | any            | NOT reachable     | true         | `unreachable` |
 * | any            | NOT reachable     | false        | `stopped`     |
 *
 * `dead_bridge` wins outright: a stale heartbeat means the listener will not
 * recover, so even a coincidentally-reachable ping should not mask it. The
 * `compiling` / `running` branches require the ping to be reachable. The
 * `unreachable` vs `stopped` split uses `lockPidAlive`: a live Godot process
 * with a dead listener is a transient reload window (retry), whereas no live
 * PID is a clean stop.
 */
export function deriveBridgeStatus(input: BridgeStatusInput): BridgeStatus {
  const { classification, ping, lockPidAlive } = input;

  if (classification === "dead_bridge") {
    return "dead_bridge";
  }
  if (ping.reachable && ping.compiling === true) {
    return "compiling";
  }
  if (ping.reachable && ping.connected === true) {
    return "running";
  }
  if (!ping.reachable && lockPidAlive) {
    return "unreachable";
  }
  return "stopped";
}

/**
 * Structured recovery hint for a coarse status. `null` unless the status has
 * a specific recovery tool to call. Today only `dead_bridge` carries a hint;
 * the shape is extensible so future failure modes can add their own.
 *
 * Per the P5.3 risk safeguard ("only emit a hint when the tool is actually
 * registered; else null + document the gap"), the `dead_bridge` hint names
 * `godot_open_mcp_console_get_logs` — the closest registered diagnostic — and
 * carries a `note` that (a) it is the bridge-fed addon collector so it may be
 * empty while the bridge is dead, and (b) a dedicated offline
 * `godot_open_mcp_read_compile_errors` is planned. When that offline reader
 * ships, swap the `tool` + drop the `note`.
 */
export function bridgeStatusRecoveryHint(status: BridgeStatus): BridgeRecoveryHint | null {
  if (status === "dead_bridge") {
    return {
      tool: "godot_open_mcp_console_get_logs",
      reason:
        "Godot is running but the bridge listener is not recovering (stale " +
        "heartbeat / plugin failed to load). Recent addon-captured logs may " +
        "show the failure that preceded the stale heartbeat.",
      note:
        "console_get_logs reads the bridge-fed addon collector, which stops " +
        "accumulating once the bridge is dead — recent pre-failure entries " +
        "may still be visible. A dedicated offline " +
        "godot_open_mcp_read_compile_errors tool is planned for a later " +
        "phase; until then, inspect the Godot editor Output / file logs " +
        "directly for compile or plugin-load errors.",
    };
  }
  return null;
}

/**
 * Operator-facing "what to do next" prose for each status. Kept short and
 * action-oriented. Adapted from Unity's `bridgeStatusNextStep` (copy
 * fidelity for the structure) with Godot-specific recovery wording (no Unity
 * Safe Mode; Godot menu path for the addon).
 */
export function bridgeStatusNextStep(status: BridgeStatus): string {
  switch (status) {
    case "running":
      return "Bridge is ready. Proceed with live-only MCP tools.";
    case "compiling":
      return "Godot is compiling / reloading. Wait for the bridge to return to idle, or poll godot_open_mcp_bridge_status again.";
    case "stopped":
      return "Bridge listener is not reachable. Ensure the Godot Editor is open with the Godot Open MCP addon enabled (Project menu > Project Settings > Plugins, or the editor's addon toggle), then call godot_open_mcp_bridge_status again to confirm. If Godot is not running, launch it with the project loaded.";
    case "unreachable":
      return "Bridge listener is not responding but Godot is running — likely a transient editor-reload window (the bridge tears down its HTTP socket during reloads). Wait a moment and call godot_open_mcp_bridge_status again; if it persists, check the addon is still enabled.";
    case "dead_bridge":
      return "Godot is running but the bridge heartbeat is stale — the addon is not running its HTTP listener (it failed to load or was disabled mid-session), so /ping will not recover on its own. Check the Godot editor Output panel for compile or plugin-load errors, re-enable the addon if it was disabled, then call godot_open_mcp_bridge_status again. Recent addon-captured logs (godot_open_mcp_console_get_logs) may show the failure.";
  }
}

/**
 * Compact lock summary for the `bridge_status` response — the pid/port/state
 * metadata an operator needs to disambiguate `stopped` (Godot down vs addon
 * off) and confirm a `dead_bridge` diagnosis. Mirrors the field set the CLI
 * `status` command will surface (Phase 6). Returns `null` when no lock was
 * read so the response shape is explicit about "no instance known".
 */
export function summarizeBridgeStatusLock(lock: InstanceLock): {
  pid: number;
  port: number;
  state: InstanceLock["state"];
  isCompiling: boolean;
  isPlaying: boolean;
  heartbeatAt: string;
  bridgeVersion: string;
  godotVersion: string;
} {
  return {
    pid: lock.pid,
    port: lock.port,
    state: lock.state,
    isCompiling: lock.isCompiling,
    isPlaying: lock.isPlaying,
    heartbeatAt: lock.heartbeatAt,
    bridgeVersion: lock.bridgeVersion,
    godotVersion: lock.godotVersion,
  };
}
