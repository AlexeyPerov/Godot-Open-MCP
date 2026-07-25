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
  classifyInstance,
  isPidAlive,
  type InstanceLock,
} from "./instance-discovery.js";
import {
  deriveBridgeStatus,
  bridgeStatusRecoveryHint,
  bridgeStatusNextStep,
  summarizeBridgeStatusLock,
  type PingProbe,
} from "./tools/bridge-status-derive.js";

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
   * Cheap live-availability probe for the router's live-first tools (P7.2).
   * Fetches `GET /ping` and reports whether the bridge answered as connected:
   *   - HTTP 200 + `connected: true` → `true`.
   *   - HTTP 503 (listener up, session not ready) → `true`. The bridge IS
   *     reachable; the router forwards to the live route so the agent sees the
   *     bridge's own loading/compiling state rather than a disk fallback.
   *   - HTTP other / connection failure / timeout → `false`.
   *
   * Never throws. Adapted from Unity Open MCP's `LiveClient.isLiveAvailable`
   * (copy fidelity for the 200/503/offline classification). Intentional delta:
   * Godot has no ping cache / compat-warning side effects on this path (those
   * arrive when a later phase adds the ping cache). The router treats `false`
   * as "fall back to offline"; a live semantic error (e.g. `scene_not_edited`)
   * is authoritative and must NOT trigger a fallback — only an unreachable
   * bridge does.
   */
  async isLiveAvailable(): Promise<boolean> {
    try {
      return await this.fetchWithTimeout(
        "/ping",
        { method: "GET" },
        PING_TIMEOUT_MS,
        async (res) => {
          if (res.status === 503) return true;
          if (!res.ok) return false;
          const body = (await res.json()) as PingResponse;
          return body.connected === true;
        },
      );
    } catch {
      return false;
    }
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
      return await this.fetchWithTimeout(
        "/ping",
        { method: "GET" },
        PING_TIMEOUT_MS,
        (res) => this.readPingResponse(res),
      );
    } catch (err) {
      return this.classifyPingFailure(err);
    }
  }

  /**
   * Body-reading half of {@link handlePing}. Runs inside the fetch timeout scope (see
   * {@link fetchWithTimeout}) so a stalled body is aborted rather than hanging the call.
   */
  private async readPingResponse(res: Response): Promise<CallToolResult> {
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
        // Read the body so a structured error code the bridge emits (e.g. "unauthorized"
        // under authMode "required" — P5.2) is surfaced verbatim rather than collapsed to a
        // generic bridge_http_error. Falls back to bridge_http_error when the body is absent
        // or not the { error: { code, message } } shape. Mirrors the postTool non-OK handling.
        const errBody = (await res.json().catch(() => null)) as HttpErrorBody | null;
        const code = errBody?.error?.code ?? "bridge_http_error";
        const message =
          errBody?.error?.message ??
          `Bridge /ping returned unexpected HTTP ${res.status}. Endpoint: ${this.baseUrl}.`;
        return makeErrorResult({ code, message });
      }

      // Guard the parse locally. An unguarded res.json() on a 200 falls into the outer catch, where
      // classifyPingFailure has no way to tell a parse failure from a connect failure and reports
      // `bridge_offline` — telling the operator to launch a Godot editor that is already running,
      // and making bridge_status derive `stopped` for a reachable bridge. postTool already
      // distinguishes this case as bridge_response_unparsable; match it.
      let body: PingResponse;
      try {
        body = (await res.json()) as PingResponse;
      } catch {
        return makeErrorResult({
          code: "bridge_response_unparsable",
          message:
            `Bridge /ping returned HTTP 200 but the body was not valid JSON. Endpoint: ${this.baseUrl}. ` +
            `The listener is reachable, so this is a bridge-side response fault rather than an offline editor.`,
        });
      }
      return {
        content: [{ type: "text", text: JSON.stringify(body) }],
        isError: false,
      };
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

      // Body reads happen inside the timeout scope — see fetchWithTimeout.
      return await this.fetchWithTimeout(
        `/tools/${toolName}`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json; charset=utf-8" },
          body: JSON.stringify(args),
        },
        fetchTimeout,
        async (res) => {
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
        },
      );
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
      // P4.8 — screenshot image envelope. When the result is an object carrying
      // `mediaType: "image/png"` and a non-empty base64 `data` string, unwrap it
      // into an MCP image content block plus a short text metadata block (the
      // metadata = result minus the base64 `data` field, so the agent gets the
      // width/height/byteLength/mode/caption/clamped fields without the base64
      // blob duplicated in a text block). The base64 payload never appears
      // inside a text JSON block on this path. Error responses (ok:false) stay
      // structured text errors. Adapted from Unity Open MCP's
      // live-client.ts `inlineImage` unwrap (copy fidelity for the image-block
      // shape), generalized to the `mediaType`+`data` envelope naming.
      if (
        result !== null &&
        typeof result === "object" &&
        !Array.isArray(result)
      ) {
        const r = result as Record<string, unknown>;
        const mediaType = r.mediaType;
        const data = r.data;
        if (
          typeof mediaType === "string" &&
          mediaType === "image/png" &&
          typeof data === "string" &&
          data.length > 0
        ) {
          const metadata: Record<string, unknown> = { ...r };
          delete metadata.data;
          return {
            content: [
              { type: "image", data, mimeType: mediaType },
              { type: "text", text: JSON.stringify(metadata) },
            ],
            isError: false,
          };
        }
      }
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
  private async fetchWithTimeout<T>(
    path: string,
    init: RequestInit,
    timeoutMs: number,
    consume: (res: Response) => Promise<T>,
  ): Promise<T> {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);

    const headers = new Headers(init.headers);
    if (this.authToken && !headers.has("Authorization")) {
      headers.set("Authorization", `Bearer ${this.authToken}`);
    }

    // The timeout must cover the response BODY, not just the headers.
    //
    // `fetch` resolves as soon as the response headers arrive, so clearing the timer when that
    // promise settles left every `res.json()` / `res.text()` unprotected and made the AbortSignal
    // unable to fire. A bridge that flushed 200 headers and then stalled the body (half-open socket
    // after sleep/wake, editor crash mid-write, a large screenshot payload over a stalling
    // connection) would hang the MCP call until undici's 300s body timeout — or indefinitely on a
    // keep-alive socket. That silently voided the deliberate BRIDGE_DEFAULT_TIMEOUT_MS floor below
    // and made `bridge_status` — the one tool whose job is to answer fast when the bridge is
    // unhealthy — hang instead.
    //
    // Callers therefore read the body inside `consume`, and the timer is only cleared once that
    // has finished.
    try {
      const res = await fetch(`${this.baseUrl}${path}`, {
        ...init,
        headers,
        signal: controller.signal,
      });
      return await consume(res);
    } finally {
      clearTimeout(timer);
    }
  }

  // ── P5.3 — `godot_open_mcp_bridge_status` composition ──────────────────
  //
  // Combines the instance-lock classifier (the same pure function the rest of
  // the client uses for dead-bridge awareness) with one /ping probe driven
  // through {@link handlePing} so the auth header + 503-fallback path match
  // what `godot_open_mcp_ping` sees. The coarse `status` token is derived by
  // the pure mapper in `tools/bridge-status-derive.ts` — this method only
  // collects the signals (lock + ping) and shapes the response.
  //
  // The tool never reports an MCP error on an offline bridge — `stopped` /
  // `unreachable` / `dead_bridge` ARE the answer in those cases. A thrown
  // error here would be a programmer mistake (e.g. malformed project root).
  // `_source: "local"` tags the response as MCP-server-synthesized (no bridge
  // tool endpoint, no Godot editor spawn). Read-only + gate-free.
  //
  // Adapted from Unity Open MCP's ToolRouter.routeBridgeStatus (copy fidelity
  // for the lock → classify → ping → derive flow + the response shape).
  // Intentional deltas: no `findUnityForProject` cold-Safe-Mode scan (no
  // equivalent out-of-band Godot process scan today); recovery tool is
  // `godot_open_mcp_console_get_logs` (the closest registered diagnostic)
  // rather than Unity's offline `read_compile_errors` (planned here later).
  async routeBridgeStatus(): Promise<CallToolResult> {
    const lockOnDisk = this.projectPath ? lockPath(this.projectPath) : null;
    let lock: InstanceLock | null = null;
    try {
      lock = this.projectPath ? readInstanceLock(this.projectPath) : null;
    } catch {
      // Unreadable lock → treat as no lock (gone). readInstanceLock itself
      // never throws (it returns null on missing/unreadable/unparseable), but
      // guard anyway so a filesystem error can never crash the status read.
      lock = null;
    }
    const classification = classifyInstance(lock);

    // One /ping probe through the same handler `godot_open_mcp_ping` uses, so
    // auth + 503-fallback + offline classification are identical. The ping
    // result is either a success (body in content[0].text) or an isError
    // structured error (bridge_offline / bridge_timeout / bridge_http_error).
    const pingResult = await this.handlePing();
    const pingProbe = normalizePingProbe(pingResult);
    const lockPidAlive = lock !== null && isPidAlive(lock.pid);

    const status = deriveBridgeStatus({
      classification,
      ping: pingProbe,
      lockPidAlive,
    });

    const body = {
      status,
      // Coarse ready flag for clients that want a single boolean: true only
      // when the bridge is connected AND idle (the rule a future
      // wait-for-ready CLI poll would terminate on).
      ready: status === "running",
      // Mirrored at the top level so agents can branch on one field without
      // digging into the `instance` sub-object.
      classification,
      recoveryHint: bridgeStatusRecoveryHint(status),
      projectPath: this.projectPath ?? null,
      instance: {
        lockPath: lockOnDisk,
        classification,
        lock: lock ? summarizeBridgeStatusLock(lock) : null,
      },
      ping: pingProbe.reachable
        ? {
            reachable: true,
            connected: pingProbe.connected,
            compiling: pingProbe.compiling,
            isPlaying: pingProbe.isPlaying,
            godotVersion: pingProbe.godotVersion,
            bridgeVersion: pingProbe.bridgeVersion,
            mode: pingProbe.mode,
          }
        : { reachable: false },
      nextStep: bridgeStatusNextStep(status),
      _source: "local",
    };

    return {
      content: [{ type: "text", text: JSON.stringify(body) }],
      isError: false,
    };
  }
}

/**
 * Normalize a `handlePing` CallToolResult into the {@link PingProbe} shape the
 * pure mapper consumes. Success results carry the PingResponse body as the
 * first text block; error results (bridge_offline / bridge_timeout /
 * bridge_http_error) mean the ping was unreachable. A non-JSON or non-object
 * body falls through to unreachable so the mapper picks `stopped` /
 * `unreachable` rather than throwing.
 *
 * Standalone (module-private) so the bridge-status unit tests can drive the
 * mapper directly without constructing a CallToolResult.
 */
function normalizePingProbe(result: CallToolResult): PingProbe {
  if (result.isError) {
    return unreachableProbe();
  }
  const first = result.content[0];
  if (!first || first.type !== "text" || typeof first.text !== "string") {
    return unreachableProbe();
  }
  let parsed: unknown = null;
  try {
    parsed = JSON.parse(first.text);
  } catch {
    return unreachableProbe();
  }
  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
    return unreachableProbe();
  }
  const body = parsed as Partial<PingResponse>;
  return {
    reachable: true,
    connected: typeof body.connected === "boolean" ? body.connected : null,
    compiling: typeof body.compiling === "boolean" ? body.compiling : null,
    isPlaying: typeof body.isPlaying === "boolean" ? body.isPlaying : null,
    godotVersion: typeof body.godotVersion === "string" ? body.godotVersion : null,
    bridgeVersion: typeof body.bridgeVersion === "string" ? body.bridgeVersion : null,
    mode: typeof body.mode === "string" ? body.mode : null,
  };
}

function unreachableProbe(): PingProbe {
  return {
    reachable: false,
    connected: null,
    compiling: null,
    isPlaying: null,
    godotVersion: null,
    bridgeVersion: null,
    mode: null,
  };
}
