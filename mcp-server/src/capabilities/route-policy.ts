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
 * them.
 *
 * P8 local tools (`manage_tools`, `generate_skill`) MUST be added here when
 * they ship, not listed as implemented early.
 *
 * The named per-tool constants below are the single source of truth for these
 * names — `tool-router.ts` imports them so its named-handler dispatch list and
 * the capability catalog's override sets cannot drift.
 */
export const CAPABILITIES_TOOL = "godot_open_mcp_capabilities";
export const BRIDGE_STATUS_TOOL = "godot_open_mcp_bridge_status";
export const PULL_EVENTS_TOOL = "godot_open_mcp_pull_events";

const LOCAL_TOOLS: ReadonlySet<string> = new Set([
  CAPABILITIES_TOOL,
  BRIDGE_STATUS_TOOL,
  PULL_EVENTS_TOOL,
]);

/**
 * Tools that NEVER probe the bridge and NEVER POST to it — they read disk
 * straight. Used for diagnostics that must work in the exact state a dead
 * bridge describes (the addon is not running its listener).
 */
export const READ_COMPILE_ERRORS_TOOL = "godot_open_mcp_read_compile_errors";

const OFFLINE_TOOLS: ReadonlySet<string> = new Set([
  READ_COMPILE_ERRORS_TOOL,
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
