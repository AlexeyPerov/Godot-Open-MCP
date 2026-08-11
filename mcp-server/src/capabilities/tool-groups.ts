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
//   - One umbrella `typed-editor` group for the typed editor surface. The
//     `asset-intelligence` group is reserved for offline asset-graph tools
//     (`find_references`, later `dependencies` / readers) and stays
//     default-off until activated via manage_tools.
//   - No `domainDefine` / `unityPackage` / `autoActivate` fields. Godot has
//     no bridge compile inventory for packs; domain groups are always
//     `available: true` (their tools exist in every 4.3+ build) and are
//     `defaultEnabled: false` until a client activates them via manage_tools.
//   - Five domain group ids reserved up front in P8 (tilemap, navigation,
//     particles, animation, csg) and filled with `assign()` calls as each
//     Phase 12 pack shipped.

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
      "Typed editor surface: nodes, scenes, resources, filesystem, " +
      "editor state/selection, console, screenshots, reflection.",
    defaultEnabled: false,
  },
  {
    id: "asset-intelligence",
    description:
      "Offline asset-graph intelligence: reverse reference lookup " +
      "(find_references), forward + reverse dependencies (dependencies), " +
      "a token-budgeted asset read (read_asset), reason-tagged project-wide " +
      "search (search_assets), and a compressed `res://` listing " +
      "(list_assets). Hidden until activated via manage_tools.",
    defaultEnabled: false,
  },
  {
    id: "tilemap",
    description:
      "TileMapLayer tools (Godot 4.3+): create a layer, assign a TileSet, " +
      "set/erase/clear cells, and list used cells.",
    defaultEnabled: false,
  },
  {
    id: "navigation",
    description:
      "NavigationRegion / NavigationAgent / NavigationLink tools (Godot 4.3+, 2D + 3D): " +
      "starter defaults, create regions/agents/links, assign a region's navigation resource, " +
      "configure agent scalars, and inspect any navigation node.",
    defaultEnabled: false,
  },
  {
    id: "particles",
    description:
      "GpuParticles2D/3D tools (Godot 4.3+, 2D + 3D): starter defaults, create an emitter, " +
      "configure allow-listed + clamped scalars, set_emitting (start/stop + optional restart), " +
      "and inspect any emitter.",
    defaultEnabled: false,
  },
  {
    id: "animation",
    description:
      "AnimationPlayer tools (Godot 4.3+): starter defaults, create an AnimationPlayer node, " +
      "add an empty AnimationLibrary, create an Animation clip (auto-creating the library when " +
      "missing), add a value / position_3d / rotation_3d / scale_3d track, insert a keyframe, " +
      "and inspect any player's libraries / animations / tracks.",
    defaultEnabled: false,
  },
  {
    id: "csg",
    description:
      "CSG primitive tools (Godot 4.3+, 3D only): starter defaults, create CsgBox3D / CsgSphere3D / " +
      "CsgCylinder3D / CsgCombiner3D nodes (with optional kind-specific scalars + boolean operation), " +
      "set the boolean operation (union / intersection / subtraction) on any CSG shape, and inspect " +
      "any CSG shape's scalar config.",
    defaultEnabled: false,
  },
  {
    id: "settings",
    description:
      "Project settings tools (Godot 4.3+): read one project.godot section (rendering / physics / " +
      "input / layer_names / autoload / application / display) or a per-section summary, and write " +
      "key/value pairs within one section via Godot's ProjectSettings API (no raw text edits).",
    defaultEnabled: false,
  },
  {
    id: "materials",
    description:
      "Materials + shaders tools (Godot 4.3+): create a StandardMaterial3D / ORMMaterial3D / " +
      "ShaderMaterial .tres, list a material's properties + values, set one property, assign a " +
      ".gdshader to a ShaderMaterial, and read a .gdshader's uniforms + types.",
    defaultEnabled: false,
  },
  {
    id: "lighting",
    description:
      "Lighting tools (Godot 4.3+, 2D + 3D): create a DirectionalLight3D / OmniLight3D / SpotLight3D " +
      "/ DirectionalLight2D / PointLight2D node (+ optional starter scalars), patch one or many " +
      "allow-listed + clamped light scalars (color / energy / range / spot_angle / attenuation / " +
      "shadow_enabled), and create or replace the WorldEnvironment node's Environment resource.",
    defaultEnabled: false,
  },
  {
    id: "audio",
    description:
      "Audio tools (Godot 4.3+): create an AudioStreamPlayer / AudioStreamPlayer2D / AudioStreamPlayer3D " +
      "node (+ optional stream + bus + starter scalars), assign an AudioStream resource to an existing " +
      "player, and set an audio bus's volume via AudioServer (native dB or linear→dB).",
    defaultEnabled: false,
  },
  {
    id: "ui",
    description:
      "UI tools (Godot 4.3+): create a Control subclass by type (Button / Label / LineEdit / TextureRect / " +
      "ColorRect / ProgressBar / CheckBox / ...) with starter full-rect anchors, bulk-patch allow-listed control " +
      "scalars, add a container (VBoxContainer / HBoxContainer / GridContainer / MarginContainer / " +
      "ScrollContainer), patch container layout properties, and assign a Theme resource to a control subtree.",
    defaultEnabled: false,
  },
  {
    id: "spatial",
    description:
      "Spatial query tool (Godot 4.3+, 2D + 3D): read-only physics ray / shape / point queries against " +
      "the edited scene's physics world (PhysicsDirectSpaceState2D/3D) with collision mask + node-path " +
      "exclude + bounded, truncated hit results. Live-only.",
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
  // P15.1 — CI regression baseline + check. Offline-routed but conceptually
  // part of the gate/verify safety surface (the project-level "did this PR
  // introduce new errors?" gate). Folded into core so they are always visible
  // alongside checkpoint_create/delta.
  "godot_open_mcp_baseline_create",
  "godot_open_mcp_regression_check",
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

// --- asset-intelligence (offline asset-graph tools) ------------------------
// find_references lands first; dependencies (forward + impact) joins next;
// P17.1 adds the generic asset read/search/list readers. defaultEnabled:
// false — activate via manage_tools.
assign("asset-intelligence", [
  "godot_open_mcp_find_references",
  "godot_open_mcp_dependencies",
  "godot_open_mcp_read_asset",
  "godot_open_mcp_search_assets",
  "godot_open_mcp_list_assets",
]);

// Domain pack groups (tilemap / navigation / particles / animation / csg)
// were reserved as empty stubs in P8 and filled progressively by the Phase 12
// packs: P12.1 tilemap (6), P12.2 navigation (7), P12.3 particles (5), P12.4
// animation (7), P12.5 csg (7). Phase 12 is now complete — every reserved stub
// carries its pack's roster.

// --- tilemap (P12.1 — Godot 4.3+ TileMapLayer domain pack) ------------------
// Six tools: create / set_tileset / set_cell / erase_cell / get_used_cells
// (read-only) / clear. Hidden until activated via manage_tools.
assign(
  "tilemap",
  [
    "tilemap_create",
    "tilemap_set_tileset",
    "tilemap_set_cell",
    "tilemap_erase_cell",
    "tilemap_get_used_cells",
    "tilemap_clear",
  ].map((suffix) => `godot_open_mcp_${suffix}`),
);

// --- navigation (P12.2 — Godot 4.3+ navigation domain pack) ------------------
// Seven tools: defaults (read-only) / region_create / region_set_mesh /
// agent_create / agent_configure / link_create / get (read-only). Hidden until
// activated via manage_tools.
assign(
  "navigation",
  [
    "navigation_defaults",
    "navigation_region_create",
    "navigation_region_set_mesh",
    "navigation_agent_create",
    "navigation_agent_configure",
    "navigation_link_create",
    "navigation_get",
  ].map((suffix) => `godot_open_mcp_${suffix}`),
);

// --- particles (P12.3 — Godot 4.3+ particles domain pack) ------------------
// Five tools: defaults (read-only) / create / configure / set_emitting / get
// (read-only). Hidden until activated via manage_tools.
assign(
  "particles",
  [
    "particles_defaults",
    "particles_create",
    "particles_configure",
    "particles_set_emitting",
    "particles_get",
  ].map((suffix) => `godot_open_mcp_${suffix}`),
);

// --- animation (P12.4 — Godot 4.3+ animation domain pack) ------------------
// Seven tools: defaults (read-only) / player_create / library_add / animation_create
// / add_track / insert_key / get (read-only). Hidden until activated via
// manage_tools.
assign(
  "animation",
  [
    "animation_defaults",
    "animation_player_create",
    "animation_library_add",
    "animation_create",
    "animation_add_track",
    "animation_insert_key",
    "animation_get",
  ].map((suffix) => `godot_open_mcp_${suffix}`),
);

// --- csg (P12.5 — Godot 4.3+ CSG domain pack) -------------------------------
// Seven tools: defaults (read-only) / box_create / sphere_create /
// cylinder_create / combiner_create / set_operation / get (read-only). Hidden
// until activated via manage_tools.
assign(
  "csg",
  [
    "csg_defaults",
    "csg_box_create",
    "csg_sphere_create",
    "csg_cylinder_create",
    "csg_combiner_create",
    "csg_set_operation",
    "csg_get",
  ].map((suffix) => `godot_open_mcp_${suffix}`),
);

// --- settings (P16.1 — Godot 4.3+ project-settings pack) --------------------
// Two tools: get_project (read-only — one section or a per-section summary) /
// set_project (mutating — key/value pairs within one section via ProjectSettings
// API). Hidden until activated via manage_tools.
assign(
  "settings",
  [
    "settings_get_project",
    "settings_set_project",
  ].map((suffix) => `godot_open_mcp_${suffix}`),
);

// --- materials (P16.2 — Godot 4.3+ materials/shaders pack) -------------------
// Five tools: material_create (mutating — StandardMaterial3D / ORMMaterial3D /
// ShaderMaterial .tres) / material_get_properties (read-only) / material_set_property
// (mutating — set one property + save) / material_set_shader (mutating — assign a
// .gdshader to a ShaderMaterial + save) / shader_get_data (read-only — a .gdshader's
// uniforms + types). Hidden until activated via manage_tools.
assign(
  "materials",
  [
    "material_create",
    "material_get_properties",
    "material_set_property",
    "material_set_shader",
    "shader_get_data",
  ].map((suffix) => `godot_open_mcp_${suffix}`),
);

// --- lighting (P16.3 — Godot 4.3+ lighting pack) ----------------------------
// Four tools: light_create (mutating — DirectionalLight3D / OmniLight3D /
// SpotLight3D / DirectionalLight2D / PointLight2D node + starter scalars) /
// light_set (mutating — patch one allow-listed light scalar) / light_modify
// (mutating — bulk patch multiple light scalars) / environment_set (mutating —
// create or replace the WorldEnvironment node's Environment resource). Hidden
// until activated via manage_tools.
assign(
  "lighting",
  [
    "light_create",
    "light_set",
    "light_modify",
    "environment_set",
  ].map((suffix) => `godot_open_mcp_${suffix}`),
);

// --- audio (P16.4 — Godot 4.3+ audio pack) ----------------------------------
// Three tools: audio_stream_player_create (mutating — AudioStreamPlayer /
// AudioStreamPlayer2D / AudioStreamPlayer3D node + optional stream + bus +
// starter scalars) / audio_stream_player_set_stream (mutating — assign an
// AudioStream resource) / audio_bus_set_volume (mutating — set a bus volume via
// AudioServer, native dB or linear→dB). Hidden until activated via manage_tools.
assign(
  "audio",
  [
    "audio_stream_player_create",
    "audio_stream_player_set_stream",
    "audio_bus_set_volume",
  ].map((suffix) => `godot_open_mcp_${suffix}`),
);

// --- ui (P16.5 — Godot 4.3+ UI controls/containers/themes pack) -------------
// Five tools: control_create (mutating — Button/Label/LineEdit/... Control
// subclass by `type` + starter full-rect anchors) / control_modify (mutating —
// bulk-patch allow-listed control scalars: text/tooltip_text/disabled/color/
// offsets/size_flags/value) / container_add (mutating — VBoxContainer/
// HBoxContainer/GridContainer/MarginContainer/ScrollContainer) /
// container_set_layout (mutating — separation/columns/alignment/margins) /
// theme_apply (mutating — load a Theme .tres and assign to a Control subtree,
// optionally recursive). Hidden until activated via manage_tools.
assign(
  "ui",
  [
    "control_create",
    "control_modify",
    "container_add",
    "container_set_layout",
    "theme_apply",
  ].map((suffix) => `godot_open_mcp_${suffix}`),
);

// --- spatial (P16.6 — Godot 4.3+ physics spatial-query pack) ----------------
// One tool: spatial_query (read-only — ray / shape / point query in 2D + 3D
// against the edited scene's PhysicsDirectSpaceState2D/3D, with collision mask
// + node-path exclude + bounded, truncated results). Hidden until activated via
// manage_tools.
assign(
  "spatial",
  ["spatial_query"].map((suffix) => `godot_open_mcp_${suffix}`),
);

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
