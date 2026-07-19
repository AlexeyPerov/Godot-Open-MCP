// Tests for the CLI argument parser.
//
// Locks down the run-tool arg shape the Validation Suite's Rust runner relies
// on (subcommand-before-flags ordering, --project/--port/--json/--args/--arg)
// plus the fall-through contract: an unknown/no command yields `command:
// undefined` so index.ts runs the stdio server instead of the CLI.

import test from "node:test";
import assert from "node:assert/strict";

import { parseCliArgs, KNOWN_COMMANDS } from "./args.js";

test("run-tool ships in KNOWN_COMMANDS", () => {
  assert.ok((KNOWN_COMMANDS as readonly string[]).includes("run-tool"));
});

test("--help / --version short-circuit", () => {
  assert.equal(parseCliArgs(["--help"]).command, "help");
  assert.equal(parseCliArgs(["-h"]).command, "help");
  assert.equal(parseCliArgs(["--version"]).command, "version");
  assert.equal(parseCliArgs(["-V"]).command, "version");
});

test("no args / bare flag / unknown command falls through (command undefined)", () => {
  assert.equal(parseCliArgs([]).command, undefined);
  assert.equal(parseCliArgs(["--project", "/p"]).command, undefined);
  assert.equal(parseCliArgs(["not-a-command"]).command, undefined);
});

test("run-tool parses tool name + shared flags", () => {
  const p = parseCliArgs([
    "run-tool",
    "godot_open_mcp_ping",
    "--project",
    "/proj/demo",
    "--json",
  ]);
  assert.equal(p.command, "run-tool");
  assert.equal(p.toolName, "godot_open_mcp_ping");
  assert.equal(p.projectPath, "/proj/demo");
  assert.equal(p.json, true);
  assert.equal(p.error, undefined);
});

test("run-tool without a tool name is a usage error", () => {
  const p = parseCliArgs(["run-tool", "--json"]);
  assert.equal(p.command, "run-tool");
  assert.ok(p.error && p.error.includes("tool name"));
});

test("--port validates an integer TCP range", () => {
  assert.equal(parseCliArgs(["run-tool", "x", "--port", "22028"]).port, 22028);
  assert.ok(parseCliArgs(["run-tool", "x", "--port", "0"]).error);
  assert.ok(parseCliArgs(["run-tool", "x", "--port", "nope"]).error);
});

test("--args merges a JSON object; --arg adds a JSON-parsed key=value", () => {
  const p = parseCliArgs([
    "run-tool",
    "x",
    "--args",
    '{"a":1}',
    "--arg",
    "b=2",
    "--arg",
    "c=hello",
  ]);
  assert.deepEqual(p.toolArgs, { a: 1, b: 2, c: "hello" });
});

test("--args rejects non-object JSON", () => {
  assert.ok(parseCliArgs(["run-tool", "x", "--args", "[1,2]"]).error);
  assert.ok(parseCliArgs(["run-tool", "x", "--args", "not json"]).error);
});

test("unknown option is a usage error", () => {
  assert.ok(parseCliArgs(["run-tool", "x", "--bogus"]).error);
});
