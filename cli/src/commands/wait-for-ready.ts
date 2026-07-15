// CLI command wrapper for `wait-for-ready`.
//
// Thin adapter over the poller (src/ping-poller.ts): resolves the bridge probe
// target (port from env / lock / deterministic hash), runs the poll loop, and
// maps the `PollOutcome` into a `CliCommandResult`.
//
// Exit codes: 0 when ready; 3 on timeout; 1 on a hard failure (dead_bridge is a
// fatal-but-distinct case — mapped to ERRORS so an operator sees the compile-
// errors hint rather than a generic timeout). Adapted from Unity's
// `runWaitForReadyCommand` (commands.ts) with the probe target swapped from the
// MCP-server LiveClient to a direct bridge HTTP fetch.

import { EXIT } from "../exit-codes.js";
import {
  pollUntilReady,
  singlePing,
  DEFAULT_WAIT_TIMEOUT_MS,
  DEFAULT_POLL_INTERVAL_MS,
  PING_FETCH_TIMEOUT_MS,
  type PollOutcome,
  type ProbeTarget,
} from "../ping-poller.js";
import {
  resolvePort,
  resolveAuthToken,
} from "../instance-discovery.js";
import type { CliCommandResult } from "../commands.js";

export interface WaitForReadyCliInput {
  /** Resolved project root (positional `path` / `--project` / cwd). */
  projectPath: string;
  /** Resolved bridge port override. */
  port?: number;
  /** Overall timeout (ms). Defaults to DEFAULT_WAIT_TIMEOUT_MS. */
  timeoutMs?: number;
  /** Poll interval (ms). Defaults to DEFAULT_POLL_INTERVAL_MS. */
  intervalMs?: number;
}

/**
 * Resolve the bridge probe target for a project: port (env override > live lock
 * > deterministic hash) + the bearer token from the live lock (when present).
 * Centralized so the standalone command and the `open --wait` chain build the
 * same target.
 */
export function resolveProbeTarget(
  projectPath: string,
  portOverride?: number,
): ProbeTarget {
  const port = resolvePort(projectPath, portOverride);
  const authToken = resolveAuthToken(projectPath, portOverride);
  return {
    port,
    baseUrl: `http://127.0.0.1:${port}`,
    authToken,
  };
}

/**
 * Run the `wait-for-ready` command. Returns a `CliCommandResult`; the
 * dispatcher prints the JSON or human summary and exits with
 * `result.exitCode`.
 */
export async function waitForReadyCommand(
  input: WaitForReadyCliInput,
): Promise<CliCommandResult> {
  const outcome = await waitForReadyInternal(input);
  return outcome;
}

/**
 * Internal entry point shared by the standalone command and the `open --wait`
 * chain. Resolves the probe target, runs the poll loop, and maps the outcome to
 * a `CliCommandResult`.
 */
export async function waitForReadyInternal(
  input: WaitForReadyCliInput,
): Promise<CliCommandResult> {
  const target = resolveProbeTarget(input.projectPath, input.port);
  const timeoutMs = input.timeoutMs ?? DEFAULT_WAIT_TIMEOUT_MS;
  const intervalMs = input.intervalMs ?? DEFAULT_POLL_INTERVAL_MS;

  const outcome: PollOutcome = await pollUntilReady(
    target,
    input.projectPath,
    (t) => singlePing(t, fetch, PING_FETCH_TIMEOUT_MS),
    { timeoutMs, intervalMs },
  );

  return outcomeToResult(target, outcome);
}

/** Map a PollOutcome to a CliCommandResult with the right exit code. */
function outcomeToResult(
  target: ProbeTarget,
  outcome: PollOutcome,
): CliCommandResult {
  const json = {
    command: "wait-for-ready",
    ready: outcome.ready,
    status: outcome.status,
    elapsedMs: outcome.elapsedMs,
    port: target.port,
    baseUrl: target.baseUrl,
    reason: outcome.reason,
    lastPing: outcome.lastPing,
  };

  if (outcome.ready) {
    return {
      exitCode: EXIT.SUCCESS,
      json,
      human: formatReadyHuman(target, outcome),
    };
  }

  // dead_bridge is a fatal-but-distinct case: the bridge will not recover until
  // the C# errors are fixed. Map to ERRORS (1) so an operator sees the hint
  // rather than a generic timeout (3).
  const exitCode =
    outcome.status === "timeout" ? EXIT.TIMEOUT : EXIT.ERRORS;

  return {
    exitCode,
    json,
    human: outcome.reason,
    errorLabel: outcome.status,
  };
}

/** Human-readable summary for a ready outcome. */
function formatReadyHuman(
  target: ProbeTarget,
  outcome: PollOutcome,
): string {
  const lines: string[] = [
    `Bridge is ready (connected, idle).`,
    `Bridge:   ${target.baseUrl}`,
    `Elapsed:  ${(outcome.elapsedMs / 1000).toFixed(1)}s`,
  ];
  const body = outcome.lastPing;
  if (body) {
    if (body.godotVersion) lines.push(`Godot:    ${body.godotVersion}`);
    if (body.bridgeVersion) lines.push(`Bridge ver: ${body.bridgeVersion}`);
    if (body.projectPath) lines.push(`Project:  ${body.projectPath}`);
  }
  return lines.join("\n");
}
