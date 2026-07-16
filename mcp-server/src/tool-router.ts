// P7.1 — the MCP-server-side routing authority.
//
// `index.ts` validates tool registration (unknown names → structured error
// BEFORE the router is ever called) and then hands every registered call to
// `ToolRouter.route`. The router picks an execution policy per call:
//
//   registered call
//     → exact local/offline handler (when declared in the named-handler map)
//     → otherwise the generic live route:  LiveClient.route → bridge
//
// P7.1 ships three local named handlers (capabilities, bridge_status,
// pull_events) plus the generic live fallback. P7.2–P7.4 extend the map with
// offline handlers — they MUST NOT add new tool-name branches to `index.ts`.
//
// Every JSON result is tagged with two MCP-server-owned metadata fields so an
// agent can answer "where did this originate?" (`_source`) and "which routing
// policy ran it?" (`_route.route`). The metadata is added AFTER the route
// completes; it is never sent to a bridge handler (local handlers build it
// into their bodies directly; live results are tagged on the way out).
//
// Adapted from Unity Open MCP's `mcp-server/src/tool-router.ts` (copy overall;
// adapt route vocabulary). Intentional deltas:
//   - No `BatchSpawn` / batch route / `ALWAYS_BATCH_TOOLS` / batch ping
//     synthesis. Godot has no headless editor batch equivalent.
//   - No Hub routes (no Godot Hub equivalent).
//   - No compressible cache / offline handlers yet — those arrive in P7.2–P7.4.
//   - Route names are `live | offline | local` only (no `batch`).
//   - `LiveClient` and `BridgeEventStream` are the only wired dependencies;
//     `ToolSessionState` / Hub backend arrive with later phases.

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";
import type { LiveClient } from "./live-client.js";
import type { BridgeEventStream } from "./event-stream.js";
import { ALL_TOOLS } from "./tools/index.js";
import { buildCapabilities, type CapabilitiesFilter } from "./capabilities/build-capabilities.js";
import { RULE_CATALOG, FIX_CATALOG } from "./capabilities/rule-catalog.js";

// ---------------------------------------------------------------------------
// Route vocabulary + metadata helpers (P7.1 §2).
// ---------------------------------------------------------------------------

/**
 * Where a payload originated. `live` = bridge round-trip; `offline` = local
 * disk parser (P7.2–P7.4); `local` = MCP-server-synthesized with no Godot
 * editor involvement. There is deliberately NO `batch` value — Godot has no
 * headless editor batch equivalent (intentional delta from Unity).
 */
export type SourceTag = "live" | "offline" | "local";

/**
 * Route names mirror {@link SourceTag} for P7.1. Later phases may emit a
 * `fallbackReason` (e.g. an offline-first tool that fell back because the live
 * bridge was unavailable).
 */
export type RouteName = SourceTag;

/**
 * Route metadata attached to every parseable JSON result. `fallbackReason` is
 * optional and only present when a route fell back from its preferred policy.
 */
export interface RouteMeta {
  route: RouteName;
  fallbackReason?: string;
}

/**
 * Tag a freshly synthesized local/offline body with `_source` and `_route`.
 * Use this when the handler builds its own body from scratch (capabilities,
 * pull_events). For an existing `CallToolResult` produced by another hop
 * (LiveClient, future offline parsers), use {@link tagJsonResult}.
 */
export function sourceResult(
  body: object,
  source: SourceTag,
  routeMeta: RouteMeta,
  isError = false,
): CallToolResult {
  const tagged = withSource(body, source, routeMeta);
  return {
    content: [{ type: "text", text: JSON.stringify(tagged) }],
    isError,
  };
}

/**
 * Stamp `_source` + `_route` onto a body object. The single authority for
 * origin tags on newly built bodies. Asserts (in tests) rather than silently
 * masking drift when the body already carries a CONFLICTING `_source` /
 * `_route` — a matching value (e.g. `bridge_status` pre-bakes `_source: "local"`
 * into its body, and the route is also `local`) is preserved untouched.
 */
export function withSource(
  body: object,
  source: SourceTag,
  routeMeta: RouteMeta,
): Record<string, unknown> {
  const record = body as Record<string, unknown>;
  assertNoSourceConflict(record, source);
  const out: Record<string, unknown> = { ...record };
  if (!("_source" in out)) out._source = source;
  out._route = routeMeta;
  return out;
}

/**
 * Tag an existing `CallToolResult` produced by another hop with route
 * metadata. Locates the first text content block carrying a parseable JSON
 * object and injects `_source` + `_route` into it, preserving every content
 * block (including image / audio blocks and any non-text blocks), `isError`,
 * and unknown top-level result fields. If no text block carries a parseable
 * JSON object, the result is returned UNCHANGED — never drop content.
 */
export function tagJsonResult(
  result: CallToolResult,
  source: SourceTag,
  routeMeta: RouteMeta,
): CallToolResult {
  if (result.content.length === 0) return result;
  // P4.8 — screenshot results carry an MCP image block followed by a text
  // metadata block. Inject _route into whichever text block carries JSON; leave
  // image / other blocks untouched. Fall back to the first block when it is
  // text (the common single-block path).
  const textIndex = result.content.findIndex((c) => c.type === "text");
  if (textIndex < 0) return result;
  const block = result.content[textIndex];
  if (block.type !== "text") return result;
  let body: Record<string, unknown>;
  try {
    const parsed = JSON.parse(block.text);
    if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
      return result;
    }
    body = parsed as Record<string, unknown>;
  } catch {
    // Non-JSON text (e.g. a raw string result) — preserve unchanged.
    return result;
  }
  const tagged = withSource(body, source, routeMeta);
  const newContent = result.content.slice();
  newContent[textIndex] = { type: "text", text: JSON.stringify(tagged) };
  return { ...result, content: newContent };
}

/**
 * Reject a body that already carries a CONFLICTING `_source` / `_route`.
 * During P7 this surfaces drift loudly rather than silently masking it (a
 * local handler that pre-bakes the wrong origin tag, or a live result that
 * already carries an offline tag, is a programmer mistake). A MATCHING value
 * is allowed — `bridge_status` legitimately pre-bakes `_source: "local"` into
 * its body, and the route is also `local`.
 */
function assertNoSourceConflict(
  body: Record<string, unknown>,
  source: SourceTag,
): void {
  const existingSource = body._source;
  if (
    existingSource !== undefined &&
    existingSource !== source
  ) {
    throw new Error(
      `ToolRouter source-tag conflict: body already carries _source=` +
        `'${String(existingSource)}' but route is '${source}'. ` +
        "This is a routing drift — fix the handler rather than masking it.",
    );
  }
  const existingRoute = body._route;
  if (
    existingRoute !== undefined &&
    typeof existingRoute === "object" &&
    existingRoute !== null &&
    "route" in existingRoute &&
    (existingRoute as { route?: unknown }).route !== source
  ) {
    throw new Error(
      `ToolRouter route-tag conflict: body already carries _route.route=` +
        `'${String((existingRoute as { route?: unknown }).route)}' but route is '${source}'.`,
    );
  }
}

// ---------------------------------------------------------------------------
// ToolRouter.
// ---------------------------------------------------------------------------

/** Tool names handled locally by the MCP server (no bridge hop). */
const CAPABILITIES_TOOL = "godot_open_mcp_capabilities";
const BRIDGE_STATUS_TOOL = "godot_open_mcp_bridge_status";
const PULL_EVENTS_TOOL = "godot_open_mcp_pull_events";

/**
 * ToolRouter selects live / offline / local per tool call. One instance per
 * stdio server process; the same `LiveClient` + `BridgeEventStream` wired in
 * `main()` feed every call. Constructed dependencies are optional only so unit
 * tests for individual handlers do not need to spin them all up — production
 * `main()` always supplies both.
 */
export class ToolRouter implements Router {
  constructor(
    private live: LiveClient,
    private projectPath: string,
    private eventStream: BridgeEventStream,
  ) {}

  async route(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    // P7.1 named-handler map: the three local tools resolved without a
    // POST /tools/{name} bridge hop. Every other registered tool name falls
    // through to the generic live route. P7.2–P7.4 extend this with offline
    // exact handlers; they MUST NOT add new branches to index.ts.
    if (toolName === CAPABILITIES_TOOL) {
      return this.routeCapabilities(args);
    }
    if (toolName === BRIDGE_STATUS_TOOL) {
      return this.routeBridgeStatus();
    }
    if (toolName === PULL_EVENTS_TOOL) {
      return this.routePullEvents(args);
    }
    return this.routeLive(toolName, args);
  }

  // ── generic live route ─────────────────────────────────────────────────

  /**
   * The generic live route. Dispatches through `LiveClient.route` (which
   * selects `GET /ping` for ping and `POST /tools/{name}` for everything else)
   * and tags the result with live route metadata. Preserves multi-block
   * results (screenshot image + text metadata) and structured errors verbatim;
   * the only addition is `_source` + `_route`.
   */
  private async routeLive(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const result = await this.live.route(toolName, args);
    return tagJsonResult(result, "live", { route: "live" });
  }

  // ── local named handlers ───────────────────────────────────────────────

  /**
   * `godot_open_mcp_capabilities` — built locally over the full tool + rule +
   * fix catalog. No bridge round-trip, no `LiveClient` dependency. The `kind`
   * and `include_planned` filters pass through byte-for-byte.
   */
  private async routeCapabilities(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const filter: CapabilitiesFilter = {};
    if (
      args.kind === "tools" ||
      args.kind === "rules" ||
      args.kind === "fixes"
    ) {
      filter.kind = args.kind;
    }
    if (typeof args.include_planned === "boolean") {
      filter.includePlanned = args.include_planned;
    }
    const result = buildCapabilities(
      { tools: ALL_TOOLS, rules: RULE_CATALOG, fixes: FIX_CATALOG },
      filter,
    );
    return sourceResult(result, "local", { route: "local" });
  }

  /**
   * `godot_open_mcp_bridge_status` — local/live hybrid. Composes the instance-
   * lock classifier with one `/ping` probe driven through `LiveClient` (so the
   * auth header + 503-fallback path match `godot_open_mcp_ping`). The
   * synthesis happens in the MCP server (no `POST /tools/bridge_status`
   * endpoint on the bridge); its body already pre-bakes `_source: "local"`,
   * which matches this route — `tagJsonResult` only adds `_route`.
   *
   * Read-only, gate-free, never errors on an offline bridge — `stopped` /
   * `unreachable` / `dead_bridge` ARE the answer in those cases. A missing
   * `LiveClient` (test-harness omission) surfaces a structured `router_not_wired`
   * error so a malformed test never throws.
   */
  private async routeBridgeStatus(): Promise<CallToolResult> {
    const result = await this.live.routeBridgeStatus();
    // The body pre-bakes `_source: "local"`; withSource allows the match and
    // only adds `_route`.
    return tagJsonResult(result, "local", { route: "local" });
  }

  /**
   * `godot_open_mcp_pull_events` — drains the per-process `BridgeEventStream`
   * (a single SSE subscription to the bridge's `GET /events` endpoint). No
   * `POST /tools/pull_events` endpoint on the bridge. The stream is injected
   * from `main()` and shared across calls so every pull amortizes one
   * connection. `connected: false` on an offline bridge is a successful status
   * result, not a routing error. The source is `local` because the call drains
   * an MCP-process queue; the queue is fed by a live SSE stream.
   *
   * `max_events` defaults to 50 and is hard-capped at 1000 (the same bounds
   * the in-process dispatcher enforced before P7.1). A missing stream
   * (test-harness omission) surfaces a structured `router_not_wired` error.
   */
  private async routePullEvents(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const rawMax = args.max_events;
    const maxEvents =
      typeof rawMax === "number" && rawMax > 0
        ? Math.min(rawMax, 1000)
        : 50;
    const result = this.eventStream.pull(maxEvents);
    return sourceResult(result, "local", { route: "local" });
  }
}
