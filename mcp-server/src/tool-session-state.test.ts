// Dedicated tests for tool-session-state.ts (P8.2) — the per-session
// tool-group visibility store and the ListTools filter it drives.
//
// Locks down the activate / deactivate / reset / source-tracking state
// machine and the ALWAYS_VISIBLE_TOOLS contract — the edges where a silent
// regression in tool-surface visibility would be most dangerous. The catalog
// invariants (DEFAULT_ENABLED_GROUPS, GROUP_IDS, groupFor coverage) are pinned
// in capabilities/tool-groups.test.ts; this file complements them at the
// session-state layer.
//
// Adapted from Unity Open MCP's mcp-server/src/tool-session-state.test.ts
// (copy for the source-tracking + filter-precedence test shape; the assertions
// are Godot-specific). Intentional deltas:
//   - No activateAuto / reconcileAutoActivation tests (omitted in P8).
//   - Default-active set is `{ core }` only (Unity also enables
//     `gate-and-verify`; Godot folds the gate surface into `core`).
//   - EXPECTED_ALWAYS_VISIBLE reflects the Godot meta-tool roster, including
//     `read_compile_errors` (the offline recovery channel) and a pre-listed
//     `manage_tools` reservation (registered in P8.3).

import { test } from "node:test";
import assert from "node:assert/strict";
import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import {
  ToolSessionState,
  filterVisibleTools,
  ALWAYS_VISIBLE_TOOL_NAMES,
} from "./tool-session-state.js";
import { DEFAULT_ENABLED_GROUPS, GROUP_IDS } from "./capabilities/tool-groups.js";
import type { FdSample } from "./process-diagnostics.js";

// ---------------------------------------------------------------------------
// Fresh-state invariants
// ---------------------------------------------------------------------------

test("fresh state has exactly { core } active", () => {
  const s = new ToolSessionState();
  assert.deepEqual(s.activeGroups(), ["core"]);
});

test("fresh state reports every default-active group as source 'default'", () => {
  const s = new ToolSessionState();
  for (const id of DEFAULT_ENABLED_GROUPS) {
    assert.equal(s.activationSource(id), "default", `group=${id}`);
  }
});

test("activationSource returns null for an inactive group", () => {
  const s = new ToolSessionState();
  assert.equal(s.activationSource("typed-editor"), null);
  assert.equal(s.activationSource("does-not-exist"), null);
});

test("isGroupActive agrees with activeGroups for every known group on a fresh state", () => {
  const s = new ToolSessionState();
  const active = new Set(s.activeGroups());
  for (const id of GROUP_IDS) {
    assert.equal(s.isGroupActive(id), active.has(id), `group=${id}`);
  }
});

// ---------------------------------------------------------------------------
// activate / deactivate / reset
// ---------------------------------------------------------------------------

test("activate adds an opt-in group and sets source to 'manual'", () => {
  const s = new ToolSessionState();
  assert.equal(s.activate("typed-editor"), true);
  assert.equal(s.isGroupActive("typed-editor"), true);
  assert.equal(s.activationSource("typed-editor"), "manual");
  assert.deepEqual(s.activeGroups(), ["core", "typed-editor"]);
});

test("activate is idempotent — second call returns false, source unchanged", () => {
  const s = new ToolSessionState();
  assert.equal(s.activate("typed-editor"), true);
  // Second call: no state change — returns false, source stays 'manual'.
  assert.equal(s.activate("typed-editor"), false);
  assert.equal(s.activationSource("typed-editor"), "manual");
});

test("activate rejects unknown ids — returns false, state unchanged", () => {
  const s = new ToolSessionState();
  assert.equal(s.activate("does-not-exist"), false);
  assert.deepEqual(s.activeGroups(), ["core"]);
});

test("activate on a deactivated default-on group flips source from 'default' to 'manual'", () => {
  // Deactivate a default-on group then re-activate it manually: the source
  // must be 'manual', not 'default' — the re-activation is an explicit act.
  const s = new ToolSessionState();
  const id = Array.from(DEFAULT_ENABLED_GROUPS)[0];
  assert.equal(s.deactivate(id), true);
  assert.equal(s.activationSource(id), null);
  assert.equal(s.activate(id), true);
  assert.equal(s.activationSource(id), "manual");
});

test("deactivate removes an active group and clears its source", () => {
  const s = new ToolSessionState();
  s.activate("typed-editor");
  assert.equal(s.activationSource("typed-editor"), "manual");
  assert.equal(s.deactivate("typed-editor"), true);
  assert.equal(s.isGroupActive("typed-editor"), false);
  assert.equal(s.activationSource("typed-editor"), null);
});

test("deactivate is idempotent on an already-inactive group", () => {
  const s = new ToolSessionState();
  assert.equal(s.deactivate("typed-editor"), false);
});

test("deactivate rejects unknown ids", () => {
  const s = new ToolSessionState();
  assert.equal(s.deactivate("does-not-exist"), false);
});

test("deactivate('core') is allowed — meta-tools stay reachable via the allow-list", () => {
  // The P8.2 contract: even when core is deactivated, ALWAYS_VISIBLE_TOOLS
  // keep the agent alive. The store does not police this; the filter does.
  const s = new ToolSessionState();
  assert.equal(s.deactivate("core"), true);
  assert.deepEqual(s.activeGroups(), []);
});

test("reset restores the default active set", () => {
  const s = new ToolSessionState();
  s.deactivate("core");
  s.activate("typed-editor");
  assert.equal(s.reset(), true);
  assert.deepEqual(s.activeGroups(), ["core"]);
});

test("reset restores every default group to source 'default'", () => {
  const s = new ToolSessionState();
  // Mutate: deactivate a default group, activate an opt-in group.
  const defaultId = Array.from(DEFAULT_ENABLED_GROUPS)[0];
  s.deactivate(defaultId);
  s.activate("typed-editor");
  s.reset();
  for (const id of DEFAULT_ENABLED_GROUPS) {
    assert.equal(s.activationSource(id), "default", `group=${id}`);
  }
  assert.equal(s.activationSource("typed-editor"), null);
});

test("activeGroups is sorted and stable", () => {
  const s = new ToolSessionState();
  s.activate("typed-editor");
  s.activate("navigation");
  const groups = s.activeGroups();
  assert.deepEqual(groups, [...groups].sort());
});

// ---------------------------------------------------------------------------
// ALWAYS_VISIBLE_TOOLS contract — locks down the meta-tool roster
// ---------------------------------------------------------------------------

// These are the meta-tools that must stay reachable regardless of session
// state. If a name is added/removed from ALWAYS_VISIBLE_TOOL_NAMES this test
// will catch it (and force a deliberate update here). Mirrors the source of
// truth in tool-session-state.ts.
const EXPECTED_ALWAYS_VISIBLE = [
  "godot_open_mcp_capabilities",
  "godot_open_mcp_manage_tools",
  "godot_open_mcp_ping",
  "godot_open_mcp_bridge_status",
  "godot_open_mcp_pull_events",
  "godot_open_mcp_read_compile_errors",
  "godot_open_mcp_restart_editor",
  "godot_open_mcp_resource_pressure",
  "godot_open_mcp_generate_skill",
  "godot_open_mcp_compile_check",
];

test("ALWAYS_VISIBLE_TOOL_NAMES matches EXPECTED_ALWAYS_VISIBLE", () => {
  assert.deepEqual(
    ALWAYS_VISIBLE_TOOL_NAMES,
    [...EXPECTED_ALWAYS_VISIBLE].sort(),
  );
});

test("every always-visible tool is reachable when every default group is deactivated", () => {
  const state = new ToolSessionState();
  for (const id of DEFAULT_ENABLED_GROUPS) state.deactivate(id);
  // Sanity: nothing is active.
  assert.deepEqual(state.activeGroups(), []);
  const filtered = filterVisibleTools(tools(...EXPECTED_ALWAYS_VISIBLE), state);
  const names = filtered.map((t) => t.name).sort();
  assert.deepEqual(names, [...EXPECTED_ALWAYS_VISIBLE].sort());
});

test("always-visible tools are visible alongside an activated opt-in group", () => {
  const state = new ToolSessionState();
  for (const id of DEFAULT_ENABLED_GROUPS) state.deactivate(id);
  state.activate("typed-editor");
  const filtered = filterVisibleTools(
    tools(
      ...EXPECTED_ALWAYS_VISIBLE,
      "godot_open_mcp_node_find",
      // validate_edit is a core tool — with core deactivated it is hidden,
      // demonstrating that only ALWAYS_VISIBLE_TOOLS survive the teardown.
      // (ping is core too but is always-visible — covered above.)
      "godot_open_mcp_validate_edit",
    ),
    state,
  );
  const names = filtered.map((t) => t.name).sort();
  assert.deepEqual(
    names,
    [...EXPECTED_ALWAYS_VISIBLE, "godot_open_mcp_node_find"].sort(),
  );
});

// T-ping — ping is a health-check tool that must survive group deactivation.
// An agent that just tore down the core group (e.g. to slim its tool surface)
// still needs to re-probe the bridge before re-activating. ping is assigned
// to the `core` group in the catalog, but ALWAYS_VISIBLE_TOOLS wins, so it
// stays reachable here.
test("ping stays visible after deactivate('core')", () => {
  const state = new ToolSessionState();
  assert.ok(state.deactivate("core"), "core should be deactivatable");
  const filtered = filterVisibleTools(
    tools("godot_open_mcp_ping", "godot_open_mcp_validate_edit"),
    state,
  );
  assert.deepEqual(
    filtered.map((t) => t.name),
    ["godot_open_mcp_ping"],
    "ping survives core deactivation; validate_edit (core, not always-visible) is hidden",
  );
});

// ---------------------------------------------------------------------------
// filterVisibleTools — default session filtering (P8.2 acceptance)
// ---------------------------------------------------------------------------

test("default-state filter hides node_find (typed-editor)", () => {
  const state = new ToolSessionState();
  const filtered = filterVisibleTools(
    tools("godot_open_mcp_node_find"),
    state,
  );
  assert.deepEqual(filtered, []);
});

test("default-state filter shows ping + validate_edit (core)", () => {
  const state = new ToolSessionState();
  const filtered = filterVisibleTools(
    tools("godot_open_mcp_ping", "godot_open_mcp_validate_edit"),
    state,
  );
  assert.deepEqual(
    filtered.map((t) => t.name),
    ["godot_open_mcp_ping", "godot_open_mcp_validate_edit"],
  );
});

test("default-state filter shows capabilities (always-visible)", () => {
  const state = new ToolSessionState();
  const filtered = filterVisibleTools(
    tools("godot_open_mcp_capabilities"),
    state,
  );
  assert.deepEqual(filtered.map((t) => t.name), ["godot_open_mcp_capabilities"]);
});

test("after activate('typed-editor'), node_find becomes visible", () => {
  const state = new ToolSessionState();
  state.activate("typed-editor");
  const filtered = filterVisibleTools(
    tools("godot_open_mcp_node_find"),
    state,
  );
  assert.deepEqual(filtered.map((t) => t.name), ["godot_open_mcp_node_find"]);
});

test("null-group tools stay visible regardless of session state", () => {
  // Synthetic tool with no group assignment (groupFor → null). The filter's
  // second rule keeps it visible even with every default group deactivated.
  const state = new ToolSessionState();
  for (const id of DEFAULT_ENABLED_GROUPS) state.deactivate(id);
  const filtered = filterVisibleTools(
    tools("godot_open_mcp_synthetic_meta_tool"),
    state,
  );
  assert.deepEqual(
    filtered.map((t) => t.name),
    ["godot_open_mcp_synthetic_meta_tool"],
  );
});

test("filterVisibleTools precedence — always-visible wins over inactive group assignment", () => {
  // ping is BOTH assigned to core AND listed in ALWAYS_VISIBLE_TOOLS. With
  // core deactivated, the always-visible check (rule 1) must win over the
  // active-group check (rule 3) — ping stays visible, validate_edit (core,
  // not always-visible) is hidden.
  const state = new ToolSessionState();
  state.deactivate("core");
  const filtered = filterVisibleTools(
    tools("godot_open_mcp_ping", "godot_open_mcp_validate_edit"),
    state,
  );
  assert.deepEqual(filtered.map((t) => t.name), ["godot_open_mcp_ping"]);
});

test("filterVisibleTools preserves input order (stable filter)", () => {
  const state = new ToolSessionState();
  state.activate("typed-editor");
  const input = tools(
    "godot_open_mcp_ping",
    "godot_open_mcp_node_find",
    "godot_open_mcp_capabilities",
  );
  const filtered = filterVisibleTools(input, state);
  // ping (core, default-on) + node_find (typed-editor, just activated) +
  // capabilities (always-visible) survive; input order preserved.
  assert.deepEqual(
    filtered.map((t) => t.name),
    ["godot_open_mcp_ping", "godot_open_mcp_node_find", "godot_open_mcp_capabilities"],
  );
});

test("filterVisibleTools honors a custom resolver instead of the catalog", () => {
  // Inject a resolver that assigns every tool to a deactivated group → all
  // non-meta tools hidden. Confirms the resolver param is honored. The
  // always-visible check runs BEFORE the resolver, so ping survives regardless.
  const state = new ToolSessionState();
  for (const id of DEFAULT_ENABLED_GROUPS) state.deactivate(id);
  const filtered = filterVisibleTools(
    tools(
      "godot_open_mcp_validate_edit",
      "godot_open_mcp_ping",
      "godot_open_mcp_capabilities",
    ),
    state,
    () => "deactivated-group",
  );
  // validate_edit resolves to a deactivated group → hidden; ping and
  // capabilities are always-visible (the ALWAYS_VISIBLE check runs before the
  // resolver).
  assert.deepEqual(
    filtered.map((t) => t.name),
    ["godot_open_mcp_ping", "godot_open_mcp_capabilities"],
  );
});

test("filterVisibleTools returns an empty array for an empty input", () => {
  const state = new ToolSessionState();
  assert.deepEqual(filterVisibleTools([], state), []);
});

// ---------------------------------------------------------------------------
// P15.4 — fd-sample ring (resource_pressure session store)
// ---------------------------------------------------------------------------

test("fresh state has an empty fd-sample ring", () => {
  const state = new ToolSessionState();
  assert.deepEqual(state.fdSamplesSnapshot(), []);
});

test("recordFdSample appends to the ring (oldest-first)", () => {
  const state = new ToolSessionState();
  state.recordFdSample({ ts: 1000, pid: 1, count: 10 });
  state.recordFdSample({ ts: 2000, pid: 1, count: 20 });
  assert.deepEqual(state.fdSamplesSnapshot(), [
    { ts: 1000, pid: 1, count: 10 },
    { ts: 2000, pid: 1, count: 20 },
  ]);
});

test("recordFdSample records null counts (probe-failure gaps)", () => {
  const state = new ToolSessionState();
  state.recordFdSample({ ts: 1000, pid: 1, count: 10 });
  state.recordFdSample({ ts: 2000, pid: 1, count: null });
  state.recordFdSample({ ts: 3000, pid: 1, count: 30 });
  assert.deepEqual(state.fdSamplesSnapshot(), [
    { ts: 1000, pid: 1, count: 10 },
    { ts: 2000, pid: 1, count: null },
    { ts: 3000, pid: 1, count: 30 },
  ]);
});

test("recordFdSample is capacity-bounded (FD_SAMPLE_RING_CAPACITY = 20)", () => {
  const state = new ToolSessionState();
  for (let i = 0; i < 25; i++) {
    state.recordFdSample({ ts: i, pid: 1, count: i });
  }
  const snap = state.fdSamplesSnapshot();
  assert.equal(snap.length, 20);
  // LRU on insertion: the oldest 5 (ts 0-4) were dropped; ts 5 remains first.
  assert.equal(snap[0].ts, 5);
  assert.equal(snap[snap.length - 1].ts, 24);
});

test("fdSamplesSnapshot returns a shallow copy (appending does not affect the ring)", () => {
  // `.slice()` is a shallow copy — appending to the snapshot does NOT affect the
  // ring's length (the snapshot is a new array). Mutating a sample object's
  // fields WOULD leak (shared reference) — that is why recordFdSample pushes
  // freshly-built objects and callers treat the snapshot as read-only.
  const state = new ToolSessionState();
  state.recordFdSample({ ts: 1000, pid: 1, count: 10 });
  const snap = state.fdSamplesSnapshot() as FdSample[];
  snap.push({ ts: 9999, pid: 1, count: 999 });
  assert.deepEqual(state.fdSamplesSnapshot(), [
    { ts: 1000, pid: 1, count: 10 },
  ]);
});

test("clearFdSamples empties the ring without touching tool-group state", () => {
  const state = new ToolSessionState();
  state.activate("typed-editor");
  state.recordFdSample({ ts: 1000, pid: 1, count: 10 });
  state.clearFdSamples();
  assert.deepEqual(state.fdSamplesSnapshot(), []);
  assert.deepEqual(state.activeGroups(), ["core", "typed-editor"]);
});

test("reset clears the fd-sample ring too (trend signal is session-scoped)", () => {
  const state = new ToolSessionState();
  state.recordFdSample({ ts: 1000, pid: 1, count: 10 });
  assert.equal(state.fdSamplesSnapshot().length, 1);
  state.reset();
  assert.deepEqual(state.fdSamplesSnapshot(), []);
});

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function tools(...names: string[]): Tool[] {
  return names.map((name) => ({
    name,
    description: `${name} fixture`,
    inputSchema: { type: "object" as const, properties: {} },
  }));
}
