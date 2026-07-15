// Compile-aware bridge readiness polling, shared by `wait-for-ready` and `ping`.
//
// Adapted from Unity Open MCP's `mcp-server/src/cli/ping-poller.ts` (copy
// fidelity for the state machine + outcome shape). The intentional delta is the
// probe target: Unity routes the poll through the MCP server's LiveClient
// (`live.route("unity_open_mcp_ping")`); the Godot CLI has no MCP client in
// process — it fetches the bridge's `GET /ping` HTTP endpoint directly with an
// optional Bearer token discovered from the instance lock. The compile/dead/
// timeout semantics are otherwise identical.
//
// Polling the bridge is its own concern: it must respect compile state (a 503
// or `compiling: true` /ping is NOT readiness), tolerate transient network
// errors during an editor reload, and fail fast on a dead-bridge signature.
// Pulling it out of the command implementations keeps the command code thin and
// lets the poll loop be unit-tested with an injected clock + injected fetch.

import {
  readInstanceLock,
  classifyInstance,
  type InstanceLock,
} from "./instance-discovery.js";

/** Poll outcome. `ready: true` ⇒ exit 0; `ready: false` ⇒ caller exits non-zero. */
export interface PollOutcome {
  ready: boolean;
  /** One of: ready | compiling | offline | dead_bridge | timeout. */
  status: "ready" | "compiling" | "offline" | "dead_bridge" | "timeout";
  /** Last /ping body captured (when one ever succeeded). */
  lastPing: PingBody | null;
  /** Human-friendly reason for the outcome; shown in non-JSON mode. */
  reason: string;
  /** Elapsed wall time of the poll, in ms. */
  elapsedMs: number;
}

/**
 * Subset of the bridge `/ping` body the poller cares about. Mirrors the bridge
 * contract (`BridgeJson.BuildPingJson`) and the mcp-server `PingResponse`.
 */
export interface PingBody {
  connected?: boolean;
  compiling?: boolean;
  isPlaying?: boolean;
  projectPath?: string | null;
  godotVersion?: string | null;
  bridgeVersion?: string;
  mode?: string;
}

/** Default overall wait budget. Matches Unity's DEFAULT_WAIT_TIMEOUT_MS. */
export const DEFAULT_WAIT_TIMEOUT_MS = 120_000;
/** Default sleep between polls. Matches Unity's DEFAULT_POLL_INTERVAL_MS. */
export const DEFAULT_POLL_INTERVAL_MS = 1_000;
/** Per-fetch timeout for the /ping HTTP probe. */
export const PING_FETCH_TIMEOUT_MS = 5_000;

export interface PollOptions {
  /** Total budget for the wait, in ms. */
  timeoutMs: number;
  /** Sleep between polls, in ms. */
  intervalMs: number;
  /** Per-fetch timeout for the /ping probe, in ms. */
  fetchTimeoutMs?: number;
  /** Wall clock used for deadline checks; injectable for tests. */
  now?: () => number;
  /** Sleep implementation; injectable for tests. Default: setTimeout. */
  sleep?: (ms: number) => Promise<void>;
}

function defaultSleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

export interface ProbeTarget {
  /** Resolved bridge port (deterministic / lock / env override). */
  port: number;
  /** Base URL — `http://127.0.0.1:${port}`. */
  baseUrl: string;
  /** Bearer token from the instance lock, when present. */
  authToken?: string;
}

/**
 * Poll the bridge until it is ready (connected, not compiling) or the deadline
 * passes. A 503 from /ping and `compiling: true` both keep the wait alive —
 * "ready" means usable, not just listening. The poll fails fast on a
 * dead-bridge signature (live PID, stale heartbeat) because /ping will never
 * recover in that state.
 *
 * @param target        The resolved bridge endpoint to probe.
 * @param projectPath   Project path, used to read the instance lock for
 *                      dead-bridge classification. Optional; when absent the
 *                      dead-bridge shortcut is skipped.
 * @param singlePoll    One /ping attempt. Returns `{ status, body }`: `status`
 *                      `"ready"` only when connected AND not compiling.
 *                      Indirected so tests can stub the network hop.
 * @param opts          Timing + clock injection.
 */
export async function pollUntilReady(
  target: ProbeTarget,
  projectPath: string | undefined,
  singlePoll: (target: ProbeTarget) => Promise<SinglePollResult>,
  opts: PollOptions,
): Promise<PollOutcome> {
  const now = opts.now ?? Date.now;
  const sleep = opts.sleep ?? defaultSleep;
  const start = now();
  const deadline = start + opts.timeoutMs;

  let lastPing: PingBody | null = null;
  let sawCompiling = false;
  let sawOffline = false;

  while (true) {
    const t = now();
    if (t >= deadline) {
      return {
        ready: false,
        status: "timeout",
        lastPing,
        elapsedMs: t - start,
        reason: formatTimeoutReason(opts.timeoutMs, sawCompiling, sawOffline),
      };
    }

    const result = await singlePoll(target);

    if (result.body) lastPing = result.body;
    if (result.status === "compiling") sawCompiling = true;
    if (result.status === "offline" || result.status === "error") {
      sawOffline = true;
    }

    if (result.status === "ready") {
      return {
        ready: true,
        status: "ready",
        lastPing: result.body ?? lastPing,
        elapsedMs: now() - start,
        reason: "Bridge is ready (connected, idle).",
      };
    }

    // Dead-bridge signature: the Godot process is alive but the bridge's plugin
    // _EnterTree never re-ran after a compile failure. /ping will not recover
    // until the C# error is fixed, so waiting is pointless.
    if (projectPath) {
      let classification;
      try {
        classification = classifyInstance(readInstanceLock(projectPath));
      } catch {
        classification = null;
      }
      if (classification === "dead_bridge") {
        return {
          ready: false,
          status: "dead_bridge",
          lastPing,
          elapsedMs: now() - start,
          reason:
            "Bridge plugin failed to reload — Godot is sitting on compile " +
            "errors (the bridge HTTP listener never restarted). Open the " +
            "Godot editor, fix the C# errors, then re-run wait-for-ready.",
        };
      }
    }

    await sleep(Math.min(opts.intervalMs, Math.max(0, deadline - now())));
  }
}

export interface SinglePollResult {
  /** `ready` only when connected AND not compiling. */
  status: "ready" | "compiling" | "offline" | "error";
  body: PingBody | null;
}

/**
 * One bridge `GET /ping` attempt translated into a poll status. Centralizes
 * the "connected AND not compiling" readiness rule so every command agrees on
 * it.
 *
 * Implementation: direct `fetch` against `http://127.0.0.1:${port}/ping` with
 * the discovered Bearer token attached (when present). HTTP 503 (bridge
 * listener up, session not ready) is treated as reachable-but-compiling, NOT
 * offline — the 503 fallback body carries `compiling:true` so the poller keeps
 * waiting. ECONNREFUSED / socket failures (TypeError) → offline. AbortController
 * timeout → treated as error (keep waiting — the bridge may be slow mid-reload).
 *
 * @param target         The resolved bridge endpoint.
 * @param fetchImpl      fetch override (tests). Defaults to the global fetch.
 * @param fetchTimeoutMs Per-attempt timeout (default PING_FETCH_TIMEOUT_MS).
 */
export async function singlePing(
  target: ProbeTarget,
  fetchImpl: typeof fetch = fetch,
  fetchTimeoutMs: number = PING_FETCH_TIMEOUT_MS,
): Promise<SinglePollResult> {
  try {
    const res = await fetchWithTimeout(
      target,
      "/ping",
      { method: "GET" },
      fetchImpl,
      fetchTimeoutMs,
    );

    // 503 = bridge listener up, BridgeSession not initialized yet. The body is
    // a valid PingResponse-shape JSON with compiling:true; treat as compiling.
    if (res.status === 503) {
      const fallback = (await res.json().catch(() => null)) as PingBody | null;
      const body = fallback ?? { connected: false, compiling: true, isPlaying: false };
      return { status: "compiling", body };
    }

    if (!res.ok) {
      // Any other non-200 status — treat as offline (keep polling). The bridge
      // may be mid-reload or the auth mode may reject the probe; either way it
      // is not ready yet.
      return { status: "offline", body: null };
    }

    const body = (await res.json().catch(() => null)) as PingBody | null;
    if (!body) {
      // 200 OK but no parseable body — treat as compiling/unknown and keep polling.
      return { status: "compiling", body: null };
    }
    if (body.compiling === true) {
      return { status: "compiling", body };
    }
    if (body.connected === false) {
      return { status: "offline", body };
    }
    return { status: "ready", body };
  } catch (err) {
    // AbortController timeout surfaces as a DOMException named "AbortError" —
    // the bridge is reachable but slow. Treat as error (keep waiting) rather
    // than offline so a transient slow poll doesn't mis-classify the bridge as
    // down. Anything else (TypeError "fetch failed" / ECONNREFUSED) is offline.
    const isTimeout = err instanceof DOMException && err.name === "AbortError";
    return isTimeout ? { status: "error", body: null } : { status: "offline", body: null };
  }
}

/**
 * fetch() with an AbortController-backed timeout and the bearer token attached.
 * Mirrors the mcp-server LiveClient.fetchWithTimeout shape (copy fidelity): the
 * timeout fires via `AbortController.abort()`, surfacing as a DOMException
 * named "AbortError". The auth header is attached when the probe target
 * carries a token.
 */
async function fetchWithTimeout(
  target: ProbeTarget,
  urlPath: string,
  init: RequestInit,
  fetchImpl: typeof fetch,
  timeoutMs: number,
): Promise<Response> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);

  const headers = new Headers(init.headers);
  if (target.authToken && !headers.has("Authorization")) {
    headers.set("Authorization", `Bearer ${target.authToken}`);
  }

  try {
    return await fetchImpl(`${target.baseUrl}${urlPath}`, {
      ...init,
      headers,
      signal: controller.signal,
    });
  } finally {
    clearTimeout(timer);
  }
}

function formatTimeoutReason(
  timeoutMs: number,
  sawCompiling: boolean,
  sawOffline: boolean,
): string {
  if (sawCompiling && !sawOffline) {
    return `Bridge was still compiling after ${Math.round(timeoutMs / 1000)}s.`;
  }
  if (sawOffline && !sawCompiling) {
    return `Bridge never became reachable within ${Math.round(timeoutMs / 1000)}s.`;
  }
  return `Bridge did not become ready within ${Math.round(timeoutMs / 1000)}s.`;
}

/** Re-exported for command code / tests. */
export type { InstanceLock };
