// CLI command result + help/version text.
//
// Adapted from Unity Open MCP's `mcp-server/src/cli/commands.ts`. Each command
// (once implemented) is a plain async function returning a CliCommandResult;
// the dispatcher (cli.ts) owns stdout/stderr/exit-code so commands stay pure
// and unit-testable.
//
// P6.1 ships only the help/version text and the CliCommandResult contract — no
// command handlers yet. Later P6 plans append handlers and register their
// names in args.ts KNOWN_COMMANDS.

import { EXIT } from "./exit-codes.js";
import {
  PROJECT_PATH_ENV_VAR,
  PORT_OVERRIDE_ENV_VAR,
  DEFAULT_BIN_NAME,
} from "./env.js";

export interface CliCommandResult {
  /** Process exit code. 0 = success, non-zero = failure. */
  exitCode: number;
  /** JSON-serializable payload. Always populated — `--json` prints it verbatim. */
  json: unknown;
  /** Human-readable multi-line summary; printed when --json is NOT set. */
  human: string;
  /** Optional structured error label (e.g. "unknown_command") surfaced in JSON. */
  errorLabel?: string;
}

/**
 * Build an unknown-command CliCommandResult. Centralized so the dispatcher and
 * the parser agree on the JSON shape.
 */
export function unknownCommandResult(
  command: string,
  known: readonly string[],
  json: boolean,
): CliCommandResult {
  const available =
    known.length > 0 ? known.join(", ") : "(none yet — see --help)";
  const payload = {
    command: null,
    error: {
      code: "unknown_command",
      message: `Unknown command '${command}'.`,
      available: known,
    },
  };
  return {
    exitCode: EXIT.ERRORS,
    json: payload,
    human:
      `Unknown command '${command}'.\n` +
      `Available commands: ${available}\n` +
      (json ? "" : `Run '${DEFAULT_BIN_NAME} --help' for usage.\n`),
    errorLabel: "unknown_command",
  };
}

export function helpText(binName: string): string {
  return [
    `Usage: ${binName} <command> [options]`,
    "",
    "Command-line tooling for Godot Open MCP — install the addon, configure",
    "MCP clients, launch the editor, and probe bridge readiness.",
    "",
    "Commands:",
    "  --help, -h                    Show this help.",
    "  --version, -V                 Print the package version.",
    "",
    "  install-plugin [path]         Install the Godot Open MCP addon into a project.",
    "  setup-mcp <agent-id> [path]   Write a stdio MCP client config (Cursor, Claude, …).",
    "  open [path]                   Launch the Godot editor for a project.",
    "  wait-for-ready [path]         Poll until the bridge is ready; exit 0/non-zero.",
    "  ping [path]                   One-shot bridge /ping probe (no wait loop).",
    "  status [path]                 Show resolved bridge port, instance lock, readiness.",
    "  configure [path]              Read/write Godot Open MCP project settings (.godot-open-mcp/settings.json).",
    "",
    "Exit codes:",
    "  0  success        command completed.",
    "  1  errors         unknown command, bad arguments, or command failed.",
    "  3  timeout        the bridge never became reachable, or a call timed out.",
    "",
    "Options:",
    "  --json                        Emit JSON instead of human-readable output (all commands).",
    `  --project <path>, -P <path>   Godot project path (default: ${PROJECT_PATH_ENV_VAR}).`,
    `  --port <n>, -p <n>            Bridge port override (default: ${PORT_OVERRIDE_ENV_VAR}).`,
    "  --source <dir>                install-plugin: local addon root to copy from.",
    "  --list                        setup-mcp: list available agent IDs and exit.",
    "  --use-local                   setup-mcp: spawn the monorepo build instead of npx.",
    "  --config-path <path>          setup-mcp: override the agent's default config file path.",
    "  --editor-path <bin>           open: explicit Godot editor binary (skips discovery).",
    "  --no-build                    open: skip the pre-open dotnet build for C# projects.",
    "  --build-configuration <cfg>   open: MSBuild configuration (default: Debug).",
    "  --wait                        open: chain into wait-for-ready after launch.",
    "  --timeout-ms <n>              wait-for-ready overall timeout in ms.",
    "  --interval-ms <n>             wait-for-ready poll interval in ms.",
    "  --list                        configure: list current project settings and exit.",
    "  --get <key>                   configure: print one setting value (authMode, bindAddress).",
    "  --set <key=value>             configure: write a setting (repeatable: --set authMode=required --set bindAddress=0.0.0.0).",
    "",
    "Environment:",
    `  ${PROJECT_PATH_ENV_VAR.padEnd(30)}Project root the bridge / MCP server operate on.`,
    `  ${PORT_OVERRIDE_ENV_VAR.padEnd(30)}Optional bridge port override.`,
    "",
    "Examples:",
    `  ${binName} --help`,
    `  ${binName} --version`,
    `  ${binName} install-plugin --json`,
    `  ${binName} setup-mcp --list`,
    `  ${binName} setup-mcp cursor /path/to/project --json`,
    `  ${binName} open /path/to/project --editor-path /usr/local/bin/godot`,
    `  ${binName} open /path/to/project --wait   # launch then wait for ready`,
    `  ${binName} wait-for-ready /path/to/project --timeout-ms 30000`,
    `  ${binName} ping /path/to/project --json`,
    `  ${binName} status /path/to/project --json`,
    `  ${binName} configure /path/to/project --list`,
    `  ${binName} configure /path/to/project --set authMode=required`,
  ].join("\n");
}

export function versionText(version: string): string {
  return `godot-open-mcp-cli ${version}`;
}
