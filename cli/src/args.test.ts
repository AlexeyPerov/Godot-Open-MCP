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
