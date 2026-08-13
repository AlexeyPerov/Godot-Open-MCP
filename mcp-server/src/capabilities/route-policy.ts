// Canonical route-policy vocabulary + per-tool overrides (P7.5).
//
// Single source of truth for "which execution policy does a tool follow?".
// Both `build-capabilities.ts` (the `godot_open_mcp_capabilities` surface) and
// `tool-router.ts` (the dispatch authority) consume this module so the
// advertised catalog and the shipped router cannot silently drift.
//
// Adapted from Unity Open MCP's in-builder `OFFLINE_TOOLS` / `OFFLINE_FIRST_TOOLS`
// declarations (mcp-server/src/capabilities/build-capabilities.ts) — copy for
// the per-tool override pattern, adapt for Godot's route vocabulary. Intentional
// deltas:
//   - Godot policies are `live | local | offline | live-first` (no Unity
//     `offline-first` and no `compressible` / `batch`).
//   - `live-first` (not `offline-first`) is used for the scene/filesystem
//     readers because live unsaved editor state and importer metadata are more
//     authoritative than disk — Godot prefers the live editor when reachable.
//   - No `batch` / batch-capable / always-batch vocabulary. Godot has no
//     headless editor batch equivalent.

/**
 * The four route policies a tool can follow. The default for any tool not in an
 * override set is {@link DEFAULT_ROUTE_POLICY} (`live`).
 *
 * - `live` — requires the bridge; no disk substitute.
 * - `local` — resolved in the MCP process (may perform a bounded status probe /
 *   event-queue drain against a live-fed stream).
 * - `offline` — resolved from disk/config; never requires the bridge.
 * - `live-first` — prefers live editor state when available; falls back to disk
 *   on classified unavailability (semantic live errors are authoritative and do
 *   NOT trigger the fallback).
 */
export type RoutePolicy = "live" | "local" | "offline" | "live-first";

/** Policy assigned to any tool not listed in an override set below. */
export const DEFAULT_ROUTE_POLICY: RoutePolicy = "live";

/**
 * Tools resolved entirely in the MCP process. They never POST to the bridge
 * `/tools/{name}` endpoint. `bridge_status` and `pull_events` may touch the
 * live transport (one bounded `/ping` probe; one SSE-driven queue drain), but
 * the call is synthesized locally — the bridge has no dedicated handler for
 * them. `manage_tools` mutates the per-session `ToolSessionState` and never
 * touches the live transport at all.
 *
 * The named per-tool constants below are the single source of truth for these
 * names — `tool-router.ts` imports them so its named-handler dispatch list and
 * the capability catalog's override sets cannot drift.
 */
export const CAPABILITIES_TOOL = "godot_open_mcp_capabilities";
export const BRIDGE_STATUS_TOOL = "godot_open_mcp_bridge_status";
export const PULL_EVENTS_TOOL = "godot_open_mcp_pull_events";
export const MANAGE_TOOLS_TOOL = "godot_open_mcp_manage_tools";
// P15.3 — terminate a wedged Godot editor. Local: acts on the OS process; the
// bridge is the thing that dies on a hang, so the tool may not depend on it.
export const RESTART_EDITOR_TOOL = "godot_open_mcp_restart_editor";
// P15.4 — sample live Godot fd/handle pressure + trend. Local: the probe runs
// server-side against the OS; the bridge is the thing that dies on resource
// exhaustion, so the tool may not depend on it.
export const RESOURCE_PRESSURE_TOOL = "godot_open_mcp_resource_pressure";
// P15.5 — generate a project-specific SKILL.md. Local: reads project.godot +
// the capability catalog + a project type scan entirely in the MCP process;
// no bridge round-trip.
export const GENERATE_SKILL_TOOL = "godot_open_mcp_generate_skill";
// P17.3 — gate intelligence: three dry-run (no-mutation) tools that project the
// gate's view of a planned scope, forecast validation cost, and explain a
// finished gate run. Local: resolved over the rule catalog + cost-hints +
// caller-provided gate data — Godot has no server-side gate-run history or
// VerifyCacheService that a live mode would need, so these never POST.
export const IMPACT_PREVIEW_TOOL = "godot_open_mcp_impact_preview";
export const GATE_BUDGET_ESTIMATE_TOOL = "godot_open_mcp_gate_budget_estimate";
export const MUTATION_EXPLAIN_TOOL = "godot_open_mcp_mutation_explain";
// P17.4 — compile_check: the ACTIVE build trigger (complement to read_compile_errors,
// the PASSIVE log reader). Spawns a fresh `dotnet build` / `godot --headless` build from
// the MCP process. Local: the route is `local` (NOT `batch` — Godot has no headless editor,
// so there is intentionally no `batch` route); like restart_editor / resource_pressure, the
// tool does its own bounded OS work without a bridge round-trip.
export const COMPILE_CHECK_TOOL = "godot_open_mcp_compile_check";
// P18.4 — dialog_policy_set: detect (and under an opted-in policy, dismiss) a blocking Godot
// editor modal via desktop automation (osascript / xdotool / PowerShell BM_CLICK). Local: a
// blocking modal stalls the bridge's main thread too, so the tool may not depend on the bridge
// for its probe path. Always-visible meta-tool.
export const DIALOG_POLICY_SET_TOOL = "godot_open_mcp_dialog_policy_set";

const LOCAL_TOOLS: ReadonlySet<string> = new Set([
  CAPABILITIES_TOOL,
  BRIDGE_STATUS_TOOL,
  PULL_EVENTS_TOOL,
  MANAGE_TOOLS_TOOL,
  RESTART_EDITOR_TOOL,
  RESOURCE_PRESSURE_TOOL,
  GENERATE_SKILL_TOOL,
  IMPACT_PREVIEW_TOOL,
  GATE_BUDGET_ESTIMATE_TOOL,
  MUTATION_EXPLAIN_TOOL,
  COMPILE_CHECK_TOOL,
  DIALOG_POLICY_SET_TOOL,
]);

/**
 * Tools that NEVER probe the bridge and NEVER POST to it — they read disk
 * straight. Used for diagnostics that must work in the exact state a dead
 * bridge describes (the addon is not running its listener).
 */
export const READ_COMPILE_ERRORS_TOOL = "godot_open_mcp_read_compile_errors";
export const FIND_REFERENCES_TOOL = "godot_open_mcp_find_references";
export const DEPENDENCIES_TOOL = "godot_open_mcp_dependencies";
// P15.1 — CI regression baseline + check. Always offline: the baseline is
// built from the offline disk scanner (Godot has no headless editor).
export const BASELINE_CREATE_TOOL = "godot_open_mcp_baseline_create";
export const REGRESSION_CHECK_TOOL = "godot_open_mcp_regression_check";
// P17.1 — offline asset intelligence: token-budgeted asset read, reason-tagged
// search, and a compressed `res://` listing. Always offline — they reuse the
// offline readers (project-index + P13.1 reference edges) and never probe the
// bridge.
export const READ_ASSET_TOOL = "godot_open_mcp_read_asset";
export const SEARCH_ASSETS_TOOL = "godot_open_mcp_search_assets";
export const LIST_ASSETS_TOOL = "godot_open_mcp_list_assets";

const OFFLINE_TOOLS: ReadonlySet<string> = new Set([
  READ_COMPILE_ERRORS_TOOL,
  FIND_REFERENCES_TOOL,
  DEPENDENCIES_TOOL,
  BASELINE_CREATE_TOOL,
  REGRESSION_CHECK_TOOL,
  READ_ASSET_TOOL,
  SEARCH_ASSETS_TOOL,
  LIST_ASSETS_TOOL,
]);

/**
 * Tools that prefer the live editor when reachable and fall back to a disk
 * reader when the bridge is classified unavailable. Chosen over `offline` for
 * scene/filesystem reads because unsaved editor state and importer metadata
 * are more authoritative than what is on disk.
 */
export const SCENE_GET_DATA_TOOL = "godot_open_mcp_scene_get_data";
export const FILESYSTEM_LIST_TOOL = "godot_open_mcp_filesystem_list";

const LIVE_FIRST_TOOLS: ReadonlySet<string> = new Set([
  SCENE_GET_DATA_TOOL,
  FILESYSTEM_LIST_TOOL,
]);

/**
 * Readonly view of the override sets so tests and the router can reason about
 * membership without duplicating the tool names. The sets are intentionally
 * exported (not just their union) so a coverage test can assert they are
 * disjoint and a future tool addition is forced to pick exactly one policy.
 */
export const SPECIAL_ROUTE_TOOLS = {
  local: LOCAL_TOOLS,
  offline: OFFLINE_TOOLS,
  "live-first": LIVE_FIRST_TOOLS,
} as const satisfies Record<Exclude<RoutePolicy, typeof DEFAULT_ROUTE_POLICY>, ReadonlySet<string>>;

/**
 * Resolve the canonical route policy for a registered tool name. Tools not in
 * any override set get {@link DEFAULT_ROUTE_POLICY}.
 *
 * Exported (rather than inlined in `buildCapabilities`) so `tool-router.ts` can
 * import the same classification the catalog advertises — the router does not
 * branch on `routePolicyFor`, but importing the override sets here is the
 * anti-drift hook (a test asserts the router's named-handler list matches the
 * non-`live` policies).
 */
export function routePolicyFor(toolName: string): RoutePolicy {
  if (LOCAL_TOOLS.has(toolName)) return "local";
  if (OFFLINE_TOOLS.has(toolName)) return "offline";
  if (LIVE_FIRST_TOOLS.has(toolName)) return "live-first";
  return DEFAULT_ROUTE_POLICY;
}

/**
 * The route policies the shipped router can actually execute. Kept as a static
 * array so the capabilities `routing` block can advertise them without a second
 * source of truth.
 *
 * Order: the catalog order used in `docs/api/mcp-tools.md` (live → local →
 * offline → live-first). `batch` is deliberately absent.
 */
export const ROUTE_POLICIES: readonly RoutePolicy[] = [
  "live",
  "local",
  "offline",
  "live-first",
];

/**
 * Compact routing summary embedded in the capabilities response. Mirrors the
 * route-policy section of `docs/api/mcp-tools.md`. It does NOT duplicate the
 * full prose — per-tool policies live on each tool entry.
 *
 * `batchSupported` is intentionally omitted (negative feature flag). The
 * `policies` array is the authoritative capability-side roster; the docs carry
 * the "no batch route" statement.
 */
export interface RoutingSummary {
  /** Most tools prefer the live bridge when it is connected. */
  liveDefault: boolean;
  /** Route policies the shipped router can execute (no `batch`). */
  policies: readonly RoutePolicy[];
}

/** The single routing summary embedded in every capabilities response. */
export const ROUTING_SUMMARY: RoutingSummary = {
  liveDefault: true,
  policies: ROUTE_POLICIES,
};
