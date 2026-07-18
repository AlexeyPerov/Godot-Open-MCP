// `godot_open_mcp_manage_tools` tool-definition tests (P8.3).
//
// Locks the catalog metadata the MCP ListTools response advertises:
//   - the tool name follows the `godot_open_mcp_*` convention,
//   - the schema requires `action` and restricts it to the four P8.3 actions,
//   - `group` is an optional string (required only for activate/deactivate,
//     enforced at the router layer not the schema),
//   - `additionalProperties: false` so a caller cannot smuggle `intent` /
//     `tags` (Unity fields intentionally omitted),
//   - the tool is registered in ALL_TOOLS so ListTools advertises it,
//   - the tool is in the always-visible allow-list so it survives any group
//     teardown.
//
// Router-layer behavior (action switch, error codes, no bridge hop, notify-
// on-change) is covered in `tool-router.test.ts`. Session-state filtering is
// covered in `tool-session-state.test.ts`. This file is the schema-only pin.
//
// Adapted from Unity Open MCP's `mcp-server/src/tools/manage-tools.test.ts`
// (copy for the schema-contract test shape). Intentional deltas: the action
// enum is the four P8.3 actions only (no `suggest` / `activate_for`); no
// `intent` / `tags` properties.

import { test } from "node:test";
import assert from "node:assert/strict";

import { manageTools } from "./manage-tools.js";
import { ALL_TOOLS } from "./index.js";
import { ALWAYS_VISIBLE_TOOL_NAMES } from "../tool-session-state.js";

// ── Tool-definition contract ────────────────────────────────────────────────

test("manage_tools tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(manageTools.name, "godot_open_mcp_manage_tools");
  assert.match(manageTools.name, /^godot_open_mcp_/);
});

test("manage_tools tool has a non-empty description", () => {
  assert.ok(typeof manageTools.description === "string");
  assert.ok((manageTools.description ?? "").length > 0);
});

test("manage_tools description mentions the four actions + the default-on group", () => {
  // The description is the agent-facing surface; it must name every action so
  // an agent that only reads ListTools can plan a call without trial-and-error.
  const description = manageTools.description ?? "";
  for (const action of ["list_groups", "activate", "deactivate", "reset"]) {
    assert.ok(
      description.includes(action),
      `description must mention action '${action}'`,
    );
  }
  // The default-on group (`core`) must be named so an agent knows the baseline.
  assert.ok(description.includes("`core`"));
});

test("manage_tools schema requires 'action'", () => {
  assert.deepEqual(manageTools.inputSchema.required, ["action"]);
});

test("manage_tools 'action' enum is exactly the four P8.3 actions", () => {
  // P8.3 scope: list_groups / activate / deactivate / reset only. Unity also
  // ships `suggest` + `activate_for`; those are intentionally omitted (no
  // intent-driven activation in P8). Pin the enum so an accidental import of
  // the Unity fields fails here.
  const actionProp = (
    manageTools.inputSchema.properties as { action?: { enum?: unknown } }
  ).action;
  assert.ok(actionProp, "action property must be declared");
  assert.deepEqual(actionProp?.enum, [
    "list_groups",
    "activate",
    "deactivate",
    "reset",
  ]);
  // Negative pin: no intent-driven actions.
  const enumValues = actionProp?.enum as string[];
  assert.ok(!enumValues.includes("suggest"), "suggest must NOT be in the enum");
  assert.ok(
    !enumValues.includes("activate_for"),
    "activate_for must NOT be in the enum",
  );
});

test("manage_tools 'group' is an optional string property", () => {
  const groupProp = (
    manageTools.inputSchema.properties as { group?: { type?: string } }
  ).group;
  assert.ok(groupProp, "group property must be declared");
  assert.equal(groupProp?.type, "string");
  // `group` is NOT in `required` — it is only meaningful for activate/deactivate,
  // enforced at the router layer.
  assert.ok(
    !(manageTools.inputSchema.required as string[]).includes("group"),
    "group must NOT be required at the schema level",
  );
});

test("manage_tools schema does NOT declare intent / tags (Unity fields omitted)", () => {
  // P8.3 omits Unity's `suggest` / `activate_for` actions and their `intent` /
  // `tags` inputs. Pin the absence so a future port does not silently reintroduce
  // them.
  const props = manageTools.inputSchema.properties as Record<string, unknown>;
  assert.ok(!("intent" in props), "intent must NOT be declared in P8.3");
  assert.ok(!("tags" in props), "tags must NOT be declared in P8.3");
});

test("manage_tools schema rejects additional properties", () => {
  assert.equal(manageTools.inputSchema.additionalProperties, false);
});

test("manage_tools is registered in ALL_TOOLS", () => {
  assert.ok(
    ALL_TOOLS.some((t) => t.name === "godot_open_mcp_manage_tools"),
    "manage_tools must be in the registry so ListTools advertises it",
  );
});

test("manage_tools is in the always-visible allow-list", () => {
  // An agent must be able to reach manage_tools before any group is active.
  // If it were filtered by group, deactivating that group would lock the
  // agent out of the visibility surface.
  assert.ok(
    ALWAYS_VISIBLE_TOOL_NAMES.includes("godot_open_mcp_manage_tools"),
    "manage_tools must be in ALWAYS_VISIBLE_TOOL_NAMES so it survives any group teardown",
  );
});
