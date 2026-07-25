// Godot editor binary discovery + launch — adapted from the Godot-MCP behavior
// reference (`cli/src/utils/godot-editor.ts`).
//
// Fidelity (per specs/execution/P6/P6.4.md):
//   - editor discovery: adapt — Godot-specific path search; no Unity Hub.
//   - launch argv: adapt — spawn `<godot> --editor --path <project>`; the
//     `--editor` flag forces the project open in the editor rather than running
//     it. Detached + `unref()` so the CLI can exit without waiting on the editor.
//
// Intentional deltas from Godot-MCP:
//   1. Env var names are `GODOT` / `GODOT_EDITOR` (the P6.4 plan's chosen
//      names) rather than `GODOT_BIN` / `GODOT4_BIN`. `GODOT_BIN` /
//      `GODOT4_BIN` are also honored for parity with the wider Godot ecosystem.
//   2. The launch helper does NOT inject any `GODOT_MCP_*` cloud / connection
//      env vars — the bridge discovers its project from the editor and the MCP
//      client reads `GODOT_PROJECT_PATH` from setup-mcp. Cloud/SignalR is
//      explicitly skipped per P6.4 scope.
//
// Pure / no spawn for the discovery half (`findGodotBinary`); the spawn lives
// in `launchEditor` so the two concerns stay separable and unit-testable.

import * as fs from "node:fs";
import * as path from "node:path";
import { spawn, type ChildProcess } from "node:child_process";
import { homedir, platform } from "node:os";

/**
 * Environment variables that, when set, point directly at a Godot editor
 * binary. Checked before PATH and common install dirs. The P6.4 plan names
 * `GODOT` / `GODOT_EDITOR`; `GODOT_BIN` / `GODOT4_BIN` are honored too for
 * parity with the wider Godot ecosystem (contributors may have those set).
 */
export const GODOT_BIN_ENV_VARS = [
  "GODOT",
  "GODOT_EDITOR",
  "GODOT_BIN",
  "GODOT4_BIN",
] as const;

/**
 * Candidate executable base-names searched on `PATH`. The Windows mono build
 * uses a `_console.exe` companion that surfaces stdout; the mono editor binary
 * itself ships under several historical names, so we probe a small set.
 */
function pathCandidateNames(os: NodeJS.Platform): string[] {
  if (os === "win32") {
    // Prefer the mono build; the `_console` variant surfaces GD.Print/stdout.
    return [
      "godot_mono.exe",
      "godot.exe",
      "Godot_mono.exe",
      "Godot.exe",
    ];
  }
  // Mono first, same as the Windows list: the addon is C#, so a non-.NET editor cannot load it.
  // Probing "godot" first meant a plain `godot` on PATH always beat a `godot_mono` sitting in the
  // same directory.
  return ["godot_mono", "godot", "Godot"];
}

/**
 * Maximum directory depth (inclusive) to descend when shallow-recursively
 * scanning a common install root for a Godot editor binary. Official Windows
 * zips extract to a nested version-stamped folder (binary two levels below the
 * root), so we need depth >= 2; 3 also covers an extra wrapper folder.
 */
const SCAN_MAX_DEPTH = 3;

/**
 * Bounded scan depth for FLAT roots — system bin dirs where the binary, if
 * present, sits directly in the dir. Scanning these recursively would issue
 * thousands of ops for no benefit.
 */
const FLAT_SCAN_DEPTH = 0;

const VERSION_RE = /v(\d+)\.(\d+)(?:\.(\d+))?/i;
function godotVersionKey(name: string): number {
  const m = VERSION_RE.exec(name);
  if (!m) return -1;
  const major = Number(m[1]);
  const minor = Number(m[2]);
  const patch = m[3] !== undefined ? Number(m[3]) : 0;
  return major * 1_000_000 + minor * 1_000 + patch;
}

const WIN_STAMPED_RE = /^godot_v.*win.*\.exe$/;
const MACOS_STAMPED_RE = /^godot_v.*(macos|osx).*$/;
const LINUX_STAMPED_RE = /^godot_v.*(linux|x11).*$/;

/**
 * Whether a file name looks like a Godot editor binary — matches both the fixed
 * historical names AND the official version-stamped release names
 * (e.g. `Godot_v4.5.1-stable_mono_win64.exe`). Case-insensitive and loose on
 * the platform/arch suffix so future arch names still resolve.
 */
export function isGodotBinaryName(name: string, os: NodeJS.Platform): boolean {
  const lower = name.toLowerCase();

  if (os === "win32") {
    if (lower === "godot.exe" || lower === "godot_mono.exe") return true;
    return WIN_STAMPED_RE.test(lower);
  }

  if (os === "darwin") {
    if (lower === "godot" || lower === "godot_mono") return true;
    return MACOS_STAMPED_RE.test(lower);
  }

  // linux + others
  if (lower === "godot" || lower === "godot_mono") return true;
  return LINUX_STAMPED_RE.test(lower);
}

/**
 * Rank a Godot binary name so we can prefer the build that surfaces the most
 * useful output. Higher = more preferred:
 *   mono `_console` (mono + stdout) → 3, mono → 2, non-mono `_console` → 1,
 *   else 0.
 */
export function godotBinaryRank(name: string): number {
  const lower = name.toLowerCase();
  const mono = lower.includes("mono");
  const console = lower.includes("_console");
  if (mono && console) return 3;
  if (mono) return 2;
  if (console) return 1;
  return 0;
}

/**
 * Recursively scan `root` for files whose name looks like a Godot editor binary,
 * descending at most `maxDepth` levels. Returns absolute paths, best-ranked
 * first. Symlinks are not followed; unreadable dirs are skipped silently.
 */
export function scanForGodotBinaries(
  root: string,
  os: NodeJS.Platform,
  maxDepth: number,
): string[] {
  const found: string[] = [];

  const walk = (dir: string, depth: number): void => {
    let entries: fs.Dirent[];
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return; // unreadable / nonexistent — skip
    }
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      if (entry.isFile()) {
        if (isGodotBinaryName(entry.name, os)) {
          found.push(full);
        }
      } else if (entry.isDirectory() && depth < maxDepth) {
        walk(full, depth + 1);
      }
    }
  };

  walk(root, 0);

  // Order: best build first, then newest version, then path (deterministic).
  //
  // Rank on the path RELATIVE TO the scan root, not on the basename. On macOS the edition and
  // version markers live in the `.app` bundle *directory* name
  // (`Godot_v4.5-stable_mono_macos.universal.app/Contents/MacOS/Godot`) — the inner executable is
  // always literally `Godot`. Ranking by basename therefore made both godotBinaryRank and
  // godotVersionKey constant, collapsing selection to a plain localeCompare on the full path: with
  // both editions installed it picked the NON-.NET build, and with two versions it picked the older
  // one. The addon is C#, so a non-.NET editor cannot load it at all, yet `open` still reported
  // success. Using the root-relative path keeps Windows/Linux behavior identical (the markers are in
  // the file name there, which the relative path includes) while fixing macOS.
  const relativeTo = (p: string): string => {
    const rel = path.relative(root, p);
    return rel === "" ? path.basename(p) : rel;
  };
  found.sort((a, b) => {
    const keyA = relativeTo(a);
    const keyB = relativeTo(b);
    const rank = godotBinaryRank(keyB) - godotBinaryRank(keyA);
    if (rank !== 0) return rank;
    const version = godotVersionKey(keyB) - godotVersionKey(keyA);
    if (version !== 0) return version;
    return a.localeCompare(b);
  });
  return found;
}

interface InstallRoot {
  path: string;
  depth: number;
}

/**
 * Per-OS common install roots to scan for a Godot editor binary when neither an
 * env override nor a PATH hit resolves one. Archive-extraction roots are
 * scanned recursively (`SCAN_MAX_DEPTH`); system bin dirs are probed flat.
 */
function commonInstallRoots(os: NodeJS.Platform): InstallRoot[] {
  const home = homedir();
  const recursive: string[] = [];
  const flat: string[] = [];

  if (os === "win32") {
    const programFiles = process.env["PROGRAMFILES"] ?? "C:\\Program Files";
    const programFilesX86 = process.env["PROGRAMFILES(X86)"] ?? "C:\\Program Files (x86)";
    const localAppData = process.env["LOCALAPPDATA"] ?? path.join(home, "AppData", "Local");
    const userProfile = process.env["USERPROFILE"] ?? home;
    recursive.push(
      path.join(userProfile, "Downloads"),
      path.join(home, "Downloads"),
      path.join(programFiles, "Godot"),
      path.join(programFilesX86, "Godot"),
      path.join(localAppData, "Programs", "Godot"),
      path.join(userProfile, "scoop", "apps", "godot"),
      path.join(userProfile, "scoop", "apps", "godot-mono"),
      path.join(
        process.env["CHOCOLATEYINSTALL"] ?? "C:\\ProgramData\\chocolatey",
        "lib",
        "godot",
      ),
      path.join(localAppData, "Microsoft", "WinGet", "Packages"),
      path.join(programFiles, "Steam", "steamapps", "common", "Godot Engine"),
      path.join(programFilesX86, "Steam", "steamapps", "common", "Godot Engine"),
    );
  } else if (os === "darwin") {
    recursive.push(
      "/Applications",
      path.join(home, "Applications"),
      path.join(home, "Downloads"),
      path.join(
        home,
        "Library",
        "Application Support",
        "Steam",
        "steamapps",
        "common",
        "Godot Engine",
      ),
    );
  } else {
    // linux + others.
    flat.push(
      "/usr/local/bin",
      "/usr/bin",
      path.join(home, ".local", "bin"),
      path.join(home, "bin"),
    );
    recursive.push(
      "/opt",
      path.join(home, ".local", "share", "godot"),
      path.join(home, "Applications"),
      path.join(home, "Downloads"),
      path.join(home, ".local", "share", "Steam", "steamapps", "common", "Godot Engine"),
      path.join(home, ".steam", "steam", "steamapps", "common", "Godot Engine"),
    );
  }

  const seen = new Set<string>();
  const roots: InstallRoot[] = [];
  const add = (p: string, depth: number): void => {
    if (seen.has(p)) return;
    seen.add(p);
    roots.push({ path: p, depth });
  };
  for (const p of recursive) add(p, SCAN_MAX_DEPTH);
  for (const p of flat) add(p, FLAT_SCAN_DEPTH);
  return roots;
}

/** Search the directories on `PATH` for a matching Godot binary. Pure fs lookup. */
function findOnPath(os: NodeJS.Platform): string | null {
  const pathEnv = process.env["PATH"] ?? "";
  if (!pathEnv) return null;
  const sep = os === "win32" ? ";" : ":";
  const dirs = pathEnv.split(sep).filter((d) => d.length > 0);
  const names = pathCandidateNames(os);
  for (const dir of dirs) {
    for (const name of names) {
      const candidate = path.join(dir, name);
      if (existsAsFile(candidate)) {
        return candidate;
      }
    }
    // Slow path: version-stamped binary sitting directly on a PATH dir.
    const stamped = scanForGodotBinaries(dir, os, 0);
    if (stamped.length > 0) {
      return stamped[0];
    }
  }
  return null;
}

function existsAsFile(p: string): boolean {
  try {
    return fs.statSync(p).isFile();
  } catch {
    return false;
  }
}

/**
 * Resolve the Godot editor binary path.
 *
 * Resolution order (first hit wins):
 *   1. An explicit `editorPath` argument (validated to be an existing file).
 *   2. `GODOT` / `GODOT_EDITOR` / `GODOT_BIN` / `GODOT4_BIN` env vars.
 *   3. The first matching Godot binary on `PATH` (fixed name or version-stamped).
 *   4. A bounded scan of per-OS common install roots (Downloads, Program Files,
 *      /Applications, /opt, …), preferring the mono `_console` build and the
 *      newest version.
 *
 * Returns the absolute path, or `null` when no Godot binary can be located.
 */
export function findGodotBinary(
  editorPath?: string,
  os: NodeJS.Platform = platform(),
): string | null {
  // 1. Explicit path.
  if (editorPath !== undefined) {
    const trimmed = editorPath.trim();
    if (trimmed.length === 0) return null;
    const resolved = path.resolve(trimmed);
    return existsAsFile(resolved) ? resolved : null;
  }

  // 2. Env overrides.
  for (const envVar of GODOT_BIN_ENV_VARS) {
    const raw = process.env[envVar];
    if (raw && raw.trim().length > 0) {
      const resolved = path.resolve(raw.trim());
      if (existsAsFile(resolved)) {
        return resolved;
      }
    }
  }

  // 3. PATH.
  const onPath = findOnPath(os);
  if (onPath) return onPath;

  // 4. Bounded scan of common install roots.
  for (const root of commonInstallRoots(os)) {
    const hits = scanForGodotBinaries(root.path, os, root.depth);
    if (hits.length > 0) {
      return hits[0];
    }
  }

  return null;
}

export interface LaunchEditorCallbacks {
  /** Fired once the OS reports the child process has spawned. */
  onSpawn?: (pid: number | undefined) => void;
  /** Fired if the spawn itself fails (binary missing, permission denied, …). */
  onError?: (err: Error) => void;
}

/**
 * Spawn the Godot editor binary opening the given project. The invocation is
 * `<godot> --editor --path <project>` (the `--editor` flag forces the project
 * to open in the editor rather than run). Spawns detached, with
 * `stdio: 'ignore'`, and `unref()`s so the parent (the CLI) can exit without
 * waiting on the editor.
 *
 * Library-safe (no process.exit, no stdout/stderr writes — observability is the
 * caller's responsibility via the optional callbacks).
 *
 * Intentional delta from Godot-MCP: NO `GODOT_MCP_*` connection env vars are
 * injected. The bridge discovers its project from the editor; the MCP client
 * reads `GODOT_PROJECT_PATH` from setup-mcp. Cloud/SignalR is out of scope
 * (P6.4).
 */
export function launchEditor(
  editorPath: string,
  projectPath: string,
  callbacks?: LaunchEditorCallbacks,
): ChildProcess {
  const args = ["--editor", "--path", path.resolve(projectPath)];

  const child = spawn(editorPath, args, {
    detached: true,
    stdio: "ignore",
    env: { ...process.env },
  });

  child.on("spawn", () => {
    callbacks?.onSpawn?.(child.pid ?? undefined);
  });

  child.on("error", (err) => {
    callbacks?.onError?.(err);
  });

  child.unref();
  return child;
}
