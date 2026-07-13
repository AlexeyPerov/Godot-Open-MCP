// Shared environment-variable and bin-name constants for the CLI.
//
// Kept in a tiny module so the args parser, the help text, and future command
// implementations all agree on the same names. These mirror
// `mcp-server/src/instance-discovery.ts` byte-for-byte — the CLI sets / reads
// the same env vars the stdio MCP server and the bridge use.

/** Project root the bridge / MCP server operate on. */
export const PROJECT_PATH_ENV_VAR = "GODOT_PROJECT_PATH";

/** Bridge port override (flag wins, else this env var, else instance lock). */
export const PORT_OVERRIDE_ENV_VAR = "GODOT_OPEN_MCP_BRIDGE_PORT";

/** Canonical bin name for help text and error prefixes. */
export const DEFAULT_BIN_NAME = "godot-open-mcp-cli";
