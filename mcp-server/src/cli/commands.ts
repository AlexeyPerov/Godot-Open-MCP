// CLI command implementations.
//
// Adapted from Unity Open MCP's `mcp-server/src/cli/commands.ts`
// (copy/adapt fidelity). Godot ships one command — `run-tool` — plus the
// shared router-stack construction that guarantees a CLI call dispatches
// through the exact same `ToolRouter` an MCP client would (so `run-tool`
// returns the same JSON the stdio server produces).
//
// Intentional deltas from Unity: no `routers.ts` module in this repo, so the
// stack builder lives here and mirrors `index.ts#getEnv` + `main()` wiring
// (LiveClient + BridgeEventStream + ToolSessionState + ToolRouter). No batch
// spawn / resource router (Godot has no headless-editor batch equivalent).

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { ALL_TOOLS } from "../tools/index.js";
import { LiveClient } from "../live-client.js";
import { BridgeEventStream } from "../event-stream.js";
import { ToolRouter } from "../tool-router.js";
import { ToolSessionState } from "../tool-session-state.js";
import {
  PORT_OVERRIDE_ENV_VAR,
  resolvePort,
  resolveAuthToken,
} from "../instance-discovery.js";
import { PROJECT_PATH_ENV_VAR } from "./help-text.js";

/** Result of a CLI command: exit code + machine (JSON) + human forms. */
export interface CliCommandResult {
  exitCode: number;
  json: unknown;
  human: string;
}

/** Resolved runtime env for a CLI invocation. */
export interface ResolvedEnv {
  projectPath: string;
  port: number;
  authToken: string | undefined;
}

/** Thrown when no project path is resolvable; caller prints + exits code 2. */
export class ResolveEnvError extends Error {}

/**
 * Resolve project path / port / auth token from explicit CLI overrides or the
 * process env. Mirrors index.ts#getEnv precedence:
 *   projectPath: --project > GODOT_PROJECT_PATH
 *   port:        --port    > GODOT_OPEN_MCP_BRIDGE_PORT > deterministic/lock
 */
export function resolveEnv(
  projectPathOverride?: string,
  portOverride?: number,
): ResolvedEnv {
  const projectPath =
    projectPathOverride ?? process.env[PROJECT_PATH_ENV_VAR];
  if (!projectPath) {
    throw new ResolveEnvError(
      `${PROJECT_PATH_ENV_VAR} environment variable is required ` +
        "(or pass --project <path>).",
    );
  }

  const rawEnvPort = process.env[PORT_OVERRIDE_ENV_VAR];
  const envPort = rawEnvPort ? Number(rawEnvPort) : undefined;
  const effectiveEnvPort =
    rawEnvPort && Number.isInteger(envPort) ? envPort : undefined;
  const chosen = portOverride ?? effectiveEnvPort;

  const port = resolvePort(projectPath, chosen);
  const authToken = resolveAuthToken(projectPath, chosen);
  return { projectPath, port, authToken };
}

/** The router stack a command runs against. */
export interface RouterStack {
  router: ToolRouter;
  eventStream: BridgeEventStream;
  projectPath: string;
  port: number;
}

/**
 * Build the router stack from resolved env. Mirrors index.ts#main() wiring so
 * a CLI call sees the identical routing decisions an MCP client would.
 */
export function buildRouterStack(env: ResolvedEnv): RouterStack {
  const live = new LiveClient(env.port, env.authToken, env.projectPath);
  const eventStream = new BridgeEventStream(
    `http://127.0.0.1:${env.port}`,
    undefined,
    env.authToken,
  );
  const sessionState = new ToolSessionState();
  const router = new ToolRouter(
    live,
    env.projectPath,
    eventStream,
    sessionState,
  );
  return { router, eventStream, projectPath: env.projectPath, port: env.port };
}

/** Tool lookup by name (parity check before routing). */
const TOOL_BY_NAME = new Map(ALL_TOOLS.map((t) => [t.name, t] as const));

export interface RunToolCommandOptions {
  toolName: string;
  toolArgs: Record<string, unknown>;
}

/** Extract the result body: the parsed JSON of the first text content, else raw. */
function extractResultBody(result: CallToolResult): unknown {
  const first = result.content?.[0];
  if (!first || first.type !== "text" || typeof first.text !== "string") {
    return result;
  }
  try {
    return JSON.parse(first.text);
  } catch {
    return first.text;
  }
}

function formatRunToolHuman(toolName: string, isError: boolean, body: unknown): string {
  const head = `${toolName}: ${isError ? "ERROR" : "ok"}`;
  const bodyStr = typeof body === "string" ? body : JSON.stringify(body, null, 2);
  return `${head}\n${bodyStr}`;
}

function wrapList(items: string[], perLine = 4): string {
  const out: string[] = [];
  for (let i = 0; i < items.length; i += perLine) {
    out.push("  " + items.slice(i, i + perLine).join(", "));
  }
  return out.join("\n");
}

/**
 * Run an MCP tool by name and shape the result into the CLI contract the
 * Validation Suite's Rust runner parses:
 *   `{ command: "run-tool", tool, isError, result }`.
 * Unknown tool → usage error (exit 2). Tool error → exit 1. Success → exit 0.
 */
export async function runRunToolCommand(
  stack: RouterStack,
  opts: RunToolCommandOptions,
): Promise<CliCommandResult> {
  if (!TOOL_BY_NAME.has(opts.toolName)) {
    // `isError: true` keeps the envelope uniform with the tool-error branch so
    // the Validation Suite's Rust runner (which keys off `isError` and defaults
    // it to false) never reports an unknown tool as a successful action.
    const json = {
      command: "run-tool",
      tool: opts.toolName,
      isError: true,
      error: {
        code: "unknown_tool",
        message: `Unknown tool '${opts.toolName}'.`,
        available: ALL_TOOLS.map((t) => t.name),
      },
    };
    return {
      exitCode: 2,
      json,
      human:
        `Unknown tool '${opts.toolName}'.\n` +
        `Available tools (${ALL_TOOLS.length}):\n` +
        wrapList(ALL_TOOLS.map((t) => t.name)),
    };
  }

  const result: CallToolResult = await stack.router.route(
    opts.toolName,
    opts.toolArgs,
  );
  const body = extractResultBody(result);
  const isError = result.isError === true;

  return {
    exitCode: isError ? 1 : 0,
    json: {
      command: "run-tool",
      tool: opts.toolName,
      isError,
      result: body,
    },
    human: formatRunToolHuman(opts.toolName, isError, body),
  };
}
