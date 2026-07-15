// CLI command wrapper for `ping`.
//
// A single bridge `/ping` probe — no poll loop. Useful for scripts that want a
// one-shot readiness check without the wait semantics. Shares the poller's
// `singlePing` + the same probe-target resolution as wait-for-ready.
//
// Exit codes: 0 when the probe reports ready; 1 otherwise (compiling / offline /
// error). Adapted from Unity's `runPingCommand` (commands.ts).

import { EXIT } from "../exit-codes.js";
import {
  singlePing,
  PING_FETCH_TIMEOUT_MS,
  type PingBody,
} from "../ping-poller.js";
import { resolveProbeTarget } from "./wait-for-ready.js";
import type { CliCommandResult } from "../commands.js";

export interface PingCliInput {
  /** Resolved project root (positional `path` / `--project` / cwd). */
  projectPath: string;
  /** Resolved bridge port override. */
  port?: number;
}

/**
 * Run the `ping` command. Returns a `CliCommandResult`; the dispatcher prints
 * the JSON or human summary and exits with `result.exitCode`.
 */
export async function pingCommand(
  input: PingCliInput,
): Promise<CliCommandResult> {
  const target = resolveProbeTarget(input.projectPath, input.port);
  const poll = await singlePing(target, fetch, PING_FETCH_TIMEOUT_MS);

  const json = {
    command: "ping",
    port: target.port,
    baseUrl: target.baseUrl,
    status: poll.status,
    ready: poll.status === "ready",
    body: poll.body,
  };

  if (poll.status === "ready" && poll.body) {
    return {
      exitCode: EXIT.SUCCESS,
      json,
      human: formatPingHuman(target, poll.body),
    };
  }

  const reason =
    poll.status === "compiling"
      ? "Bridge is reachable but Godot is compiling."
      : poll.status === "offline"
        ? `Bridge is not reachable at ${target.baseUrl}.`
        : `Ping failed (${poll.status}).`;
  return {
    exitCode: EXIT.ERRORS,
    json,
    human: reason,
    errorLabel: poll.status,
  };
}

/** Human-readable summary for a ready ping body. */
function formatPingHuman(
  target: { baseUrl: string },
  body: PingBody,
): string {
  const lines: string[] = [
    `Bridge:   ${target.baseUrl}`,
    `connected: ${body.connected ?? "unknown"}`,
    `compiling: ${body.compiling ?? "unknown"}`,
    `isPlaying: ${body.isPlaying ?? "unknown"}`,
  ];
  if (body.godotVersion) lines.push(`Godot:    ${body.godotVersion}`);
  if (body.bridgeVersion) lines.push(`Bridge ver: ${body.bridgeVersion}`);
  if (body.projectPath) lines.push(`Project:  ${body.projectPath}`);
  if (body.mode) lines.push(`mode:     ${body.mode}`);
  return lines.join("\n");
}
