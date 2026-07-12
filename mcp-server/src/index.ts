#!/usr/bin/env node

// stdio MCP server bootstrap.
//
// Wires the SDK `Server` to a `StdioServerTransport`, registers the
// `ListTools` / `CallTool` handlers against the tool registry, and exits
// cleanly when the transport closes (AI client disconnect / stdin EOF).
//
// Scope (per execution-plan-5 + execution-plan-6 + execution-plan-7):
//   - stdio startup + package structure (P1.5),
//   - clean process lifecycle on connect/disconnect (P1.5),
//   - instance discovery — bridge port resolved from GODOT_PROJECT_PATH +
//     GODOT_OPEN_MCP_BRIDGE_PORT at startup (P1.6),
//   - `godot_open_mcp_ping` round-trip: tool registry (P1.7) → live bridge
//     client (P1.7) → bridge `GET /ping`. Mutating tool dispatch arrives in
//     later phases (P2.x onwards); until then route() surfaces a structured
//     "tool_not_routed" error for any non-ping tool name.

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  ListToolsRequestSchema,
  CallToolRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { ALL_TOOLS } from "./tools/index.js";
import { buildCapabilities, type CapabilitiesFilter } from "./capabilities/build-capabilities.js";
import { RULE_CATALOG, FIX_CATALOG } from "./capabilities/rule-catalog.js";
import { readPackageVersion } from "./package-version.js";
import {
  PORT_OVERRIDE_ENV_VAR,
  resolvePort,
  resolveAuthToken,
} from "./instance-discovery.js";
import { LiveClient } from "./live-client.js";
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
 * CallTool handler. Defensive: returns a structured `isError` response for
 * unknown tools instead of throwing so a malformed client cannot kill the
 * server process. Routed tools dispatch through the supplied LiveClient.
 *
 * One tool is resolved LOCALLY (no bridge hop): `godot_open_mcp_capabilities`.
 * Its response is built in-process from ALL_TOOLS + the rule/fix catalog, mirroring Unity Open MCP's
 * tool-router `routeCapabilities` (local-only). It must NOT POST to the bridge.
 *
 * `liveClient` is optional so the unit test for the unknown-tool path does
 * not need to spin up a client. The stdio `main()` always supplies one.
 */
export async function handleCallTool(params: {
  name: string;
  arguments?: unknown;
}, liveClient?: LiveClient): Promise<CallToolResult> {
  const toolName = params.name;
  const isRegistered = ALL_TOOLS.some((t) => t.name === toolName);
  if (!isRegistered) {
    return {
      isError: true,
      content: [
        {
          type: "text",
          text: `Unknown tool: ${toolName}. Registered tools: ${ALL_TOOLS.map((t) => t.name).join(", ") || "(none)"}.`,
        },
      ],
    };
  }

  const args =
    params.arguments && typeof params.arguments === "object"
      ? (params.arguments as Record<string, unknown>)
      : {};

  // P3.8 — capabilities is built locally over the full tool + rule + fix catalog. No bridge round-trip,
  // so it does not need a liveClient and never POSTs. The kind / include_planned filters pass through.
  if (toolName === "godot_open_mcp_capabilities") {
    const filter: CapabilitiesFilter = {};
    if (args.kind === "tools" || args.kind === "rules" || args.kind === "fixes") {
      filter.kind = args.kind;
    }
    if (typeof args.include_planned === "boolean") {
      filter.includePlanned = args.include_planned;
    }
    const result = buildCapabilities(
      { tools: ALL_TOOLS, rules: RULE_CATALOG, fixes: FIX_CATALOG },
      filter,
    );
    return {
      content: [{ type: "text", text: JSON.stringify(result) }],
    };
  }

  // P5.3 — bridge_status is a local/live hybrid. It composes the instance-lock classifier with one
  // /ping probe (driven through LiveClient so the auth header + ping cache path match godot_open_mcp_ping).
  // The synthesis happens in the MCP server (no POST /tools/bridge_status endpoint on the bridge), so it
  // is special-cased here rather than routed through liveClient.route. Unlike capabilities, it DOES need
  // a liveClient (to run the /ping probe); the no-liveClient guard below returns the structured test-harness
  // error for it. Read-only, gate-free, never errors on an offline bridge — `stopped` IS the answer there.
  if (toolName === "godot_open_mcp_bridge_status") {
    if (!liveClient) {
      return {
        isError: true,
        content: [
          {
            type: "text",
            text: `Tool ${toolName} is registered but no live client is wired (test harness omission).`,
          },
        ],
      };
    }
    return liveClient.routeBridgeStatus();
  }

  if (!liveClient) {
    return {
      isError: true,
      content: [
        {
          type: "text",
          text: `Tool ${toolName} is registered but no live client is wired (test harness omission).`,
        },
      ],
    };
  }
  return liveClient.route(toolName, args);
}

/**
 * Build the MCP server. Kept as a factory so tests (and later multi-project
 * hosting) can construct a server without touching process state.
 *
 * @param serverName — base name reported in the MCP `initialize` response.
 * @param liveClient — routes registered tool calls to the bridge. Optional so
 *                     tests that only exercise handleListTools don't need one.
 */
export function createServer(
  serverName = "godot-open-mcp",
  liveClient?: LiveClient,
): Server {
  const server = new Server(
    { name: serverName, version: PACKAGE_VERSION },
    {
      // `listChanged: true` is declared now so P8.4 (manage_tools list-changed
      // notifications) can flip the visible tool set without a capability
      // renegotiation.
      capabilities: { tools: { listChanged: true } },
    },
  );

  server.setRequestHandler(ListToolsRequestSchema, () => handleListTools());

  server.setRequestHandler(CallToolRequestSchema, (request) =>
    handleCallTool(request.params, liveClient),
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
 * `bridgeAuthToken` feed the live client.
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
  const env = getEnv();
  const liveClient = new LiveClient(
    env.bridgePort,
    env.bridgeAuthToken,
    env.projectPath,
  );
  const server = createServer("godot-open-mcp", liveClient);
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
