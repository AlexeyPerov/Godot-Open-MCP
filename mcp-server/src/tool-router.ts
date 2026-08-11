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
  FIND_REFERENCES_TOOL,
  DEPENDENCIES_TOOL,
  BASELINE_CREATE_TOOL,
  REGRESSION_CHECK_TOOL,
  READ_ASSET_TOOL,
  SEARCH_ASSETS_TOOL,
  LIST_ASSETS_TOOL,
  RESTART_EDITOR_TOOL,
  RESOURCE_PRESSURE_TOOL,
  GENERATE_SKILL_TOOL,
  IMPACT_PREVIEW_TOOL,
  GATE_BUDGET_ESTIMATE_TOOL,
  MUTATION_EXPLAIN_TOOL,
} from "./capabilities/route-policy.js";
import {
  previewImpact,
  estimateBudget,
  explainMutation,
  resolveRules,
  type RuleFilter,
  type ExplainInput,
} from "./capabilities/gate-intelligence.js";
import {
  TOOL_GROUPS,
  GROUP_IDS,
  toolsInGroup,
} from "./capabilities/tool-groups.js";
import type { ToolSessionState } from "./tool-session-state.js";
import { readSceneGetDataOffline } from "./offline/scene-get-data.js";
import { listProjectDirectoryOffline } from "./offline/project-index.js";
import { findReferencesOffline } from "./offline/references.js";
import { dependenciesOffline } from "./offline/dependencies.js";
import { readAssetOffline } from "./offline/read-asset.js";
import { searchAssetsOffline } from "./offline/search-assets.js";
import { listAssetsOffline } from "./offline/list-assets.js";
import { identifyGodotProject } from "./offline/project-config.js";
import { scanProjectOffline } from "./baseline/scan.js";
import {
  buildBaseline,
  loadBaseline,
  saveBaseline,
  normalizeProfile,
  BASELINE_SCHEMA_VERSION,
  type PlatformProfile,
} from "./baseline/baseline-schema.js";
import { compareBaselines, formatRegressionSummary } from "./baseline/regression-compare.js";
import { readProfileAndDetail, parseResultBody } from "./output-profile.js";
import { readFile } from "node:fs/promises";
import { isAbsolute, join } from "node:path";
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
import {
  readInstanceLock,
  isPidAlive,
  HEARTBEAT_STALE_MS,
} from "./instance-discovery.js";
import {
  killEditorProcess,
  detectHangSignature,
  DEFAULT_KILL_GRACE_MS,
  MAX_KILL_WAIT_MS,
  type KillResult,
  type HangSignatureResult,
} from "./editor-process-control.js";
import {
  countFileDescriptors,
  probeFdCeiling,
  computeFdHeadroom,
  analyzeFdTrend,
  LAUNCH_CONTEXT_CAVEAT,
  type FdCountResult,
  type FdCeilingResult,
} from "./process-diagnostics.js";
import {
  generateSkill as generateSkillImpl,
  truncateForPreview,
  knownClientKeys,
  type GenerateSkillOptions,
} from "./skill/generate-skill.js";

/**
 * Tool name for the live `scene_list_opened` handler, used by
 * `routeRestartEditor`'s opportunistic dirty-scene probe. Not part of a
 * route-policy override set (it is a live tool); kept here as a named constant
 * so a rename is caught at compile time rather than as a routing drift.
 */
const SCENE_LIST_OPENED_TOOL = "godot_open_mcp_scene_list_opened";

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
 *   - `READ_COMPILE_ERRORS_TOOL` / `FIND_REFERENCES_TOOL` /
 *     `DEPENDENCIES_TOOL` — always offline
 *     (never probe the bridge).
 */

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
    if (toolName === FIND_REFERENCES_TOOL) {
      return this.routeFindReferences(args);
    }
    if (toolName === DEPENDENCIES_TOOL) {
      return this.routeDependencies(args);
    }
    if (toolName === READ_ASSET_TOOL) {
      return this.routeReadAsset(args);
    }
    if (toolName === SEARCH_ASSETS_TOOL) {
      return this.routeSearchAssets(args);
    }
    if (toolName === LIST_ASSETS_TOOL) {
      return this.routeListAssets(args);
    }
    if (toolName === BASELINE_CREATE_TOOL) {
      return this.routeBaselineCreate(args);
    }
    if (toolName === REGRESSION_CHECK_TOOL) {
      return this.routeRegressionCheck(args);
    }
    if (toolName === RESTART_EDITOR_TOOL) {
      return this.routeRestartEditor(args);
    }
    if (toolName === RESOURCE_PRESSURE_TOOL) {
      return this.routeResourcePressure(args);
    }
    if (toolName === GENERATE_SKILL_TOOL) {
      return this.routeGenerateSkill(args);
    }
    if (toolName === IMPACT_PREVIEW_TOOL) {
      return this.routeImpactPreview(args);
    }
    if (toolName === GATE_BUDGET_ESTIMATE_TOOL) {
      return this.routeGateBudgetEstimate(args);
    }
    if (toolName === MUTATION_EXPLAIN_TOOL) {
      return this.routeMutationExplain(args);
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

  // ── P17.3 gate intelligence (local, dry-run) ────────────────────────────
  //
  // Three read-only tools resolved entirely in the MCP process over the rule
  // catalog + cost-hints + caller-provided gate data. They never mutate and
  // never POST to the bridge (Godot has no server-side gate-run history or
  // VerifyCacheService that a live mode would need). The pure logic lives in
  // `capabilities/gate-intelligence.ts` so it is unit-testable without a router.

  /**
   * Read a `string[]` argument (case-insensitive key match tolerated for the
   * snake_case form). Returns `undefined` when absent or not a string array.
   */
  private stringArrayArg(
    args: Record<string, unknown>,
    key: string,
  ): string[] | undefined {
    const v = args[key];
    if (Array.isArray(v) && v.every((x) => typeof x === "string")) {
      return v as string[];
    }
    return undefined;
  }

  /**
   * `godot_open_mcp_impact_preview` — project the gate's view of a planned scope
   * (resolved rules + per-path classification + risk band) without mutating.
   */
  private async routeImpactPreview(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const pathsHint = this.stringArrayArg(args, "paths_hint");
    if (!pathsHint || pathsHint.length === 0) {
      return sourceResult(
        { error: { code: "missing_parameter", message: "impact_preview requires a non-empty 'paths_hint' array." } },
        "local",
        { route: "local" },
        true,
      );
    }
    const filter: RuleFilter = {};
    const categories = this.stringArrayArg(args, "categories");
    const includeRules = this.stringArrayArg(args, "include_rules");
    const excludeRules = this.stringArrayArg(args, "exclude_rules");
    if (categories) filter.categories = categories;
    if (includeRules) filter.includeRules = includeRules;
    if (excludeRules) filter.excludeRules = excludeRules;
    const result = previewImpact(pathsHint, filter);
    return sourceResult(result, "local", { route: "local" });
  }

  /**
   * `godot_open_mcp_gate_budget_estimate` — forecast validation duration +
   * issue budget + token band for a planned scope (heuristic; no live scan).
   */
  private async routeGateBudgetEstimate(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const pathsHint = this.stringArrayArg(args, "paths_hint");
    if (!pathsHint || pathsHint.length === 0) {
      return sourceResult(
        { error: { code: "missing_parameter", message: "gate_budget_estimate requires a non-empty 'paths_hint' array." } },
        "local",
        { route: "local" },
        true,
      );
    }
    const filter: RuleFilter = {};
    const categories = this.stringArrayArg(args, "categories");
    const includeRules = this.stringArrayArg(args, "include_rules");
    const excludeRules = this.stringArrayArg(args, "exclude_rules");
    if (categories) filter.categories = categories;
    if (includeRules) filter.includeRules = includeRules;
    if (excludeRules) filter.excludeRules = excludeRules;
    const result = estimateBudget(pathsHint, resolveRules(filter));
    return sourceResult(result, "local", { route: "local" });
  }

  /**
   * `godot_open_mcp_mutation_explain` — narrative over a finished gate run from
   * caller-provided data. Never fails on partial input.
   */
  private async routeMutationExplain(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const input: ExplainInput = {};
    if (
      args.outcome === "passed" || args.outcome === "warned" ||
      args.outcome === "failed" || args.outcome === "skipped" ||
      args.outcome === "unavailable"
    ) {
      input.outcome = args.outcome;
    }
    // snake_case arg → camelCase ExplainInput field for the numeric fields.
    const intArgs: Array<[string, keyof ExplainInput]> = [
      ["new_errors", "newErrors"],
      ["new_warnings", "newWarnings"],
      ["resolved_errors", "resolvedErrors"],
      ["resolved_warnings", "resolvedWarnings"],
      ["total_ms", "totalMs"],
      ["checkpoint_ms", "checkpointMs"],
      ["validation_ms", "validationMs"],
    ];
    for (const [argKey, field] of intArgs) {
      if (typeof args[argKey] === "number" && Number.isFinite(args[argKey] as number)) {
        (input as Record<string, unknown>)[field] = args[argKey];
      }
    }
    const newIssueKeys = this.stringArrayArg(args, "new_issue_keys");
    const resolvedIssueKeys = this.stringArrayArg(args, "resolved_issue_keys");
    const agentNextSteps = this.stringArrayArg(args, "agent_next_steps");
    const categoriesRun = this.stringArrayArg(args, "categories_run");
    if (newIssueKeys) input.newIssueKeys = newIssueKeys;
    if (resolvedIssueKeys) input.resolvedIssueKeys = resolvedIssueKeys;
    if (agentNextSteps) input.agentNextSteps = agentNextSteps;
    if (categoriesRun) input.categoriesRun = categoriesRun;
    if (typeof args.tool_name === "string") input.toolName = args.tool_name;
    if (typeof args.mutation_error === "string") input.mutationError = args.mutation_error;
    const result = explainMutation(input);
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

  // ── P13.1 — always-offline `find_references` ─────────────────────────────

  /**
   * `godot_open_mcp_find_references` — always-offline reverse dependency
   * lookup. NEVER probes the bridge. Scans `.tscn`/`.tres` (optional `.gd`)
   * for `[ext_resource]` / `uid://` references to the target path or uid.
   *
   * Requires exactly one of `asset_path` / `uid`. Profile defaults to
   * compact (counts + byKind/byFolder); balanced/full return the per-asset
   * list (paged when `page_size` is set).
   */
  private async routeFindReferences(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const routeMeta: RouteMeta = { route: "offline" };

    const assetPath =
      typeof args.asset_path === "string" ? args.asset_path : undefined;
    const uid = typeof args.uid === "string" ? args.uid : undefined;
    const hasPath = typeof assetPath === "string" && assetPath !== "";
    const hasUid = typeof uid === "string" && uid !== "";
    if (!hasPath && !hasUid) {
      return sourceResult(
        {
          error: {
            code: "missing_parameter",
            message:
              "find_references requires exactly one of 'asset_path' or 'uid'.",
          },
        },
        "offline",
        routeMeta,
        true,
      );
    }
    if (hasPath && hasUid) {
      return sourceResult(
        {
          error: {
            code: "invalid_request",
            message:
              "find_references accepts asset_path OR uid, not both.",
          },
        },
        "offline",
        routeMeta,
        true,
      );
    }

    const { detail } = readProfileAndDetail(args, "summary");
    const pageSize =
      typeof args.page_size === "number" && args.page_size > 0
        ? Math.floor(args.page_size)
        : undefined;
    // When page_size is set, forward maxResults=0 (unlimited sentinel) so the
    // inner builder does not pre-cap before paging.
    const maxResults =
      pageSize !== undefined
        ? 0
        : typeof args.max_results === "number"
          ? args.max_results
          : 100;
    const maxPerFile =
      typeof args.max_per_file === "number" && args.max_per_file > 0
        ? Math.floor(args.max_per_file)
        : 5;
    const includeScripts = args.include_scripts === true;
    const cursor =
      typeof args.cursor === "string" ? args.cursor : undefined;

    const result = await findReferencesOffline({
      assetPath: hasPath ? assetPath : undefined,
      uid: hasUid ? uid : undefined,
      detail,
      maxResults,
      maxPerFile,
      pageSize,
      cursor,
      includeScripts,
      projectRoot: this.projectPath,
    });

    return sourceResult(result, "offline", routeMeta);
  }

  /**
   * `godot_open_mcp_dependencies` — always-offline forward + reverse
   * dependency lookup. NEVER probes the bridge.
   */
  private async routeDependencies(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const routeMeta: RouteMeta = { route: "offline" };

    const assetPath =
      typeof args.asset_path === "string" ? args.asset_path : undefined;
    const uid = typeof args.uid === "string" ? args.uid : undefined;
    const hasPath = typeof assetPath === "string" && assetPath !== "";
    const hasUid = typeof uid === "string" && uid !== "";
    if (!hasPath && !hasUid) {
      return sourceResult(
        {
          error: {
            code: "missing_parameter",
            message:
              "dependencies requires exactly one of 'asset_path' or 'uid'.",
          },
        },
        "offline",
        routeMeta,
        true,
      );
    }
    if (hasPath && hasUid) {
      return sourceResult(
        {
          error: {
            code: "invalid_request",
            message: "dependencies accepts asset_path OR uid, not both.",
          },
        },
        "offline",
        routeMeta,
        true,
      );
    }

    const detailRaw =
      typeof args.detail === "string" ? args.detail : "normal";
    const detail =
      detailRaw === "summary" || detailRaw === "normal" ? detailRaw : "normal";
    const maxResults =
      typeof args.max_results === "number" ? args.max_results : 100;
    const includeImpact = args.include_impact === true;
    let maxImpactDepth = 5;
    if (typeof args.max_impact_depth === "number") {
      maxImpactDepth = Math.min(20, Math.max(1, Math.floor(args.max_impact_depth)));
    }

    const result = await dependenciesOffline({
      assetPath: hasPath ? assetPath : undefined,
      uid: hasUid ? uid : undefined,
      detail,
      maxResults,
      includeImpact,
      maxImpactDepth,
      projectRoot: this.projectPath,
    });

    return sourceResult(result, "offline", routeMeta);
  }

  // ── P17.1 — always-offline `read_asset` / `search_assets` / `list_assets` ──

  /**
   * `godot_open_mcp_read_asset` — always-offline token-budgeted asset summary.
   * NEVER probes the bridge. Parses `.tres`/`.tscn`/`.gdshader`/`.import` text
   * on disk.
   */
  private async routeReadAsset(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const routeMeta: RouteMeta = { route: "offline" };

    const assetPath =
      typeof args.asset_path === "string" ? args.asset_path : "";
    if (assetPath === "") {
      return sourceResult(
        {
          error: {
            code: "missing_parameter",
            message: "read_asset requires 'asset_path'.",
          },
        },
        "offline",
        routeMeta,
        true,
      );
    }

    const { detail } = readProfileAndDetail(args, "summary");
    const pageSize =
      typeof args.page_size === "number" && args.page_size > 0
        ? Math.floor(args.page_size)
        : undefined;
    const cursor = typeof args.cursor === "string" ? args.cursor : undefined;
    const maxPerSection =
      typeof args.max_per_section === "number" && args.max_per_section > 0
        ? Math.floor(args.max_per_section)
        : undefined;

    const result = await readAssetOffline({
      assetPath,
      detail,
      pageSize,
      cursor,
      maxPerSection,
      projectRoot: this.projectPath,
    });

    return sourceResult(result, "offline", routeMeta);
  }

  /**
   * `godot_open_mcp_search_assets` — always-offline reason-tagged project-wide
   * search. NEVER probes the bridge.
   */
  private async routeSearchAssets(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const routeMeta: RouteMeta = { route: "offline" };

    const { detail } = readProfileAndDetail(args, "summary");
    const pageSize =
      typeof args.page_size === "number" && args.page_size > 0
        ? Math.floor(args.page_size)
        : undefined;
    const cursor = typeof args.cursor === "string" ? args.cursor : undefined;
    const maxResults =
      pageSize !== undefined
        ? 0
        : typeof args.max_results === "number"
          ? args.max_results
          : 100;

    const result = await searchAssetsOffline({
      name: typeof args.name === "string" ? args.name : undefined,
      kind: typeof args.kind === "string" ? args.kind : undefined,
      nodeType: typeof args.node_type === "string" ? args.node_type : undefined,
      script: typeof args.script === "string" ? args.script : undefined,
      uid: typeof args.uid === "string" ? args.uid : undefined,
      detail,
      maxResults,
      pageSize,
      cursor,
      projectRoot: this.projectPath,
    });

    return sourceResult(result, "offline", routeMeta);
  }

  /**
   * `godot_open_mcp_list_assets` — always-offline compressed `res://` listing.
   * NEVER probes the bridge.
   */
  private async routeListAssets(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const routeMeta: RouteMeta = { route: "offline" };

    const { detail } = readProfileAndDetail(args, "summary");
    const pageSize =
      typeof args.page_size === "number" && args.page_size > 0
        ? Math.floor(args.page_size)
        : undefined;
    const cursor = typeof args.cursor === "string" ? args.cursor : undefined;
    const maxPerFolder =
      typeof args.max_per_folder === "number" && args.max_per_folder > 0
        ? Math.floor(args.max_per_folder)
        : undefined;
    const folder = typeof args.folder === "string" ? args.folder : undefined;

    const result = await listAssetsOffline({
      folder,
      type: typeof args.type === "string" ? args.type : undefined,
      maxPerFolder,
      detail,
      pageSize,
      cursor,
      projectRoot: this.projectPath,
    });

    return sourceResult(result, "offline", routeMeta);
  }

  // ── P15.1 — always-offline `baseline_create` + `regression_check` ───────

  /**
   * `godot_open_mcp_baseline_create` — always-offline. Runs the offline
   * whole-project scan and writes a schema-v1 baseline JSON. NEVER probes the
   * bridge.
   *
   * The default path is `CI/godot-open-mcp-baseline.json` (relative to the
   * project root); parent directories are created when missing. The result
   * body carries the absolute resolved path, the severity summary, the per-rule
   * issue counts, the `ciExcludedRules` the offline scanner cannot detect, and
   * the scan duration. The baseline is meant to be committed and compared
   * against by `regression_check` in CI.
   */
  private async routeBaselineCreate(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const routeMeta: RouteMeta = { route: "offline" };

    const profile = normalizeProfile(args.platform_profile);
    const relPath =
      typeof args.baseline_path === "string" && args.baseline_path !== ""
        ? args.baseline_path
        : "CI/godot-open-mcp-baseline.json";
    const baselinePath = this.resolveProjectPath(relPath);

    const scan = await scanProjectOffline({ projectRoot: this.projectPath });
    const baseline = buildBaseline(
      scan.issues,
      scan.categoriesRun,
      scan.ciExcludedRules,
      profile,
    );

    try {
      await saveBaseline(baseline, baselinePath);
    } catch (e) {
      return sourceResult(
        {
          error: {
            code: "baseline_write_failed",
            message: `failed to write baseline to '${baselinePath}': ${(e as Error)?.message ?? "unknown error"}`,
            path: baselinePath,
          },
        },
        "offline",
        routeMeta,
        true,
      );
    }

    const body = {
      baselinePath,
      schemaVersion: BASELINE_SCHEMA_VERSION,
      platformProfile: profile,
      summary: baseline.summary,
      rules: baseline.rules.map((r) => ({
        ruleId: r.ruleId,
        error: r.error,
        warn: r.warn,
        issueKeyCount: r.issueKeys.length,
      })),
      ciExcludedRules: baseline.ciExcludedRules,
      scannedFileCount: scan.scannedFiles.length,
      durationMs: scan.durationMs,
    };
    return sourceResult(body, "offline", routeMeta);
  }

  /**
   * `godot_open_mcp_regression_check` — always-offline. Compares the current
   * offline scan against a baseline file by error-count delta. NEVER probes the
   * bridge.
   *
   * Exit-code contract (surfaced as `exitCode` in the result body):
   *   0 — no regression
   *   1 — regression (global threshold breach OR any per-category breach)
   *   2 — baseline missing
   *   3 — baseline invalid (unreadable / unparseable / schema-version mismatch)
   *
   * The result also carries a compact multi-line `summary` string suitable for
   * CI logs and the structured `regression` detail (baseline/current summaries,
   * global delta + threshold, and a per-rule breakdown when per-category
   * thresholds were supplied).
   */
  private async routeRegressionCheck(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const routeMeta: RouteMeta = { route: "offline" };

    const relPath = typeof args.baseline_path === "string" ? args.baseline_path : "";
    if (relPath === "") {
      return sourceResult(
        {
          error: {
            code: "missing_parameter",
            message: "regression_check requires 'baseline_path'.",
          },
          exitCode: 3,
        },
        "offline",
        routeMeta,
        true,
      );
    }
    const baselinePath = this.resolveProjectPath(relPath);

    const loaded = await loadBaseline(baselinePath);
    if (!loaded.ok) {
      // exit 2 = missing, exit 3 = invalid.
      const exitCode = loaded.reason === "missing" ? 2 : 3;
      const body: Record<string, unknown> = {
        error: {
          code: loaded.reason === "missing" ? "baseline_missing" : "baseline_invalid",
          message: loaded.reason === "missing"
            ? `baseline file not found at '${loaded.path}'.`
            : loaded.message,
          path: loaded.path,
        },
        exitCode,
      };
      return sourceResult(body, "offline", routeMeta, true);
    }
    const baseline = loaded.baseline;

    const profile = normalizeProfile(args.platform_profile);
    const globalThreshold =
      typeof args.regression_threshold === "number" &&
      Number.isFinite(args.regression_threshold) &&
      args.regression_threshold >= 0
        ? Math.trunc(args.regression_threshold)
        : 0;

    const perCategory = parsePerCategoryThresholds(args.per_category_thresholds);

    const scan = await scanProjectOffline({ projectRoot: this.projectPath });
    const current = buildBaseline(
      scan.issues,
      scan.categoriesRun,
      scan.ciExcludedRules,
      profile,
    );

    const regression = compareBaselines(
      current,
      baseline,
      globalThreshold,
      perCategory,
    );
    const summary = formatRegressionSummary(regression);

    const body = {
      baselinePath,
      schemaVersion: BASELINE_SCHEMA_VERSION,
      platformProfile: profile,
      exitCode: regression.regressed ? 1 : 0,
      regressed: regression.regressed,
      summary,
      regression,
      scannedFileCount: scan.scannedFiles.length,
      durationMs: scan.durationMs,
    };
    return sourceResult(body, "offline", routeMeta, regression.regressed);
  }

  /**
   * Resolve a baseline/output path argument to an absolute native path. Bare
   * relative paths are anchored at the project root (the directory containing
   * `project.godot`), matching Unity's `ResolveProjectPath` convention. Already
   * absolute paths pass through unchanged.
   */
  private resolveProjectPath(relOrAbsolute: string): string {
    if (relOrAbsolute === "") return this.projectPath;
    return isAbsolute(relOrAbsolute)
      ? relOrAbsolute
      : join(this.projectPath, relOrAbsolute);
  }

  // ── P15.3 — local `restart_editor` (terminate a wedged Godot process) ───

  /**
   * `godot_open_mcp_restart_editor` — terminate the hung Godot editor after
   * confirming the Godot hang signature (crash marker in the log OR frozen
   * main thread: live PID + unreachable /ping + stale log). Requires explicit
   * `confirm: true`. The relaunch half is intentionally deferred (the Hub /
   * operator owns the interactive-Godot launch recipe); the response tells the
   * operator/agent to relaunch via the Hub/CLI.
   *
   * Local route: the bridge is the thing that dies on a hang, so the tool may
   * NOT depend on it for its primary path. It consults the bridge
   * OPPORTUNISTICALLY for two signals — the /ping reachability (feeds the
   * frozen signature) and the active-scene-dirty summary (surfaced as a
   * warning before the kill). A failure on either probe leaves the file-based
   * verdict standing.
   *
   * Safety contract:
   *   1. Dry-run (confirm false/absent) — return the PID + diagnosis, no side
   *      effect.
   *   2. Refuse when the hang signature is ABSENT — never restart on a
   *      fixable compile failure.
   *   3. Refuse when no live Godot PID matches the project's instance lock.
   *   4. Surface `dirtyScenesWarning` when the bridge is still reachable.
   *   5. Kill: SIGTERM → grace window → SIGKILL (POSIX) / taskkill /T /F
   *      (Windows).
   *
   * Adapted from Unity Open MCP's `routeRestartEditor` (copy for the
   * confirm-gate + dry-run + signature-check + dirty-scenes composition);
   * intentional deltas:
   *   - Signature is Godot-specific (crash marker OR frozen). Unity keys on an
   *     fd-exhaustion string; Godot has no equivalent.
   *   - PID resolution from the instance lock (instance-discovery.ts), not an
   *     OS process scan (`findUnityForProject`). Godot writes its PID to the
   *     lock on startup.
   *   - No live-console signature fallback (Unity probes the live console for
   *     the fd-exhaustion exception). Godot's hang is not a logged exception
   *     with a discoverable console entry, so the log file is authoritative.
   *   - Dirty scenes via `godot_open_mcp_scene_list_opened` (Godot has no
   *     dedicated dirty-summary tool; scene_list_opened carries `isDirty` per
   *     open scene).
   */
  private async routeRestartEditor(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const routeMeta: RouteMeta = { route: "local" };
    const confirm = args.confirm === true;
    const graceMs =
      typeof args.kill_grace_ms === "number" &&
      Number.isInteger(args.kill_grace_ms) &&
      args.kill_grace_ms >= 0 &&
      args.kill_grace_ms <= MAX_KILL_WAIT_MS
        ? args.kill_grace_ms
        : DEFAULT_KILL_GRACE_MS;

    // 1. Resolve the Godot PID from the instance lock. The bridge writes its
    //    PID there on startup; readInstanceLock never throws (returns null on
    //    missing/unreadable/unparseable). The MCP server already trusts this
    //    file for port resolution (P1.6).
    const lock = readInstanceLock(this.projectPath);
    const lockPid = lock && typeof lock.pid === "number" ? lock.pid : null;
    const pidAlive = lockPid !== null && lockPid > 0 && isPidAlive(lockPid);

    // 2. Read the Godot log tail + detect the hang signature. Reuses the same
    //    offline log-path resolution + bounded tail read as read_compile_errors
    //    so the diagnosis is consistent with what the agent already saw. The
    //    log read is best-effort: a missing/unreadable log degrades to
    //    "logExists: false" (the frozen signature requires a log to exist).
    const { logTail, logExists, logStale, hasCompileErrors, logPath } =
      await this.readGodotLogForRestart();

    // 3. /ping reachability (feeds the frozen signature). Opportunistic: a
    //    failure leaves the file-based crash-marker verdict standing.
    let pingUnreachable = true;
    try {
      if (typeof this.live.isLiveAvailable === "function") {
        pingUnreachable = !(await this.live.isLiveAvailable());
      }
    } catch {
      pingUnreachable = true;
    }

    const signature = detectHangSignature({
      logTail,
      logExists,
      hasCompileErrors,
      logStale,
      pingUnreachable,
      pidAlive,
    });

    // 4. Dry-run branch: confirm was false/absent. Return the diagnosis + the
    //    PID the tool WOULD kill, with no side effect.
    if (!confirm) {
      return sourceResult(
        {
          action: "restart_editor",
          confirm: false,
          dryRun: true,
          signaturePresent: signature.present,
          ...(signature.source !== null ? { signatureSource: signature.source } : {}),
          ...(lockPid !== null && pidAlive ? { wouldKillPid: lockPid } : {}),
          ...(lockPid !== null && !pidAlive
            ? { pidFromLock: lockPid, pidAlive: false }
            : {}),
          ...(logPath !== null ? { logPath } : {}),
          diagnosis: signature.note,
          projectPath: this.projectPath,
          message: signature.present
            ? "Dry-run preview. The Godot editor appears wedged. Pass " +
              "confirm: true to terminate the hung process. Killing the " +
              "editor can destroy unsaved scene work and in-flight asset " +
              "imports — relaunch via the Hub/CLI afterward."
            : "Dry-run preview. " + signature.note,
        },
        "local",
        routeMeta,
      );
    }

    // 5. Confirmed-path refusals. Each guard surfaces a structured code so an
    //    agent can branch on why the kill did not happen.
    if (!signature.present) {
      return sourceResult(
        {
          error: {
            code: "restart_signature_absent",
            message: signature.note,
            ...(logPath !== null ? { logPath } : {}),
          },
        },
        "local",
        routeMeta,
        true,
      );
    }
    if (!pidAlive || lockPid === null) {
      return sourceResult(
        {
          error: {
            code: "godot_process_not_found",
            message:
              "No live Godot process matches this project's instance lock. " +
              "The editor may have already exited, or the instance lock is " +
              "missing/stale. Relaunch Godot for this project via the Hub/CLI.",
            projectPath: this.projectPath,
            ...(lockPid !== null ? { pidFromLock: lockPid, pidAlive: false } : {}),
          },
        },
        "local",
        routeMeta,
        true,
      );
    }

    // 6. Opportunistic active-scene-dirty check. The editor is hung, but the
    //    bridge HTTP listener might still be up long enough to answer a
    //    read-only status call. If so, surface dirtyScenes[] as a warning so
    //    the operator knows what unsaved work the kill will lose. This does
    //    NOT block the kill — the editor is hung, saving is not an option.
    const dirtyScenesWarning = await this.collectDirtyScenesWarning();

    // 7. Kill the editor.
    const kill = await killEditorProcess(lockPid, graceMs);
    return this.buildRestartEditorKillResponse(
      lockPid,
      graceMs,
      kill,
      dirtyScenesWarning,
      signature,
    );
  }

  /**
   * Read the Godot log tail + derive the inputs the hang-signature detector
   * needs. Reuses the same log-path resolution + bounded tail read as
   * `routeReadCompileErrors` so the diagnosis is consistent. Returns
   * `logExists: false` when no log was found (missing or logging disabled),
   * and `logStale: false` when the log exists and was written recently (a
   * fresh log means the editor is actively writing — not frozen). Never
   * throws; a read failure degrades to an empty tail.
   */
  private async readGodotLogForRestart(): Promise<{
    logTail: string;
    logExists: boolean;
    logStale: boolean;
    hasCompileErrors: boolean;
    logPath: string | null;
  }> {
    const empty = {
      logTail: "",
      logExists: false,
      logStale: false,
      hasCompileErrors: false,
      logPath: null as string | null,
    };
    try {
      const project = await identifyGodotProject(this.projectPath);
      if (!project.ok) return empty;
      const projectRoot = project.info.projectRoot;
      const projectGodotText = await readFile(`${projectRoot}/project.godot`, "utf-8");
      const settings = parseProjectLogSettings(projectGodotText);
      const platform = process.platform as GodotLogPlatform;
      const userDataRoot = godotUserDataRoot(settings, platform);
      const resolved = resolveLogPaths(settings, userDataRoot);
      const selected = selectLog(resolved.currentLogPath, resolved.logDir, {
        includeRotated: true,
        maxLogFiles: settings.maxLogFiles || DEFAULT_MAX_LOG_FILES,
        loggingDisabled: !settings.fileLoggingEnabled,
      });
      if (selected.notFound) return empty;
      const tail = readLogTail(selected.path, DEFAULT_LOG_TAIL_BYTES);
      if (tail.error !== undefined || !tail.exists) return empty;
      const diagnostics = extractCompileDiagnostics(tail.content);
      // Compile errors are FIXABLE failures — their presence forces the hang
      // signature ABSENT regardless of any other signal. We key on C# +
      // GDScript + load errors (the diagnostic kinds that point at source
      // code a fix would address), NOT the conservative `other` fallback.
      const hasCompileErrors = diagnostics.some(
        (d) =>
          d.severity === "error" &&
          (d.kind === "csharp" ||
            d.kind === "gdscript" ||
            d.kind === "script_load" ||
            d.kind === "addon_load"),
      );
      // A live Godot editor writes to its log periodically; a stale log (older
      // than HEARTBEAT_STALE_MS) + live PID + unreachable /ping is the frozen
      // signature. Use the same threshold as the instance-lock classifier so
      // the two views agree.
      const logStale =
        tail.mtimeMs !== undefined && Date.now() - tail.mtimeMs > HEARTBEAT_STALE_MS;
      return {
        logTail: tail.content,
        logExists: true,
        logStale,
        hasCompileErrors,
        logPath: selected.path,
      };
    } catch {
      return empty;
    }
  }

  /**
   * Opportunistic active-scene-dirty probe. Calls `scene_list_opened` through
   * the live client (only when the bridge is reachable) and extracts the
   * scenes carrying `isDirty: true`. Returns null when the bridge is down OR
   * no dirty scenes were found. Never throws — the dirty-scene signal is
   * opportunistic, not load-bearing.
   */
  private async collectDirtyScenesWarning(): Promise<
    Array<{ name: string; path: string; isDirty: boolean }> | null
  > {
    try {
      if (typeof this.live.isLiveAvailable !== "function") return null;
      const liveAvailable = await this.live.isLiveAvailable();
      if (!liveAvailable) return null;
      const result = await this.live.route(SCENE_LIST_OPENED_TOOL, {});
      const body = parseResultBody(result);
      if (!body || !Array.isArray(body.scenes)) return null;
      const dirty = (body.scenes as Array<Record<string, unknown>>)
        .filter((s) => s.isDirty === true)
        .map((s) => ({
          name: typeof s.name === "string" ? s.name : "",
          path: typeof s.path === "string" ? s.path : "",
          isDirty: true,
        }));
      return dirty.length > 0 ? dirty : null;
    } catch {
      return null;
    }
  }

  /** Build the confirmed-kill response from a {@link KillResult}. */
  private buildRestartEditorKillResponse(
    pid: number,
    graceMs: number,
    kill: KillResult,
    dirtyScenesWarning: Array<{ name: string; path: string; isDirty: boolean }> | null,
    signature: HangSignatureResult,
  ): CallToolResult {
    const routeMeta: RouteMeta = { route: "local" };
    if (kill.terminated) {
      return sourceResult(
        {
          action: "restart_editor",
          confirm: true,
          killed: true,
          pid: kill.pid,
          method: kill.method,
          elapsedMs: kill.elapsedMs,
          graceMs,
          ...(signature.source !== null ? { signatureSource: signature.source } : {}),
          ...(dirtyScenesWarning !== null
            ? {
                dirtyScenesWarning,
                dirtyScenesNote:
                  "These scenes had unsaved changes when the editor was " +
                  "killed — that work is lost. Saving is not an option when " +
                  "the editor is hung; surface this to the operator.",
              }
            : {}),
          nextSteps: [
            "Godot editor terminated. Relaunch Godot for this project via " +
              "the Hub/CLI (the MCP server does not own the interactive-editor " +
              "launch recipe — the flags the Hub/operator used at original " +
              "launch are not knowable from here).",
            "After relaunch, poll godot_open_mcp_bridge_status until it " +
              'returns status: "running" to confirm the bridge reconnected ' +
              "and wrote a fresh instance lock.",
          ],
        },
        "local",
        routeMeta,
      );
    }
    // Kill failed — the editor is still alive (or its state is unknown).
    return sourceResult(
      {
        action: "restart_editor",
        confirm: true,
        killed: false,
        pid: kill.pid,
        reason: kill.reason,
        message: kill.message,
        graceMs,
        nextSteps: [
          "The automated kill did not terminate the editor. The operator " +
            "must force-quit Godot manually (Activity Monitor / Task " +
            "Manager / `kill -9 <pid>`).",
          kill.reason === "not_found"
            ? "The PID vanished between the scan and the kill — the editor " +
              "may have exited on its own. Re-run godot_open_mcp_bridge_status " +
              "to confirm state before retrying."
            : "After a manual force-quit, relaunch Godot via the Hub/CLI and " +
              "poll godot_open_mcp_bridge_status until running.",
        ],
      },
      "local",
      routeMeta,
      true,
    );
  }

  // ── P15.4 — local `resource_pressure` (proactive fd/handle leak warning) ─

  /**
   * `godot_open_mcp_resource_pressure` — sample the live Godot process's
   * fd/handle count and report headroom + trend. Proactive counterpart to
   * `restart_editor` (reactive kill) and `read_compile_errors` (diagnosis):
   * catches a slow fd/handle leak across recompiles/reloads BEFORE the editor
   * wedges. The bridge is the thing that dies on resource exhaustion, so the
   * probe runs server-side against the OS and does NOT require the bridge.
   *
   * Local route (no `POST /tools/resource_pressure` on the bridge). Resolves
   * the live Godot PID from the instance lock (same source as bridge_status),
   * or accepts an explicit `pid`. Probes the fd count per-OS (macOS `lsof`;
   * Linux `/proc/<pid>/fd`; Windows `Get-Process.HandleCount` — approximate)
   * AND probes the per-OS ceiling (Linux `/proc/<pid>/limits`; macOS
   * `launchctl limit maxfiles`; Windows none → null). The actionable signal is
   * the TREND (rising/leaking), not the absolute count — the ceiling is a
   * best-effort reference. Samples live in the session-scoped ring in
   * `ToolSessionState` (no disk cache); a failed probe still records a sample
   * (count: null) so the trend detector sees the gap.
   *
   * Adapted from Unity Open MCP's `routeResourcePressure` (copy for the
   * pid-resolution + sample-record + response-shape composition); intentional
   * deltas:
   *   - PID resolution from the instance lock (instance-discovery.ts), not an
   *     OS process scan (`findUnityForProject`). Godot writes its PID to the
   *     lock on startup.
   *   - The ceiling is PROBED per-OS, not Unity's fixed Mono 1024. The headroom
   *     math takes the probed ceiling as a parameter; when it is null (Windows)
   *     the state is `unknown` and the trend carries the signal.
   *   - The leak threshold falls back to an absolute constant when the ceiling
   *     is unknown, so a leak is still detectable on Windows.
   */
  private async routeResourcePressure(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const routeMeta: RouteMeta = { route: "local" };

    // 1. Resolve the PID. An explicit pid arg wins; otherwise read the instance
    //    lock (same source as bridge_status — the bridge writes its PID there
    //    on startup, and the MCP server already trusts this file for port
    //    resolution). The probe must NOT depend on the bridge.
    let pid: number | null = null;
    if (
      typeof args.pid === "number" &&
      Number.isInteger(args.pid) &&
      args.pid > 0
    ) {
      pid = args.pid;
    } else {
      const lock = readInstanceLock(this.projectPath);
      if (lock && typeof lock.pid === "number" && lock.pid > 0) {
        pid = lock.pid;
      }
    }
    if (pid === null) {
      return sourceResult(
        {
          error: {
            code: "godot_process_not_found",
            message:
              "No live Godot PID was found in this project's instance lock, " +
              "and no explicit pid was supplied. resource_pressure samples " +
              "the OS process directly (it does not depend on the bridge) — " +
              "open Godot for this project first, or pass an explicit pid.",
            projectPath: this.projectPath,
          },
        },
        "local",
        routeMeta,
        true,
      );
    }

    // 2. Probe fd count + per-OS ceiling. Both never throw. A failed fd probe
    //    still records a sample (count: null) so the trend detector sees the
    //    gap and does not interpolate across it.
    const probe: FdCountResult = countFileDescriptors(pid);
    const ceilingProbe: FdCeilingResult = probeFdCeiling(pid);
    const count = probe.count;
    const ceiling =
      ceilingProbe.ceiling !== null && Number.isFinite(ceilingProbe.ceiling)
        ? ceilingProbe.ceiling
        : null;
    const approximate = "approximate" in probe ? probe.approximate : false;
    const headroom = computeFdHeadroom(count, ceiling, approximate);

    // 3. Record the sample + compute the trend over the session ring.
    const ts = Date.now();
    this.sessionState.recordFdSample({ ts, pid, count });
    const samples = this.sessionState.fdSamplesSnapshot();
    const trend = analyzeFdTrend(samples, ceiling);

    // 4. Build the response. The launchContextCaveat is always present so an
    //    agent (and the operator) understand the per-OS ceiling nuance. The
    //    warning block is non-null only when there is something to surface
    //    (warn/critical state OR a leaking trend); ok+stable stays silent.
    const fdMethod = probe.method;
    const ceilingMethod = ceilingProbe.method;
    const probeReason = "reason" in probe ? probe.reason : null;
    const probeMessage = "message" in probe ? probe.message : null;
    const ceilingReason =
      "reason" in ceilingProbe ? ceilingProbe.reason : null;

    const warningState =
      headroom.state === "warn" ||
      headroom.state === "critical" ||
      trend.state === "leaking"
        ? this.buildResourcePressureWarning(headroom, trend)
        : null;

    return sourceResult(
      {
        pid,
        fdCount: count,
        fdMethod,
        approximate,
        ceiling,
        ceilingMethod,
        ...(ceilingReason !== null ? { ceilingReason } : {}),
        headroom: headroom.headroom,
        pressureRatio: headroom.pressureRatio,
        state: headroom.state,
        reliable: headroom.reliable,
        trend,
        samples: samples.map((s) => ({
          ts: s.ts,
          pid: s.pid,
          count: s.count,
        })),
        sampleCount: samples.length,
        launchContextCaveat: LAUNCH_CONTEXT_CAVEAT,
        ...(probeReason !== null ? { probeReason } : {}),
        ...(probeMessage !== null ? { probeMessage } : {}),
        ...(warningState !== null
          ? {
              warning: warningState.warning,
              agentNextSteps: warningState.agentNextSteps,
            }
          : {}),
      },
      "local",
      routeMeta,
    );
  }

  /**
   * Compose the warning block + agent next-steps for a warn/critical/leaking
   * resource_pressure result. Pulled out of {@link routeResourcePressure} so the
   * three severity branches read cleanly.
   */
  private buildResourcePressureWarning(
    headroom: ReturnType<typeof computeFdHeadroom>,
    trend: ReturnType<typeof analyzeFdTrend>,
  ): {
    warning: { level: string; message: string };
    agentNextSteps: string[];
  } {
    const ceilingDesc =
      headroom.ceiling !== null
        ? `the ${headroom.ceiling}-descriptor ceiling`
        : "the probed fd ceiling";
    if (headroom.state === "critical") {
      return {
        warning: {
          level: "critical",
          message:
            `Godot fd usage is at ${Math.round(headroom.pressureRatio * 100)}% of ` +
            `${ceilingDesc} — the editor is close to exhausting its fd budget. ` +
            `Save scene work and restart Godot via the Hub/CLI now, before the ` +
            `bridge (the thing that dies on exhaustion) hangs.`,
        },
        agentNextSteps: RESOURCE_PRESSURE_AGENT_NEXT_STEPS,
      };
    }
    if (trend.state === "leaking") {
      return {
        warning: {
          level: "leaking",
          message:
            `Godot fd usage is climbing monotonically across samples (leak in ` +
            `progress): trend delta ${trend.delta} over ${trend.sampleCount} ` +
            `sample(s). Save scene work and plan a restart before the count ` +
            `crosses ${ceilingDesc}.`,
        },
        agentNextSteps: RESOURCE_PRESSURE_AGENT_NEXT_STEPS,
      };
    }
    // warn (headroom.state === "warn")
    return {
      warning: {
        level: "warn",
        message:
          `Godot fd usage is at ${Math.round(headroom.pressureRatio * 100)}% of ` +
          `${ceilingDesc}. Monitor the trend; if it keeps climbing across ` +
          `recompiles/reloads, save scene work and restart Godot via the ` +
          `Hub/CLI before the editor hangs.`,
      },
      agentNextSteps: RESOURCE_PRESSURE_AGENT_NEXT_STEPS,
    };
  }

  // ── P15.5 — local `generate_skill` (project-specific SKILL.md generator) ─

  /**
   * `godot_open_mcp_generate_skill` — emit a project-specific skill file that
   * reflects the actual project state (Godot version, enabled plugins,
   * autoloads, available tools + verify rules + fixes, key `class_name` /
   * Node-Resource subclasses) and MERGE it with the canonical playbook. The
   * canonical playbook (`skills/godot-open-mcp/SKILL.md`) stays hand-authored
   * and is never overwritten — the generator appends a `# Project inventory`
   * section after a `---` separator.
   *
   * Local route (no `POST /tools/generate_skill` on the bridge). Reads
   * `project.godot` + the capability catalog (`buildCapabilities` over the same
   * `ALL_TOOLS` + rule/fix catalogs `routeCapabilities` uses) + a project type
   * scan entirely in the MCP process. `write:false` (default) returns the
   * content as a string (preview, truncated to keep the JSON envelope bounded);
   * `write:true` persists to one or more client skill dirs via
   * `skills/client-paths.json` (unknown client keys are skipped, never abort).
   *
   * Adapted from Unity Open MCP's `routeGenerateSkill` (adapt for the
   * write/clients/include_workflow args + the merge-with-template composer;
   * the project-state read + type scan are Godot-native — see
   * `skill/generate-skill.ts`). Never throws — a missing project.godot / a
   * missing template both degrade gracefully (standalone inventory).
   */
  private async routeGenerateSkill(
    args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    const routeMeta: RouteMeta = { route: "local" };

    const options: GenerateSkillOptions = {};
    if (args.write === true) options.write = true;
    if (
      Array.isArray(args.clients) &&
      args.clients.every((c) => typeof c === "string")
    ) {
      options.clients = args.clients as string[];
    }
    if (typeof args.include_workflow === "boolean") {
      options.includeWorkflow = args.include_workflow;
    }

    // Build the capability surface over the same catalogs `routeCapabilities`
    // uses so the generated skill's "Available tools / Verify rules / Fixes"
    // blocks match what the agent discovers via godot_open_mcp_capabilities.
    const caps = buildCapabilities(
      { tools: ALL_TOOLS, rules: RULE_CATALOG, fixes: FIX_CATALOG },
      {},
    );

    let result;
    try {
      result = await generateSkillImpl(this.projectPath, caps, options);
    } catch (e) {
      // The orchestrator is designed not to throw (a missing project.godot
      // degrades to a standalone inventory), but defend a programmatic caller
      // from an unexpected disk error so the tool never crashes the server.
      return sourceResult(
        {
          error: {
            code: "generate_skill_failed",
            message:
              `generate_skill failed unexpectedly: ${(e as Error)?.message ?? "unknown error"}. ` +
              "The project may be unreadable; check project.godot and retry.",
            projectPath: this.projectPath,
          },
        },
        "local",
        routeMeta,
        true,
      );
    }

    const knownClients = knownClientKeys();
    return sourceResult(
      {
        action: "generate_skill",
        write: options.write === true,
        mergedWithTemplate: result.mergedWithTemplate,
        projectName: result.project.projectName,
        godotVersion: result.project.godotVersion,
        bridgeInstalled: result.project.bridgeInstalled,
        verifyInstalled: result.project.verifyInstalled,
        pluginCount: result.project.plugins.length,
        typeCount: result.project.types.length,
        ...(options.write === true
          ? {
              written: result.written.map((w) => ({
                client: w.client,
                relativePath: w.relativePath,
                existed: w.existed,
              })),
              knownClients,
            }
          : {}),
        // Bounded preview — the full skill is on disk when write:true, or is
        // the returned string when write:false. Truncating keeps the JSON
        // envelope from blowing the response size for large inventories.
        preview: truncateForPreview(result.skill),
        nextSteps:
          options.write === true
            ? [
                "Skill written. Agents driving this project should reload the " +
                  "skill (restart the MCP client or re-read the skill file) so " +
                  "the new inventory takes effect.",
                "Regenerate after plugin or script changes via " +
                  "godot_open_mcp_generate_skill with write:true.",
              ]
            : [
                "Preview only — no files written. Pass write:true to persist " +
                  "to the client skill dirs (defaults to [\"claude\"]; use " +
                  "clients: [...] for more).",
                "Regenerate after plugin or script changes to keep the skill " +
                  "current.",
              ],
      },
      "local",
      routeMeta,
    );
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

/**
 * Shared agent next-steps surfaced on a warn/critical/leaking
 * `resource_pressure` result. The editor is approaching resource exhaustion
 * and the bridge cannot recover on its own — the operator must save + restart
 * before the wedge. After relaunch, a fresh Godot process starts back near a
 * low fd count.
 */
const RESOURCE_PRESSURE_AGENT_NEXT_STEPS: string[] = [
  "Surface this to the operator — the Godot editor is approaching " +
    "resource exhaustion and the bridge (the thing that dies on exhaustion) " +
    "cannot recover on its own.",
  "If scene work is unsaved, recommend saving now while the bridge is " +
    "still healthy.",
  "When the operator is ready, restart Godot via the Hub/CLI. After " +
    "relaunch, call resource_pressure again to sample the fresh process's " +
    "fd baseline (a fresh process starts back near zero).",
];

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

/**
 * Parse the `per_category_thresholds` regression_check argument into a
 * `Map<ruleId, threshold>`. Returns `null` when the input is absent/empty (so
 * the compare falls back to the global-only path). Non-negative integer values
 * only; invalid entries are dropped silently (defense in depth — the JSON
 * Schema already constrains the shape).
 */
function parsePerCategoryThresholds(
  raw: unknown,
): Map<string, number> | null {
  if (raw === null || typeof raw !== "object") return null;
  const entries = Object.entries(raw as Record<string, unknown>);
  if (entries.length === 0) return null;
  const map = new Map<string, number>();
  for (const [ruleId, value] of entries) {
    if (typeof value !== "number" || !Number.isFinite(value) || value < 0) {
      continue;
    }
    map.set(ruleId, Math.trunc(value));
  }
  return map.size > 0 ? map : null;
}

