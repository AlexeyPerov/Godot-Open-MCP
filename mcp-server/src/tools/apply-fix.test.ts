// `godot_open_mcp_apply_fix` tool-definition tests (P3.7). Pins the catalog metadata the MCP ListTools
// response advertises: name prefix per ADR-003, non-empty description, the property set, the required
// `issue_id`, the `dry_run` default, and the `additionalProperties: false` guard. The live round-trip
// (POST /tools/godot_open_mcp_apply_fix → bridge ApplyFixTool → FixProviderRegistry / gate / rollback) is
// covered by the C# bridge tests; this file only asserts the stdio-advertised contract.

import { test } from "node:test";
import assert from "node:assert/strict";
import { applyFix } from "./apply-fix.js";
import { ALL_TOOLS } from "./index.js";

test("apply_fix tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(applyFix.name, "godot_open_mcp_apply_fix");
  assert.match(applyFix.name, /^godot_open_mcp_/);
});

test("apply_fix tool has a non-empty description", () => {
  assert.ok(typeof applyFix.description === "string");
  assert.ok((applyFix.description ?? "").length > 0);
});

test("apply_fix declares an object input schema with additionalProperties:false", () => {
  assert.equal(applyFix.inputSchema.type, "object");
  assert.equal(applyFix.inputSchema.additionalProperties, false);
});

test("apply_fix requires issue_id", () => {
  assert.deepEqual(applyFix.inputSchema.required, ["issue_id"]);
});

test("apply_fix advertises issue_id, fix_id, dry_run, paths_hint, gate", () => {
  const props = applyFix.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.issue_id, "issue_id property present");
  assert.ok(props.fix_id, "fix_id property present");
  assert.ok(props.dry_run, "dry_run property present");
  assert.ok(props.paths_hint, "paths_hint property present");
  assert.ok(props.gate, "gate property present");
});

test("apply_fix dry_run defaults to true (preview by default)", () => {
  const props = applyFix.inputSchema.properties as Record<string, { default?: unknown }>;
  assert.equal(props.dry_run.default, true);
});

test("apply_fix gate is enforce|warn|off", () => {
  const props = applyFix.inputSchema.properties as Record<string, { enum?: unknown[] }>;
  assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
});

test("apply_fix is registered in ALL_TOOLS", () => {
  assert.ok(ALL_TOOLS.some((t) => t.name === "godot_open_mcp_apply_fix"));
});
