// Per-session tool-group visibility state (P8.2).
//
// Pure in-memory store: ephemeral, per connected MCP client/session. The MCP
// server is the authority for session visibility; the bridge does NOT track
// session state. Every MCP-server restart restores the catalog's default-on
// groups. One stdio server process has one connected client and one store.
//
// In P8.2 the only mutators are direct calls on the store (unit tests /
// temporary manual harness). `godot_open_mcp_manage_tools` (P8.3) becomes the
// user-facing mutator. ListTools reads the store via `filterVisibleTools` to
// drop tools whose group is not active.
//
// The store is intentionally not keyed by session id — the stdio MCP server
// has exactly one client per process. HTTP/SSE MCP transports would need a
// per-client map (Phase 12+ concern).
//
// In addition to the manual activation path, the store records why each active
// group is active (manual vs default). Auto-activation (`source: "auto"`,
// driven by the live bridge's compiled-tool inventory) is omitted in P8 — Godot
// has no bridge compile-gating for domain packs yet. The `ActivationSource`
// type keeps the vocabulary lean on purpose; Phase 12 may add `"auto"` when
// pack auto-activation needs it.
//
// Adapted from Unity Open MCP's mcp-server/src/tool-session-state.ts (copy for
// the store + filterVisibleTools; the activate/deactivate/reset contract is
// identical). Intentional deltas:
//   - No `activateAuto` / `reconcileAutoActivation` (Godot has no package
//     auto-activation in P8).
//   - `ActivationSource` is `"default" | "manual"` only (no `"auto"` yet).
//   - `ALWAYS_VISIBLE_TOOLS` carries Godot meta-tool names; includes
//     `read_compile_errors` (the offline recovery channel that must survive any
//     group teardown) and `manage_tools` (reserved early so P8.3 does not have
//     to revisit the allow-list when the tool registers).

import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import {
  DEFAULT_ENABLED_GROUPS,
  GROUP_IDS,
  groupFor,
} from "./capabilities/tool-groups.js";
import {
  FD_SAMPLE_RING_CAPACITY,
  type FdSample,
} from "./process-diagnostics.js";

/**
 * Why a group is active in the current session.
 * - `"default"`  — default-on group (in {@link DEFAULT_ENABLED_GROUPS}).
 * - `"manual"`   — activated via a direct store call today; via
 *                  `godot_open_mcp_manage_tools(action=activate)` once P8.3
 *                  ships.
 *
 * `"auto"` (Unity's third variant, driven by package auto-activation) is
 * intentionally omitted in P8 — Godot has no bridge compile-gating for domain
 * packs yet. Phase 12 may extend this union when pack auto-activation lands.
 */
export type ActivationSource = "default" | "manual";

/**
 * Names of always-visible tools (meta-tools with no group assignment). These
 * are never filtered by the session state — an agent can always reach them.
 *
 * `godot_open_mcp_ping` is included: it is the precise connectivity health
 * check (vs `bridge_status`, which is the coarse operator snapshot). A health
 * probe must survive `manage_tools(deactivate, core)` — an agent that just
 * tore down the core group still needs to re-probe the bridge before
 * re-activating. `ping` is also assigned to the `core` group in
 * `capabilities/tool-groups.ts`; the always-visible check runs first in
 * {@link filterVisibleTools}, so the group assignment is a fallback that never
 * applies.
 *
 * `manage_tools` is reserved here before the tool itself ships in P8.3. Listing
 * it early means P8.3 can register the tool without revisiting this allow-list.
 */
const ALWAYS_VISIBLE_TOOLS: ReadonlySet<string> = new Set([
  "godot_open_mcp_capabilities",
  "godot_open_mcp_manage_tools",
  "godot_open_mcp_ping",
  "godot_open_mcp_bridge_status",
  "godot_open_mcp_pull_events",
  "godot_open_mcp_read_compile_errors",
  // P15.3 — restart_editor is the recovery tool for a wedged editor. It must
  // survive any group teardown so an operator can always recover, even when no
  // group (including core) is active.
  "godot_open_mcp_restart_editor",
  // P15.4 — resource_pressure is the proactive diagnostic counterpart to
  // restart_editor. Always-visible so an operator can sample pressure and
  // catch a leak before the editor wedges.
  "godot_open_mcp_resource_pressure",
  // P15.5 — generate_skill emits a project-specific SKILL.md. Always-visible
  // so an operator can regenerate the skill after plugin/script changes
  // regardless of which groups are active.
  "godot_open_mcp_generate_skill",
  // P17.4 — compile_check is the triggered build (active counterpart to
  // read_compile_errors). Always-visible so an agent can verify a fix compiles
  // without depending on any group being active.
  "godot_open_mcp_compile_check",
]);

/**
 * Per-session tool-group visibility store.
 *
 * Lifecycle:
 *  - Constructed once per stdio server process (one connected MCP client).
 *  - Initial active set is {@link DEFAULT_ENABLED_GROUPS} — the groups marked
 *    `defaultEnabled: true` in the canonical tool-group catalog (see
 *    `capabilities/tool-groups.ts`). The lean baseline is `core` only — the
 *    gate surface (validate_edit / checkpoint_create / delta / apply_fix) is
 *    folded into `core` so a fresh session keeps the safety surface reachable.
 *  - Mutated only by {@link activate} / {@link deactivate} / {@link reset}
 *    (direct calls in P8.2; called from the manage_tools router in P8.3).
 *  - Read by {@link isGroupActive} (future manage_tools list_groups) and
 *    {@link filterVisibleTools} (the ListTools handler in `index.ts`).
 */
export class ToolSessionState {
  private active = new Set<string>(DEFAULT_ENABLED_GROUPS);
  /**
   * Per-active-group source tracking. Default-on groups map to `"default"`;
   * manually-activated groups map to `"manual"`. A group that was deactivated
   * and then re-activated manually flips to `"manual"` (manual intent wins).
   * Absent from the map ⇒ the group is not active.
   */
  private source = new Map<string, ActivationSource>();

  /**
   * Session-scoped fd-sample ring for `godot_open_mcp_resource_pressure`
   * (P15.4). Capacity-bounded (LRU on insertion); a `null` count is recorded
   * too so the trend detector sees probe-failure gaps and does not falsely
   * interpolate across them. No disk cache — a server restart clears history.
   */
  private fdSamples: FdSample[] = [];

  constructor() {
    for (const id of DEFAULT_ENABLED_GROUPS) this.source.set(id, "default");
  }

  /** Snapshot of currently-active group ids, sorted for stable output. */
  activeGroups(): string[] {
    return Array.from(this.active).sort();
  }

  /** True when the group is in the active set. */
  isGroupActive(groupId: string): boolean {
    return this.active.has(groupId);
  }

  /** Why the group is active, or `null` when it is not active. */
  activationSource(groupId: string): ActivationSource | null {
    return this.source.get(groupId) ?? null;
  }

  /**
   * Activate a group. Returns true if state changed (group was not active).
   * Unknown groups are rejected with `false` — callers should validate via
   * {@link GROUP_IDS} first and surface a structured error. Idempotent: a
   * no-op (returns `false`) when the group is already active.
   */
  activate(groupId: string): boolean {
    if (!GROUP_IDS.has(groupId)) return false;
    if (this.active.has(groupId)) return false;
    this.active.add(groupId);
    this.source.set(groupId, "manual");
    return true;
  }

  /**
   * Deactivate a group. Returns true if state changed (group was active).
   * Unknown groups are rejected with `false`. Deactivating the `core` group is
   * allowed — the meta-tools (capabilities, manage_tools, ping,
   * read_compile_errors) stay reachable via {@link ALWAYS_VISIBLE_TOOLS}, but
   * the rest of the core surface goes dark until the session re-activates it.
   */
  deactivate(groupId: string): boolean {
    if (!GROUP_IDS.has(groupId)) return false;
    if (!this.active.has(groupId)) return false;
    this.active.delete(groupId);
    this.source.delete(groupId);
    return true;
  }

  /**
   * Restore the default active set (see {@link DEFAULT_ENABLED_GROUPS}). Always
   * returns true — `reset` is unconditional, even when the active set already
   * matches the defaults (callers that need a no-op signal can compare
   * `activeGroups()` before and after).
   */
  reset(): boolean {
    this.active = new Set(DEFAULT_ENABLED_GROUPS);
    this.source = new Map();
    for (const id of DEFAULT_ENABLED_GROUPS) this.source.set(id, "default");
    // P15.4 — clear the fd-sample ring too. The trend signal is session-scoped;
    // a reset means "start over" and stale samples from a prior workflow would
    // mislead the trend detector.
    this.fdSamples = [];
    return true;
  }

  // -------------------------------------------------------------------------
  // P15.4 — session-scoped fd samples for resource_pressure.
  // -------------------------------------------------------------------------

  /**
   * Record one fd sample at the tail of the ring. Capacity-bounded (LRU on
   * insertion). A `null` count is recorded too — the trend detector needs to
   * see probe-failure gaps so it does not falsely interpolate across them.
   */
  recordFdSample(sample: FdSample): void {
    this.fdSamples.push(sample);
    const excess = this.fdSamples.length - FD_SAMPLE_RING_CAPACITY;
    if (excess > 0) this.fdSamples.splice(0, excess);
  }

  /** Snapshot of the recorded fd samples (oldest-first). */
  fdSamplesSnapshot(): readonly FdSample[] {
    return this.fdSamples.slice();
  }

  /** Drop all recorded fd samples without touching the tool-group state. */
  clearFdSamples(): void {
    this.fdSamples = [];
  }
}

/**
 * Filter a tool list to the tools visible in the current session.
 *
 * Visibility rules (precedence high → low):
 *  1. The tool name is in {@link ALWAYS_VISIBLE_TOOLS} → always visible.
 *  2. The tool has no group assignment (`groupFor` returns null) → always
 *     visible (defensive — matches the catalog intent for meta-tools).
 *  3. The tool's group is in the session's active set → visible.
 *  4. Otherwise → hidden.
 *
 * `resolveGroup` is plumbed in so tests can swap the resolver; production
 * callers omit it and get the default catalog resolver (`groupFor`).
 */
export function filterVisibleTools(
  tools: Tool[],
  state: ToolSessionState,
  resolveGroup: (toolName: string) => string | null = groupFor,
): Tool[] {
  return tools.filter((tool) => {
    if (ALWAYS_VISIBLE_TOOLS.has(tool.name)) return true;
    const group = resolveGroup(tool.name);
    if (group === null) return true;
    return state.isGroupActive(group);
  });
}

/**
 * Snapshot of the always-visible allow-list, sorted. Exported for parity
 * tests that lock down the meta-tool contract — a name added or removed here
 * must update the test in `tool-session-state.test.ts` in the same PR.
 */
export const ALWAYS_VISIBLE_TOOL_NAMES: readonly string[] = Array.from(
  ALWAYS_VISIBLE_TOOLS,
).sort();
