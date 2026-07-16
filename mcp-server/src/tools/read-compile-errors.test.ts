// `godot_open_mcp_read_compile_errors` tool-definition contract tests (P7.4).
//
// Pins the MCP catalog metadata the ListTools response advertises:
//   - name follows the `godot_open_mcp_*` convention,
//   - description is non-empty and names the offline route,
//   - input schema carries tail_bytes / include_rotated / max_diagnostics
//     with the documented defaults + bounds, and NO `log_path` argument
//     (the no-arbitrary-file-read safeguard),
//   - the tool is registered in ALL_TOOLS (the bridge-status recovery hint
//     references it by name, so registration MUST be in the same change).
//
// The router-level offline-routing assertions (no bridge call, _source=offline)
// live in tool-router.test.ts; the composition/body-shape assertions live in
// tool-router.test.ts too (they need a temp project on disk).
//
// Adapted from Unity Open MCP's read-compile-errors tool-definition tests
// (copy fidelity for the schema-shape contract).
//
// Built + run via the project test config:
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";

import { readCompileErrors } from "./read-compile-errors.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// name + registration
// ---------------------------------------------------------------------------

test("read_compile_errors tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(readCompileErrors.name, "godot_open_mcp_read_compile_errors");
  assert.match(readCompileErrors.name, /^godot_open_mcp_/);
});

test("read_compile_errors is registered in ALL_TOOLS", () => {
  // The dead_bridge recoveryHint references this tool by name; the risk
  // safeguard requires the tool to be registered in the SAME change.
  assert.ok(
    ALL_TOOLS.some((t) => t.name === "godot_open_mcp_read_compile_errors"),
    "read_compile_errors must be in ALL_TOOLS so the bridge_status recovery hint resolves",
  );
});

// ---------------------------------------------------------------------------
// description
// ---------------------------------------------------------------------------

test("read_compile_errors has a non-empty description naming the offline route", () => {
  assert.ok(typeof readCompileErrors.description === "string");
  const desc = readCompileErrors.description ?? "";
  assert.ok(desc.length > 0);
  // The description must advertise the offline nature so an agent knows it
  // works without the bridge.
  assert.ok(/offline/i.test(desc), "description must mention 'offline'");
  // And the diagnostic surface (C# + GDScript + script/addon load).
  assert.ok(/CS/i.test(desc), "description must mention CS errors");
  assert.ok(/GDScript/i.test(desc), "description must mention GDScript");
});

// ---------------------------------------------------------------------------
// input schema
// ---------------------------------------------------------------------------

test("read_compile_errors schema declares tail_bytes with default 262144 + 4096–1048576 bounds", () => {
  const schema = readCompileErrors.inputSchema as {
    type: string;
    properties: { tail_bytes?: { default: number; minimum: number; maximum: number } };
    additionalProperties: boolean;
  };
  assert.equal(schema.type, "object");
  assert.equal(schema.additionalProperties, false);
  const tb = schema.properties.tail_bytes;
  assert.ok(tb, "tail_bytes property must exist");
  assert.equal(tb.default, 262144);
  assert.equal(tb.minimum, 4096);
  assert.equal(tb.maximum, 1048576);
});

test("read_compile_errors schema declares include_rotated default true", () => {
  const schema = readCompileErrors.inputSchema as {
    properties: { include_rotated?: { default: boolean } };
  };
  const ir = schema.properties.include_rotated;
  assert.ok(ir, "include_rotated property must exist");
  assert.equal(ir.default, true);
});

test("read_compile_errors schema declares max_diagnostics with default 50 + 1–200 bounds", () => {
  const schema = readCompileErrors.inputSchema as {
    properties: { max_diagnostics?: { default: number; minimum: number; maximum: number } };
  };
  const md = schema.properties.max_diagnostics;
  assert.ok(md, "max_diagnostics property must exist");
  assert.equal(md.default, 50);
  assert.equal(md.minimum, 1);
  assert.equal(md.maximum, 200);
});

test("read_compile_errors schema exposes NO log_path argument (no arbitrary file-read surface)", () => {
  // The plan explicitly forbids a per-call log_path — it would turn a
  // diagnostic tool into a general local file reader. The only path source
  // is the operator env override GODOT_OPEN_MCP_LOG_FILE.
  const schema = readCompileErrors.inputSchema as {
    properties: Record<string, unknown>;
  };
  assert.ok(
    !("log_path" in schema.properties),
    "log_path MUST NOT be exposed as a tool argument",
  );
  assert.ok(
    !("path" in schema.properties),
    "path MUST NOT be exposed as a tool argument",
  );
});
