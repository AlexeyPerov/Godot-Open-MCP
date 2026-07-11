// `godot_open_mcp_console_get_logs` / `console_clear_logs` tool-definition tests (P4.7). Pins the
// catalog metadata the MCP ListTools response advertises to AI clients for the console log tools —
// name prefix per ADR-003, non-empty descriptions, the Godot-adapted property sets, the
// `additionalProperties: false` guards, and the absence of gate surface (both tools are gate-free:
// get is read-only; clear mutates only ephemeral addon state). The live round-trip
// (POST /tools/godot_open_mcp_console_{get,clear}_logs → bridge handler → collector) is exercised
// against a local HTTP stub + headless Godot smoke; this file only asserts the contracts advertised
// over stdio.
//
// Adapted from editor-application-state.test.ts (copy fidelity for the catalog-metadata test shape),
// with property-set assertions specific to each P4.7 schema. The key contrast with the P4.5/P4.6
// mutating tools: neither console tool exposes paths_hint or gate — clear is gate-free direct
// (ephemeral state, no checkpoint/delta coverage).

import { test } from "node:test";
import assert from "node:assert/strict";
import { consoleGetLogs } from "./console-get-logs.js";
import { consoleClearLogs } from "./console-clear-logs.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// console_get_logs — read-only (no paths_hint / gate).
// ---------------------------------------------------------------------------

test("console_get_logs tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(consoleGetLogs.name, "godot_open_mcp_console_get_logs");
  assert.match(consoleGetLogs.name, /^godot_open_mcp_/);
});

test("console_get_logs tool has a non-empty description", () => {
  assert.ok(typeof consoleGetLogs.description === "string");
  assert.ok((consoleGetLogs.description ?? "").length > 0);
});

test("console_get_logs tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(consoleGetLogs.inputSchema.type, "object");
  assert.equal(consoleGetLogs.inputSchema.additionalProperties, false);
});

test("console_get_logs exposes the Godot-adapted property set", () => {
  const props = consoleGetLogs.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "include_stack_trace",
    "last_minutes",
    "log_type_filter",
    "max_entries",
  ]);
});

test("console_get_logs does NOT expose gate surface (read-only)", () => {
  const props = consoleGetLogs.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "read-only tool — no gate surface");
  assert.equal(props.gate, undefined, "read-only tool — no gate surface");
});

test("console_get_logs max_entries defaults to 100 with bounds", () => {
  const props = consoleGetLogs.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number; maximum?: number }
  >;
  assert.equal(props.max_entries.default, 100);
  assert.equal(props.max_entries.minimum, 1);
  assert.equal(props.max_entries.maximum, 1000);
});

test("console_get_logs include_stack_trace defaults to false", () => {
  const props = consoleGetLogs.inputSchema.properties as Record<string, { default?: boolean }>;
  assert.equal(props.include_stack_trace.default, false);
});

test("console_get_logs last_minutes defaults to 0 with non-negative minimum", () => {
  const props = consoleGetLogs.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number }
  >;
  assert.equal(props.last_minutes.default, 0);
  assert.equal(props.last_minutes.minimum, 0);
});

test("console_get_logs log_type_filter items enum is log/warning/error", () => {
  const props = consoleGetLogs.inputSchema.properties as Record<
    string,
    { type?: string; items?: { type?: string; enum?: string[] } }
  >;
  assert.equal(props.log_type_filter.type, "array");
  assert.deepEqual(props.log_type_filter.items?.enum, ["log", "warning", "error"]);
});

test("console_get_logs has no required array", () => {
  assert.equal(consoleGetLogs.inputSchema.required, undefined);
});

// ---------------------------------------------------------------------------
// console_clear_logs — gate-free direct (no paths_hint / gate either).
// ---------------------------------------------------------------------------

test("console_clear_logs tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(consoleClearLogs.name, "godot_open_mcp_console_clear_logs");
  assert.match(consoleClearLogs.name, /^godot_open_mcp_/);
});

test("console_clear_logs tool has a non-empty description", () => {
  assert.ok(typeof consoleClearLogs.description === "string");
  assert.ok((consoleClearLogs.description ?? "").length > 0);
});

test("console_clear_logs tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(consoleClearLogs.inputSchema.type, "object");
  assert.equal(consoleClearLogs.inputSchema.additionalProperties, false);
});

test("console_clear_logs has an empty property set (no inputs)", () => {
  const props = consoleClearLogs.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props), []);
});

test("console_clear_logs does NOT expose gate surface (gate-free direct)", () => {
  // Clear mutates only ephemeral addon-owned collector state — checkpoint/delta cannot cover it, so
  // there is no paths_hint and no gate. Never invent a fake paths_hint for it.
  const props = consoleClearLogs.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "gate-free direct — no paths_hint");
  assert.equal(props.gate, undefined, "gate-free direct — no gate");
});

test("console_clear_logs has no required array", () => {
  assert.equal(consoleClearLogs.inputSchema.required, undefined);
});

// ---------------------------------------------------------------------------
// Registration.
// ---------------------------------------------------------------------------

test("ALL_TOOLS registers both P4.7 console log tools", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(
    names.includes("godot_open_mcp_console_get_logs"),
    "console_get_logs missing from ALL_TOOLS",
  );
  assert.ok(
    names.includes("godot_open_mcp_console_clear_logs"),
    "console_clear_logs missing from ALL_TOOLS",
  );
});
