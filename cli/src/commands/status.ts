// CLI command wrapper for `status`.
//
// Thin adapter over the library-safe `assembleStatus()` (src/lib/status.ts):
// resolves argv into options, runs the assembly, and maps the resulting
// `StatusJson` into a `CliCommandResult` with the right exit code.
//
// Exit codes: 0 when the bridge is `running` (connected + idle); 1 otherwise
// (compiling / stopped / unreachable / dead_bridge). The single short status
// probe does NOT distinguish a timeout from a hard failure — that distinction
// is reserved for `wait-for-ready` (exit 3). For a one-shot status probe, `1`
// is the conventional "not ready" code that shells and CI expect.

import { EXIT } from "../exit-codes.js";
import { assembleStatus, type StatusJson, type BridgeStatus } from "../lib/status.js";
import type { CliCommandResult } from "../commands.js";

export interface StatusCliInput {
  /** Resolved project root (positional `path` / `--project` / cwd). */
  projectPath: string;
  /** Resolved bridge port override. */
  port?: number;
}

/**
 * Run the `status` command. Returns a `CliCommandResult`; the dispatcher prints
 * the JSON or human summary and exits with `result.exitCode`.
 */
export async function statusCommand(
  input: StatusCliInput,
): Promise<CliCommandResult> {
  const json = await assembleStatus({
    projectPath: input.projectPath,
    portOverride: input.port,
  });

  if (json.status === "running") {
    return {
      exitCode: EXIT.SUCCESS,
      json,
      human: formatStatusHuman(json),
    };
  }

  return {
    exitCode: EXIT.ERRORS,
    json,
    human: formatStatusHuman(json),
    errorLabel: json.status,
  };
}

// ---------------------------------------------------------------------------
// Human-readable formatting
// ---------------------------------------------------------------------------

/**
 * Build the multi-line human summary. Organized so the coarse `status` token
 * is the headline and the supporting signals (addon, instance, ping, hints)
 * follow. The recovery hint lines are kept short and action-oriented.
 */
function formatStatusHuman(s: StatusJson): string {
  const lines: string[] = [];
  lines.push(`Status:   ${s.status}${s.ready ? " (ready)" : ""}`);
  lines.push(`Project:  ${s.projectPath}${s.isGodotProject ? "" : " (not a Godot project)"}`);
  lines.push(`Bridge:   ${s.baseUrl}`);
  lines.push(`Auth:     ${s.authTokenDiscovered ? "token discovered (sent as Bearer)" : "no token (authMode must be 'none')"}`);

  // Addon presence + plugin-enabled state.
  const addonState = !s.addon.present
    ? "not installed"
    : s.addon.enabled
      ? "installed + enabled"
      : "installed but NOT enabled";
  lines.push(`Addon:    ${addonState}`);

  // Instance lock summary.
  if (s.instance) {
    lines.push(
      `Instance: ${s.instance.classification} (pid ${s.instance.pid}, state ${s.instance.state ?? "?"}, heartbeat ${(s.instance.heartbeatAgeMs / 1000).toFixed(1)}s ago)`,
    );
  } else {
    lines.push(`Instance: gone (no instance lock)`);
  }

  // Ping summary — reachable status + body fields when present.
  lines.push(`Ping:     ${s.ping.status}${s.ping.ok ? " (ready)" : ""}`);
  if (s.ping.body) {
    const b = s.ping.body;
    if (b.godotVersion) lines.push(`Godot:    ${b.godotVersion}`);
    if (b.bridgeVersion) lines.push(`Bridge ver: ${b.bridgeVersion}`);
    if (b.compiling) lines.push(`State:    compiling`);
    if (b.isPlaying) lines.push(`Playmode: playing`);
  }

  // Action-oriented hint for non-running states.
  const hint = statusHint(s.status);
  if (hint) {
    lines.push("", hint);
  }

  return lines.join("\n");
}

/**
 * Short, action-oriented hint for a non-running status. Mirrors the MCP
 * `bridge_status` `nextStep` prose (mcp-server/src/tools/bridge-status-derive.ts
 * `bridgeStatusNextStep`) trimmed for the CLI's narrower context. Kept here so
 * the human output ends with a "what to do next" line an operator can follow.
 */
function statusHint(status: BridgeStatus): string | null {
  switch (status) {
    case "running":
      return null;
    case "compiling":
      return "Godot is compiling / reloading. Wait for the bridge to return to idle, then re-run status.";
    case "stopped":
      return "Bridge listener is not reachable. Open the Godot Editor with the Godot Open MCP addon enabled (Project menu > Project Settings > Plugins), or launch it with `godot-open-mcp-cli open`, then re-run status.";
    case "unreachable":
      return "Bridge listener is not responding but Godot is running — likely a transient editor-reload window. Wait a moment and re-run status; if it persists, check the addon is still enabled.";
    case "dead_bridge":
      return "Godot is running but the bridge heartbeat is stale — the addon is not running its HTTP listener (it failed to load or was disabled mid-session). Check the Godot editor Output panel for compile or plugin-load errors, re-enable the addon, then re-run status.";
  }
}
