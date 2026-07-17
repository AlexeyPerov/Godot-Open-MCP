// Canonical tool-group catalog (P8.1).
//
// Single source of truth for the per-session tool-group visibility system.
// Two surfaces consume this catalog:
//
//   - `godot_open_mcp_capabilities` `toolGroups` block (this plan — compiled-
//     state only; no per-session activation flags yet).
//   - `godot_open_mcp_manage_tools` + ListTools filter (P8.2 / P8.3 — session
//     activation state, ListTools visibility, list_changed notifications).
//
// Every registered MCP tool maps to a group id via `groupFor(toolName)`. Tools
// with no entry map to `null` and are always visible — they are server meta-
// tools (capabilities, bridge_status, pull_events, read_compile_errors, and
// manage_tools itself when it ships in P8.3).
//
// Groups are stable lowercase identifiers. `DEFAULT_ENABLED_GROUPS` is derived
// from catalog entries whose `defaultEnabled` flag is true. Other groups are
// hidden from ListTools until activated for the connected MCP session (P8.2).
//
// P8 scope is intentionally lean: Godot has no bridge compile-gating for
// domain packs yet, so the Unity `domainDefine` / `unityPackage` /
// `autoActivate` catalog fields are omitted here. Domain stubs are reserved
// with empty tool rosters so Phase 12 packs reuse the same ids without a
// rename. The `available` flag on each capabilities entry is always `true`
// in P8; P12 will flip it to `false` for uninstalled packs without reshaping
// the field.
//
// Adapted from Unity Open MCP's mcp-server/src/capabilities/tool-groups.ts
// (copy for the ToolGroup interface, the assign-table pattern, and the
// derived DEFAULT_ENABLED_GROUPS / GROUP_IDS helpers). Intentional deltas:
//   - Godot default-on set is `core` only. Unity also enables
//     `gate-and-verify`; Godot folds the gate surface (validate_edit,
//     checkpoint_create, delta, apply_fix) into `core` so the roadmap
//     "only core visible" line stays literally true while the safety
//     surface stays reachable.
//   - One umbrella `typed-editor` group. Godot does not split
//     asset-intelligence / build-settings / diagnostics in P8 — the
//     prompt-size win comes from hiding the whole typed surface behind one
//     activate.
//   - No `domainDefine` / `unityPackage` / `autoActivate` fields. Godot has
//     no bridge compile inventory for packs yet; stubs are always
//     `available: true` with empty tool lists.
//   - Five domain stub ids reserved up front (tilemap, navigation,
//     particles, animation, csg) mirroring the Godot-MCP extension-catalog
//     pack boundaries. They carry no `assign()` calls in P8.

/**
 * Catalog entry for one tool group.
 */
export interface ToolGroup {
  /** Stable lowercase group id (e.g. `"core"`, `"typed-editor"`). */
  id: string;
  /** Short human-readable description of what the group covers. */
  description: string;
  /** True when the group is enabled by default for every fresh session. */
  defaultEnabled: boolean;
}

/**
 * Ordered catalog. Order is preserved in `godot_open_mcp_capabilities`
 * `toolGroups` output and in the future `manage_tools(list_groups)` response
 * so consumers render a stable list.
 */
export const TOOL_GROUPS: ToolGroup[] = [
  {
    id: "core",
    description:
      "Essential entry points and the gate/verify safety surface: ping, " +
      "validate_edit, checkpoint_create, delta, apply_fix. Always on for a " +
      "fresh session.",
    defaultEnabled: true,
  },
  {
    id: "typed-editor",
    description:
      "Typed editor surface: nodes, scenes, scripts, resources, filesystem, " +
      "editor state/selection, console, screenshots, reflection.",
    defaultEnabled: false,
  },
  {
    id: "tilemap",
    description:
      "TileMapLayer tools (domain pack). Empty until the pack ships.",
    defaultEnabled: false,
  },
  {
    id: "navigation",
    description:
      "NavigationRegion / NavigationAgent tools (domain pack). Empty until the pack ships.",
    defaultEnabled: false,
  },
  {
    id: "particles",
    description:
      "GpuParticles tools (domain pack). Empty until the pack ships.",
    defaultEnabled: false,
  },
  {
    id: "animation",
    description:
      "AnimationPlayer tools (domain pack). Empty until the pack ships.",
    defaultEnabled: false,
  },
  {
    id: "csg",
    description:
      "CSG primitive tools (domain pack). Empty until the pack ships.",
    defaultEnabled: false,
  },
];

/**
 * Set of group ids enabled by default for every fresh session. P8 derives
 * exactly `core`; the gate surface (validate_edit / checkpoint_create /
 * delta / apply_fix) lives inside `core` so it is always reachable.
 */
export const DEFAULT_ENABLED_GROUPS: ReadonlySet<string> = new Set(
  TOOL_GROUPS.filter((g) => g.defaultEnabled).map((g) => g.id),
);

/** All known group ids — validates future `manage_tools` activate/deactivate input. */
export const GROUP_IDS: ReadonlySet<string> = new Set(
  TOOL_GROUPS.map((g) => g.id),
);

const GROUP_BY_ID: ReadonlyMap<string, ToolGroup> = new Map(
  TOOL_GROUPS.map((g) => [g.id, g]),
);

/** Lookup one group by id. Returns `undefined` for an unknown id. */
export function getGroup(id: string): ToolGroup | undefined {
  return GROUP_BY_ID.get(id);
}

// ---------------------------------------------------------------------------
// Per-tool group assignment — the authoritative mapping from a registered MCP
// tool name to its group id. Tools not listed here default to `null` (always
// visible). The group vocabulary is the curated, session-visibility-relevant
// subset of the registered surface.
//
// A parity test (tool-groups.test.ts) asserts every registered non-meta
// `ALL_TOOLS` entry is either in the always-visible allow-list OR maps to a
// known group id — so adding a tool without an `assign()` call fails CI.
//
// KEEP THIS TABLE ALIGNED with the typed-editor roster when adding tools.
// ---------------------------------------------------------------------------

const TOOL_GROUP_ASSIGNMENT: Record<string, string> = {};

function assign(group: string, names: string[]): void {
  for (const name of names) TOOL_GROUP_ASSIGNMENT[name] = group;
}

// --- core (gate/verify safety surface folded in) ----------------------------
assign("core", [
  "godot_open_mcp_ping",
  "godot_open_mcp_validate_edit",
  "godot_open_mcp_checkpoint_create",
  "godot_open_mcp_delta",
  "godot_open_mcp_apply_fix",
]);

// --- typed-editor (umbrella group for the whole typed editor surface) -------
// All registered node_* / scene_* / resource_* / filesystem_* / editor_* /
// console_* / screenshot_* / reflection_* tools ride one group so a single
// activate brings up the full typed surface. `script_*` tools are not yet
// registered in ALL_TOOLS; their group ids are reserved here for the phase
// that ships them.
assign(
  "typed-editor",
  [
    // Node
    "node_find",
    "node_create",
    "node_modify",
    "node_set_parent",
    "node_duplicate",
    "node_delete",
    // Scene
    "scene_open",
    "scene_save",
    "scene_list_opened",
    "scene_get_data",
    "scene_create",
    // Script tools are not yet registered in ALL_TOOLS; when the phase that
    // ships them lands, add their `script_*` suffixes here. The parity test
    // (every assigned name exists in ALL_TOOLS) catches a stale reservation.
    // Resource
    "resource_find",
    "resource_get_data",
    "resource_create",
    "resource_modify",
    "resource_move",
    "resource_delete",
    // Filesystem
    "filesystem_list",
    "filesystem_reimport",
    // Editor state + selection
    "editor_application_get_state",
    "editor_application_set_state",
    "editor_selection_get",
    "editor_selection_set",
    // Console
    "console_get_logs",
    "console_clear_logs",
    // Screenshots
    "screenshot_viewport",
    "screenshot_camera",
    "screenshot_isolated",
    // Reflection
    "reflection_method_find",
    "reflection_method_call",
  ].map((suffix) => `godot_open_mcp_${suffix}`),
);

// Domain stub groups (tilemap / navigation / particles / animation / csg)
// carry no assign() calls in P8 — they are reserved ids with empty rosters.

// ---------------------------------------------------------------------------
// Read API
// ---------------------------------------------------------------------------

/**
 * Resolve a tool name to its group id. Returns `null` for tools with no
 * assignment (server meta-tools: capabilities, bridge_status, pull_events,
 * read_compile_errors, and manage_tools when it ships in P8.3). `null` means
 * "always visible" — P8.2's ListTools filter and P8.3's manage_tools never
 * hide a null-group tool.
 *
 * Note: `ping` is assigned to `core` AND will also be listed in
 * `ALWAYS_VISIBLE_TOOLS` in P8.2 — same Unity pattern, so the health probe
 * survives `deactivate(core)`.
 */
export function groupFor(toolName: string): string | null {
  return TOOL_GROUP_ASSIGNMENT[toolName] ?? null;
}

/**
 * Roster of tool names in one group, sorted. Returns `[]` for stub groups
 * with no assignments and for unknown ids. Used by the capabilities
 * `toolGroups` block (this plan) and the future `manage_tools(list_groups)`
 * (P8.3).
 */
export function toolsInGroup(groupId: string): string[] {
  const out: string[] = [];
  for (const [tool, group] of Object.entries(TOOL_GROUP_ASSIGNMENT)) {
    if (group === groupId) out.push(tool);
  }
  out.sort();
  return out;
}
