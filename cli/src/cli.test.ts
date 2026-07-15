// Tests for the CLI dispatcher (src/cli.ts).
//
// runCli writes to process.stdout / process.stderr and returns an exit code
// without calling process.exit — so tests can capture the streams and assert
// on the outcome. We swap process.stdout/stderr for an in-memory fake during
// each run and restore them afterwards.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";

import { runCli, writeAndDrain, type DrainableWritable } from "./cli.js";
import { helpText, versionText, unknownCommandResult } from "./commands.js";

// ---------------------------------------------------------------------------
// fake stream helpers
// ---------------------------------------------------------------------------

function makeFakeStream(): DrainableWritable & { chunks: string[] } {
  const chunks: string[] = [];
  // A single write impl that accepts the optional callback (the overload
  // union collapses to this signature at runtime).
  const stream: DrainableWritable & { chunks: string[] } = {
    chunks,
    write(chunk: string, cb?: (err?: Error | null) => void) {
      chunks.push(chunk);
      cb?.();
      return true;
    },
    once(_event: "drain", _listener: () => void) {
      return undefined;
    },
  };
  return stream;
}

interface Captured {
  stdout: string;
  stderr: string;
  outcome: { handled: boolean; exitCode: number };
}

async function runWithCaptured(
  argv: string[],
  opts: { version?: string; json?: boolean } = {},
): Promise<Captured> {
  const fakeOut = makeFakeStream();
  const fakeErr = makeFakeStream();
  const realOut = process.stdout;
  const realErr = process.stderr;
  // Replace the write streams. process.stdout/stderr are read-only getters on
  // Node, so assign via Object.defineProperty (works under --test).
  Object.defineProperty(process, "stdout", { value: fakeOut, configurable: true });
  Object.defineProperty(process, "stderr", { value: fakeErr, configurable: true });
  try {
    const outcome = await runCli({
      version: opts.version ?? "0.0.1",
      binName: "godot-open-mcp-cli",
      argv,
    });
    return {
      stdout: fakeOut.chunks.join(""),
      stderr: fakeErr.chunks.join(""),
      outcome,
    };
  } finally {
    Object.defineProperty(process, "stdout", { value: realOut, configurable: true });
    Object.defineProperty(process, "stderr", { value: realErr, configurable: true });
  }
}

// ---------------------------------------------------------------------------
// --help / -h
// ---------------------------------------------------------------------------

test("runCli: --help prints usage to stdout and exits 0", async () => {
  const c = await runWithCaptured(["--help"]);
  assert.equal(c.outcome.handled, true);
  assert.equal(c.outcome.exitCode, 0);
  assert.match(c.stdout, /Usage: godot-open-mcp-cli/);
  assert.equal(c.stderr, "");
});

test("runCli: -h is an alias for --help", async () => {
  const c = await runWithCaptured(["-h"]);
  assert.equal(c.outcome.exitCode, 0);
  assert.match(c.stdout, /Usage: godot-open-mcp-cli/);
});

test("runCli: bare invocation (no args) prints help and exits 0", async () => {
  // No MCP-server fallthrough — the Godot CLI prints help when invoked bare.
  const c = await runWithCaptured([]);
  assert.equal(c.outcome.handled, true);
  assert.equal(c.outcome.exitCode, 0);
  assert.match(c.stdout, /Usage: godot-open-mcp-cli/);
});

// ---------------------------------------------------------------------------
// --version / -V
// ---------------------------------------------------------------------------

test("runCli: --version prints version to stdout and exits 0", async () => {
  const c = await runWithCaptured(["--version"], { version: "1.2.3" });
  assert.equal(c.outcome.handled, true);
  assert.equal(c.outcome.exitCode, 0);
  assert.match(c.stdout, /godot-open-mcp-cli 1\.2\.3/);
  assert.equal(c.stderr, "");
});

test("runCli: -V is an alias for --version", async () => {
  const c = await runWithCaptured(["-V"], { version: "0.4.0" });
  assert.equal(c.outcome.exitCode, 0);
  assert.match(c.stdout, /0\.4\.0/);
});

// ---------------------------------------------------------------------------
// unknown command
// ---------------------------------------------------------------------------

test("runCli: unknown command exits non-zero with structured error", async () => {
  const c = await runWithCaptured(["bogus"]);
  assert.equal(c.outcome.handled, true);
  assert.notEqual(c.outcome.exitCode, 0);
  assert.match(c.stderr, /Unknown command 'bogus'/);
});

test("runCli: unknown command with --json emits JSON on stdout", async () => {
  const c = await runWithCaptured(["--json", "bogus"]);
  assert.notEqual(c.outcome.exitCode, 0);
  // --json routes the structured payload to stdout.
  const parsed = JSON.parse(c.stdout) as {
    error: { code: string; message: string };
  };
  assert.equal(parsed.error.code, "parse_error");
  assert.match(parsed.error.message, /Unknown command 'bogus'/);
});

// ---------------------------------------------------------------------------
// parse error (bad flag value)
// ---------------------------------------------------------------------------

test("runCli: --port with non-numeric value exits non-zero", async () => {
  // The bad flag value errors before any --help short-circuit fires.
  const c = await runWithCaptured(["--port", "abc"]);
  assert.notEqual(c.outcome.exitCode, 0);
  assert.match(c.stderr, /--port/);
});

test("runCli: --port with non-numeric value + --json emits structured error", async () => {
  const c = await runWithCaptured(["--json", "--port", "abc"]);
  assert.notEqual(c.outcome.exitCode, 0);
  const parsed = JSON.parse(c.stdout) as {
    error: { code: string };
  };
  assert.equal(parsed.error.code, "parse_error");
});

test("runCli: unknown flag exits non-zero", async () => {
  const c = await runWithCaptured(["--nonsense"]);
  assert.notEqual(c.outcome.exitCode, 0);
  assert.match(c.stderr, /Unknown option/);
});

// ---------------------------------------------------------------------------
// handled is always true (no MCP fallthrough)
// ---------------------------------------------------------------------------

test("runCli: every invocation is handled (no stdio fallthrough)", async () => {
  for (const argv of [[], ["--help"], ["--version"], ["bogus"], ["--port", "x"]]) {
    const c = await runWithCaptured(argv);
    assert.equal(c.outcome.handled, true, `argv=${JSON.stringify(argv)}`);
  }
});

// ---------------------------------------------------------------------------
// install-plugin dispatch (P6.2)
// ---------------------------------------------------------------------------

/** Build a temp Godot project + addon source for the dispatch tests. */
function buildInstallFixture(): { dir: string; project: string; source: string } {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "godot-open-mcp-cli-"));
  const project = path.join(dir, "proj");
  fs.mkdirSync(project, { recursive: true });
  fs.writeFileSync(
    path.join(project, "project.godot"),
    '[application]\n\nname="T"\nconfig_version=5\n',
  );
  const source = path.join(dir, "addon-src");
  fs.mkdirSync(path.join(source, "Editor"), { recursive: true });
  fs.writeFileSync(
    path.join(source, "plugin.cfg"),
    '[plugin]\n\nname="Godot Open MCP"\n',
  );
  return { dir, project, source };
}

test("runCli: install-plugin <path> --source <dir> succeeds with human output", async () => {
  const fx = buildInstallFixture();
  try {
    const c = await runWithCaptured([
      "install-plugin",
      fx.project,
      "--source",
      fx.source,
    ]);
    assert.equal(c.outcome.exitCode, 0);
    assert.match(c.stdout, /installed and enabled/);
    assert.ok(
      fs.existsSync(
        path.join(fx.project, "addons", "godot_open_mcp", "plugin.cfg"),
      ),
    );
  } finally {
    fs.rmSync(fx.dir, { recursive: true, force: true });
  }
});

test("runCli: install-plugin --json emits structured success on stdout", async () => {
  const fx = buildInstallFixture();
  try {
    const c = await runWithCaptured([
      "--json",
      "install-plugin",
      fx.project,
      "--source",
      fx.source,
    ]);
    assert.equal(c.outcome.exitCode, 0);
    const parsed = JSON.parse(c.stdout) as {
      command: string;
      changed: boolean;
      pluginPath: string;
    };
    assert.equal(parsed.command, "install-plugin");
    assert.equal(parsed.changed, true);
    assert.equal(parsed.pluginPath, "res://addons/godot_open_mcp/plugin.cfg");
  } finally {
    fs.rmSync(fx.dir, { recursive: true, force: true });
  }
});

test("runCli: install-plugin on a non-project exits non-zero", async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "godot-open-mcp-cli-"));
  try {
    const notAProject = path.join(dir, "empty");
    fs.mkdirSync(notAProject, { recursive: true });
    const c = await runWithCaptured([
      "install-plugin",
      notAProject,
      "--source",
      notAProject, // also has no plugin.cfg, but not_godot_project fires first
    ]);
    assert.notEqual(c.outcome.exitCode, 0);
    assert.match(c.stderr, /project\.godot/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("runCli: install-plugin --source requires a value", async () => {
  const c = await runWithCaptured(["install-plugin", "--source"]);
  assert.notEqual(c.outcome.exitCode, 0);
  assert.match(c.stderr, /--source/);
});

// ---------------------------------------------------------------------------
// setup-mcp dispatch (P6.3)
// ---------------------------------------------------------------------------

/** Build a temp Godot project for the setup-mcp dispatch tests. */
function buildSetupMcpFixture(): { dir: string; project: string } {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "godot-open-mcp-cli-"));
  const project = path.join(dir, "proj");
  fs.mkdirSync(project, { recursive: true });
  fs.writeFileSync(
    path.join(project, "project.godot"),
    '[application]\n\nname="T"\nconfig_version=5\n',
  );
  return { dir, project };
}

test("runCli: setup-mcp --list prints agent ids and exits 0", async () => {
  const c = await runWithCaptured(["setup-mcp", "--list"]);
  assert.equal(c.outcome.exitCode, 0);
  assert.match(c.stdout, /cursor/);
  assert.match(c.stdout, /claude-desktop/);
  assert.match(c.stdout, /claude-code/);
});

test("runCli: setup-mcp --list --json emits structured agent list on stdout", async () => {
  const c = await runWithCaptured(["--json", "setup-mcp", "--list"]);
  assert.equal(c.outcome.exitCode, 0);
  const parsed = JSON.parse(c.stdout) as { command: string; agents: string[] };
  assert.equal(parsed.command, "setup-mcp");
  assert.ok(parsed.agents.includes("cursor"));
});

test("runCli: setup-mcp <agent> <path> succeeds with human output", async () => {
  const fx = buildSetupMcpFixture();
  try {
    const c = await runWithCaptured(["setup-mcp", "cursor", fx.project]);
    assert.equal(c.outcome.exitCode, 0);
    assert.match(c.stdout, /cursor/);
    assert.match(c.stdout, /stdio/);
    assert.ok(
      fs.existsSync(path.join(fx.project, ".cursor", "mcp.json")),
    );
  } finally {
    fs.rmSync(fx.dir, { recursive: true, force: true });
  }
});

test("runCli: setup-mcp --json emits structured success on stdout", async () => {
  const fx = buildSetupMcpFixture();
  try {
    const c = await runWithCaptured(["--json", "setup-mcp", "cursor", fx.project]);
    assert.equal(c.outcome.exitCode, 0);
    const parsed = JSON.parse(c.stdout) as {
      command: string;
      changed: boolean;
      agentId: string;
      transport: string;
      stdio: { command: string; args: string[]; env: Record<string, string> };
    };
    assert.equal(parsed.command, "setup-mcp");
    assert.equal(parsed.agentId, "cursor");
    assert.equal(parsed.transport, "stdio");
    assert.equal(parsed.stdio.command, "npx");
    assert.ok(parsed.stdio.env.GODOT_PROJECT_PATH);
  } finally {
    fs.rmSync(fx.dir, { recursive: true, force: true });
  }
});

test("runCli: setup-mcp on a non-project exits non-zero (project-scoped agent)", async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "godot-open-mcp-cli-"));
  try {
    const notAProject = path.join(dir, "empty");
    fs.mkdirSync(notAProject, { recursive: true });
    const c = await runWithCaptured(["setup-mcp", "cursor", notAProject]);
    assert.notEqual(c.outcome.exitCode, 0);
    assert.match(c.stderr, /project\.godot/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("runCli: setup-mcp unknown agent exits non-zero", async () => {
  const fx = buildSetupMcpFixture();
  try {
    const c = await runWithCaptured(["setup-mcp", "bogus", fx.project]);
    assert.notEqual(c.outcome.exitCode, 0);
    assert.match(c.stderr, /bogus/);
  } finally {
    fs.rmSync(fx.dir, { recursive: true, force: true });
  }
});

test("runCli: setup-mcp with no agent-id and no --list exits non-zero", async () => {
  const fx = buildSetupMcpFixture();
  try {
    const c = await runWithCaptured(["setup-mcp", fx.project]);
    // With one positional, the parser treats it as the agent id → unknown agent.
    // (The user must pass `--list` or an explicit agent id.)
    assert.notEqual(c.outcome.exitCode, 0);
  } finally {
    fs.rmSync(fx.dir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// open / wait-for-ready / ping dispatch (P6.4)
// ---------------------------------------------------------------------------

/** Build a temp Godot project for the open/wait/ping dispatch tests. */
function buildOpenFixture(): { dir: string; project: string } {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "godot-open-mcp-cli-"));
  const project = path.join(dir, "proj");
  fs.mkdirSync(project, { recursive: true });
  fs.writeFileSync(
    path.join(project, "project.godot"),
    '[application]\n\nname="T"\nconfig_version=5\n',
  );
  return { dir, project };
}

test("runCli: open on a non-project exits non-zero (project_not_found)", async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "godot-open-mcp-cli-"));
  try {
    const notAProject = path.join(dir, "empty");
    fs.mkdirSync(notAProject, { recursive: true });
    const c = await runWithCaptured(["open", notAProject]);
    assert.notEqual(c.outcome.exitCode, 0);
    assert.match(c.stderr, /project\.godot/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("runCli: open --json on a valid project without an editor resolves editor_not_found", async () => {
  // No Godot on the host → editor discovery fails. We assert the structured
  // error label rather than a launch; this is skipped when Godot IS installed.
  const fx = buildOpenFixture();
  // Clear Godot env vars + empty PATH so discovery misses (best-effort).
  const savedGodot = process.env.GODOT;
  const savedEditor = process.env.GODOT_EDITOR;
  const savedBin = process.env.GODOT_BIN;
  const savedPath = process.env.PATH;
  delete process.env.GODOT;
  delete process.env.GODOT_EDITOR;
  delete process.env.GODOT_BIN;
  process.env.PATH = "";
  try {
    const c = await runWithCaptured(["--json", "open", fx.project]);
    const parsed = JSON.parse(c.stdout) as {
      command: string;
      launched: boolean;
      error?: { code: string };
    };
    assert.equal(parsed.command, "open");
    // If Godot is installed on the host, the open may succeed — only assert the
    // editor_not_found path when discovery genuinely missed.
    if (parsed.error?.code === "editor_not_found") {
      assert.equal(parsed.launched, false);
      assert.notEqual(c.outcome.exitCode, 0);
    } else {
      // Godot was found and launched — success path.
      assert.equal(c.outcome.exitCode, 0);
    }
  } finally {
    if (savedGodot === undefined) delete process.env.GODOT;
    else process.env.GODOT = savedGodot;
    if (savedEditor === undefined) delete process.env.GODOT_EDITOR;
    else process.env.GODOT_EDITOR = savedEditor;
    if (savedBin === undefined) delete process.env.GODOT_BIN;
    else process.env.GODOT_BIN = savedBin;
    process.env.PATH = savedPath;
    fs.rmSync(fx.dir, { recursive: true, force: true });
  }
});

test("runCli: open --editor-path requires a value", async () => {
  const c = await runWithCaptured(["open", "--editor-path"]);
  assert.notEqual(c.outcome.exitCode, 0);
  assert.match(c.stderr, /--editor-path/);
});

test("runCli: open --build-configuration requires a value", async () => {
  const c = await runWithCaptured(["open", "--build-configuration"]);
  assert.notEqual(c.outcome.exitCode, 0);
  assert.match(c.stderr, /--build-configuration/);
});

test("runCli: wait-for-ready on a project with no bridge exits non-zero (timeout)", async () => {
  // No bridge running → poll exhausts the timeout. Use a short timeout so the
  // test is fast. exitCode is TIMEOUT (3) when the bridge never answers.
  const fx = buildOpenFixture();
  try {
    const c = await runWithCaptured([
      "--json",
      "wait-for-ready",
      fx.project,
      "--timeout-ms",
      "200",
      "--interval-ms",
      "100",
    ]);
    assert.notEqual(c.outcome.exitCode, 0);
    assert.equal(c.outcome.exitCode, 3);
    const parsed = JSON.parse(c.stdout) as {
      command: string;
      ready: boolean;
      status: string;
    };
    assert.equal(parsed.command, "wait-for-ready");
    assert.equal(parsed.ready, false);
    // No bridge → either timeout (never reachable) or dead_bridge; both are
    // non-ready. The common case is timeout.
    assert.notEqual(parsed.ready, true);
  } finally {
    fs.rmSync(fx.dir, { recursive: true, force: true });
  }
});

test("runCli: wait-for-ready --json emits structured payload on stdout", async () => {
  const fx = buildOpenFixture();
  try {
    const c = await runWithCaptured([
      "--json",
      "wait-for-ready",
      fx.project,
      "--timeout-ms",
      "200",
    ]);
    const parsed = JSON.parse(c.stdout) as {
      command: string;
      port: number;
      baseUrl: string;
    };
    assert.equal(parsed.command, "wait-for-ready");
    assert.ok(parsed.port >= 20000 && parsed.port <= 29999);
    assert.match(parsed.baseUrl, /http:\/\/127\.0\.0\.1:\d+/);
  } finally {
    fs.rmSync(fx.dir, { recursive: true, force: true });
  }
});

test("runCli: ping on a project with no bridge exits non-zero (offline)", async () => {
  const fx = buildOpenFixture();
  try {
    const c = await runWithCaptured(["--json", "ping", fx.project]);
    assert.notEqual(c.outcome.exitCode, 0);
    const parsed = JSON.parse(c.stdout) as {
      command: string;
      status: string;
      ready: boolean;
    };
    assert.equal(parsed.command, "ping");
    assert.equal(parsed.ready, false);
    // No bridge → offline (or error on a slow host); never ready.
    assert.notEqual(parsed.status, "ready");
  } finally {
    fs.rmSync(fx.dir, { recursive: true, force: true });
  }
});

test("runCli: ping --json emits the resolved port + baseUrl", async () => {
  const fx = buildOpenFixture();
  try {
    const c = await runWithCaptured(["--json", "ping", fx.project, "--port", "23456"]);
    const parsed = JSON.parse(c.stdout) as {
      command: string;
      port: number;
      baseUrl: string;
    };
    assert.equal(parsed.command, "ping");
    assert.equal(parsed.port, 23456);
    assert.equal(parsed.baseUrl, "http://127.0.0.1:23456");
  } finally {
    fs.rmSync(fx.dir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// writeAndDrain
// ---------------------------------------------------------------------------

test("writeAndDrain: resolves after the callback fires", async () => {
  const fake = makeFakeStream();
  await writeAndDrain(fake, "hello");
  assert.deepEqual(fake.chunks, ["hello"]);
});

test("writeAndDrain: rejects on write error", async () => {
  const failing: DrainableWritable = {
    write(_chunk: string, cb?: (err?: Error | null) => void) {
      cb?.(new Error("broken pipe"));
      return false;
    },
    once() {
      return undefined;
    },
  };
  await assert.rejects(writeAndDrain(failing, "x"), /broken pipe/);
});

// ---------------------------------------------------------------------------
// helpText / versionText / unknownCommandResult
// ---------------------------------------------------------------------------

test("helpText: mentions key sections and options", () => {
  const text = helpText("godot-open-mcp-cli");
  for (const opt of ["--json", "--project", "--port", "--timeout-ms", "--interval-ms"]) {
    assert.ok(text.includes(opt), `help missing ${opt}`);
  }
  // P6.3 setup-mcp flags are advertised.
  assert.ok(text.includes("--list"));
  assert.ok(text.includes("--use-local"));
  assert.ok(text.includes("--config-path"));
  // P6.4 open / wait-for-ready / ping flags + commands are advertised.
  assert.ok(text.includes("--editor-path"));
  assert.ok(text.includes("--no-build"));
  assert.ok(text.includes("--build-configuration"));
  assert.ok(text.includes("--wait"));
  assert.ok(text.includes("  open [path]"));
  assert.ok(text.includes("  wait-for-ready [path]"));
  assert.ok(text.includes("  ping [path]"));
  assert.ok(text.includes("GODOT_PROJECT_PATH"));
  assert.ok(text.includes("GODOT_OPEN_MCP_BRIDGE_PORT"));
  assert.ok(text.includes("Exit codes"));
});

test("versionText: prints package + version", () => {
  assert.equal(versionText("0.1.0"), "godot-open-mcp-cli 0.1.0");
});

test("unknownCommandResult: structured error shape", () => {
  const r = unknownCommandResult("bogus", [], false);
  assert.equal(r.exitCode, 1);
  assert.equal(r.errorLabel, "unknown_command");
  const json = r.json as { error: { code: string; message: string } };
  assert.equal(json.error.code, "unknown_command");
  assert.match(json.error.message, /Unknown command 'bogus'/);
});
