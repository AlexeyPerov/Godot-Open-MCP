// Canonical tool-group catalog tests (P8.1).
//
// Pins the catalog invariants, the derived DEFAULT_ENABLED_GROUPS / GROUP_IDS
// sets, the per-tool assignment table, and full registry coverage over the
// real ALL_TOOLS. The parity test (groupFor coverage) is the anti-drift hook:
// adding a non-meta tool without an `assign()` call fails CI. Session-state
// filtering (ToolSessionState / filterVisibleTools) lands in P8.2 and is NOT
// covered here.
//
// Adapted from Unity Open MCP's tool-groups.test.ts (copy for the catalog-
// invariant test shape; the assertions are Godot-specific).

import { test } from "node:test";
import assert from "node:assert/strict";

import { ALL_TOOLS } from "../tools/index.js";
import {
  TOOL_GROUPS,
  DEFAULT_ENABLED_GROUPS,
  GROUP_IDS,
  getGroup,
  groupFor,
  toolsInGroup,
} from "./tool-groups.js";

// ---------------------------------------------------------------------------
// Catalog invariants
// ---------------------------------------------------------------------------

test("TOOL_GROUPS has stable, unique, lowercase kebab-case ids", () => {
  const ids = TOOL_GROUPS.map((g) => g.id);
  assert.equal(new Set(ids).size, ids.length, "group ids must be unique");
  for (const id of ids) {
    assert.equal(id, id.toLowerCase(), `${id} must be lowercase`);
    assert.ok(
      /^[a-z][a-z0-9-]*$/.test(id),
      `${id} must be lowercase kebab-case`,
    );
  }
});

test("TOOL_GROUPS id order is pinned (stable across catalog edits)", () => {
  // Catalog order is the order capabilities / future list_groups render.
  // Pinning the array — not a sorted snapshot — catches an accidental
  // reordering. A deliberate reorder updates this assertion in the same PR.
  assert.deepEqual(
    TOOL_GROUPS.map((g) => g.id),
    [
      "core",
      "typed-editor",
      "tilemap",
      "navigation",
      "particles",
      "animation",
      "csg",
    ],
  );
});

test("every group carries a non-empty description", () => {
  for (const g of TOOL_GROUPS) {
    assert.ok(
      typeof g.description === "string" && g.description.length > 10,
      `${g.id} must have a meaningful description`,
    );
  }
});

test("DEFAULT_ENABLED_GROUPS is exactly { core }", () => {
  // P8 roadmap contract: only `core` is visible at the start of a fresh
  // session. Unity also enables `gate-and-verify`; Godot folds the gate
  // surface (validate_edit / checkpoint_create / delta / apply_fix) into
  // core so this set stays literally `{ "core" }`.
  assert.deepEqual(Array.from(DEFAULT_ENABLED_GROUPS).sort(), ["core"]);
  assert.ok(DEFAULT_ENABLED_GROUPS.has("core"));
});

test("DEFAULT_ENABLED_GROUPS matches the catalog's defaultEnabled entries", () => {
  // Single source of truth — the set is derived from the catalog. Asserting
  // against the catalog keeps this test honest when defaults change instead
  // of hard-coding a stale snapshot.
  const expected = TOOL_GROUPS.filter((g) => g.defaultEnabled)
    .map((g) => g.id)
    .sort();
  assert.deepEqual(Array.from(DEFAULT_ENABLED_GROUPS).sort(), expected);
});

test("GROUP_IDS matches TOOL_GROUPS ids", () => {
  assert.deepEqual(
    Array.from(GROUP_IDS).sort(),
    TOOL_GROUPS.map((g) => g.id).sort(),
  );
});

// ---------------------------------------------------------------------------
// Specific group presence
// ---------------------------------------------------------------------------

test("core group is registered and default-on", () => {
  const core = getGroup("core");
  assert.ok(core, "core group must exist");
  assert.equal(core!.defaultEnabled, true);
});

test("typed-editor group is registered, opt-in, with a non-empty roster", () => {
  // The umbrella group for the whole typed surface. Opt-in (defaultEnabled
  // false) so a fresh session advertises only `core`. Must carry a non-empty
  // roster once assignments land.
  const typed = getGroup("typed-editor");
  assert.ok(typed, "typed-editor group must exist");
  assert.equal(typed!.defaultEnabled, false);
  const roster = toolsInGroup("typed-editor");
  assert.ok(
    roster.length > 0,
    "typed-editor must have at least one assigned tool",
  );
});

test("the five domain stub groups are present with empty rosters", () => {
  // Reserved ids so Phase 12 packs reuse them without a rename. Empty in P8
  // (no assign() calls) and always `available: true` in capabilities.
  const stubIds = ["tilemap", "navigation", "particles", "animation", "csg"];
  for (const id of stubIds) {
    const g = getGroup(id);
    assert.ok(g, `${id} stub group must exist`);
    assert.equal(g!.defaultEnabled, false, `${id} must be opt-in`);
    assert.deepEqual(
      toolsInGroup(id),
      [],
      `${id} roster must be empty in P8`,
    );
  }
});

test("getGroup returns undefined for unknown ids", () => {
  assert.equal(getGroup("does-not-exist"), undefined);
});

// ---------------------------------------------------------------------------
// Per-tool assignment
// ---------------------------------------------------------------------------

test("groupFor returns null for always-visible meta-tools", () => {
  // These tools MUST stay always-visible so an agent can reach them before
  // any other group is active. P8.2 enforces this via ALWAYS_VISIBLE_TOOLS in
  // tool-session-state.ts; P8.1 only pins the catalog-side classification.
  // P8.3 adds manage_tools — it mutates the per-session store and must stay
  // reachable across any group teardown.
  for (const name of [
    "godot_open_mcp_capabilities",
    "godot_open_mcp_bridge_status",
    "godot_open_mcp_pull_events",
    "godot_open_mcp_read_compile_errors",
    "godot_open_mcp_manage_tools",
  ]) {
    assert.equal(
      groupFor(name),
      null,
      `${name} must be always-visible (null group)`,
    );
  }
});

test("groupFor assigns ping + the gate surface to core", () => {
  // Ping is the health probe; the gate surface is folded into core (P8 delta
  // vs Unity, which keeps gate-and-verify as its own default-on group). Ping
  // will ALSO be always-visible in P8.2 — dual membership is intentional.
  for (const name of [
    "godot_open_mcp_ping",
    "godot_open_mcp_validate_edit",
    "godot_open_mcp_checkpoint_create",
    "godot_open_mcp_delta",
    "godot_open_mcp_apply_fix",
  ]) {
    assert.equal(groupFor(name), "core", `${name} must map to core`);
  }
});

test("groupFor assigns representative typed-editor tools to typed-editor", () => {
  for (const name of [
    "godot_open_mcp_node_find",
    "godot_open_mcp_node_create",
    "godot_open_mcp_scene_open",
    "godot_open_mcp_scene_get_data",
    "godot_open_mcp_resource_get_data",
    "godot_open_mcp_filesystem_list",
    "godot_open_mcp_editor_application_get_state",
    "godot_open_mcp_console_get_logs",
    "godot_open_mcp_screenshot_viewport",
    "godot_open_mcp_reflection_method_find",
  ]) {
    assert.equal(
      groupFor(name),
      "typed-editor",
      `${name} must map to typed-editor`,
    );
  }
});

test("groupFor returns null for unknown tool names", () => {
  assert.equal(groupFor("godot_open_mcp_does_not_exist"), null);
});

test("groupFor(ping) === core (dual membership pin)", () => {
  // Explicit pin from the P8.1 acceptance criteria: ping is assigned to core
  // AND will be always-visible in P8.2. The catalog side is `core` only.
  assert.equal(groupFor("godot_open_mcp_ping"), "core");
});

// ---------------------------------------------------------------------------
// toolsInGroup roster integrity
// ---------------------------------------------------------------------------

test("toolsInGroup rosters are sorted and round-trip via groupFor", () => {
  for (const g of TOOL_GROUPS) {
    const roster = toolsInGroup(g.id);
    const sorted = [...roster].sort();
    assert.deepEqual(roster, sorted, `${g.id} roster must be sorted`);
    for (const t of roster) {
      assert.equal(
        groupFor(t),
        g.id,
        `${t} must map back to ${g.id}`,
      );
    }
  }
});

test("toolsInGroup returns [] for unknown ids", () => {
  assert.deepEqual(toolsInGroup("does-not-exist"), []);
});

test("toolsInGroup core roster matches the assign table", () => {
  const roster = toolsInGroup("core");
  // The five core tools — ping + the gate surface folded in.
  assert.deepEqual(roster, [
    "godot_open_mcp_apply_fix",
    "godot_open_mcp_checkpoint_create",
    "godot_open_mcp_delta",
    "godot_open_mcp_ping",
    "godot_open_mcp_validate_edit",
  ]);
});

// ---------------------------------------------------------------------------
// Registry coverage — every registered non-meta tool is classified.
// ---------------------------------------------------------------------------

/**
 * Always-visible meta-tools: these are intentionally NOT in any group
 * (groupFor → null) and bypass the future ListTools filter. The parity test
 * below asserts every OTHER tool in ALL_TOOLS maps to a known group id.
 *
 * P8.3 adds `godot_open_mcp_manage_tools` — registered in P8.3, it mutates
 * the per-session store that filters ListTools and must always be reachable
 * so an agent can re-enable a group it just tore down. Matches the canonical
 * allow-list in `tool-session-state.ts`.
 */
const ALWAYS_VISIBLE_TOOLS: ReadonlySet<string> = new Set([
  "godot_open_mcp_capabilities",
  "godot_open_mcp_bridge_status",
  "godot_open_mcp_pull_events",
  "godot_open_mcp_read_compile_errors",
  "godot_open_mcp_manage_tools",
]);

test("every registered non-meta tool maps to a known group id", () => {
  // The anti-drift hook: a newly registered tool MUST land in an assign()
  // call (or be added to ALWAYS_VISIBLE_TOOLS above). A tool that falls
  // through to `null` here would be silently always-visible — usually a
  // catalog gap, not intent.
  const orphans: string[] = [];
  for (const tool of ALL_TOOLS) {
    if (ALWAYS_VISIBLE_TOOLS.has(tool.name)) continue;
    const g = groupFor(tool.name);
    if (g === null) {
      orphans.push(tool.name);
      continue;
    }
    assert.ok(
      GROUP_IDS.has(g),
      `${tool.name} maps to unknown group '${g}'`,
    );
  }
  assert.deepEqual(
    orphans,
    [],
    `non-meta tools with no group assignment (add an assign() call or add to ALWAYS_VISIBLE_TOOLS): ${orphans.join(", ")}`,
  );
});

test("every assigned tool name exists in ALL_TOOLS", () => {
  // The inverse direction: an assign() entry that points at a tool that is
  // not registered is dead weight. Catches typos and stale reservations
  // (e.g. script_* entries left in place after a rename).
  const registered = new Set(ALL_TOOLS.map((t) => t.name));
  const stale: string[] = [];
  for (const group of TOOL_GROUPS) {
    for (const name of toolsInGroup(group.id)) {
      if (!registered.has(name)) stale.push(`${name} (group: ${group.id})`);
    }
  }
  assert.deepEqual(
    stale,
    [],
    `assigned tools that are not in ALL_TOOLS (typo or stale reservation): ${stale.join(", ")}`,
  );
});

test("no tool is assigned to more than one group", () => {
  // assign() overwrites silently, so this is structurally impossible today —
  // but the test pins the invariant so a future catalog refactor that
  // introduces multi-assignment is caught.
  const seen = new Map<string, string>();
  for (const group of TOOL_GROUPS) {
    for (const name of toolsInGroup(group.id)) {
      const prev = seen.get(name);
      assert.equal(
        prev,
        undefined,
        `${name} is assigned to both '${prev}' and '${group.id}'`,
      );
      seen.set(name, group.id);
    }
  }
});

test("every registered tool is either meta, core, or typed-editor", () => {
  // P8-specific regression guard: the only groups that carry tools in P8 are
  // `core` and `typed-editor`. If a tool ends up in a domain stub group,
  // either the stub shipped early or the assign() landed in the wrong place.
  for (const tool of ALL_TOOLS) {
    const g = groupFor(tool.name);
    if (g === null) continue; // meta-tool
    assert.ok(
      g === "core" || g === "typed-editor",
      `${tool.name} is in group '${g}' — P8 only populates core + typed-editor`,
    );
  }
});
