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
// P7.5 — the named-handler constants below are imported from the shared
// route-policy module so the router's dispatch list and the capability
// catalog's advertised policies cannot drift. The parity test in
// route-policy.test.ts asserts every non-`live` policy tool has a matching
// named handler here (and vice versa).
import {
  CAPABILITIES_TOOL,
  BRIDGE_STATUS_TOOL,
  PULL_EVENTS_TOOL,
  MANAGE_TOOLS_TOOL,
  SCENE_GET_DATA_TOOL,
  FILESYSTEM_LIST_TOOL,
  READ_COMPILE_ERRORS_TOOL,
} from "./capabilities/route-policy.js";
import {
  TOOL_GROUPS,
  GROUP_IDS,
  toolsInGroup,
} from "./capabilities/tool-groups.js";
import type { ToolSessionState } from "./tool-session-state.js";
import { readSceneGetDataOffline } from "./offline/scene-get-data.js";
import { listProjectDirectoryOffline } from "./offline/project-index.js";
import { identifyGodotProject } from "./offline/project-config.js";
import { readFile } from "node:fs/promises";
import {
  parseProjectLogSettings,
  godotUserDataRoot,
  resolveLogPaths,
  readLogTail,
  selectLog,
  detectStaleLog,
  DEFAULT_LOG_TAIL_BYTES,
  MIN_LOG_TAIL_BYTES,
  MAX_LOG_TAIL_BYTES,
  DEFAULT_MAX_LOG_FILES,
  LOG_FILE_ENV_OVERRIDE,
  type GodotLogPlatform,
} from "./godot-log.js";
import { extractCompileDiagnostics } from "./compiler-errors.js";

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

/**
 * Tool names handled by a named route in the router. P7.5 imports these from
 * the shared `route-policy.ts` module so the dispatch list and the capability
 * catalog's `routePolicy` field agree. The parity test in
 * `route-policy.test.ts` asserts every non-`live` policy tool has a matching
 * named handler here (and vice versa).
 *
 * Handler semantics (kept here because the dispatch logic lives in this file):
 *   - `CAPABILITIES_TOOL` / `BRIDGE_STATUS_TOOL` / `PULL_EVENTS_TOOL` — local
 *     (no bridge hop). bridge_status + pull_events may touch the live transport
 *     but synthesize the response in-process.
 *   - `SCENE_GET_DATA_TOOL` / `FILESYSTEM_LIST_TOOL` — live-first with offline
 *     fallback (probe once; forward live when reachable, else read disk).
 *   - `READ_COMPILE_ERRORS_TOOL` — always offline (never probes the bridge).

/**
 * ToolRouter selects live / offline / local per tool call. One instance per
 * stdio server process; the same `LiveClient` + `BridgeEventStream` wired in
 * `main()` feed every call. Constructed dependencies are optional only so unit
 * tests for individual handlers do not need to spin them all up — production
 * `main()` always supplies both.
 *
 * `sessionState` (P8.3) is the per-session tool-group visibility store that
 * ListTools filters through and `manage_tools` mutates. The same instance is
 * shared between ListTools and the router so an activate/deactivate call is
 * visible to the next ListTools response. Constructing two stores would desync
 * the two surfaces; the factory in `index.ts` constructs exactly one.
 *
 * `notifyToolListChanged` (P8.4) is the optional callback fired when an
 * activate/deactivate/reset call changes the visible tool set. The bootstrap in
 * `index.ts` wires the real `notifications/tools/list_changed` emitter (a
 * closure over the SDK Server). The router fires it ONLY on an actual change —
 * an idempotent activate is a no-op for state AND for the notification. Notify
 * failures are isolated: a rejecting notifier is swallowed + logged so the
 * manage_tools result is never flipped to `isError` by a transport fault.
 */
export class ToolRouter implements Router {
  constructor(
    private live: LiveClient,
    private projectPath: string,
    private eventStream: BridgeEventStream,
    private sessionState: ToolSessionState,
    private notifyToolListChanged?: () => void | Promise<void>,
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
    if (toolName === SCENE_GET_DATA_TOOL) {
      return this.routeSceneGetData(args);
    }
    if (toolName === FILESYSTEM_LIST_TOOL) {
      return this.routeFilesystemList(args);
    }
    if (toolName === READ_COMPILE_ERRORS_TOOL) {
      return this.routeReadCompileErrors(args);
    }
    if (toolName === MANAGE_TOOLS_TOOL) {
      return this.routeManageTools(args);
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

  // ── P7.2 — live-first `scene_get_data` with offline fallback ───────────

  /**
   * `godot_open_mcp_scene_get_data` — the first live-first/offline-fallback
   * tool. Routing policy (P7.2 spec §7):
   *
   *   1. Probe `live.isLiveAvailable()` once. If the bridge answered as
   *      connected (or returned 503 — reachable but loading), forward the
   *      unchanged args to the live handler and tag the result `live`. The
   *      live read reflects unsaved editor state, which the disk parse cannot.
   *   2. If the bridge is unavailable, require a `path` argument (the offline
   *      reader has no "edited scene" context to fall back on). Missing
   *      `path` → `path_required_offline`.
   *   3. Run the offline parser and tag the result `_source: "offline"` +
   *      `_route: { route: "offline", fallbackReason: "live_unavailable" }`.
   *   4. NEVER fall back to disk after a semantic live error (e.g.
   *      `scene_not_edited`) — the live editor answered authoritatively; only
   *      an unreachable bridge triggers the fallback.
   *   5. NEVER fall back on an auth failure or malformed bridge response —
   *      those surface to the caller as live errors, not as "unavailable".
   *
   * `isLiveAvailable` returns false only for connection failure / non-503 HTTP
   * errors / `connected: false` bodies — exactly the unavailability signal the
   * fallback keys on. A missing `LiveClient` (test-harness omission) surfaces
   * a structured `router_not_wired` error so a malformed test never throws.
   */
  private async routeSceneGetData(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    if (typeof this.live.isLiveAvailable !== "function") {
      return sourceResult(
        {
          error: {
            code: "router_not_wired",
            message:
              "ToolRouter is not wired to a LiveClient with isLiveAvailable().",
          },
        },
        "offline",
        { route: "offline", fallbackReason: "router_not_wired" },
        true,
      );
    }

    const liveAvailable = await this.live.isLiveAvailable();
    if (liveAvailable) {
      // Live wins — reflects unsaved editor state. Forward unchanged and tag.
      const result = await this.live.route(SCENE_GET_DATA_TOOL, args);
      return tagJsonResult(result, "live", { route: "live" });
    }

    // Bridge unavailable → offline disk parse. `path` is required offline.
    const path = typeof args.path === "string" ? args.path : "";
    if (path === "") {
      return sourceResult(
        {
          error: {
            code: "path_required_offline",
            message:
              "scene_get_data requires a 'path' (res://...tscn) when the Godot editor is not running. " +
              "With the bridge online, omitting 'path' reads the currently edited scene.",
          },
        },
        "offline",
        { route: "offline", fallbackReason: "live_unavailable" },
        true,
      );
    }

    const read = await readSceneGetDataOffline(
      path,
      args.hierarchy_depth,
      this.projectPath,
    );
    if (!read.ok) {
      return sourceResult(
        { error: read.error },
        "offline",
        { route: "offline", fallbackReason: "live_unavailable" },
        true,
      );
    }
    return sourceResult(
      read.result,
      "offline",
      { route: "offline", fallbackReason: "live_unavailable" },
    );
  }

  // ── P7.3 — live-first `filesystem_list` with offline fallback ──────────

  /**
   * `godot_open_mcp_filesystem_list` — the second live-first/offline-fallback
   * tool (P7.3). Routing policy mirrors `scene_get_data`:
   *
   *   1. Probe `live.isLiveAvailable()` once. If the bridge answered as
   *      connected (or returned 503 — reachable but loading), forward the
   *      unchanged args to the live handler and tag the result `live`. The
   *      live read returns authoritative importer resource type + UID from the
   *      EditorFileSystem index, which the disk listing cannot.
   *   2. If the bridge is unavailable, run the offline disk listing and tag
   *      the result `_source: "offline"` + `_route: { route: "offline",
   *      fallbackReason: "live_unavailable" }`.
   *   3. NEVER fall back to disk after a semantic live error (e.g.
   *      `directory_not_found`, `invalid_path`) — the live editor answered
   *      authoritatively; only an unreachable bridge triggers the fallback.
   *   4. NEVER fall back on an auth failure or malformed bridge response.
   *
   * Unlike `scene_get_data`, `path` is OPTIONAL offline too — the listing
   * defaults to the project root (`res://`), same as the live contract. The
   * `page_size` / `cursor` / `include_hidden` args pass through unchanged to
   * the offline listing, which preserves the live response shape
   * field-for-field (plus `stateSource: "disk"`).
   */
  private async routeFilesystemList(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    if (typeof this.live.isLiveAvailable !== "function") {
      return sourceResult(
        {
          error: {
            code: "router_not_wired",
            message:
              "ToolRouter is not wired to a LiveClient with isLiveAvailable().",
          },
        },
        "offline",
        { route: "offline", fallbackReason: "router_not_wired" },
        true,
      );
    }

    const liveAvailable = await this.live.isLiveAvailable();
    if (liveAvailable) {
      // Live wins — authoritative importer metadata from the import index.
      const result = await this.live.route(FILESYSTEM_LIST_TOOL, args);
      return tagJsonResult(result, "live", { route: "live" });
    }

    // Bridge unavailable → offline disk listing. `path` defaults to res://
    // (project root), same as the live contract.
    const path = typeof args.path === "string" ? args.path : "res://";
    const listed = await listProjectDirectoryOffline(path, this.projectPath, {
      pageSize: typeof args.page_size === "number" ? args.page_size : undefined,
      cursor: typeof args.cursor === "string" ? args.cursor : undefined,
      includeHidden: args.include_hidden === true,
    });
    if (!listed.ok) {
      return sourceResult(
        { error: listed.error },
        "offline",
        { route: "offline", fallbackReason: "live_unavailable" },
        true,
      );
    }
    return sourceResult(
      listed.result,
      "offline",
      { route: "offline", fallbackReason: "live_unavailable" },
    );
  }

  // ── P7.4 — always-offline `read_compile_errors` ─────────────────────────

  /**
   * `godot_open_mcp_read_compile_errors` — always-offline diagnostic.
   *
   * Routing policy (P7.4 spec §8): NEVER call `isLiveAvailable`. NEVER call the
   * bridge. Return `_source: "offline"` + `_route.route: "offline"` (no
   * `fallbackReason` — offline is the primary route, not a fallback).
   *
   * Pipeline:
   *   1. Identify the project (`identifyGodotProject`). A missing/unreadable
   *      `project.godot` → `project_not_found` / `project_config_unreadable`.
   *   2. Parse the bounded log-settings subset from `project.godot`.
   *   3. Resolve the per-platform user-data root + log paths (env override →
   *      project setting → default `user://logs/godot.log`).
   *   4. Pick the current vs newest-rotated log via `selectLog`.
   *   5. If file logging is disabled and no log exists → `logging_disabled`
   *      (a successful, explanatory result — the tool succeeded, the file just
   *      is not there).
   *   6. If no log exists at all → `log_not_found` (successful result).
   *   7. Read a bounded tail; a read failure on an existing log →
   *      `editor_log_unreadable` (hard error).
   *   8. Extract structured diagnostics; dedupe + cap at `max_diagnostics`.
   *   9. Stale-log advisory: compare cited-source mtimes to the selected log.
   *  10. Compose status (`compile_failed` | `project_unhealthy` |
   *      `warnings_only` | `no_errors_found`) + `unhealthy` + `headline`.
   *
   * `tail_bytes` is clamped to [4096, 1 MiB] (the schema enforces the same
   * bounds); `max_diagnostics` is clamped to [1, 200]; `include_rotated`
   * defaults true.
   */
  private async routeReadCompileErrors(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    // Argument normalization (the schema enforces the same bounds; the cap is
    // applied here so a programmatic caller cannot bypass it).
    const tailBytes = clampInt(
      args.tail_bytes,
      DEFAULT_LOG_TAIL_BYTES,
      MIN_LOG_TAIL_BYTES,
      MAX_LOG_TAIL_BYTES,
    );
    const maxDiagnostics = clampInt(args.max_diagnostics, 50, 1, 200);
    const includeRotated = args.include_rotated !== false; // default true

    const routeMeta: RouteMeta = { route: "offline" };

    // 1. Identify the project.
    const project = await identifyGodotProject(this.projectPath);
    if (!project.ok) {
      return sourceResult({ error: project.error }, "offline", routeMeta, true);
    }
    const projectRoot = project.info.projectRoot;

    // 2. Parse the bounded log-settings subset from project.godot. The marker
    //    was already read by identifyGodotProject; re-read it here for the
    //    log settings (the read is bounded + cached by the OS). A read failure
    //    here is `project_config_unreadable`.
    let projectGodotText: string;
    try {
      projectGodotText = await readFileBounded(
        `${projectRoot}/project.godot`,
      );
    } catch {
      return sourceResult(
        {
          error: {
            code: "project_config_unreadable",
            message:
              "cannot read 'project.godot' for log settings after identification.",
          },
        },
        "offline",
        routeMeta,
        true,
      );
    }
    const settings = parseProjectLogSettings(projectGodotText);

    // 3. Resolve paths. Platform follows process.platform; env follows
    //    process.env. The env override is honored inside resolveLogPaths.
    const platform = process.platform as GodotLogPlatform;
    const userDataRoot = godotUserDataRoot(settings, platform);
    const resolved = resolveLogPaths(settings, userDataRoot);

    // 4. Pick current vs rotated.
    const selected = selectLog(resolved.currentLogPath, resolved.logDir, {
      includeRotated,
      maxLogFiles: settings.maxLogFiles || DEFAULT_MAX_LOG_FILES,
      loggingDisabled: !settings.fileLoggingEnabled,
    });

    // 5/6. logging_disabled / log_not_found are successful, explanatory
    //     results (the tool itself succeeded; the file just does not exist).
    if (selected.notFound) {
      const status: ReadCompileStatus =
        selected.loggingDisabled ? "logging_disabled" : "log_not_found";
      const body = buildReadCompileErrorsBody({
        status,
        unhealthy: false,
        headline: explanationHeadline(status, resolved, selected),
        diagnostics: [],
        errorCount: 0,
        warningCount: 0,
        logPath: resolved.currentLogPath,
        selectedLogKind: selected.kind,
        usedRotatedFallback: selected.usedRotatedFallback,
        logSource: resolved.source,
        loggingDisabled: settings.fileLoggingEnabled
          ? false
          : !settings.fileLoggingEnabled,
        tailBytes,
        truncated: false,
        envOverrideUsed: process.env[LOG_FILE_ENV_OVERRIDE] !== undefined,
      });
      return sourceResult(body, "offline", routeMeta);
    }

    // 7. Bounded tail read. An existing-but-unreadable log is a hard error.
    const tail = readLogTail(selected.path, tailBytes);
    if (tail.error !== undefined) {
      return sourceResult(
        {
          error: {
            code: "editor_log_unreadable",
            message: tail.error,
          },
        },
        "offline",
        routeMeta,
        true,
      );
    }

    // 8. Extract + dedupe + cap.
    const diagnostics = extractCompileDiagnostics(tail.content, maxDiagnostics);
    const errorCount = diagnostics.filter((d) => d.severity === "error").length;
    const warningCount = diagnostics.filter(
      (d) => d.severity === "warning",
    ).length;

    // 9. Stale-log advisory.
    const citedFiles = diagnostics
      .map((d) => d.file)
      .filter((f): f is string => f !== null);
    const stale = detectStaleLog(selected.path, citedFiles, projectRoot);

    // 10. Compose status.
    const status = deriveReadCompileStatus(errorCount, warningCount);
    const headline = composeHeadline(status, errorCount, warningCount);

    const body = buildReadCompileErrorsBody({
      status,
      unhealthy: status === "compile_failed" || status === "project_unhealthy",
      headline,
      diagnostics,
      errorCount,
      warningCount,
      logPath: selected.path,
      selectedLogKind: selected.kind,
      usedRotatedFallback: selected.usedRotatedFallback,
      logSource: resolved.source,
      loggingDisabled: false,
      tailBytes,
      truncated: tail.truncated,
      staleLogSuspected: stale.staleLogSuspected,
      staleLogNewerFiles: stale.newerFiles,
      staleLogHint: stale.hint,
      logMtimeMs: tail.mtimeMs,
      envOverrideUsed: process.env[LOG_FILE_ENV_OVERRIDE] !== undefined,
    });
    return sourceResult(body, "offline", routeMeta);
  }

  // ── P8.3 — local `manage_tools` (per-session visibility mutator) ────────

  /**
   * `godot_open_mcp_manage_tools` — the only mutator of `ToolSessionState`.
   * Resolved entirely in the MCP server (no bridge round-trip); the body is
   * tagged `_source: "local"` + `_route.route: "local"`. Four actions:
   *
   *   - `list_groups` — read-only. Returns the catalog with per-group `active`,
   *     `defaultEnabled`, `activationSource` (`"default" | "manual" | null`),
   *     the tool roster, and the active set snapshot.
   *   - `activate` / `deactivate` — toggle one group (requires `group`). The
   *     store returns whether state changed; the router fires the optional
   *     `notifyToolListChanged` callback ONLY when state actually changed.
   *     Idempotent calls report `changed: false` and do not notify.
   *   - `reset` — restore `core` only. The store always returns `true`; the
   *     router computes `changed` by snapshotting `activeGroups()` before and
   *     after so an idempotent reset (state already at defaults) reports
   *     `changed: false` and does not notify.
   *
   * Structured errors (never throws):
   *   - missing `action` → `missing_parameter` (the schema requires `action`;
   *     this branch defends a programmatic caller that bypassed validation).
   *   - unknown `action` → `unknown_action` (lists the valid actions).
   *   - activate/deactivate without `group` → `missing_parameter`.
   *   - activate/deactivate with unknown `group` → `unknown_group` (lists the
   *     valid ids; hint to use `list_groups`).
   *
   * Adapted from Unity Open MCP's `routeManageTools` (copy for the action
   * switch + error contract; the list_groups payload strips Unity's
   * `available` / `availableReason` / `unityPackage` / `packageDependency` /
   * `autoActivated` fields because Godot has no bridge compile inventory or
   * package auto-activation in P8).
   */
  private async routeManageTools(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const routeMeta: RouteMeta = { route: "local" };
    const action = typeof args.action === "string" ? args.action : "";
    const group = typeof args.group === "string" ? args.group.trim() : "";

    // list_groups — read-only snapshot of the catalog + session state.
    if (action === "list_groups") {
      const groups = TOOL_GROUPS.map((g) => {
        const tools = toolsInGroup(g.id);
        return {
          id: g.id,
          description: g.description,
          defaultEnabled: g.defaultEnabled,
          active: this.sessionState.isGroupActive(g.id),
          activationSource: this.sessionState.activationSource(g.id),
          toolCount: tools.length,
          tools,
        };
      });
      return sourceResult(
        {
          groups,
          activeGroups: this.sessionState.activeGroups(),
          note:
            "Activate a group to add its tools to your ListTools surface; " +
            "deactivate to hide them. State is per-session and ephemeral — " +
            "it resets to `core` only when the MCP server restarts.",
        },
        "local",
        routeMeta,
      );
    }

    // reset — restore the default-on groups. Compute `changed` by snapshot
    // (the store always returns true from reset()).
    if (action === "reset") {
      const before = this.sessionState.activeGroups();
      this.sessionState.reset();
      const after = this.sessionState.activeGroups();
      const changed = !activeGroupsEqual(before, after);
      if (changed) {
        await this.notifyListChangedSafely();
      }
      return sourceResult(
        {
          reset: true,
          changed,
          activeGroups: after,
          message: changed
            ? "Tool-group visibility restored to `core` only. The next " +
              "ListTools response reflects the default surface; MCP " +
              "clients that support listChanged will refresh automatically."
            : "Tool-group visibility was already at the defaults (`core` " +
              "only); no change.",
        },
        "local",
        routeMeta,
      );
    }

    // activate / deactivate — toggle one group.
    if (action === "activate" || action === "deactivate") {
      if (!group) {
        return sourceResult(
          {
            error: {
              code: "missing_parameter",
              message: `'group' is required for action '${action}'.`,
            },
          },
          "local",
          routeMeta,
          true,
        );
      }
      if (!GROUP_IDS.has(group)) {
        return sourceResult(
          {
            error: {
              code: "unknown_group",
              message:
                `Unknown group '${group}'. Valid ids: ` +
                `${Array.from(GROUP_IDS).sort().join(", ")}. ` +
                `Call manage_tools with action 'list_groups' to see the catalog.`,
            },
          },
          "local",
          routeMeta,
          true,
        );
      }
      const changed =
        action === "activate"
          ? this.sessionState.activate(group)
          : this.sessionState.deactivate(group);
      if (changed) {
        await this.notifyListChangedSafely();
      }
      return sourceResult(
        {
          action,
          group,
          changed,
          activeGroups: this.sessionState.activeGroups(),
          message: manageToolsMessage(action, group, changed),
        },
        "local",
        routeMeta,
      );
    }

    // Missing action (defensive — the schema requires it) OR an unknown value.
    if (action === "") {
      return sourceResult(
        {
          error: {
            code: "missing_parameter",
            message: "'action' is required.",
          },
        },
        "local",
        routeMeta,
        true,
      );
    }
    return sourceResult(
      {
        error: {
          code: "unknown_action",
          message:
            `Unknown action '${action}'. Valid actions: list_groups, ` +
            `activate, deactivate, reset.`,
        },
      },
      "local",
      routeMeta,
      true,
    );
  }

  /**
   * Fire the optional {@link notifyToolListChanged} callback, swallowing any
   * rejection or throw so the manage_tools result is never flipped to
   * `isError` by a transport fault (P8.4 §3 failure isolation). The bootstrap
   * closure in `index.ts` already swallows transport errors with a stderr log;
   * this is defense-in-depth for unit tests that inject a rejecting notifier
   * without the bootstrap wrapper. The error is logged to stderr here too so a
   * faulting fake notifier is visible in test output.
   */
  private async notifyListChangedSafely(): Promise<void> {
    if (!this.notifyToolListChanged) return;
    try {
      await this.notifyToolListChanged();
    } catch (err) {
      console.error(
        "[godot-open-mcp] manage_tools notifier threw; suppressing:",
        err,
      );
    }
  }
}

// ---------------------------------------------------------------------------
// P8.3 — `manage_tools` helpers.
// ---------------------------------------------------------------------------

/**
 * Compare two `activeGroups()` snapshots for set equality. Both inputs are
 * sorted arrays (the store returns them sorted); a shallow deepEqual is
 * sufficient. Used by `routeManageTools` to compute whether `reset` actually
 * changed the visible set.
 */
function activeGroupsEqual(
  a: readonly string[],
  b: readonly string[],
): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) {
    if (a[i] !== b[i]) return false;
  }
  return true;
}

/** Compose the human-readable message for an activate/deactivate result. */
function manageToolsMessage(
  action: string,
  group: string,
  changed: boolean,
): string {
  if (action === "activate") {
    return changed
      ? `Group '${group}' activated. Its tools will appear in the next ` +
          "ListTools response; MCP clients that support listChanged will " +
          "refresh automatically."
      : `Group '${group}' was already active.`;
  }
  // deactivate
  return changed
    ? `Group '${group}' deactivated. Its tools are now hidden from the ` +
        "next ListTools response; MCP clients that support listChanged " +
        "will refresh automatically."
    : `Group '${group}' was already inactive.`;
}

// ---------------------------------------------------------------------------
// P7.4 — `read_compile_errors` composition helpers.
// ---------------------------------------------------------------------------

/** Status vocabulary for the read_compile_errors result. */
type ReadCompileStatus =
  | "compile_failed"
  | "project_unhealthy"
  | "warnings_only"
  | "no_errors_found"
  | "logging_disabled"
  | "log_not_found";

/** Derive the status from the error/warning counts. `compile_failed` when C#/
   *  GDScript errors are present; `project_unhealthy` when only load/addon
   *  errors are present; `warnings_only` for warnings without errors;
   *  `no_errors_found` for a clean log. */
function deriveReadCompileStatus(
  errorCount: number,
  warningCount: number,
): ReadCompileStatus {
  if (errorCount > 0) return "compile_failed";
  if (warningCount > 0) return "warnings_only";
  return "no_errors_found";
}

/** Compose a one-line headline for the status. Empty when there is nothing to
 *  triage (`no_errors_found`). */
function composeHeadline(
  status: ReadCompileStatus,
  errorCount: number,
  warningCount: number,
): string {
  switch (status) {
    case "compile_failed":
      return `${errorCount} compile error(s) found in the Godot log — the bridge will not recover until the source errors are fixed.`;
    case "warnings_only":
      return `${warningCount} warning(s) found; no errors.`;
    case "no_errors_found":
      return "";
    default:
      return "";
  }
}

/** Explanation headline for the non-error statuses (logging_disabled /
 *  log_not_found). Tells the operator what to do next. */
function explanationHeadline(
  status: ReadCompileStatus,
  resolved: { currentLogPath: string; source: string },
  selected: { usedRotatedFallback: boolean },
): string {
  if (status === "logging_disabled") {
    return (
      "File logging is disabled in project.godot — Godot is not writing a log " +
      "file. Enable `debug/file_logging/enable_file_logging = true` in " +
      "project.godot (or Project Settings > Debug > File Logging) and " +
      "reproduce the failure, then call this tool again."
    );
  }
  // log_not_found
  const rotatedNote = selected.usedRotatedFallback
    ? " (a rotated log was used)"
    : "";
  return (
    `No Godot log file found at '${resolved.currentLogPath}'${rotatedNote}. ` +
    "If file logging is enabled, reproduce the failure in the Godot editor " +
    "first; the log is written as the editor runs."
  );
}

/** Build the result body object. Fields are ordered for agent readability:
 *  status + unhealthy first (the triage surface), then counts, then the
 *  diagnostic list, then provenance. */
function buildReadCompileErrorsBody(input: {
  status: ReadCompileStatus;
  unhealthy: boolean;
  headline: string;
  diagnostics: ReturnType<typeof extractCompileDiagnostics>;
  errorCount: number;
  warningCount: number;
  logPath: string;
  selectedLogKind: "current" | "rotated";
  usedRotatedFallback: boolean;
  logSource: "env_override" | "project_setting" | "default";
  loggingDisabled: boolean;
  tailBytes: number;
  truncated: boolean;
  staleLogSuspected?: boolean;
  staleLogNewerFiles?: string[];
  staleLogHint?: string;
  logMtimeMs?: number;
  envOverrideUsed: boolean;
}): Record<string, unknown> {
  const body: Record<string, unknown> = {
    status: input.status,
    unhealthy: input.unhealthy,
    headline: input.headline,
    errorCount: input.errorCount,
    warningCount: input.warningCount,
    diagnostics: input.diagnostics,
    logPath: input.logPath,
    selectedLogKind: input.selectedLogKind,
    usedRotatedFallback: input.usedRotatedFallback,
    logSource: input.logSource,
    loggingDisabled: input.loggingDisabled,
    tailBytes: input.tailBytes,
    truncated: input.truncated,
    envOverrideUsed: input.envOverrideUsed,
  };
  if (input.staleLogSuspected === true) {
    body.staleLogSuspected = true;
    body.staleLogNewerFiles = input.staleLogNewerFiles ?? [];
    body.staleLogHint = input.staleLogHint ?? "";
  }
  if (input.logMtimeMs !== undefined) {
    body.logMtimeMs = input.logMtimeMs;
  }
  return body;
}

/** Clamp an integer argument to [min, max] with a default fallback for
 *  non-integer input. */
function clampInt(
  raw: unknown,
  def: number,
  min: number,
  max: number,
): number {
  if (typeof raw !== "number" || !Number.isFinite(raw)) return def;
  const n = Math.trunc(raw);
  if (n < min) return min;
  if (n > max) return max;
  return n;
}

/** Bounded read of a UTF-8 text file. Throws on read failure so the caller
 *  maps it to the structured error. Used only for the already-validated
 *  `project.godot` marker (identifyGodotProject already enforced the byte cap).
 *  Re-reads the file rather than threading the text through to keep the
 *  composition layer decoupled from identifyGodotProject's internals. */
async function readFileBounded(path: string): Promise<string> {
  return readFile(path, "utf-8");
}
