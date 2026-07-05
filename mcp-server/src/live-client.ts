// Live bridge client (P1.7).
//
// The MCP-server-side counterpart to the bridge's HTTP listener. Holds the
// resolved bridge endpoint (loopback host + port + optional bearer token) and
// routes tool calls into HTTP requests.
//
// Scope (per execution-plan-7):
//   - `godot_open_mcp_ping` → bridge `GET /ping`, returning the live health
//     payload on success and a structured error on failure.
//   - Failure responses distinguish `bridge_offline` (ECONNREFUSED — bridge
//     not running / wrong port) from `bridge_timeout` (AbortError — bridge
//     too slow to respond within the client timeout).
//   - The bearer token from the instance lock (P5.2 wiring) is attached as
//     `Authorization: Bearer <token>` on every request when present; when
//     absent no header is sent and the bridge must be in authMode "none".
//
// What is deliberately NOT here yet (later phases): mutating tool dispatch
// (`POST /tools/{name}`), the gate envelope, compile-wait / 503 retry,
// dead-bridge fail-fast via the instance lock, endpoint refresh from the lock,
// agent identity header, SSE event stream, resource reads. The structure of
// `route()` is shaped so those land cleanly: ping is the special-case
// direct-to-`/ping` path, every other tool name will fall through to the
// (future) tool-dispatch branch.
//
// Adapted from Unity Open MCP's mcp-server/src/live-client.ts (copy fidelity
// for the ping path): same fetch-with-timeout helper, same PingResponse shape,
// same offline-hint construction. The retry / classification / dialog-dismiss
// machinery from the Unity original is intentionally deferred — see the
// execution-plan-7 acceptance criteria.

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { makeErrorResult } from "./results.js";
import {
  lockPath,
  readInstanceLock,
} from "./instance-discovery.js";

/** Default fetch timeout for /ping. Matches Unity's PING_TIMEOUT_MS. */
const PING_TIMEOUT_MS = 5_000;

/** Tool name → route key for the ping probe (the only tool wired in P1.7). */
export const PING_TOOL_NAME = "godot_open_mcp_ping";

/**
 * Bridge `GET /ping` response body. Field set mirrors the bridge's
 * `BridgeJson.BuildPingJson` (packages/bridge/Editor/Bridge/BridgeJson.cs) and
 * the TS-side shape used by Unity Open MCP. `godotVersion` replaces Unity's
 * `unityVersion`; every other field is identical so an MCP client migrating
 * between the two projects sees the same readiness contract.
 */
export interface PingResponse {
  connected: boolean;
  projectPath: string | null;
  godotVersion: string | null;
  bridgeVersion: string;
  mode: string;
  compiling: boolean;
  isPlaying: boolean;
}

/**
 * Offline hint shown when the bridge cannot be reached. Adapted from Unity's
 * OFFLINE_HINT (copy fidelity): names the per-project port formula and points
 * the agent at the instance lock file. The home dir + lock path are Godot's
 * (`~/.godot-open-mcp/instances/<hash>.json`).
 */
const OFFLINE_HINT =
  "Ensure the Godot Editor is open with the bridge addon enabled. " +
  "The bridge port is per-project (20000 + sha256(projectPath) % 10000), not " +
  "fixed — if Godot is open the MCP server may be aimed at the wrong port. " +
  "Check the instance lock at ~/.godot-open-mcp/instances/<sha256(projectPath)>.json " +
  "for the live port/pid, or set GODOT_OPEN_MCP_BRIDGE_PORT. If Godot is not " +
  "open, launch it with the project loaded.";

/**
 * Build a per-instance offline hint that names THIS project's lock file path
 * and (when the lock is readable) its port/pid/state, so an agent debugging a
 * `bridge_offline` knows exactly where to look. Falls back to OFFLINE_HINT
 * when the project path is unknown (older callers / tests).
 *
 * Adapted from Unity's buildOfflineHint (copy fidelity) with the Godot home
 * dir convention.
 */
function buildOfflineHint(projectPath: string | undefined): string {
  if (!projectPath) return OFFLINE_HINT;
  const lock = lockPath(projectPath);
  const base = `${OFFLINE_HINT} This project's lock file: ${lock}`;
  try {
    const inst = readInstanceLock(projectPath);
    if (inst) {
      return `${base}. Lock state: pid=${inst.pid}, port=${inst.port}, state=${inst.state}`;
    }
  } catch {
    // best-effort — fall through to the base hint
  }
  return `${base} (lock not readable — Godot may not be running).`;
}

/**
 * LiveClient routes MCP tool calls into bridge HTTP requests.
 *
 * Construct one per resolved bridge endpoint (the stdio server does this once
 * at startup; later phases may build transient clients for per-request port
 * overrides). The class is intentionally minimal in P1.7 — only the ping
 * round-trip is wired.
 */
export class LiveClient {
  /** Base URL of the resolved bridge endpoint, e.g. `http://127.0.0.1:22028`. */
  private baseUrl: string;
  /** Per-session bearer token (P5.2 wiring). Undefined when no live lock was found. */
  private authToken: string | undefined;
  /** Absolute Godot project path, used to read the instance lock for the offline hint. */
  private projectPath: string | undefined;

  constructor(
    port: number,
    authToken?: string,
    projectPath?: string,
  ) {
    this.baseUrl = `http://127.0.0.1:${port}`;
    this.authToken = authToken;
    this.projectPath = projectPath;
  }

  /**
   * Route a tool call to the bridge. P1.7 wires only the ping path; every
   * other tool name surfaces a structured "not yet implemented" error so the
   * CallTool dispatcher never throws. Later phases replace the fallback with
   * `POST /tools/{name}` dispatch through the gate flow.
   */
  async route(
    toolName: string,
    _args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    if (toolName === PING_TOOL_NAME) {
      return this.handlePing();
    }
    return makeErrorResult({
      code: "tool_not_routed",
      message:
        `Tool '${toolName}' is not routed to the bridge in this build. ` +
        "Only godot_open_mcp_ping is wired in P1.7; mutating tool dispatch arrives in later phases.",
    });
  }

  /**
   * `godot_open_mcp_ping` handler. Fetches `GET /ping` from the resolved
   * bridge endpoint and normalizes the response:
   *   - HTTP 200 + valid body → success, body returned verbatim as JSON text.
   *   - HTTP 503 (bridge listener up, session not ready) → still a reachable
   *     bridge; return the fallback body with `connected:false` so the agent
   *     sees the bridge is loading rather than offline.
   *   - HTTP other → `bridge_http_error` with the status.
   *   - ECONNREFUSED / socket failure (TypeError) → `bridge_offline` (bridge
   *     not running / wrong port). The offline hint names this project's lock.
   *   - AbortController timeout (AbortError) → `bridge_timeout` (bridge is
   *     up but too slow to answer within PING_TIMEOUT_MS).
   *
   * The two failure classes are distinguished so an agent can branch: retry
   * makes sense for a timeout (the bridge may recover), while bridge_offline
   * points at configuration / launching Godot.
   */
  private async handlePing(): Promise<CallToolResult> {
    try {
      const res = await this.fetchWithTimeout("/ping", { method: "GET" });

      // 503 = bridge listener up, BridgeSession not initialized yet. Treat it
      // as a reachable-but-not-ready bridge rather than offline: the body is
      // a valid PingResponse-shape JSON the agent can inspect.
      if (res.status === 503) {
        const fallback = (await res.json().catch(() => null)) as PingResponse | null;
        const body = fallback ?? {
          connected: false,
          projectPath: null,
          godotVersion: null,
          bridgeVersion: "unknown",
          mode: "live",
          compiling: true,
          isPlaying: false,
        };
        return {
          content: [{ type: "text", text: JSON.stringify(body) }],
          isError: false,
        };
      }

      if (!res.ok) {
        return makeErrorResult({
          code: "bridge_http_error",
          message: `Bridge /ping returned unexpected HTTP ${res.status}. Endpoint: ${this.baseUrl}.`,
        });
      }

      const body = (await res.json()) as PingResponse;
      return {
        content: [{ type: "text", text: JSON.stringify(body) }],
        isError: false,
      };
    } catch (err) {
      return this.classifyPingFailure(err);
    }
  }

  /**
   * Map a thrown /ping failure to the right structured error code. Split out
   * from handlePing so the test suite can drive the classifier directly
   * without spinning up a bridge stub that times out (which would slow the
   * suite by PING_TIMEOUT_MS per case).
   *
   * Distinguishes:
   *   - `bridge_timeout` — AbortError (the fetch's own AbortController fired).
   *     The bridge endpoint exists but is too slow to answer.
   *   - `bridge_offline` — anything else (TypeError "fetch failed" covers
   *     ECONNREFUSED / no listener / wrong port). The bridge is not reachable
   *     at all.
   */
  private classifyPingFailure(err: unknown): CallToolResult {
    const isTimeout =
      err instanceof DOMException && err.name === "AbortError";
    if (isTimeout) {
      // Intentional delta from Unity Open MCP: Unity supplies a `detail`
      // override that replaces the default envelope, which loses the long
      // human message in `input.message` (makeErrorResult uses `detail` as a
      // full body replacement). Agents debugging a timeout need the long
      // message — the timeout wait, the endpoint, and the recovery hint — so
      // we drop the override and let the default envelope carry it. The
      // structured code (`bridge_timeout`) is still the first field.
      return makeErrorResult({
        code: "bridge_timeout",
        message:
          `Bridge /ping did not respond within ${PING_TIMEOUT_MS}ms. ` +
          `The bridge is reachable at ${this.baseUrl} but too slow — the editor ` +
          "may be stalled on a long main-thread op. Retry, or check the editor " +
          "is responsive.",
      });
    }
    // Same delta as above: drop the `detail` override so the default envelope
    // carries the full offline hint (lock-file path, env override, recovery
    // steps). The structured code (`bridge_offline`) is the first field.
    return makeErrorResult({
      code: "bridge_offline",
      message: `Bridge is not reachable at ${this.baseUrl}. ${buildOfflineHint(this.projectPath)}`,
    });
  }

  /**
   * fetch() with an AbortController-backed timeout and the bearer token
   * attached. Mirrors Unity's LiveClient.fetchWithTimeout (copy fidelity): the
   * timeout fires via `AbortController.abort()`, which surfaces to the caller
   * as a DOMException named "AbortError" — the signature classifyPingFailure
   * keys off to distinguish timeout from connection failure.
   *
   * The auth header is merged (not overwritten) so a caller-supplied header
   * always wins; in practice ping sends no caller headers, so the discovered
   * token is attached as-is when present.
   */
  private fetchWithTimeout(
    path: string,
    init: RequestInit,
    timeoutMs: number = PING_TIMEOUT_MS,
  ): Promise<Response> {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);

    const headers = new Headers(init.headers);
    if (this.authToken && !headers.has("Authorization")) {
      headers.set("Authorization", `Bearer ${this.authToken}`);
    }

    return fetch(`${this.baseUrl}${path}`, {
      ...init,
      headers,
      signal: controller.signal,
    }).finally(() => clearTimeout(timer));
  }
}
