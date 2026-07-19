// Thin CLI help + version text.
//
// Adapted from Unity Open MCP's `mcp-server/src/cli/help-text.ts`
// (copy/adapt fidelity). Godot ships a focused CLI surface: `run-tool`
// plus `--help` / `--version`. When invoked with no recognized command,
// the same `bin` falls through to the stdio MCP server (MCP client mode).

export const PROJECT_PATH_ENV_VAR = "GODOT_PROJECT_PATH";
export const PORT_ENV_VAR = "GODOT_OPEN_MCP_BRIDGE_PORT";

export function helpText(binName: string): string {
  return [
    `Usage: ${binName} <command> [options]`,
    "",
    "Thin CLI for Godot Open MCP — wraps the MCP server for CI / scripting and",
    "for the Validation Suite's mcp_tool actions. When invoked with no command,",
    "runs the stdio MCP server (MCP client mode).",
    "",
    "Commands:",
    "  run-tool <name>               Invoke an MCP tool by name; print its JSON result.",
    "  --help, -h                    Show this help.",
    "  --version, -V                 Print the package version.",
    "",
    "Options:",
    "  --json                        Emit JSON instead of human-readable output.",
    `  --project <path>, -P <path>   Godot project path (default: ${PROJECT_PATH_ENV_VAR}).`,
    `  --port <n>, -p <n>            Bridge port override (default: ${PORT_ENV_VAR}).`,
    "  --args '<json>'               JSON object of tool args (run-tool).",
    "  --arg key=value               One tool arg (run-tool, repeatable; JSON-parsed if valid).",
    "",
    "Exit codes:",
    "  0  success        tool returned isError=false.",
    "  1  tool error     tool returned isError=true.",
    "  2  usage error    unknown tool, bad args, or missing project path.",
    "",
    "Environment:",
    `  ${PROJECT_PATH_ENV_VAR.padEnd(30)}Required (or pass --project <path>).`,
    `  ${PORT_ENV_VAR.padEnd(30)}Optional bridge port override.`,
    "",
    "Examples:",
    `  ${binName} run-tool godot_open_mcp_ping --project /path/to/demo --json`,
    `  ${binName} run-tool godot_open_mcp_bridge_status -P /path/to/demo`,
  ].join("\n");
}

export function versionText(version: string): string {
  return `godot-open-mcp ${version}`;
}
