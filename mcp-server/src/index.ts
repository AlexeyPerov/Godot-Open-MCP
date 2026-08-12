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
//   - P8.2 — `ListTools` filters `ALL_TOOLS` through per-session tool-group
//     visibility state. A fresh `ToolSessionState` advertises only `core`
//     plus always-visible meta-tools; CallTool is NOT filtered (hiding is a
//     prompt-size control, not an authorization boundary). One store per
//     server process, threaded into the handler from `createServer`.

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  ListToolsRequestSchema,
  CallToolRequestSchema,
  ListResourcesRequestSchema,
  ReadResourceRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { ALL_TOOLS } from "./tools/index.js";
import { ALL_RESOURCES } from "./resources/index.js";
import { ResourceRouter } from "./resources/resource-router.js";
import { readPackageVersion } from "./package-version.js";
import {
  PORT_OVERRIDE_ENV_VAR,
  resolvePort,
  resolveAuthToken,
} from "./instance-discovery.js";
import { LiveClient } from "./live-client.js";
import { BridgeEventStream } from "./event-stream.js";
import { ToolRouter } from "./tool-router.js";
import { ToolSessionState, filterVisibleTools } from "./tool-session-state.js";
import type { Router, CallToolResult } from "./router.js";
import { KNOWN_COMMANDS } from "./cli/args.js";
import { runCli } from "./cli/cli.js";

// Read the version from package.json at runtime so `npm version` and the
// maintainer-panel version-bump keep the reported server version in sync
// without editing this source file.
const PACKAGE_VERSION = readPackageVersion();

/**
 * ListTools handler. Filters {@link ALL_TOOLS} through the per-session
 * tool-group visibility store: a fresh session advertises only `core`
 * (ping + the gate/verify safety surface) plus always-visible meta-tools
 * (capabilities, bridge_status, pull_events, read_compile_errors). Activating
 * an opt-in group via a direct store mutation (P8.2) or the future
 * `godot_open_mcp_manage_tools` tool (P8.3) adds its tools to subsequent
 * ListTools responses. Exported so the contract can be unit-tested without
 * going through the SDK's private handler table.
 *
 * P8.2 contract — CallTool is NOT filtered by group: hiding is a prompt-size
 * control, not an authorization boundary. A known-but-hidden tool name still
 * routes when called (see {@link handleCallTool}).
 */
export async function handleListTools(
  sessionState: ToolSessionState,
): Promise<{ tools: typeof ALL_TOOLS }> {
  return { tools: filterVisibleTools(ALL_TOOLS, sessionState) };
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
 * The factory constructs exactly one {@link ToolSessionState} per server
 * process and threads it into the ListTools handler. The same instance is
 * returned alongside the {@link Server} so `main()` can inject it into the
 * `manage_tools` router without reconstructing the store (the stdio MCP
 * server has exactly one client per process; a second store would desync
 * ListTools and manage_tools).
 *
 * @param serverName — base name reported in the MCP `initialize` response.
 * @param router — routes registered tool calls. Optional so tests that only
 *                 exercise handleListTools don't need one; the stdio `main()`
 *                 always supplies a fully-wired ToolRouter.
 * @param options — optional injection points:
 *   - `sessionState` — inject an existing {@link ToolSessionState} instead of
 *     constructing a fresh one. Tests use this to drive a session through a
 *     known mutation before the server reads it. Production callers omit it.
 *   - `resourceRouter` — when supplied (P18.2), the server advertises the
 *     `resources` capability and registers `resources/list` + `resources/read`
 *     handlers against {@link ALL_RESOURCES} / the router. Omitted in tests
 *     that only exercise tools; the stdio `main()` always supplies one.
 *
 * @returns `{ server, sessionState }` — the wired {@link Server} and the
 *   session store it consults. Callers that need to mutate visibility (P8.3
 *   `manage_tools`) hold the returned `sessionState`; callers that only need
 *   the {@link Server} (most tests) ignore it.
 */
export function createServer(
  serverName = "godot-open-mcp",
  router?: Router,
  options?: {
    sessionState?: ToolSessionState;
    resourceRouter?: ResourceRouter;
  },
): { server: Server; sessionState: ToolSessionState } {
  // One session store per server process. Injected when supplied (tests);
  // freshly constructed otherwise (production main()). The store is the
  // single source of truth for ListTools visibility and (P8.3) manage_tools
  // mutations, so it MUST be shared, not reconstructed per handler.
  const sessionState = options?.sessionState ?? new ToolSessionState();

  const server = new Server(
    { name: serverName, version: PACKAGE_VERSION },
    {
      // `listChanged: true` is declared now so P8.4 (manage_tools list-changed
      // notifications) can flip the visible tool set without a capability
      // renegotiation.
      // P18.2 — `resources: {}` is advertised only when a ResourceRouter is
      // wired so a client never sees a resources capability with no handlers.
      capabilities: options?.resourceRouter
        ? { tools: { listChanged: true }, resources: {} }
        : { tools: { listChanged: true } },
    },
  );

  server.setRequestHandler(ListToolsRequestSchema, () =>
    handleListTools(sessionState),
  );

  server.setRequestHandler(CallToolRequestSchema, (request) =>
    handleCallTool(request.params, router),
  );

  // P18.2 — resources/list + resources/read. Registered only when a router is
  // wired (production main()). resources/list returns the static catalog;
  // resources/read routes the URI through the ResourceRouter, which wraps
  // existing tool / scanner outputs (read-only, never throws).
  if (options?.resourceRouter) {
    const resourceRouter = options.resourceRouter;
    server.setRequestHandler(ListResourcesRequestSchema, () => ({
      resources: ALL_RESOURCES,
    }));
    server.setRequestHandler(ReadResourceRequestSchema, (request) =>
      resourceRouter.read(request.params.uri),
    );
  }

  return { server, sessionState };
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
  // P8.2/P8.3 — one ToolSessionState per server process, constructed up front
  // so the same instance flows into both the ListTools handler (via
  // createServer) and the manage_tools router (via ToolRouter). Constructing
  // two stores would desync ListTools and manage_tools; the shared instance is
  // the single source of truth for per-session tool-group visibility.
  const sessionState = new ToolSessionState();
  // P8.4 — emit `notifications/tools/list_changed` when manage_tools mutates
  // the visible tool set. The notifier closes over the SDK Server (the only
  // object that owns the transport), but in Godot the router is constructed
  // BEFORE `createServer` returns the server — the inverse of Unity's order
  // (Unity builds server → notifier → router inside createServer; Godot's
  // factory takes a pre-built router). A lazy `serverRef` defers the capture so
  // the closure never observes null at call time: manage_tools only runs after
  // `main()` has fully booted the server. Transport errors are swallowed + sent
  // to stderr so a dead transport cannot flip `isError` on the manage_tools
  // result (failure-isolation contract, P8.4 §3).
  let serverRef: Server | null = null;
  const notifyToolListChanged = async (): Promise<void> => {
    if (!serverRef) return;
    try {
      await serverRef.notification({
        method: "notifications/tools/list_changed",
      });
    } catch (err) {
      console.error(
        "[godot-open-mcp] Failed to send tools/list_changed notification:",
        err,
      );
    }
  };
  // P7.1 — every registered call dispatches through ToolRouter. The router owns
  // live/offline/local selection (capabilities / bridge_status / pull_events /
  // manage_tools are local named handlers; scene_get_data / filesystem_list are
  // live-first; read_compile_errors is always-offline; everything else routes
  // live) and the `_source` / `_route` metadata. index.ts only validates
  // registration + normalizes args.
  //
  // P8.3 — `sessionState` is injected so `manage_tools` can mutate the same
  // store ListTools reads. P8.4 — `notifyToolListChanged` is the real
  // `notifications/tools/list_changed` emitter (defined above); the router
  // fires it ONLY when an activate/deactivate/reset actually changes the
  // visible set. list_groups + idempotent calls never fire it.
  const router = new ToolRouter(
    liveClient,
    env.projectPath,
    eventStream,
    sessionState,
    notifyToolListChanged,
  );
  // P18.2 — resource router for the four read-only resource URIs. Wraps
  // existing logic (offline scanner, baseline loader, LiveClient.routeBridge
  // Status, static tool-group catalog). Read-only; the only live hop is the
  // bridge/status /ping probe.
  const resourceRouter = new ResourceRouter({
    live: liveClient,
    projectPath: env.projectPath,
    port: env.bridgePort,
  });
  const { server } = createServer("godot-open-mcp", router, {
    sessionState,
    resourceRouter,
  });
  serverRef = server;
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
  bootstrap().catch((err) => {
    console.error("godot-open-mcp fatal:", err);
    process.exit(1);
  });
}

/**
 * Process entry fork. When argv[0] is a known CLI subcommand (`run-tool`) or an
 * explicit `--help` / `--version`, run the thin CLI and exit with its code.
 * Otherwise fall through to the stdio MCP server so a single `bin` works for
 * both CI/scripting (`godot-open-mcp run-tool …`) and MCP clients (which spawn
 * `node dist/index.js`). Mirrors Unity Open MCP's launcher fork.
 */
async function bootstrap(): Promise<void> {
  const firstArg = process.argv[2];
  const looksLikeCli =
    firstArg !== undefined &&
    ((KNOWN_COMMANDS as readonly string[]).includes(firstArg) ||
      firstArg === "--help" ||
      firstArg === "-h" ||
      firstArg === "--version" ||
      firstArg === "-V");
  if (looksLikeCli) {
    const outcome = await runCli({ version: PACKAGE_VERSION });
    if (outcome.handled) {
      process.exit(outcome.exitCode);
    }
    // handled === false only when argv had no recognized command; fall through.
  }
  await main();
}
