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
//     client (P1.7) → bridge `GET /ping`.
//   - P7.1 — every registered `CallTool` dispatches through `ToolRouter`. The
//     router owns live/offline/local selection and the `_source` / `_route`
//     metadata. `index.ts` only validates registration (unknown names are
//     rejected before the router runs) and normalizes non-object `arguments`.

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
import { LiveClient } from "./live-client.js";
import { BridgeEventStream } from "./event-stream.js";
import { ToolRouter } from "./tool-router.js";
import type { Router, CallToolResult } from "./router.js";

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
 * server process. Registered tools dispatch through the supplied
 * {@link Router} (P7.1), which owns live/offline/local selection and the
 * `_source` / `_route` metadata.
 *
 * `router` is optional so unit tests for the unknown-tool / missing-wiring
 * paths do not need to construct one. The stdio `main()` always supplies a
 * fully-wired `ToolRouter`; a registered call with no router attached returns
 * a structured `router_not_wired` error (the same never-throw contract).
 */
export async function handleCallTool(params: {
  name: string;
  arguments?: unknown;
}, router?: Router): Promise<CallToolResult> {
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

  if (!router) {
    // Test-harness omission. Production main() always wires a ToolRouter.
    return {
      isError: true,
      content: [
        {
          type: "text",
          text: `Tool ${toolName} is registered but no router is wired (test harness omission).`,
        },
      ],
    };
  }

  return router.route(toolName, args);
}

/**
 * Build the MCP server. Kept as a factory so tests (and later multi-project
 * hosting) can construct a server without touching process state.
 *
 * @param serverName — base name reported in the MCP `initialize` response.
 * @param router — routes registered tool calls. Optional so tests that only
 *                 exercise handleListTools don't need one; the stdio `main()`
 *                 always supplies a fully-wired ToolRouter.
 */
export function createServer(
  serverName = "godot-open-mcp",
  router?: Router,
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
    handleCallTool(request.params, router),
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
  // P5.4 — one SSE subscription per server process. The MCP server is the only long-lived hop
  // between the bridge and the LLM; a per-process reader amortizes the connection and lets every
  // `godot_open_mcp_pull_events` call share the same buffered queue. The same auth token as the
  // live client (resolved from the instance lock) gates the SSE stream.
  const eventStream = new BridgeEventStream(
    `http://127.0.0.1:${env.bridgePort}`,
    undefined,
    env.bridgeAuthToken,
  );
  // P7.1 — every registered call dispatches through ToolRouter. The router owns
  // live/offline/local selection (capabilities / bridge_status / pull_events are
  // local named handlers; everything else routes live) and the `_source` /
  // `_route` metadata. index.ts only validates registration + normalizes args.
  const router = new ToolRouter(liveClient, env.projectPath, eventStream);
  const server = createServer("godot-open-mcp", router);
  const transport = new StdioServerTransport();

  // Clean shutdown on disconnect. The SDK closes the transport when stdin
  // closes (AI client exits); we surface that as a normal process exit so
  // supervisors do not log a crash and CI smoke tests can assert exit code 0.
  transport.onclose = () => {
    console.error("[godot-open-mcp] stdio transport closed; exiting.");
    eventStream.stop();
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
