// Triggered build check for `godot_open_mcp_compile_check` (P17.4).
//
// Where the sibling `read_compile_errors` (P7.4) is the PASSIVE reader — it
// reads whatever the live editor already wrote to the Godot log — this module
// is the ACTIVE trigger: it spawns a fresh build (no live bridge, no live
// editor required) and reports whether the project compiles right now. An agent
// uses it to verify a fix compiled WITHOUT asking the operator to re-open the
// editor or relying on the bridge-fed collector (which is dead precisely when
// the bridge addon itself failed to compile).
//
// GREENFIELD. Unity Open MCP's `compile_check` spawns a headless Unity editor
// to recompile; Godot has no headless editor equivalent (pinned by route-
// policy.ts — there is intentionally no `batch` route). Instead this module
// shells out to the platform's actual Godot build model:
//
//   - C# project (a `.csproj`/`.sln` is present)  → `dotnet build <target>`
//   - GDScript / tool-script project (otherwise)  → `godot --headless …`
//
// Only the tool SHAPE is adapted from Unity: `timeout_ms` cap, structured
// errors (`file`/`line`/`code`/`message`), and the `project_locked` guard
// (Unity's `editor_instance_locked` pre-spawn check). The error-parsing layer
// reuses `extractCompileDiagnostics` from compiler-errors.ts — it already
// normalizes both MSBuild `CSxxxx` lines and Godot's GDScript parse forms.
//
// The spawn is injectable (`SpawnFn`) so unit tests exercise detection,
// command resolution, parsing, and timeout without a real `dotnet`/`godot`
// install. The default `defaultSpawn` implementation uses `child_process.spawn`
// with a SIGTERM → SIGKILL escalation + bounded UTF-8-correct output buffers
// (the same discipline Unity's `batch-spawn.ts` `BoundedTextAccumulator` uses).

import { spawn } from "node:child_process";
import { readdirSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import {
  extractCompileDiagnostics,
  type CompileDiagnostic,
  type DiagnosticKind,
} from "./compiler-errors.js";

// ---------------------------------------------------------------------------
// Project-type detection
// ---------------------------------------------------------------------------

/** Coarse build-model classification derived from the project's files. */
export type ProjectType = "csharp" | "gdscript";

/** A build target resolved during detection. */
export interface DetectedProject {
  type: ProjectType;
  /**
   * For a C# project: the `.sln` (preferred) or `.csproj` path relative to the
   * project root that `dotnet build` should target. `null` for GDScript (the
   * `godot` CLI targets the whole project, or a caller-supplied script).
   */
  buildTarget: string | null;
}

/**
 * Directories never descended into during C# project-file detection. Matches
 * Godot's own ignore set (`.godot` cache + import sidecars) plus the .NET build
 * outputs and common VCS/tooling dirs.
 */
const DETECT_IGNORE_DIRS = new Set([
  ".godot",
  ".git",
  "node_modules",
  "obj",
  "bin",
  ".import",
  ".vs",
  ".idea",
]);

/** Hard cap on directory entries scanned during detection so a pathological
 *  tree cannot make detection walk forever. A real Godot project is well under
 *  this; the cap is a defense in depth. */
const DETECT_MAX_ENTRIES = 10_000;

/** Maximum recursion depth for the detection walk. */
const DETECT_MAX_DEPTH = 6;

/**
 * Classify a Godot project root's build model by scanning for C# project files.
 *
 * A `.sln` or `.csproj` anywhere under the root (excluding build/cache dirs)
 * makes the project `csharp`; otherwise it is `gdscript`. When multiple C#
 * targets exist, a `.sln` is preferred over any `.csproj` (it aggregates the
 * whole solution). The chosen target is returned relative to `projectRoot` so
 * `dotnet build` receives a stable path regardless of where it is invoked from.
 *
 * Returns `null` only when `projectRoot` does not exist or is not a directory —
 * the caller (`routeCompileCheck`) runs `identifyGodotProject` first, so this
 * is a defensive fallback. A real Godot project (even pure-GDScript) always
 * classifies as `gdscript`.
 *
 * Pure (no I/O beyond the bounded readdir walk) and deterministic.
 */
export function detectProjectType(projectRoot: string): DetectedProject | null {
  try {
    const st = statSync(projectRoot);
    if (!st.isDirectory()) return null;
  } catch {
    return null;
  }

  let entriesScanned = 0;
  // `.sln` paths collected separately so a solution can be preferred over any
  // individual `.csproj`. First-seen wins within each kind.
  let firstCsproj: string | null = null;
  let firstSln: string | null = null;

  const walk = (dir: string, depth: number): void => {
    if (depth > DETECT_MAX_DEPTH) return;
    let names: string[];
    try {
      names = readdirSync(dir);
    } catch {
      return;
    }
    for (const name of names) {
      if (entriesScanned >= DETECT_MAX_ENTRIES) return;
      entriesScanned += 1;
      if (DETECT_IGNORE_DIRS.has(name)) continue;
      const full = join(dir, name);
      let st;
      try {
        st = statSync(full);
      } catch {
        continue;
      }
      if (st.isDirectory()) {
        walk(full, depth + 1);
        continue;
      }
      const lower = name.toLowerCase();
      if (lower.endsWith(".sln")) {
        firstSln ??= relative(projectRoot, full);
        // A solution is the strongest signal — stop once one is found.
        return;
      }
      if (lower.endsWith(".csproj") && firstCsproj === null) {
        firstCsproj = relative(projectRoot, full);
      }
    }
  };

  walk(projectRoot, 0);

  if (firstSln !== null) return { type: "csharp", buildTarget: firstSln };
  if (firstCsproj !== null) return { type: "csharp", buildTarget: firstCsproj };
  return { type: "gdscript", buildTarget: null };
}

// ---------------------------------------------------------------------------
// Build-command resolution
// ---------------------------------------------------------------------------

/** Which builder backs a resolved command. */
export type Builder = "dotnet" | "godot";

/** A fully-resolved spawn command for one build model. */
export interface BuildCommand {
  builder: Builder;
  /** The binary to execute (`dotnet`, or the resolved Godot executable). */
  command: string;
  /** Argv (excluding the command itself). */
  args: string[];
}

/**
 * Operator env override for the Godot executable. When set, the spawn uses this
 * path verbatim instead of looking up `godot` on PATH. Follows the
 * `GODOT_OPEN_MCP_*` env-var convention (`GODOT_OPEN_MCP_LOG_FILE`,
 * `GODOT_OPEN_MCP_BRIDGE_PORT`).
 */
export const GODOT_PATH_ENV_OVERRIDE = "GODOT_OPEN_MCP_GODOT_PATH";

/**
 * Resolve the Godot executable path. Honors the operator env override
 * `GODOT_OPEN_MCP_GODOT_PATH`; otherwise falls back to the bare `godot` name
 * (resolved through PATH by the spawn). Never throws — the caller surfaces a
 * `builder_not_found` if the spawn refuses (ENOENT).
 */
export function resolveGodotPath(envOverride?: string): string {
  if (typeof envOverride === "string" && envOverride.trim() !== "") {
    return envOverride;
  }
  return "godot";
}

/**
 * Build the spawn command for a detected project.
 *
 * - C#  → `dotnet build <buildTarget>` (the `.sln`/`.csproj` from detection).
 *   `dotnet` is always resolved through PATH (the .NET SDK installs it there).
 * - GDScript + `scriptPath` → `godot --headless --check-only --script <path>`
 *   (parses exactly one script and quits — the Godot CLI's only per-script
 *   parse check).
 * - GDScript + no `scriptPath` → `godot --headless --quit` (loads the project:
 *   parses `project.godot`, autoloads, and the main scene, then quits after the
 *   first main-loop iteration). This surfaces STARTUP-TIME parse errors only —
 *   scripts that are never loaded at startup are not checked. That limitation
 *   is documented in the tool description + docs; a caller can target a single
 *   script with `script_path` for an exact check.
 *
 * `scriptPath` is passed verbatim to `godot --script`; it may be a `res://`
 * path or a native path. The router validates it is a non-empty string before
 * calling.
 */
export function resolveBuildCommand(
  projectRoot: string,
  detected: DetectedProject,
  scriptPath: string | null,
  godotPath: string,
): BuildCommand {
  void projectRoot; // cwd is set by the spawn, not threaded into the command.
  if (detected.type === "csharp") {
    const args = ["build"];
    if (detected.buildTarget !== null) args.push(detected.buildTarget);
    return { builder: "dotnet", command: "dotnet", args };
  }
  // GDScript.
  if (scriptPath !== null) {
    return {
      builder: "godot",
      command: godotPath,
      args: ["--headless", "--check-only", "--script", scriptPath],
    };
  }
  return {
    builder: "godot",
    command: godotPath,
    args: ["--headless", "--quit"],
  };
}

// ---------------------------------------------------------------------------
// Spawn abstraction
// ---------------------------------------------------------------------------

export interface SpawnBuildOptions {
  cwd: string;
  timeoutMs: number;
}

export interface BuildSpawnResult {
  command: string;
  args: string[];
  stdout: string;
  stderr: string;
  /** Process exit code, or `null` when the process did not exit normally
   *  (refused / killed). */
  exitCode: number | null;
  timedOut: boolean;
  /** The spawn refused (ENOENT — binary not on PATH / not executable). */
  refused: boolean;
  /** Refusal detail (the `Error.message` from the spawn `error` event). */
  refusedMessage: string | null;
  durationMs: number;
}

/** Injectable spawn. The default implementation is {@link defaultSpawn}; tests
 *  pass a stub to exercise the orchestrator without a real build toolchain. */
export type SpawnFn = (
  command: string,
  args: string[],
  opts: SpawnBuildOptions,
) => Promise<BuildSpawnResult>;

/** Per-stream output cap. A Godot/dotnet build log is modest; the cap is a
 *  defense against a pathological chatty build retaining a multi-MiB buffer. */
const MAX_OUTPUT_BYTES = 2 * 1024 * 1024;

/** SIGTERM → SIGKILL grace window so a wedged build process is reaped rather
 *  than lingering until the parent dies. Short relative to the overall timeout
 *  but long enough for `dotnet`/`godot` to flush + release file handles. */
const SIGKILL_GRACE_MS = 5_000;

/**
 * Default `SpawnFn` backed by `child_process.spawn`.
 *
 * Captures stdout + stderr into bounded (tail-retaining) UTF-8-correct buffers
 * (each multi-byte sequence straddling a chunk boundary would otherwise decode
 * to U+FFFD and corrupt a `CSxxxx`/GDScript line). Enforces `timeoutMs` with a
 * SIGTERM → SIGKILL escalation. Never rejects — every outcome (clean exit,
 * non-zero exit, timeout, spawn refusal) resolves to a `BuildSpawnResult` so the
 * orchestrator can map it to a structured result.
 */
export function defaultSpawn(
  command: string,
  args: string[],
  opts: SpawnBuildOptions,
): Promise<BuildSpawnResult> {
  return new Promise((resolve) => {
    const startTime = Date.now();
    let settled = false;

    // Bounded tail-retaining byte buffers per stream. Whole-buffer drop keeps
    // the cap without splitting a multi-byte sequence (the retained tail is
    // where compiler diagnostics land).
    const stdoutBuf: Buffer[] = [];
    let stdoutLen = 0;
    const stderrBuf: Buffer[] = [];
    let stderrLen = 0;
    const pushChunk = (bufs: Buffer[], chunk: Buffer, len: {
      v: number;
    }): void => {
      bufs.push(chunk);
      len.v += chunk.length;
      while (len.v > MAX_OUTPUT_BYTES && bufs.length > 1) {
        const head = bufs.shift()!;
        len.v -= head.length;
      }
    };
    const stdoutLenBox = { v: 0 };
    const stderrLenBox = { v: 0 };

    let timedOut = false;
    let killTimer: NodeJS.Timeout | null = null;

    let child: ReturnType<typeof spawn>;
    try {
      child = spawn(command, args, {
        cwd: opts.cwd,
        stdio: ["ignore", "pipe", "pipe"],
        windowsHide: true,
      });
    } catch (err) {
      // Synchronous spawn failure (extremely rare; usually ENOENT arrives via
      // the async `error` event instead). Resolve as a refusal.
      resolve({
        command,
        args,
        stdout: "",
        stderr: "",
        exitCode: null,
        timedOut: false,
        refused: true,
        refusedMessage: err instanceof Error ? err.message : String(err),
        durationMs: Date.now() - startTime,
      });
      return;
    }

    const finish = (result: Omit<BuildSpawnResult, "command" | "args">): void => {
      if (settled) return;
      settled = true;
      clearTimeout(timeoutHandle);
      if (killTimer) clearTimeout(killTimer);
      resolve({ command, args, ...result });
    };

    const timeoutHandle = setTimeout(() => {
      timedOut = true;
      try {
        child.kill("SIGTERM");
      } catch {
        /* already dead */
      }
      killTimer = setTimeout(() => {
        try {
          child.kill("SIGKILL");
        } catch {
          /* already dead */
        }
      }, SIGKILL_GRACE_MS);
    }, Math.max(0, opts.timeoutMs));

    child.stdout?.on("data", (chunk: Buffer) => {
      pushChunk(stdoutBuf, chunk, stdoutLenBox);
    });
    child.stderr?.on("data", (chunk: Buffer) => {
      pushChunk(stderrBuf, chunk, stderrLenBox);
    });

    child.on("error", (err) => {
      // ENOENT (binary not found) arrives here. The buffer may have partial
      // output; flush it through with the refusal flag set.
      finish({
        stdout: Buffer.concat(stdoutBuf).toString("utf8"),
        stderr: Buffer.concat(stderrBuf).toString("utf8"),
        exitCode: null,
        timedOut: false,
        refused: true,
        refusedMessage: err.message,
        durationMs: Date.now() - startTime,
      });
    });

    child.on("close", (code) => {
      finish({
        stdout: Buffer.concat(stdoutBuf).toString("utf8"),
        stderr: Buffer.concat(stderrBuf).toString("utf8"),
        exitCode: code ?? null,
        timedOut,
        refused: false,
        refusedMessage: null,
        durationMs: Date.now() - startTime,
      });
    });
  });
}

// ---------------------------------------------------------------------------
// Timeout clamp
// ---------------------------------------------------------------------------

export const DEFAULT_COMPILE_TIMEOUT_MS = 300_000;
export const MIN_COMPILE_TIMEOUT_MS = 30_000;
export const MAX_COMPILE_TIMEOUT_MS = 600_000;

/**
 * Clamp a raw `timeout_ms` input to the allowed band `[30s, 600s]`, defaulting
 * to 300s for non-finite/non-integer input. Mirrors the JSON Schema bounds; the
 * clamp is applied here too so a programmatic caller cannot bypass it.
 */
export function clampCompileTimeout(ms: unknown): number {
  if (typeof ms !== "number" || !Number.isFinite(ms)) {
    return DEFAULT_COMPILE_TIMEOUT_MS;
  }
  const n = Math.trunc(ms);
  if (n < MIN_COMPILE_TIMEOUT_MS) return MIN_COMPILE_TIMEOUT_MS;
  if (n > MAX_COMPILE_TIMEOUT_MS) return MAX_COMPILE_TIMEOUT_MS;
  return n;
}

// ---------------------------------------------------------------------------
// Result shapes
// ---------------------------------------------------------------------------

/** A normalized compiler/parser diagnostic surfaced to the caller. Subset of
 *  `CompileDiagnostic` carrying the fields an agent acts on. */
export interface CompileError {
  /** `csharp | gdscript | script_load | addon_load | other`. */
  kind: DiagnosticKind;
  /** `error | warning`. */
  severity: "error" | "warning";
  /** `res://`-relative or native path, or `null`. */
  file: string | null;
  /** 1-based line, or `null`. */
  line: number | null;
  /** Compiler/parser code (`CS0246`, etc.), or `null`. */
  code: string | null;
  /** The human-readable message. */
  message: string;
  /** The original matched line, verbatim. */
  raw: string;
}

/** Orchestrator outcome. `checked` is the normal path (build ran to
 *  completion or timed out); `builder_not_found` is a spawn refusal. */
export type CompileCheckOutcome = "checked" | "builder_not_found";

export interface CompileCheckResult {
  outcome: CompileCheckOutcome;
  projectType: ProjectType;
  builder: Builder;
  command: string;
  args: string[];
  durationMs: number;
  /** `checked` only — process exit code (`null` on timeout/refusal). */
  exitCode?: number | null;
  /** `checked` only — true when the build hit `timeout_ms` (killed). */
  timedOut?: boolean;
  /** `checked` only — exit 0 AND no error-severity diagnostics. False on
   *  timeout, non-zero exit, or any error. */
  success?: boolean;
  /** `checked` only — count of error-severity diagnostics. */
  errorCount?: number;
  /** `checked` only — count of warning-severity diagnostics. */
  warningCount?: number;
  /** `checked` only — normalized diagnostics (errors first, then warnings). */
  errors?: CompileError[];
  /** `checked` only — bounded tail of the combined build output (stdout +
   *  stderr), for context when diagnostics are sparse. */
  outputTail?: string;
  /** `builder_not_found` only — the spawn refusal message. */
  refusedMessage?: string;
}

// ---------------------------------------------------------------------------
// Output shaping helpers
// ---------------------------------------------------------------------------

/** Maximum bytes of combined build output retained as `outputTail`. */
const MAX_OUTPUT_TAIL_BYTES = 16_384;

/** Bounded tail of a combined output string. */
function tailOutput(combined: string): string {
  if (combined.length <= MAX_OUTPUT_TAIL_BYTES) return combined;
  return combined.slice(combined.length - MAX_OUTPUT_TAIL_BYTES);
}

/** Convert the parser's `CompileDiagnostic` into the tool-facing `CompileError`
 *  shape and split by severity. Errors first (oldest-in-tail order), then
 *  warnings. */
function shapeDiagnostics(
  diags: CompileDiagnostic[],
): { errors: CompileError[]; warnings: CompileError[] } {
  const errors: CompileError[] = [];
  const warnings: CompileError[] = [];
  for (const d of diags) {
    const entry: CompileError = {
      kind: d.kind,
      severity: d.severity,
      file: d.file,
      line: d.line,
      code: d.code,
      message: d.message,
      raw: d.raw,
    };
    if (d.severity === "warning") warnings.push(entry);
    else errors.push(entry);
  }
  return { errors, warnings };
}

// ---------------------------------------------------------------------------
// Orchestrator
// ---------------------------------------------------------------------------

export interface CompileCheckInput {
  projectRoot: string;
  /** Optional GDScript target (`res://` or native path). Ignored for C#. */
  scriptPath: string | null;
  /** Clamped timeout in milliseconds. */
  timeoutMs: number;
  /** Resolved Godot executable (env override or `"godot"`). */
  godotPath: string;
  /** Injectable spawn (defaults to {@link defaultSpawn}). */
  spawnFn?: SpawnFn;
}

/**
 * Run a triggered build check.
 *
 * Pipeline:
 *   1. Detect the project type (C# vs GDScript) from the project files.
 *   2. Resolve the spawn command (`dotnet build` or `godot --headless …`).
 *   3. Spawn the build (cwd = project root, `timeoutMs` enforced with SIGTERM →
 *      SIGKILL escalation).
 *   4. On a spawn refusal (ENOENT) → `outcome: "builder_not_found"`.
 *   5. Otherwise parse stdout+stderr via `extractCompileDiagnostics`, split into
 *      errors/warnings, and compute `success` (exit 0 + no errors, not timed
 *      out). Surface a bounded `outputTail` for context.
 *
 * Never throws — every failure maps to a structured `CompileCheckResult`. The
 * caller (`routeCompileCheck`) maps `builder_not_found` to an `isError` envelope.
 */
export async function runCompileCheck(
  input: CompileCheckInput,
): Promise<CompileCheckResult> {
  const detected =
    detectProjectType(input.projectRoot) ?? {
      type: "gdscript" as ProjectType,
      buildTarget: null,
    };
  const buildCmd = resolveBuildCommand(
    input.projectRoot,
    detected,
    input.scriptPath,
    input.godotPath,
  );

  const spawnFn = input.spawnFn ?? defaultSpawn;
  const spawned = await spawnFn(buildCmd.command, buildCmd.args, {
    cwd: input.projectRoot,
    timeoutMs: input.timeoutMs,
  });

  const base = {
    projectType: detected.type,
    builder: buildCmd.builder,
    command: spawned.command,
    args: spawned.args,
    durationMs: spawned.durationMs,
  };

  if (spawned.refused) {
    return {
      outcome: "builder_not_found",
      ...base,
      refusedMessage: spawned.refusedMessage ?? undefined,
    };
  }

  const combined = `${spawned.stdout}\n${spawned.stderr}`;
  const diags = extractCompileDiagnostics(combined);
  const { errors, warnings } = shapeDiagnostics(diags);

  const exitCode = spawned.exitCode;
  const timedOut = spawned.timedOut;
  const success =
    !timedOut && exitCode === 0 && errors.length === 0;

  return {
    outcome: "checked",
    ...base,
    exitCode,
    timedOut,
    success,
    errorCount: errors.length,
    warningCount: warnings.length,
    errors: [...errors, ...warnings],
    outputTail: tailOutput(combined),
  };
}
