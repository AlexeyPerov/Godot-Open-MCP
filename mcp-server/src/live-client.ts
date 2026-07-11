// Live bridge client (P1.7 / P2.1).
//
// The MCP-server-side counterpart to the bridge's HTTP listener. Holds the
// resolved bridge endpoint (loopback host + port + optional bearer token) and
// routes tool calls into HTTP requests.
//
// Scope:
//   - `godot_open_mcp_ping` → bridge `GET /ping`, returning the live health
//     payload on success and a structured error on failure (P1.7).
//   - Every other registered tool → bridge `POST /tools/{name}` with the tool
//     args as the JSON body, unwrapping the canonical `{ ok, result, error }`
//     envelope into a CallToolResult (P2.1).
//   - Failure responses distinguish `bridge_offline` (ECONNREFUSED — bridge
//     not running / wrong port) from `bridge_timeout` (AbortError — bridge
//     too slow to respond within the client timeout).
//   - The bearer token from the instance lock (P5.2 wiring) is attached as
//     `Authorization: Bearer <token>` on every request when present; when
//     absent no header is sent and the bridge must be in authMode "none".
//
// What is deliberately NOT here yet (later phases): the gate envelope
// (P3.5 — P2.1 mutators dispatch directly with no gate wrapping), compile-wait
// / 503 retry, dead-bridge fail-fast via the instance lock, endpoint refresh
// from the lock, agent identity header, SSE event stream, resource reads. The
// structure of `route()` is shaped so those land cleanly: ping is the
// special-case direct-to-`/ping` path, every other tool name falls through to
// `postTool`.
//
// Adapted from Unity Open MCP's mcp-server/src/live-client.ts (copy fidelity
// for the ping path + the postTool fetch shape): same fetch-with-timeout
// helper, same PingResponse shape, same offline-hint construction. The retry /
// classification / dialog-dismiss machinery from the Unity original is
// intentionally deferred — see the execution-plan acceptance criteria. P2.1
// ships the simpler canonical `{ ok, result, error }` envelope (Unity uses a
// richer gate-aware mutation envelope); the gate flow arrives in P3.5.

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { makeErrorResult } from "./results.js";
import {
  lockPath,
  readInstanceLock,
} from "./instance-discovery.js";

/** Default fetch timeout for /ping. Matches Unity's PING_TIMEOUT_MS. */
const PING_TIMEOUT_MS = 5_000;

/**
 * The bridge's default per-tool dispatch timeout (ms) when the caller omits
 * `timeout_ms`. Mirrors `BridgeRequestBody.DefaultTimeoutMs` in the bridge
 * (packages/bridge/Editor/Bridge/BridgeRequestBody.cs) so the client floors its
 * fetch timeout above the bridge's own wait — if the client aborted first it
 * would re-POST while the bridge is still processing (a duplicate-mutation
 * hazard for non-idempotent tools). Adapted from Unity's
 * `BRIDGE_DEFAULT_TIMEOUT_MS`.
 */
const BRIDGE_DEFAULT_TIMEOUT_MS = 30_000;

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
 * Bridge canonical success envelope (P2.1). Every successful tool dispatch
 * returns `{ "ok": true, "result": ... }` where `result` is the handler's JSON
 * output verbatim. Mirrors `BridgeEnvelope.BuildSuccess` in the bridge
 * (packages/bridge/Editor/Bridge/BridgeEnvelope.cs).
 */
interface BridgeSuccessEnvelope {
  ok: true;
  result: unknown;
}

/**
 * Bridge canonical failure envelope (P2.1). Every failed tool dispatch returns
 * `{ "ok": false, "error": { "code", "message" } }`. Mirrors
 * `BridgeEnvelope.BuildFailure` in the bridge. The `code` is a stable
 * machine-readable string (e.g. `invalid_request`, `main_thread_blocked`,
 * `timeout`, `execution_error`); `message` is human-readable.
 */
interface BridgeFailureEnvelope {
  ok: false;
  error: { code: string; message: string };
}

/**
 * The HTTP-level error body the bridge writes for routing/transport faults
 * (404 tool_not_found, 405 method_not_allowed, 400 invalid_request, 500
 * bridge_internal_error). Same `{ error: { code, message } }` shape as the
 * dispatch failure envelope, but delivered with a non-200 HTTP status.
 */
interface HttpErrorBody {
  error?: { code?: string; message?: string };
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
   * Route a tool call to the bridge. Ping is the special-case direct-to-`/ping`
   * path (P1.7); every other registered tool name dispatches through
   * `POST /tools/{name}` via `postTool` (P2.1). The CallTool dispatcher never
   * throws — every failure path returns a structured `isError` result.
   */
  async route(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    if (toolName === PING_TOOL_NAME) {
      return this.handlePing();
    }
    return this.postTool(toolName, args);
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
   * `POST /tools/{name}` dispatch (P2.1). Sends the tool args as the JSON body
   * and unwraps the bridge's canonical `{ ok, result, error }` envelope into a
   * CallToolResult:
   *   - HTTP 200 + `ok:true`  → success; `result` returned verbatim as JSON text.
   *   - HTTP 200 + `ok:false` → the request reached the dispatcher and was
   *     processed but the tool failed (invalid_request, main_thread_blocked,
   *     timeout, execution_error, ...). Surfaced as `isError:true` with the
   *     bridge's `error.code` / `error.message`.
   *   - HTTP 4xx/5xx          → routing/transport fault (tool_not_found,
   *     method_not_allowed, invalid_request body, bridge_internal_error).
   *     Surfaced as `bridge_http_error` with the status + the body's error
   *     code/message when available.
   *   - ECONNREFUSED / socket failure (TypeError) → `bridge_offline`.
   *   - AbortController timeout (AbortError) → `bridge_timeout`.
   *
   * The client fetch timeout is floored at `BRIDGE_DEFAULT_TIMEOUT_MS` + slack
   * so the client never aborts before the bridge has had a chance to return its
   * own timeout envelope — without this floor, a small/absent `timeout_ms`
   * would make the client re-POST while the bridge is still processing
   * (duplicate-mutation hazard). Adapted from Unity's `postTool` (copy
   * fidelity for the fetch shape + timeout floor), simplified to the P2.1
   * canonical envelope (no mutation/gate envelope, no compile-wait 503 retry).
   */
  private async postTool(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    try {
      // Floor the client fetch timeout above the bridge's own default wait so
      // the client never preempts the bridge's timeout envelope. An explicit
      // large timeout_ms still wins via the Math.max.
      const timeoutMs =
        typeof args.timeout_ms === "number" ? args.timeout_ms : 60_000;
      const fetchTimeout = Math.max(
        timeoutMs + 10_000,
        BRIDGE_DEFAULT_TIMEOUT_MS + 10_000,
      );

      const res = await this.fetchWithTimeout(
        `/tools/${toolName}`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json; charset=utf-8" },
          body: JSON.stringify(args),
        },
        fetchTimeout,
      );

      if (!res.ok) {
        // HTTP-level routing/transport fault (404/405/400/500). The body may
        // carry a structured { error: { code, message } }; fall back to a
        // generic bridge_http_error when it doesn't.
        const body = (await res
          .json()
          .catch(() => null)) as HttpErrorBody | null;
        const code = body?.error?.code ?? "bridge_http_error";
        const message =
          body?.error?.message ??
          `Bridge /tools/${toolName} returned HTTP ${res.status}`;
        return makeErrorResult({ code, message });
      }

      // HTTP 200 — unwrap the canonical { ok, result, error } envelope.
      const rawText = await res.text();
      let parsed: unknown = null;
      try {
        parsed = JSON.parse(rawText);
      } catch {
        // 200 OK with a non-JSON body is a bridge contract violation (the
        // bridge always emits valid JSON envelopes). Surface it as a structured
        // error rather than manufacturing a fake success.
        return makeErrorResult({
          code: "bridge_response_unparsable",
          message:
            `Bridge returned HTTP 200 for '${toolName}' but the body was not ` +
            `valid JSON. This is a bridge contract violation — the bridge ` +
            "should always emit a { ok, result, error } envelope. Raw body: " +
            (rawText.length > 200 ? rawText.slice(0, 200) + "…" : rawText),
        });
      }

      return this.unwrapEnvelope(toolName, parsed);
    } catch (err) {
      // Reuse the ping failure classifier: the same fetch-with-timeout +
      // bearer-token plumbing backs both paths, so a connection failure or
      // timeout classifies identically (bridge_offline vs bridge_timeout).
      return this.classifyPingFailure(err);
    }
  }

  /**
   * Unwrap the bridge's canonical `{ ok, result, error }` envelope into a
   * CallToolResult. Split out from `postTool` so the envelope-parsing contract
   * is unit-testable without spinning up an HTTP stub.
   *
   *   - `ok:true`  → success; `result` serialized verbatim as the text block.
   *                  A missing `result` field is treated as `null` (a handler
   *                  that returns no payload still produces a valid success).
   *   - `ok:false` → failure; the bridge's `error.code` / `error.message`
   *                  become the structured error. A missing `error` object is
   *                  surfaced as `execution_error` (defensive — the bridge
   *                  always emits `error` on `ok:false`).
   *   - neither    → a body that is not the canonical envelope. Surface as
   *                  `bridge_response_unparsable` so the contract drift is
   *                  visible rather than silently misframed.
   */
  private unwrapEnvelope(toolName: string, parsed: unknown): CallToolResult {
    if (typeof parsed !== "object" || parsed === null) {
      return makeErrorResult({
        code: "bridge_response_unparsable",
        message:
          `Bridge returned a non-object body for '${toolName}'. Expected ` +
          "{ ok, result, error }; got a " +
          (parsed === null ? "null" : typeof parsed) +
          ".",
      });
    }

    const env = parsed as Partial<BridgeSuccessEnvelope> &
      Partial<BridgeFailureEnvelope>;
    if (env.ok === true) {
      // Success. `result` may be any JSON value (object, array, scalar, null);
      // serialize it verbatim so the agent sees exactly what the handler
      // produced. A missing `result` is treated as null.
      const result = "result" in env ? env.result : null;
      return {
        content: [{ type: "text", text: JSON.stringify(result) }],
        isError: false,
      };
    }

    if (env.ok === false) {
      // Failure. The bridge always emits { code, message } under `error`; fall
      // back defensively if a malformed envelope omits it. When the bridge also
      // carries a `result` field (P4.3 partial-failure contract — e.g.
      // resource_move where the primary moved but the sidecar didn't), surface
      // the observed-state payload via `detail` so the agent can recover.
      const err = env.error ?? { code: "execution_error", message: undefined };
      const detail = "result" in env ? { error: err, result: env.result } : undefined;
      return makeErrorResult({
        code: err.code ?? "execution_error",
        message:
          err.message ??
          `Tool '${toolName}' failed with ok:false but no error message.`,
        detail,
      });
    }

    // `ok` is missing or not a boolean — contract drift.
    return makeErrorResult({
      code: "bridge_response_unparsable",
      message:
        `Bridge returned a body for '${toolName}' that is not the canonical ` +
        "{ ok, result, error } envelope (the `ok` field is missing or not a " +
        "boolean). This is a bridge contract violation.",
    });
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
