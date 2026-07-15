// Library-safe `open` — materialize the one-command developer loop's "launch
// the editor" step without any cloud/connection machinery.
//
// Adapted from the Godot-MCP behavior reference (`cli/src/lib/open.ts`) with the
// cloud/SignalR env-var assembly, the `--url`/`--token`/`--mode` flags, and the
// `findGodotProcess` already-running short-circuit stripped (P6.4 scope: no
// cloud env, no process scan). The validate → optional build → discover editor
// → spawn flow carries over.
//
// Fidelity tags (per specs/execution/P6/P6.4.md):
//   - open launch: adapt — spawn `<godot> --editor --path <project>`, detached.
//   - Godot editor discovery: adapt (utils/godot-editor.ts).
//   - pre-open C# build: adapt — optional `dotnet build`; `--no-build` flag.
//   - cloud/SignalR connect opts: skip — never set `GODOT_MCP_*`.
//
// Library-safe: no stdout noise, no `process.exit`, no throws past the public
// boundary; returns a `{ kind: "success" | "failure" }` union.

import * as fs from "node:fs";
import * as path from "node:path";
import { spawn, type ChildProcess } from "node:child_process";

import { findGodotBinary, launchEditor } from "../utils/godot-editor.js";
import type {
  OpenProjectFailure,
  OpenProjectOptions,
  OpenProjectResult,
  OpenProjectSuccess,
  SpawnLike,
} from "./types.js";

/**
 * Open a Godot project in the Godot editor — the library-callable equivalent of
 * the `open` CLI command.
 *
 * Flow:
 *  1. Resolve + validate the project dir (`project.godot` must exist).
 *  2. When `build !== false` and a `.csproj` exists at the root, run
 *     `dotnet build`. A non-zero build fails the open BEFORE launch (launching
 *     anyway would reproduce the disable-addon failure this guards against).
 *     GDScript-only projects skip the build.
 *  3. Resolve the Godot editor binary (explicit `--editor-path` → env → PATH →
 *     common install roots).
 *  4. Spawn `<godot> --editor --path <project>` detached + unref'd so the CLI
 *     can exit immediately.
 *
 * Never injects cloud / connection env vars (P6.4 scope).
 */
export async function openProject(
  opts: OpenProjectOptions,
): Promise<OpenProjectResult> {
  const warnings: string[] = [];
  let resolvedProjectPath: string | undefined;
  let resolvedEditorPath: string | undefined;
  let built = false;

  // 1. Resolve + validate the project dir.
  if (typeof opts?.projectPath !== "string" || opts.projectPath.length === 0) {
    return fail("project_not_found", "projectPath is required and must be a non-empty string.", warnings);
  }
  const projectPath = path.resolve(opts.projectPath);
  resolvedProjectPath = projectPath;
  if (!fs.existsSync(projectPath)) {
    return fail(
      "project_not_found",
      `Project path does not exist: ${projectPath}`,
      warnings,
      { projectPath },
    );
  }
  if (!fs.existsSync(path.join(projectPath, "project.godot"))) {
    return fail(
      "not_godot_project",
      `Not a Godot project (missing project.godot): ${projectPath}`,
      warnings,
      { projectPath },
    );
  }

  // 2. Optional pre-open C# build. GDScript-only projects (no .csproj) skip.
  if (opts.build !== false) {
    const csproj = findRootCsproj(projectPath);
    if (csproj !== null) {
      const buildResult = await runDotnetBuild(
        csproj,
        opts.buildConfiguration ?? "Debug",
        opts.dotnetPath,
        opts.buildSpawnImpl,
      );
      if (buildResult.kind === "failure") {
        return fail(
          "build_failed",
          `Build before open failed; not launching the editor (it would disable the addon).\n${buildResult.message}`,
          warnings,
          { projectPath },
        );
      }
      built = true;
    }
    // No .csproj → GDScript-only, build auto-skipped.
  }

  // 3. Resolve the editor binary.
  const editorPath = findGodotBinary(opts.editorPath);
  if (!editorPath) {
    return fail(
      "editor_not_found",
      noEditorMessage(),
      warnings,
      { projectPath },
    );
  }
  resolvedEditorPath = editorPath;

  // 4. Spawn detached + unref'd.
  const spawnResult = await spawnEditor(editorPath, projectPath);
  if (spawnResult.kind === "error") {
    warnings.push(`Editor spawn reported an error: ${spawnResult.message}`);
    return fail(
      "launch_failed",
      `Failed to launch the Godot editor: ${spawnResult.message}`,
      warnings,
      { projectPath, editorPath },
    );
  }

  const success: OpenProjectSuccess = {
    kind: "success",
    success: true,
    launched: true,
    editorPath,
    editorPid: spawnResult.pid,
    projectPath,
    built,
    warnings,
  };
  // Reference the resolved-path locals so a future failure path that sets them
  // before throwing still surfaces them in the failure payload.
  void resolvedProjectPath;
  void resolvedEditorPath;
  return success;
}

/**
 * Find a single `.csproj` directly at the project root. Returns its absolute
 * path, or null when none exists (GDScript-only project). When multiple exist,
 * returns the first sorted by name (deterministic); the build step builds only
 * one — Godot projects conventionally have exactly one root csproj.
 */
function findRootCsproj(projectPath: string): string | null {
  let entries: string[];
  try {
    entries = fs.readdirSync(projectPath);
  } catch {
    return null;
  }
  const csprojs = entries
    .filter((n) => n.toLowerCase().endsWith(".csproj"))
    .sort();
  if (csprojs.length === 0) return null;
  return path.join(projectPath, csprojs[0]);
}

interface BuildSuccess {
  kind: "success";
}
interface BuildFailure {
  kind: "failure";
  message: string;
}

/**
 * Run `dotnet build <csproj> -c <configuration>`. Non-zero exit → failure with
 * the exit code in the message. The spawn is synchronous-waited (the open
 * command blocks on the build before launching). Injected for tests.
 */
function runDotnetBuild(
  csproj: string,
  configuration: string,
  dotnetPath: string | undefined,
  spawnImpl: OpenProjectOptions["buildSpawnImpl"],
): Promise<BuildSuccess | BuildFailure> {
  const cmd = dotnetPath ?? "dotnet";
  const args = ["build", csproj, "-c", configuration];
  return new Promise((resolve) => {
    let child: ChildProcess | SpawnLike;
    try {
      if (spawnImpl) {
        child = spawnImpl(cmd, args, { cwd: path.dirname(csproj), stdio: "ignore" });
      } else {
        child = spawn(cmd, args, {
          cwd: path.dirname(csproj),
          stdio: "ignore",
        });
      }
    } catch (err) {
      resolve({
        kind: "failure",
        message: `Could not start 'dotnet build' (${cmd}): ${errMsg(err)}`,
      });
      return;
    }
    let settled = false;
    const finish = (result: BuildSuccess | BuildFailure): void => {
      if (settled) return;
      settled = true;
      resolve(result);
    };
    child.on("error", (err: Error) =>
      finish({ kind: "failure", message: `dotnet build failed to start: ${err.message}` }),
    );
    child.on("close", (code: number | null) => {
      if (code === 0) {
        finish({ kind: "success" });
      } else {
        finish({
          kind: "failure",
          message: `dotnet build exited with code ${code} (configuration: ${configuration}, project: ${path.basename(csproj)}).`,
        });
      }
    });
  });
}

interface SpawnOk {
  kind: "ok";
  pid: number | undefined;
}
interface SpawnErr {
  kind: "error";
  message: string;
}

/**
 * Spawn the editor and resolve to the PID once spawned, or to an error if the
 * spawn itself fails. Mirrors Godot-MCP's `waitForSpawn` with a short timeout
 * so the open command does not block forever waiting for a spawn event.
 */
function spawnEditor(
  editorPath: string,
  projectPath: string,
): Promise<SpawnOk | SpawnErr> {
  return new Promise((resolve) => {
    let settled = false;
    const finish = (result: SpawnOk | SpawnErr): void => {
      if (settled) return;
      settled = true;
      resolve(result);
    };
    let child: ChildProcess;
    try {
      child = launchEditor(editorPath, projectPath, {
        onSpawn: (pid) => finish({ kind: "ok", pid }),
        onError: (err) => finish({ kind: "error", message: err.message }),
      });
    } catch (err) {
      finish({ kind: "error", message: errMsg(err) });
      return;
    }
    // Short timeout: if neither spawn nor error fires within 2s, treat the
    // launch as ok-with-unknown-pid (the process detached; the CLI exits).
    const timer = setTimeout(() => {
      finish({ kind: "ok", pid: child.pid ?? undefined });
    }, 2_000);
    timer.unref();
  });
}

function noEditorMessage(): string {
  return (
    "No Godot editor binary found.\n" +
    "Searched: --editor-path, the GODOT / GODOT_EDITOR (and GODOT_BIN / GODOT4_BIN) env vars,\n" +
    "your PATH, and common install locations (Downloads, Program Files, /Applications, /opt, …)\n" +
    "including version-stamped release names like \"Godot_v<version>-stable_mono_win64.exe\".\n" +
    "To fix, do one of:\n" +
    "  • pass the full path: godot-open-mcp-cli open --editor-path \"<path-to-Godot-executable>\"\n" +
    "  • set an env var: GODOT=<path-to-Godot-executable> (or GODOT_EDITOR / GODOT_BIN / GODOT4_BIN)\n" +
    "  • add the Godot binary to your PATH."
  );
}

function errMsg(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

/** Build a failure result, threading the warnings + optional resolved paths. */
function fail(
  errorLabel: OpenProjectFailure["errorLabel"],
  message: string,
  warnings: string[],
  extra?: { projectPath?: string; editorPath?: string },
): OpenProjectFailure {
  return {
    kind: "failure",
    success: false,
    errorLabel,
    projectPath: extra?.projectPath,
    editorPath: extra?.editorPath,
    warnings,
    error: new Error(message),
  };
}
