// Tests for the CLI argument parser (src/args.ts). Pure-function tests —
// no I/O, no process state.
//
// The parser short-circuits on --help / --version (returns immediately), so
// shared flags must appear BEFORE --help / --version to be parsed. Flag-only
// error cases (bad --port value, unknown flag) surface an error before any
// short-circuit fires.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  parseCliArgs,
  KNOWN_COMMANDS,
  type ParsedCli,
} from "./args.js";

function parse(argv: string[]): ParsedCli {
  return parseCliArgs(argv);
}

// ---------------------------------------------------------------------------
// command recognition
// ---------------------------------------------------------------------------

test("parseCliArgs: --help short-circuits to help command", () => {
  assert.equal(parse(["--help"]).command, "help");
  assert.equal(parse(["-h"]).command, "help");
});

test("parseCliArgs: --version short-circuits to version command", () => {
  assert.equal(parse(["--version"]).command, "version");
  assert.equal(parse(["-V"]).command, "version");
});

test("parseCliArgs: unknown command is an error", () => {
  const p = parse(["bogus"]);
  assert.equal(p.command, null);
  assert.match(p.error ?? "", /Unknown command 'bogus'/);
});

test("parseCliArgs: no argv → defaults to help (no MCP fallthrough)", () => {
  const p = parse([]);
  assert.equal(p.command, "help");
  assert.equal(p.error, undefined);
});

test("parseCliArgs: KNOWN_COMMANDS includes install-plugin (P6.2)", () => {
  // Commands register as their plans land. P6.2 adds install-plugin;
  // setup-mcp (P6.3), open / wait-for-ready (P6.4), status / configure (P6.5)
  // append later.
  assert.ok([...KNOWN_COMMANDS].includes("install-plugin"));
});

test("parseCliArgs: KNOWN_COMMANDS includes setup-mcp (P6.3)", () => {
  assert.ok([...KNOWN_COMMANDS].includes("setup-mcp"));
});

test("parseCliArgs: KNOWN_COMMANDS includes open / wait-for-ready / ping (P6.4)", () => {
  assert.ok([...KNOWN_COMMANDS].includes("open"));
  assert.ok([...KNOWN_COMMANDS].includes("wait-for-ready"));
  assert.ok([...KNOWN_COMMANDS].includes("ping"));
});

test("parseCliArgs: KNOWN_COMMANDS includes status / configure (P6.5)", () => {
  assert.ok([...KNOWN_COMMANDS].includes("status"));
  assert.ok([...KNOWN_COMMANDS].includes("configure"));
});

// ---------------------------------------------------------------------------
// shared flags (placed before --help so the parser sees them before the
// short-circuit returns)
// ---------------------------------------------------------------------------

test("parseCliArgs: --json is captured", () => {
  assert.equal(parse(["--json", "--help"]).json, true);
});

test("parseCliArgs: --project / -P override", () => {
  assert.equal(parse(["--project", "/p", "--help"]).projectPath, "/p");
  assert.equal(parse(["-P", "/p", "--help"]).projectPath, "/p");
});

test("parseCliArgs: --project requires a value", () => {
  assert.match(parse(["--project"]).error ?? "", /--project/);
  assert.match(parse(["--project", "--json"]).error ?? "", /--project/);
});

test("parseCliArgs: --port / -p override parses to a number", () => {
  assert.equal(parse(["--port", "19120", "--help"]).port, 19120);
  assert.equal(parse(["-p", "19120", "--help"]).port, 19120);
});

test("parseCliArgs: --port rejects non-numeric / out-of-range", () => {
  assert.match(parse(["--port", "abc"]).error ?? "", /--port/);
  assert.match(parse(["--port", "-5"]).error ?? "", /--port/);
  assert.match(parse(["--port", "0"]).error ?? "", /--port/);
});

test("parseCliArgs: --port requires a value", () => {
  assert.match(parse(["--port"]).error ?? "", /--port/);
});

test("parseCliArgs: --timeout-ms and --interval-ms parse to numbers", () => {
  const p = parse(["--timeout-ms", "5000", "--interval-ms", "250", "--help"]);
  assert.equal(p.timeoutMs, 5000);
  assert.equal(p.intervalMs, 250);
});

test("parseCliArgs: --timeout-ms rejects zero / negative / non-integer", () => {
  assert.match(parse(["--timeout-ms", "0"]).error ?? "", /--timeout-ms/);
  assert.match(parse(["--timeout-ms", "-1"]).error ?? "", /--timeout-ms/);
  assert.match(parse(["--timeout-ms", "1.5"]).error ?? "", /--timeout-ms/);
});

test("parseCliArgs: --interval-ms rejects non-positive", () => {
  assert.match(parse(["--interval-ms", "0"]).error ?? "", /--interval-ms/);
  assert.match(parse(["--interval-ms", "-1"]).error ?? "", /--interval-ms/);
});

test("parseCliArgs: unknown flag is an error", () => {
  const p = parse(["--nonsense"]);
  assert.deepEqual(p.unknown, ["--nonsense"]);
  assert.match(p.error ?? "", /Unknown option/);
});

// ---------------------------------------------------------------------------
// install-plugin command + --source (P6.2)
// ---------------------------------------------------------------------------

test("parseCliArgs: install-plugin is a recognized command", () => {
  const p = parse(["install-plugin", "/p", "--source", "/s"]);
  assert.equal(p.command, "install-plugin");
  assert.equal(p.error, undefined);
  assert.equal(p.positionalPath, "/p");
  assert.equal(p.source, "/s");
});

test("parseCliArgs: install-plugin without a positional is valid", () => {
  // [path] is optional — the dispatcher falls back to --project / env / cwd.
  const p = parse(["install-plugin"]);
  assert.equal(p.command, "install-plugin");
  assert.equal(p.error, undefined);
  assert.equal(p.positionalPath, undefined);
});

test("parseCliArgs: --source requires a value", () => {
  assert.match(parse(["install-plugin", "--source"]).error ?? "", /--source/);
  assert.match(
    parse(["install-plugin", "--source", "--json"]).error ?? "",
    /--source/,
  );
});

test("parseCliArgs: --source value may start with a path char", () => {
  // Relative paths like ./addon are valid source values.
  const p = parse(["install-plugin", "--source", "./addon"]);
  assert.equal(p.source, "./addon");
});

test("parseCliArgs: flags may interleave with install-plugin args", () => {
  const p = parse(["--json", "install-plugin", "/p", "--source", "/s"]);
  assert.equal(p.command, "install-plugin");
  assert.equal(p.json, true);
  assert.equal(p.positionalPath, "/p");
  assert.equal(p.source, "/s");
});

// ---------------------------------------------------------------------------
// flag ordering / position invariance
// ---------------------------------------------------------------------------

test("parseCliArgs: flags may appear before --help", () => {
  assert.equal(parse(["--json", "--help"]).command, "help");
  assert.equal(parse(["--json", "--help"]).json, true);
});

test("parseCliArgs: flags may appear before --version", () => {
  assert.equal(parse(["--json", "--version"]).command, "version");
  assert.equal(parse(["--json", "--version"]).json, true);
});

// ---------------------------------------------------------------------------
// setup-mcp command + agent-id positional + flags (P6.3)
// ---------------------------------------------------------------------------

test("parseCliArgs: setup-mcp <agent-id> [path] — two positionals parsed", () => {
  const p = parse(["setup-mcp", "cursor", "/p"]);
  assert.equal(p.command, "setup-mcp");
  assert.equal(p.error, undefined);
  assert.equal(p.agentId, "cursor");
  assert.equal(p.positionalPath, "/p");
});

test("parseCliArgs: setup-mcp with only an agent-id is valid", () => {
  // [path] is optional — the dispatcher falls back to --project / env / cwd.
  const p = parse(["setup-mcp", "claude-desktop"]);
  assert.equal(p.command, "setup-mcp");
  assert.equal(p.error, undefined);
  assert.equal(p.agentId, "claude-desktop");
  assert.equal(p.positionalPath, undefined);
});

test("parseCliArgs: setup-mcp with no agent-id is valid (dispatcher decides --list vs missing)", () => {
  // Bare `setup-mcp` is either `--list` or a missing-agent error — the
  // dispatcher handles that branch; the parser must accept it.
  const p = parse(["setup-mcp"]);
  assert.equal(p.command, "setup-mcp");
  assert.equal(p.error, undefined);
  assert.equal(p.agentId, undefined);
});

test("parseCliArgs: setup-mcp --list short-circuits without an agent-id", () => {
  const p = parse(["setup-mcp", "--list"]);
  assert.equal(p.command, "setup-mcp");
  assert.equal(p.list, true);
  assert.equal(p.agentId, undefined);
});

test("parseCliArgs: setup-mcp --use-local is captured", () => {
  const p = parse(["setup-mcp", "cursor", "/p", "--use-local"]);
  assert.equal(p.useLocal, true);
  assert.equal(p.agentId, "cursor");
});

test("parseCliArgs: setup-mcp --config-path requires a value", () => {
  assert.match(parse(["setup-mcp", "--config-path"]).error ?? "", /--config-path/);
  assert.match(
    parse(["setup-mcp", "--config-path", "--json"]).error ?? "",
    /--config-path/,
  );
});

test("parseCliArgs: setup-mcp --config-path captures the value", () => {
  const p = parse(["setup-mcp", "cursor", "--config-path", "/tmp/x.json"]);
  assert.equal(p.configPath, "/tmp/x.json");
});

test("parseCliArgs: setup-mcp with too many positionals is an error", () => {
  const p = parse(["setup-mcp", "cursor", "/p", "extra"]);
  assert.match(p.error ?? "", /Unexpected positional/);
});

test("parseCliArgs: setup-mcp flags may interleave with positionals", () => {
  const p = parse(["--json", "setup-mcp", "cursor", "--use-local", "/p"]);
  assert.equal(p.command, "setup-mcp");
  assert.equal(p.json, true);
  assert.equal(p.useLocal, true);
  assert.equal(p.agentId, "cursor");
  assert.equal(p.positionalPath, "/p");
});

// ---------------------------------------------------------------------------
// error path: error takes precedence over the help default
// ---------------------------------------------------------------------------

test("parseCliArgs: unknown command error is not overridden by help default", () => {
  // An unknown command sets error AND leaves command null. The dispatcher
  // checks error before the help default, so this must surface as an error,
  // not silently print help.
  const p = parse(["bogus"]);
  assert.equal(p.command, null);
  assert.ok(p.error);
});

// ---------------------------------------------------------------------------
// open / wait-for-ready / ping commands + flags (P6.4)
// ---------------------------------------------------------------------------

test("parseCliArgs: open [path] is recognized and captures the positional", () => {
  const p = parse(["open", "/p"]);
  assert.equal(p.command, "open");
  assert.equal(p.error, undefined);
  assert.equal(p.positionalPath, "/p");
});

test("parseCliArgs: open with no positional is valid", () => {
  const p = parse(["open"]);
  assert.equal(p.command, "open");
  assert.equal(p.error, undefined);
  assert.equal(p.positionalPath, undefined);
});

test("parseCliArgs: open --editor-path requires a value", () => {
  assert.match(parse(["open", "--editor-path"]).error ?? "", /--editor-path/);
  assert.match(
    parse(["open", "--editor-path", "--json"]).error ?? "",
    /--editor-path/,
  );
});

test("parseCliArgs: open --editor-path captures the value", () => {
  const p = parse(["open", "/p", "--editor-path", "/usr/local/bin/godot"]);
  assert.equal(p.editorPath, "/usr/local/bin/godot");
});

test("parseCliArgs: open --no-build sets the flag", () => {
  const p = parse(["open", "/p", "--no-build"]);
  assert.equal(p.noBuild, true);
});

test("parseCliArgs: open --build-configuration captures the value", () => {
  const p = parse(["open", "/p", "--build-configuration", "Release"]);
  assert.equal(p.buildConfiguration, "Release");
});

test("parseCliArgs: open --build-configuration requires a value", () => {
  assert.match(
    parse(["open", "--build-configuration"]).error ?? "",
    /--build-configuration/,
  );
});

test("parseCliArgs: open --wait sets the flag", () => {
  const p = parse(["open", "/p", "--wait"]);
  assert.equal(p.wait, true);
});

test("parseCliArgs: open flags may interleave", () => {
  const p = parse([
    "--json",
    "open",
    "/p",
    "--editor-path",
    "/g",
    "--no-build",
    "--wait",
  ]);
  assert.equal(p.command, "open");
  assert.equal(p.json, true);
  assert.equal(p.positionalPath, "/p");
  assert.equal(p.editorPath, "/g");
  assert.equal(p.noBuild, true);
  assert.equal(p.wait, true);
});

test("parseCliArgs: wait-for-ready [path] is recognized", () => {
  const p = parse(["wait-for-ready", "/p", "--timeout-ms", "5000"]);
  assert.equal(p.command, "wait-for-ready");
  assert.equal(p.error, undefined);
  assert.equal(p.positionalPath, "/p");
  assert.equal(p.timeoutMs, 5000);
});

test("parseCliArgs: wait-for-ready --interval-ms captures the value", () => {
  const p = parse(["wait-for-ready", "--interval-ms", "750"]);
  assert.equal(p.intervalMs, 750);
});

test("parseCliArgs: ping [path] is recognized", () => {
  const p = parse(["ping", "/p", "--port", "23456"]);
  assert.equal(p.command, "ping");
  assert.equal(p.error, undefined);
  assert.equal(p.positionalPath, "/p");
  assert.equal(p.port, 23456);
});

test("parseCliArgs: ping with no positional is valid", () => {
  const p = parse(["ping"]);
  assert.equal(p.command, "ping");
  assert.equal(p.error, undefined);
});

// ---------------------------------------------------------------------------
// status / configure (P6.5)
// ---------------------------------------------------------------------------

test("parseCliArgs: status [path] is recognized", () => {
  const p = parse(["status", "/p", "--port", "23456"]);
  assert.equal(p.command, "status");
  assert.equal(p.error, undefined);
  assert.equal(p.positionalPath, "/p");
  assert.equal(p.port, 23456);
});

test("parseCliArgs: configure [path] is recognized", () => {
  const p = parse(["configure", "/p"]);
  assert.equal(p.command, "configure");
  assert.equal(p.error, undefined);
  assert.equal(p.positionalPath, "/p");
});

test("parseCliArgs: configure --list sets the flag", () => {
  const p = parse(["configure", "/p", "--list"]);
  assert.equal(p.list, true);
});

test("parseCliArgs: configure --get <key> captures the value", () => {
  const p = parse(["configure", "/p", "--get", "authMode"]);
  assert.equal(p.getKey, "authMode");
});

test("parseCliArgs: configure --get requires a value", () => {
  assert.match(parse(["configure", "/p", "--get"]).error ?? "", /--get/);
});

test("parseCliArgs: configure --set key=value captures the assignment", () => {
  const p = parse(["configure", "/p", "--set", "authMode=required"]);
  assert.deepEqual(p.setAssignments, ["authMode=required"]);
});

test("parseCliArgs: configure --set is repeatable", () => {
  const p = parse([
    "configure",
    "/p",
    "--set",
    "authMode=required",
    "--set",
    "bindAddress=0.0.0.0",
  ]);
  assert.deepEqual(p.setAssignments, [
    "authMode=required",
    "bindAddress=0.0.0.0",
  ]);
});

test("parseCliArgs: configure --set without '=' is an error", () => {
  assert.match(parse(["configure", "/p", "--set", "authMode"]).error ?? "", /key=value/);
});

test("parseCliArgs: configure --set with no value is an error", () => {
  assert.match(parse(["configure", "/p", "--set"]).error ?? "", /--set/);
});

// ---------------------------------------------------------------------------
// --port range (regression)
// ---------------------------------------------------------------------------
//
// parsePositiveInt only rejected <= 0 / non-integers, so an out-of-range port passed validation and
// was then silently DISCARDED downstream: resolvePort re-validates the range and, on failure, falls
// back to the lock/hash port. `--port 99999` therefore probed a completely different port with no
// indication the override had been ignored — while the parser's own message promised "1-65535".

test("parseCliArgs: --port accepts the range boundaries", () => {
  assert.equal(parse(["status", "/p", "--port", "1"]).port, 1);
  assert.equal(parse(["status", "/p", "--port", "65535"]).port, 65535);
});

test("parseCliArgs: --port rejects a value above 65535", () => {
  const p = parse(["status", "/p", "--port", "65536"]);
  assert.match(p.error ?? "", /1-65535/);
  assert.equal(p.port, undefined, "an out-of-range port must not be carried forward");
});

test("parseCliArgs: --port rejects 99999 rather than silently falling back", () => {
  assert.match(parse(["status", "/p", "--port", "99999"]).error ?? "", /1-65535/);
});

test("parseCliArgs: --port still rejects zero, negatives and non-integers", () => {
  for (const bad of ["0", "-1", "1.5", "abc"]) {
    assert.match(
      parse(["status", "/p", "--port", bad]).error ?? "",
      /1-65535/,
      `--port ${bad} must be rejected`,
    );
  }
});
