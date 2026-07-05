#!/usr/bin/env node

// stdio MCP server bootstrap (P1.5).
//
// Wires the SDK `Server` to a `StdioServerTransport`, registers the
// `ListTools` / `CallTool` handlers against the tool registry, and exits
// cleanly when the transport closes (AI client disconnect / stdin EOF).
//
// Scope (per execution-plan-5 + execution-plan-6):
//   - stdio startup + package structure,
//   - clean process lifecycle on connect/disconnect,
//   - instance discovery — bridge port resolved from GODOT_PROJECT_PATH +
//     GODOT_OPEN_MCP_BRIDGE_PORT at startup (P1.6),
//   - no live-bridge routing, no CLI dispatch yet.
// The ping tool + live client (P1.7) land next, at which point `createServer`
// will gain the port/auth arguments and the `CallTool` handler will delegate
// to a `ToolRouter`. The shape here is the minimal foundation those phases
// extend.

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  ListToolsRequestSchema,
  CallToolRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { ALL_TOOLS } from "./tools/index.js";
import { readPackageVersion } from "./package-version.js";
import {
  PORT_OVERRIDE_ENV_VAR,
  resolvePort,
  resolveAuthToken,
} from "./instance-discovery.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

// Read the version from package.json at runtime so `npm version` and the
// maintainer-panel version-bump keep the reported server version in sync
// without editing this source file.
const PACKAGE_VERSION = readPackageVersion();

/**
 * ListTools handler. Returns the full tool registry; later phases (P8.2)
 * filter this through per-session tool-group visibility state before
 * returning. Exported so the contract can be unit-tested without going
 * through the SDK's private handler table.
 */
export async function handleListTools(): Promise<{ tools: typeof ALL_TOOLS }> {
  return { tools: ALL_TOOLS };
}

/**
 * CallTool handler. Defensive: with an empty registry, well-behaved clients
 * never call this. Returns a structured error rather than throwing so a
 * malformed client cannot kill the server process. Exported for unit testing.
 */
export async function handleCallTool(params: {
  name: string;
  arguments?: unknown;
}): Promise<CallToolResult> {
  return {
    isError: true,
    content: [
      {
        type: "text",
        text: `Unknown tool: ${params.name}. No tools are registered in this scaffold yet.`,
      },
    ],
  };
}

/**
 * Build the MCP server. Kept as a factory so tests (and later multi-project
 * hosting) can construct a server without touching process state.
 *
 * @param serverName — base name reported in the MCP `initialize` response.
 */
export function createServer(serverName = "godot-open-mcp"): Server {
  const server = new Server(
    { name: serverName, version: PACKAGE_VERSION },
    {
      // `listChanged: true` is declared now so P8.4 (manage_tools list-changed
      // notifications) can flip the visible tool set without a capability
      // renegotiation. With an empty registry no notification is sent yet.
      capabilities: { tools: { listChanged: true } },
    },
  );

  server.setRequestHandler(ListToolsRequestSchema, () => handleListTools());

  server.setRequestHandler(CallToolRequestSchema, (request) =>
    handleCallTool(request.params),
  );

  return server;
}

/**
 * Resolve environment for the MCP server.
 *
 * `GODOT_PROJECT_PATH` is the project root the bridge runs against. It is
 * mandatory: every later phase (instance-discovery port resolution, live
 * routing, offline reads) keys off it. `GODOT_OPEN_MCP_BRIDGE_PORT` overrides
 * the deterministic port (parsed here so a malformed value falls back to the
 * hash rather than faulting startup). The resolved `bridgePort` /
 * `bridgeAuthToken` feed the live client (P1.7).
 */
function getEnv(): {
  projectPath: string;
  bridgePort: number;
  bridgeAuthToken: string | undefined;
} {
  const projectPath = process.env.GODOT_PROJECT_PATH;
  if (!projectPath) {
    console.error(
      "godot-open-mcp: GODOT_PROJECT_PATH environment variable is required.",
    );
    process.exit(1);
  }
  const rawPort = process.env[PORT_OVERRIDE_ENV_VAR];
  const envPort = parseEnvPort(rawPort);
  const bridgePort = resolvePort(projectPath, envPort);
  const bridgeAuthToken = resolveAuthToken(projectPath, envPort);
  console.error(
    `[godot-open-mcp] project path: ${projectPath} -> bridge port ${bridgePort}` +
      (rawPort !== undefined && rawPort !== ""
        ? ` (${PORT_OVERRIDE_ENV_VAR}=${rawPort})`
        : " (deterministic / lock)"),
  );
  return { projectPath, bridgePort, bridgeAuthToken };
}

/**
 * Parse the `GODOT_OPEN_MCP_BRIDGE_PORT` override. Returns undefined for
 * empty / non-integer / out-of-TCP-range values so `resolvePort` falls back
 * to the deterministic hash + lock lookup. Mirrors the bridge-side validation
 * (InstancePortResolver.IsValidPort).
 */
function parseEnvPort(raw: string | undefined): number | undefined {
  if (raw === undefined || raw === "") return undefined;
  const parsed = Number(raw);
  if (!Number.isInteger(parsed) || parsed < 1 || parsed > 65535) {
    console.error(
      `[godot-open-mcp] ignoring invalid ${PORT_OVERRIDE_ENV_VAR}=${raw} (must be an integer in [1, 65535])`,
    );
    return undefined;
  }
  return parsed;
}

async function main(): Promise<void> {
  // Resolved up-front so instance-discovery runs at startup and the live
  // client (P1.7) can attach to the right bridge without a re-read. The
  // values are intentionally not yet threaded into createServer/CallTool —
  // that wiring lands with the ping tool + live client in P1.7.
  const env = getEnv();
  const server = createServer();
  const transport = new StdioServerTransport();

  // Clean shutdown on disconnect. The SDK closes the transport when stdin
  // closes (AI client exits); we surface that as a normal process exit so
  // supervisors do not log a crash and CI smoke tests can assert exit code 0.
  transport.onclose = () => {
    console.error("[godot-open-mcp] stdio transport closed; exiting.");
    process.exit(0);
  };

  await server.connect(transport);
}

// Only boot the stdio server when this module is the process entry point.
// Tests import `createServer` / `handleListTools` / `handleCallTool` directly
// and must not trigger the env check or transport wiring. Comparing resolved
// paths (rather than the raw `file://` URL) is robust to symlinked Node
// invocations.
import { fileURLToPath } from "node:url";
const isMain = fileURLToPath(import.meta.url) === process.argv[1];
if (isMain) {
  main().catch((err) => {
    console.error("godot-open-mcp fatal:", err);
    process.exit(1);
  });
}
