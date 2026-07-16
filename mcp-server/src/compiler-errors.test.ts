// Structured diagnostic extraction tests (P7.4).
//
// Pins the parser layers backing `godot_open_mcp_read_compile_errors`:
//   - C# `path(line,col): error CSxxxx: message` (the Unity parity case,
//     adapted from Unity's compiler-errors.test.ts).
//   - MSBuild + Windows-drive + `res://` variants Godot's .NET integration
//     emits.
//   - GDScript parse errors / warnings / `SCRIPT ERROR` blocks / runtime
//     annotations.
//   - Script / resource load failures + addon/plugin load failures.
//   - Deduplication + cap + conservative `other` fallback.
//
// Adapted from Unity Open MCP's `mcp-server/src/compiler-errors.test.ts` (copy
// the C# extraction / dedup / cap patterns). GDScript + script-load + addon-
// load layers are GREENFIELD — Godot-specific, no Unity equivalent.
//
// Built + run via the project test config:
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  extractCompileDiagnostics,
  MAX_COMPILE_DIAGNOSTICS,
  type CompileDiagnostic,
} from "./compiler-errors.js";

// ---------------------------------------------------------------------------
// C# compiler diagnostics
// ---------------------------------------------------------------------------

const CS_LINE =
  "res://scripts/player.cs(75,27): error CS1061: 'Player' does not contain a definition for 'Jump'";

test("extractCompileDiagnostics: parses C# file/line/column/code/message", () => {
  const log = [
    "Some preamble line",
    CS_LINE,
    "res://other.cs(10,2): error CS0103: The name 'Bar' does not exist in the current context",
  ].join("\n");

  const diags = extractCompileDiagnostics(log);
  assert.equal(diags.length, 2);
  assert.deepEqual(diags[0], {
    kind: "csharp",
    severity: "error",
    file: "res://scripts/player.cs",
    line: 75,
    column: 27,
    code: "CS1061",
    message: "'Player' does not contain a definition for 'Jump'",
    raw: CS_LINE,
  } satisfies CompileDiagnostic);
  assert.equal(diags[1].file, "res://other.cs");
  assert.equal(diags[1].line, 10);
  assert.equal(diags[1].column, 2);
  assert.equal(diags[1].code, "CS0103");
});

test("extractCompileDiagnostics: handles a C# line without a column", () => {
  const log = "res://x.cs(42): error CS1002: ; expected";
  const diags = extractCompileDiagnostics(log);
  assert.equal(diags.length, 1);
  assert.equal(diags[0].line, 42);
  assert.equal(diags[0].column, null, "column null when not present");
  assert.equal(diags[0].code, "CS1002");
});

test("extractCompileDiagnostics: handles Windows-drive absolute C# paths", () => {
  const log =
    "C:\\proj\\src\\Foo.cs(10,14): error CS0246: The type or namespace name 'Bar' could not be found";
  const diags = extractCompileDiagnostics(log);
  assert.equal(diags.length, 1);
  assert.equal(diags[0].file, "C:\\proj\\src\\Foo.cs");
  assert.equal(diags[0].line, 10);
  assert.equal(diags[0].column, 14);
  assert.equal(diags[0].code, "CS0246");
});

test("extractCompileDiagnostics: captures C# warnings with severity=warning", () => {
  const log = "res://x.cs(5,1): warning CS0168: The variable 'x' is declared but never used";
  const diags = extractCompileDiagnostics(log);
  assert.equal(diags.length, 1);
  assert.equal(diags[0].severity, "warning");
  assert.equal(diags[0].code, "CS0168");
});

test("extractCompileDiagnostics: dedupes identical C# lines", () => {
  const log = `${CS_LINE}\n${CS_LINE}\n${CS_LINE}`;
  const diags = extractCompileDiagnostics(log);
  assert.equal(diags.length, 1);
});

// ---------------------------------------------------------------------------
// GDScript diagnostics
// ---------------------------------------------------------------------------

test("extractCompileDiagnostics: parses GDScript Parse Error form", () => {
  const log = [
    "Some preamble",
    "res://scripts/player.gd:12 - Parse Error: The identifier 'jump' isn't declared in the current scope.",
    "trailing line",
  ].join("\n");

  const diags = extractCompileDiagnostics(log);
  assert.equal(diags.length, 1);
  assert.equal(diags[0].kind, "gdscript");
  assert.equal(diags[0].severity, "error");
  assert.equal(diags[0].file, "res://scripts/player.gd");
  assert.equal(diags[0].line, 12);
  assert.equal(diags[0].code, null);
  assert.ok(diags[0].message.includes("isn't declared"));
});

test("extractCompileDiagnostics: parses GDScript Parse Warning form", () => {
  const log = "res://scripts/player.gd:8 - Parse Warning: The variable 'x' is never used.";
  const diags = extractCompileDiagnostics(log);
  assert.equal(diags.length, 1);
  assert.equal(diags[0].kind, "gdscript");
  assert.equal(diags[0].severity, "warning");
  assert.equal(diags[0].line, 8);
});

test("extractCompileDiagnostics: parses GDScript SCRIPT ERROR block", () => {
  // Godot emits the message first, then the path:line on the next line.
  const log = [
    "SCRIPT ERROR: Method 'jump' is not declared on.",
    "   res://scripts/player.gd:42",
  ].join("\n");

  const diags = extractCompileDiagnostics(log);
  assert.equal(diags.length, 1);
  assert.equal(diags[0].kind, "gdscript");
  assert.equal(diags[0].severity, "error");
  assert.equal(diags[0].file, "res://scripts/player.gd");
  assert.equal(diags[0].line, 42);
  assert.ok(diags[0].message.includes("jump"));
});

test("extractCompileDiagnostics: GDScript runtime annotation surfaces only error-like messages", () => {
  // Message reads like an error → surfaced.
  const log = "res://x.gd:5: Invalid get index 'foo' (on base: 'Node').";
  const diags = extractCompileDiagnostics(log);
  assert.equal(diags.length, 1);
  assert.equal(diags[0].kind, "gdscript");
  assert.equal(diags[0].line, 5);
});

test("extractCompileDiagnostics: GDScript runtime annotation skips benign lines", () => {
  // No error-like marker → skipped (do not classify every runtime print).
  const log = "res://x.gd:5: Hello from the game.";
  const diags = extractCompileDiagnostics(log);
  // The runtime-annotation regex requires an error-like message; a plain print
  // is ignored.
  const gd = diags.filter((d) => d.kind === "gdscript");
  assert.equal(gd.length, 0);
});

// ---------------------------------------------------------------------------
// Script / addon load failures
// ---------------------------------------------------------------------------

test("extractCompileDiagnostics: captures Failed to load script/resource", () => {
  const log = [
    "SCRIPT ERROR: something",
    "Failed to load script: res://addons/myaddon/plugin.gd",
    "Failed to load resource: res://scenes/broken.tscn",
  ].join("\n");

  const diags = extractCompileDiagnostics(log);
  const loads = diags.filter((d) => d.kind === "script_load");
  assert.equal(loads.length, 2);
  assert.equal(loads[0].file, "res://addons/myaddon/plugin.gd");
  assert.equal(loads[1].file, "res://scenes/broken.tscn");
  assert.equal(loads[0].severity, "error");
});

test("extractCompileDiagnostics: captures addon/plugin load failures", () => {
  const log =
    "Failed to initialize addon: res://addons/myaddon/plugin.cfg\n" +
    "Cannot reload scene: res://scenes/main.tscn";
  const diags = extractCompileDiagnostics(log);
  const addons = diags.filter((d) => d.kind === "addon_load");
  assert.ok(addons.length >= 1, "addon load failure surfaced");
  assert.equal(addons[0].severity, "error");
});

// ---------------------------------------------------------------------------
// Deduplication + cap
// ---------------------------------------------------------------------------

test("extractCompileDiagnostics: returns [] for empty / no-error input", () => {
  assert.deepEqual(extractCompileDiagnostics(""), []);
  assert.deepEqual(extractCompileDiagnostics("all good, no errors"), []);
});

test("extractCompileDiagnostics: caps at MAX_COMPILE_DIAGNOSTICS", () => {
  // Identical lines dedupe to 1.
  const one = "res://f.cs(1,1): error CS0000: x\n";
  assert.equal(extractCompileDiagnostics(one.repeat(MAX_COMPILE_DIAGNOSTICS + 50)).length, 1);

  // Distinct lines cap at the bound.
  const distinct = Array.from(
    { length: MAX_COMPILE_DIAGNOSTICS + 50 },
    (_, i) => `res://f${i}.cs(${i + 1},1): error CS0000: msg ${i}`,
  ).join("\n");
  assert.equal(extractCompileDiagnostics(distinct).length, MAX_COMPILE_DIAGNOSTICS);
});

test("extractCompileDiagnostics: honors a custom cap", () => {
  const distinct = Array.from(
    { length: 10 },
    (_, i) => `res://f${i}.cs(${i + 1},1): error CS0000: msg ${i}`,
  ).join("\n");
  assert.equal(extractCompileDiagnostics(distinct, 3).length, 3);
});

test("extractCompileDiagnostics: dedupes across kind by normalized tuple", () => {
  // Same diagnostic via two different log lines (one CS form, one repeat) —
  // the normalized tuple matches so only one record is kept.
  const log = [
    "res://x.cs(5,1): error CS1002: ; expected",
    "res://x.cs(5,1): error CS1002: ; expected",
  ].join("\n");
  assert.equal(extractCompileDiagnostics(log).length, 1);
});

// ---------------------------------------------------------------------------
// Conservative `other` fallback
// ---------------------------------------------------------------------------

test("extractCompileDiagnostics: surfaces unmatched ERROR: lines via `other`", () => {
  // A bare `ERROR:` line with no path/line — none of the specific layers
  // match (no CS locator, no `res://.gd`, no SCRIPT ERROR + path block, no
  // Failed-to-load). The conservative `other` fallback catches it.
  const log = "ERROR: Something went wrong with no path";
  const diags = extractCompileDiagnostics(log);
  const other = diags.filter((d) => d.kind === "other");
  assert.ok(other.length >= 1, "unmatched ERROR line surfaced as other");
  assert.equal(other[0].severity, "error");
});

test("extractCompileDiagnostics: does not classify the bare word 'error' in prose", () => {
  const log = "This line mentions an error in passing but is not a diagnostic.";
  const diags = extractCompileDiagnostics(log);
  assert.equal(diags.length, 0, "prose with the word 'error' is not classified");
});
