/**
 * Pure MCP stdio-entry builders — no Tauri/IPC imports, so they can run under
 * `node --test` for the CLI-parity fixture assertion. The Rust writer
 * (`config/mcp_config.rs`) and the CLI's `setup-mcp` produce the same shape;
 * this module is the TS mirror used for instant preview + the parity test.
 */

import { MCP_SERVER_NAME } from "../version.ts";

export type McpSource = "npx-published" | "local-checkout";

export interface McpEntryInput {
  projectPath: string;
  source: McpSource;
  packageVersion?: string | null;
  monorepoPath?: string | null;
  overridePort?: number | null;
}

export interface StdioProps {
  command: string;
  args: string[];
  env: Record<string, string>;
}

/** Build the `env` block. `GODOT_PROJECT_PATH` first, optional port second. */
export function buildMcpEnv(
  projectPath: string,
  overridePort?: number | null,
): Record<string, string> {
  const env: Record<string, string> = { GODOT_PROJECT_PATH: projectPath };
  if (typeof overridePort === "number" && overridePort >= 1) {
    env.GODOT_OPEN_MCP_BRIDGE_PORT = String(overridePort);
  }
  return env;
}

/** Build the uniform stdio descriptor (command/args/env) for a source. */
export function buildStdioProps(input: McpEntryInput): StdioProps {
  const env = buildMcpEnv(input.projectPath, input.overridePort);
  if (input.source === "local-checkout") {
    const repo = (input.monorepoPath ?? "").replace(/\/+$/, "");
    return { command: "node", args: [`${repo}/mcp-server/dist/index.js`], env };
  }
  const version =
    input.packageVersion && input.packageVersion.length > 0 ? input.packageVersion : null;
  const pkg = version ? `${MCP_SERVER_NAME}@${version}` : MCP_SERVER_NAME;
  return { command: "npx", args: ["-y", pkg], env };
}

/** The bare `{ command, args, env }` entry (Cursor / Claude / …). */
export function buildBareEntry(input: McpEntryInput): Record<string, unknown> {
  const p = buildStdioProps(input);
  return { command: p.command, args: p.args, env: p.env };
}
