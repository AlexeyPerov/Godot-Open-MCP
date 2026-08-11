// P17.3 gate-intelligence tool-definition tests.
//
// Pins the catalog metadata the MCP ListTools response advertises for the three
// dry-run gate-intelligence tools — name prefix per ADR-003, non-empty
// descriptions, the Godot-adapted property sets, the `additionalProperties: false`
// guards, and registration in ALL_TOOLS. The local dispatch + pure logic are
// exercised by the gate-intelligence logic test + the router parity test; this
// file only asserts the contracts advertised over stdio.

import { test } from "node:test";
import assert from "node:assert/strict";
import { impactPreview } from "./impact-preview.js";
import { gateBudgetEstimate } from "./gate-budget-estimate.js";
import { mutationExplain } from "./mutation-explain.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// impact_preview — read-only (paths_hint required; no gate surface).
// ---------------------------------------------------------------------------

test("impact_preview tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(impactPreview.name, "godot_open_mcp_impact_preview");
  assert.match(impactPreview.name, /^godot_open_mcp_/);
});

test("impact_preview has a non-empty description", () => {
  assert.ok(typeof impactPreview.description === "string");
  assert.ok((impactPreview.description ?? "").length > 0);
});

test("impact_preview declares an object schema with additionalProperties:false", () => {
  assert.equal(impactPreview.inputSchema.type, "object");
  assert.equal(impactPreview.inputSchema.additionalProperties, false);
});

test("impact_preview exposes the Godot-adapted property set", () => {
  const props = impactPreview.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "categories",
    "exclude_rules",
    "include_rules",
    "paths_hint",
  ]);
});

test("impact_preview requires paths_hint and is dry-run (no gate field)", () => {
  assert.deepEqual(impactPreview.inputSchema.required, ["paths_hint"]);
  const props = impactPreview.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.gate, undefined, "dry-run tools carry no gate surface");
  assert.equal(props.paths, undefined, "uses paths_hint, not paths");
});

// ---------------------------------------------------------------------------
// gate_budget_estimate — read-only (paths_hint required; no gate surface).
// ---------------------------------------------------------------------------

test("gate_budget_estimate tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(gateBudgetEstimate.name, "godot_open_mcp_gate_budget_estimate");
  assert.match(gateBudgetEstimate.name, /^godot_open_mcp_/);
});

test("gate_budget_estimate has a non-empty description", () => {
  assert.ok(typeof gateBudgetEstimate.description === "string");
  assert.ok((gateBudgetEstimate.description ?? "").length > 0);
});

test("gate_budget_estimate declares an object schema with additionalProperties:false", () => {
  assert.equal(gateBudgetEstimate.inputSchema.type, "object");
  assert.equal(gateBudgetEstimate.inputSchema.additionalProperties, false);
});

test("gate_budget_estimate exposes the Godot-adapted property set", () => {
  const props = gateBudgetEstimate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "categories",
    "exclude_rules",
    "include_rules",
    "paths_hint",
  ]);
});

test("gate_budget_estimate requires paths_hint and carries no Unity mode field", () => {
  assert.deepEqual(gateBudgetEstimate.inputSchema.required, ["paths_hint"]);
  const props = gateBudgetEstimate.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.mode, undefined, "Godot is heuristic-only — no cache/sample mode");
  assert.equal(props.gate, undefined);
});

// ---------------------------------------------------------------------------
// mutation_explain — read-only (all-optional input; no gate surface).
// ---------------------------------------------------------------------------

test("mutation_explain tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(mutationExplain.name, "godot_open_mcp_mutation_explain");
  assert.match(mutationExplain.name, /^godot_open_mcp_/);
});

test("mutation_explain has a non-empty description", () => {
  assert.ok(typeof mutationExplain.description === "string");
  assert.ok((mutationExplain.description ?? "").length > 0);
});

test("mutation_explain declares an object schema with additionalProperties:false", () => {
  assert.equal(mutationExplain.inputSchema.type, "object");
  assert.equal(mutationExplain.inputSchema.additionalProperties, false);
});

test("mutation_explain input is all-optional (no required fields)", () => {
  // Degrades gracefully on partial input — nothing is mandatory.
  assert.equal(mutationExplain.inputSchema.required, undefined);
});

test("mutation_explain exposes the gate-block field vocabulary", () => {
  const props = mutationExplain.inputSchema.properties as Record<string, unknown>;
  const keys = Object.keys(props).sort();
  for (const expected of [
    "outcome", "new_errors", "new_warnings", "resolved_errors", "resolved_warnings",
    "agent_next_steps", "new_issue_keys", "resolved_issue_keys", "tool_name",
    "total_ms", "checkpoint_ms", "validation_ms", "categories_run", "mutation_error",
  ]) {
    assert.ok(keys.includes(expected), `mutation_explain missing field ${expected}`);
  }
});

test("mutation_explain outcome enum covers the gate outcomes", () => {
  const props = mutationExplain.inputSchema.properties as Record<string, any>;
  assert.deepEqual(props.outcome.enum, ["passed", "warned", "failed", "skipped", "unavailable"]);
});

// ---------------------------------------------------------------------------
// Registration
// ---------------------------------------------------------------------------

test("ALL_TOOLS registers the three P17.3 gate-intelligence tools", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(names.includes("godot_open_mcp_impact_preview"), "impact_preview missing");
  assert.ok(names.includes("godot_open_mcp_gate_budget_estimate"), "gate_budget_estimate missing");
  assert.ok(names.includes("godot_open_mcp_mutation_explain"), "mutation_explain missing");
});
