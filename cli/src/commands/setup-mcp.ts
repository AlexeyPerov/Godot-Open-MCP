// CLI command wrapper for `setup-mcp`.
//
// Thin adapter over the library-safe `setupMcp()` (src/lib/setup-mcp.ts):
// resolves argv into options, runs the writer, and maps the result union into a
// `CliCommandResult` the dispatcher prints + exits on. Also owns the `--list`
// surface (prints the agent registry as a small table, exits 0).
//
// Exit codes: 0 on success (including `changed: false`), 1 on failure — matches
// the P6 shared invariants.

import { EXIT } from "../exit-codes.js";
import { setupMcp, MCP_SERVER_NAME, getAgentIds } from "../lib/setup-mcp.js";
import type {
  SetupMcpFailure,
  SetupMcpResult,
  SetupMcpSuccess,
} from "../lib/types.js";
import type { CliCommandResult } from "../commands.js";

export interface SetupMcpCliInput {
  /** Agent id (`cursor`, `claude-desktop`, …) — required unless `list` is set. */
  agentId?: string;
  /** Resolved project root (positional `path` / `--project` / cwd). */
  projectPath: string;
  /** `--use-local` — spawn the monorepo build instead of `npx`. */
  useLocal?: boolean;
  /** `--list` — print the agent registry and exit 0. */
  list?: boolean;
  /** `--config-path <path>` override. */
  configPath?: string;
  /** Package version, used to pin the npx args. */
  packageVersion?: string;
}

/**
 * Run the `setup-mcp` command. Returns a `CliCommandResult`; the dispatcher
 * prints the JSON or human summary and exits with `result.exitCode`.
 */
export async function setupMcpCommand(
  input: SetupMcpCliInput,
): Promise<CliCommandResult> {
  // --list short-circuits with the agent registry table.
  if (input.list) {
    return listResult();
  }

  if (!input.agentId || input.agentId.length === 0) {
    const available = getAgentIds().join(", ");
    const payload = {
      command: "setup-mcp",
      error: {
        code: "missing_agent_id",
        message: `agent-id is required. Available: ${available}.`,
        available: getAgentIds(),
      },
    };
    return {
      exitCode: EXIT.ERRORS,
      json: payload,
      human: `agent-id is required.\nAvailable agents: ${available}\n`,
      errorLabel: "missing_agent_id",
    };
  }

  const result = await setupMcp({
    agentId: input.agentId,
    godotProjectPath: input.projectPath,
    useLocal: input.useLocal,
    configPath: input.configPath,
    packageVersion: input.packageVersion,
  });

  return result.kind === "success" ? successResult(result) : failureResult(result);
}

/** Build the `--list` `CliCommandResult`. */
function listResult(): CliCommandResult {
  const ids = getAgentIds();
  const lines: string[] = ["Available MCP client agents:", ""];
  for (const id of ids) {
    lines.push(`  ${id}`);
  }
  lines.push("", `Run \`setup-mcp <agent-id> [path]\` to write a stdio config.`);

  return {
    exitCode: EXIT.SUCCESS,
    json: { command: "setup-mcp", list: true, agents: ids },
    human: lines.join("\n"),
  };
}

/** Build the success `CliCommandResult` (human + JSON). */
function successResult(result: SetupMcpSuccess): CliCommandResult {
  const json = {
    command: "setup-mcp",
    changed: result.changed,
    agentId: result.agentId,
    configPath: result.configPath,
    serverName: result.serverName,
    transport: result.transport,
    stdio: result.stdio,
    warnings: result.warnings,
  };

  const headline = result.changed
    ? `Wrote stdio MCP config for ${result.agentId}.`
    : `Stdio MCP config for ${result.agentId} was already up to date.`;

  const lines: string[] = [
    headline,
    `Config file: ${result.configPath}`,
    `Server name: ${result.serverName}`,
    `Transport:   ${result.transport}`,
    `Command:     ${result.stdio.command} ${result.stdio.args.join(" ")}`,
    `Env:         GODOT_PROJECT_PATH=${result.stdio.env.GODOT_PROJECT_PATH}`,
  ];
  for (const warning of result.warnings) {
    lines.push("", `Warning: ${warning}`);
  }

  return {
    exitCode: EXIT.SUCCESS,
    json,
    human: lines.join("\n"),
  };
}

/** Build the failure `CliCommandResult` (human + JSON + errorLabel). */
function failureResult(result: SetupMcpFailure): CliCommandResult {
  const json = {
    command: "setup-mcp",
    changed: false,
    error: {
      code: result.errorLabel,
      message: result.error.message,
    },
    warnings: result.warnings,
  };

  const lines: string[] = [`Failed to configure MCP client: ${result.error.message}`];
  for (const warning of result.warnings) {
    lines.push("", `Warning: ${warning}`);
  }

  return {
    exitCode: EXIT.ERRORS,
    json,
    human: lines.join("\n"),
    errorLabel: result.errorLabel,
  };
}

// Re-export so the dispatcher + tests can reference the canonical server name.
export { MCP_SERVER_NAME };
export type { SetupMcpResult };
