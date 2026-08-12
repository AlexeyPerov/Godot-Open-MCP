// compile-check logic + tool-definition tests (P17.4).
//
// Exercises the pure logic (project-type detection, build-command resolution,
// timeout clamp, Godot-path resolution, and the orchestrator's outcome mapping
// through an injectable fake spawn) and the tool-definition contract advertised
// over stdio. The real spawn path (defaultSpawn) is not exercised here — it
// depends on a `dotnet`/`godot` install — but the SIGTERM → SIGKILL + bounded-
// buffer discipline mirrors the Unity batch-spawn pattern that is already
// covered upstream. The project_locked guard + dispatch wiring are covered by
// the router parity test (route-policy.test.ts).

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
  detectProjectType,
  resolveBuildCommand,
  resolveGodotPath,
  clampCompileTimeout,
  runCompileCheck,
  defaultSpawn,
  GODOT_PATH_ENV_OVERRIDE,
  DEFAULT_COMPILE_TIMEOUT_MS,
  MIN_COMPILE_TIMEOUT_MS,
  MAX_COMPILE_TIMEOUT_MS,
  type SpawnFn,
  type DetectedProject,
} from "./compile-check.js";
import { compileCheck } from "./tools/compile-check.js";
import { ALL_TOOLS } from "./tools/index.js";

// ---------------------------------------------------------------------------
// tmp-project fixture
// ---------------------------------------------------------------------------

/** Create a throwaway Godot-ish project root with a `project.godot` marker and
 *  the given extra files/dirs. Returns the absolute root; caller cleans up. */
function makeProject(files: Record<string, string> = {}): string {
  const root = mkdtempSync(join(tmpdir(), "compile-check-"));
  writeFileSync(join(root, "project.godot"), ";config_version=5\n");
  for (const [rel, content] of Object.entries(files)) {
    const full = join(root, rel);
    mkdirSync(join(full, ".."), { recursive: true });
    writeFileSync(full, content);
  }
  return root;
}

// ---------------------------------------------------------------------------
// detectProjectType
// ---------------------------------------------------------------------------

test("detectProjectType: pure-GDScript project → gdscript", () => {
  const root = makeProject({ "scripts/player.gd": "extends Node2D\n" });
  try {
    assert.deepEqual(detectProjectType(root), {
      type: "gdscript",
      buildTarget: null,
    });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("detectProjectType: .csproj present → csharp with relative target", () => {
  const root = makeProject({ "Project.csproj": "<Project/>" });
  try {
    const got = detectProjectType(root);
    assert.equal(got?.type, "csharp");
    assert.equal(got?.buildTarget, "Project.csproj");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("detectProjectType: .sln preferred over .csproj", () => {
  const root = makeProject({
    "Project.sln": "sln",
    "Project.csproj": "<Project/>",
    "sub/Other.csproj": "<Project/>",
  });
  try {
    const got = detectProjectType(root);
    assert.equal(got?.type, "csharp");
    assert.equal(got?.buildTarget, "Project.sln", "solution wins over csproj");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("detectProjectType: nested .csproj under a real subdir → csharp, relative path", () => {
  const root = makeProject({ "game/Main.csproj": "<Project/>" });
  try {
    const got = detectProjectType(root);
    assert.equal(got?.type, "csharp");
    assert.equal(got?.buildTarget, join("game", "Main.csproj"));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("detectProjectType: ignores .godot / obj / bin / node_modules", () => {
  const root = makeProject({
    ".godot/foo.csproj": "<Project/>",
    "obj/build.csproj": "<Project/>",
    "bin/out.csproj": "<Project/>",
    "node_modules/pkg/pkg.csproj": "<Project/>",
  });
  try {
    assert.deepEqual(detectProjectType(root), {
      type: "gdscript",
      buildTarget: null,
    });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("detectProjectType: missing root → null", () => {
  assert.equal(detectProjectType(join(tmpdir(), "compile-check-does-not-exist-xyz")), null);
});

test("detectProjectType: file (not dir) → null", () => {
  const root = makeProject();
  try {
    assert.equal(detectProjectType(join(root, "project.godot")), null);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// resolveBuildCommand
// ---------------------------------------------------------------------------

const GODOT = "godot";

test("resolveBuildCommand: csharp → dotnet build <target>", () => {
  const cmd = resolveBuildCommand(
    "/proj",
    { type: "csharp", buildTarget: "Project.sln" },
    null,
    GODOT,
  );
  assert.equal(cmd.builder, "dotnet");
  assert.equal(cmd.command, "dotnet");
  assert.deepEqual(cmd.args, ["build", "Project.sln"]);
});

test("resolveBuildCommand: csharp without target → dotnet build", () => {
  const cmd = resolveBuildCommand(
    "/proj",
    { type: "csharp", buildTarget: null },
    null,
    GODOT,
  );
  assert.deepEqual(cmd.args, ["build"]);
});

test("resolveBuildCommand: gdscript, no script → godot --headless --quit", () => {
  const cmd = resolveBuildCommand(
    "/proj",
    { type: "gdscript", buildTarget: null },
    null,
    GODOT,
  );
  assert.equal(cmd.builder, "godot");
  assert.equal(cmd.command, GODOT);
  assert.deepEqual(cmd.args, ["--headless", "--quit"]);
});

test("resolveBuildCommand: gdscript + script_path → godot --headless --check-only --script", () => {
  const cmd = resolveBuildCommand(
    "/proj",
    { type: "gdscript", buildTarget: null },
    "res://scripts/player.gd",
    GODOT,
  );
  assert.deepEqual(cmd.args, [
    "--headless",
    "--check-only",
    "--script",
    "res://scripts/player.gd",
  ]);
});

test("resolveBuildCommand: csharp ignores script_path", () => {
  const cmd = resolveBuildCommand(
    "/proj",
    { type: "csharp", buildTarget: "P.csproj" },
    "res://ignored.gd",
    GODOT,
  );
  assert.deepEqual(cmd.args, ["build", "P.csproj"]);
});

// ---------------------------------------------------------------------------
// clampCompileTimeout + resolveGodotPath
// ---------------------------------------------------------------------------

test("clampCompileTimeout: defaults + bounds", () => {
  assert.equal(clampCompileTimeout(undefined), DEFAULT_COMPILE_TIMEOUT_MS);
  assert.equal(clampCompileTimeout(NaN), DEFAULT_COMPILE_TIMEOUT_MS);
  assert.equal(clampCompileTimeout("100"), DEFAULT_COMPILE_TIMEOUT_MS);
  assert.equal(clampCompileTimeout(0), MIN_COMPILE_TIMEOUT_MS);
  assert.equal(clampCompileTimeout(1), MIN_COMPILE_TIMEOUT_MS);
  assert.equal(clampCompileTimeout(999_999_999), MAX_COMPILE_TIMEOUT_MS);
  assert.equal(clampCompileTimeout(120000), 120000);
});

test("resolveGodotPath: env override wins, empty falls back to PATH godot", () => {
  assert.equal(resolveGodotPath("/usr/local/bin/godot4"), "/usr/local/bin/godot4");
  assert.equal(resolveGodotPath("   "), "godot");
  assert.equal(resolveGodotPath(undefined), "godot");
});

test("GODOT_PATH_ENV_OVERRIDE follows the GODOT_OPEN_MCP_* convention", () => {
  assert.equal(GODOT_PATH_ENV_OVERRIDE, "GODOT_OPEN_MCP_GODOT_PATH");
});

// ---------------------------------------------------------------------------
// runCompileCheck — injectable fake spawn
// ---------------------------------------------------------------------------

/** Build a fake SpawnFn that returns a canned BuildSpawnResult for the given
 *  command, asserting the orchestrator invoked the expected builder. */
function fakeSpawn(opts: {
  stdout?: string;
  stderr?: string;
  exitCode: number | null;
  timedOut?: boolean;
  refused?: boolean;
  refusedMessage?: string | null;
  expectBuilder?: "dotnet" | "godot";
  expectCommandContains?: string;
}): SpawnFn {
  return async (command, args, spawnOpts) => {
    if (opts.expectBuilder === "dotnet") {
      assert.equal(command, "dotnet", "should invoke dotnet for a C# project");
    }
    if (opts.expectBuilder === "godot") {
      assert.equal(command, "godot", "should invoke godot for a GDScript project");
    }
    if (opts.expectCommandContains) {
      assert.ok(
        command.includes(opts.expectCommandContains),
        `command '${command}' should contain '${opts.expectCommandContains}'`,
      );
    }
    assert.ok(typeof spawnOpts.cwd === "string" && spawnOpts.cwd.length > 0);
    assert.ok(spawnOpts.timeoutMs > 0);
    return {
      command,
      args,
      stdout: opts.stdout ?? "",
      stderr: opts.stderr ?? "",
      exitCode: opts.exitCode,
      timedOut: opts.timedOut === true,
      refused: opts.refused === true,
      refusedMessage: opts.refusedMessage ?? null,
      durationMs: 42,
    };
  };
}

test("runCompileCheck: clean C# build → success", async () => {
  const root = makeProject({ "P.csproj": "<Project/>" });
  try {
    const result = await runCompileCheck({
      projectRoot: root,
      scriptPath: null,
      timeoutMs: 60_000,
      godotPath: GODOT,
      spawnFn: fakeSpawn({
        stdout: "Build succeeded.",
        exitCode: 0,
        expectBuilder: "dotnet",
      }),
    });
    assert.equal(result.outcome, "checked");
    assert.equal(result.projectType, "csharp");
    assert.equal(result.builder, "dotnet");
    assert.equal(result.success, true);
    assert.equal(result.exitCode, 0);
    assert.equal(result.errorCount, 0);
    assert.equal(result.warningCount, 0);
    assert.equal(result.timedOut, false);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("runCompileCheck: C# build with CSxxxx errors → parsed, success false", async () => {
  const root = makeProject({ "P.csproj": "<Project/>" });
  try {
    const stdout =
      "Player.cs(12,9): error CS0103: The name 'Foo' does not exist in the current context\n" +
      "Player.cs(20,5): warning CS0219: The variable 'x' is assigned but never used\n";
    const result = await runCompileCheck({
      projectRoot: root,
      scriptPath: null,
      timeoutMs: 60_000,
      godotPath: GODOT,
      spawnFn: fakeSpawn({ stdout, exitCode: 1 }),
    });
    assert.equal(result.outcome, "checked");
    assert.equal(result.success, false);
    assert.equal(result.errorCount, 1);
    assert.equal(result.warningCount, 1);
    assert.equal(result.exitCode, 1);
    const err = result.errors!;
    assert.equal(err[0].code, "CS0103");
    assert.equal(err[0].severity, "error");
    assert.equal(err[0].line, 12);
    assert.equal(err[0].message.includes("'Foo'"), true);
    // Errors are listed before warnings.
    assert.equal(err[0].severity, "error");
    assert.equal(err[1].severity, "warning");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("runCompileCheck: GDScript parse error surfaced via stderr", async () => {
  const root = makeProject({ "player.gd": "extends Node2D\n" });
  try {
    const stderr =
      "res://player.gd:5 - Parse Error: Could not resolve symbol 'foo'.\n";
    const result = await runCompileCheck({
      projectRoot: root,
      scriptPath: null,
      timeoutMs: 60_000,
      godotPath: GODOT,
      spawnFn: fakeSpawn({
        stderr,
        exitCode: 1,
        expectBuilder: "godot",
      }),
    });
    assert.equal(result.projectType, "gdscript");
    assert.equal(result.builder, "godot");
    assert.equal(result.success, false);
    assert.equal(result.errorCount, 1);
    const err = result.errors![0];
    assert.equal(err.kind, "gdscript");
    assert.equal(err.file, "res://player.gd");
    assert.equal(err.line, 5);
    assert.ok(err.message.includes("'foo'"));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("runCompileCheck: GDScript with script_path → godot --check-only --script", async () => {
  const root = makeProject({ "player.gd": "extends Node2D\n" });
  try {
    let capturedArgs: string[] = [];
    const spawnFn: SpawnFn = async (_cmd, args) => {
      capturedArgs = args;
      return {
        command: "godot",
        args,
        stdout: "",
        stderr: "",
        exitCode: 0,
        timedOut: false,
        refused: false,
        refusedMessage: null,
        durationMs: 5,
      };
    };
    await runCompileCheck({
      projectRoot: root,
      scriptPath: "res://player.gd",
      timeoutMs: 60_000,
      godotPath: GODOT,
      spawnFn,
    });
    assert.deepEqual(capturedArgs, [
      "--headless",
      "--check-only",
      "--script",
      "res://player.gd",
    ]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("runCompileCheck: timeout → timedOut true, success false", async () => {
  const root = makeProject();
  try {
    const result = await runCompileCheck({
      projectRoot: root,
      scriptPath: null,
      timeoutMs: 60_000,
      godotPath: GODOT,
      spawnFn: fakeSpawn({
        stdout: "building...",
        exitCode: null,
        timedOut: true,
      }),
    });
    assert.equal(result.outcome, "checked");
    assert.equal(result.timedOut, true);
    assert.equal(result.success, false);
    assert.equal(result.exitCode, null);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("runCompileCheck: spawn refused → builder_not_found", async () => {
  const root = makeProject();
  try {
    const result = await runCompileCheck({
      projectRoot: root,
      scriptPath: null,
      timeoutMs: 60_000,
      godotPath: GODOT,
      spawnFn: fakeSpawn({
        exitCode: null,
        refused: true,
        refusedMessage: "spawn dotnet ENOENT",
      }),
    });
    assert.equal(result.outcome, "builder_not_found");
    assert.equal(result.refusedMessage, "spawn dotnet ENOENT");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("runCompileCheck: warnings-only build is still a success", async () => {
  const root = makeProject({ "P.csproj": "<Project/>" });
  try {
    const stdout =
      "Player.cs(1,1): warning CS0219: The variable 'x' is assigned but never used\n";
    const result = await runCompileCheck({
      projectRoot: root,
      scriptPath: null,
      timeoutMs: 60_000,
      godotPath: GODOT,
      spawnFn: fakeSpawn({ stdout, exitCode: 0 }),
    });
    assert.equal(result.success, true);
    assert.equal(result.warningCount, 1);
    assert.equal(result.errorCount, 0);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("runCompileCheck: honors GODOT_OPEN_MCP_GODOT_PATH override", async () => {
  const root = makeProject();
  try {
    const custom = "/opt/godot/Godot.app/Contents/MacOS/Godot";
    let invoked = "";
    const spawnFn: SpawnFn = async (command, args) => ({
      command,
      args,
      stdout: "",
      stderr: "",
      exitCode: 0,
      timedOut: false,
      refused: false,
      refusedMessage: null,
      durationMs: 1,
    });
    // Wrap to capture the command the orchestrator resolved.
    const wrap: SpawnFn = async (command, args, opts) => {
      invoked = command;
      return spawnFn(command, args, opts);
    };
    await runCompileCheck({
      projectRoot: root,
      scriptPath: null,
      timeoutMs: 60_000,
      godotPath: resolveGodotPath(custom),
      spawnFn: wrap,
    });
    assert.equal(invoked, custom, "override path is used verbatim");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// defaultSpawn — shape contract only (does not require a toolchain)
// ---------------------------------------------------------------------------

test("defaultSpawn: refuses for a non-existent binary → refused:true, resolves (never rejects)", async () => {
  const result = await defaultSpawn(
    "this-binary-definitely-does-not-exist-xyz",
    [],
    { cwd: tmpdir(), timeoutMs: 5_000 },
  );
  assert.equal(result.refused, true);
  assert.equal(result.exitCode, null);
  assert.ok(result.durationMs >= 0);
});

// ---------------------------------------------------------------------------
// Tool-definition contract
// ---------------------------------------------------------------------------

test("compile_check tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(compileCheck.name, "godot_open_mcp_compile_check");
  assert.match(compileCheck.name, /^godot_open_mcp_/);
});

test("compile_check has a non-empty description", () => {
  assert.ok(typeof compileCheck.description === "string");
  assert.ok((compileCheck.description ?? "").length > 0);
});

test("compile_check declares an object schema with additionalProperties:false", () => {
  const schema = compileCheck.inputSchema as Record<string, unknown>;
  assert.equal(schema.type, "object");
  assert.equal(schema.additionalProperties, false);
});

test("compile_check exposes the expected property set", () => {
  const props = compileCheck.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["script_path", "timeout_ms"]);
});

test("compile_check timeout_ms bounds match the clamp", () => {
  const props = compileCheck.inputSchema.properties as Record<
    string,
    { minimum: number; maximum: number; default: number }
  >;
  assert.equal(props.timeout_ms.minimum, MIN_COMPILE_TIMEOUT_MS);
  assert.equal(props.timeout_ms.maximum, MAX_COMPILE_TIMEOUT_MS);
  assert.equal(props.timeout_ms.default, DEFAULT_COMPILE_TIMEOUT_MS);
});

test("compile_check is registered in ALL_TOOLS", () => {
  assert.ok(
    ALL_TOOLS.some((t) => t.name === "godot_open_mcp_compile_check"),
    "compile_check must be registered in ALL_TOOLS",
  );
});
