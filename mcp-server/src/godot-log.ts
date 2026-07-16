// Godot project log path resolution + bounded tail + rotated-log fallback +
// stale-log detection, for the offline `godot_open_mcp_read_compile_errors`
// tool (P7.4).
//
// `console_get_logs` depends on the bridge-fed addon collector and stops
// accumulating when the addon fails to compile or load. This module is the ONE
// diagnostic channel that survives a dead bridge: it reads the project's
// configured Godot log file from disk. Godot writes C#/GDScript/parser/plugin
// diagnostics there regardless of bridge health, so the tool retrieves them
// without touching Godot or the bridge.
//
// Adapted from Unity Open MCP's `mcp-server/src/unity-log.ts` (copy fidelity
// for the bounded seek/read + provenance pattern + stale-log mtime compare;
// ADAPT the path resolver). Intentional deltas:
//   - Godot logs to `user://logs/godot.log` by default (project-scoped, under
//     the per-platform user data dir), NOT Unity's global `~/Library/Logs/Unity`
//     per-user log. Resolution parses the project's `debug/file_logging/*`
//     settings instead of relying on a fixed editor-global path.
//   - File logging is OFF by default in Godot (opt-in via
//     `debug/file_logging/enable_file_logging`). The resolver surfaces that as
//     a distinct `logging_disabled` status rather than a missing-file error.
//   - A safe operator env override `GODOT_OPEN_MCP_LOG_FILE` is honored so a
//     custom `--log-file` (not discoverable from disk) can be propagated when
//     launching the MCP server. It is operator configuration, never per-call
//     input — the tool exposes NO `log_path` argument (no arbitrary file read).
//   - No Unity Safe Mode / Editor.log project-relative path story.
//
// Cross-platform user-data-dir behavior verified against the Godot 4.3+ docs
// (https://docs.godotengine.org/en/4.3/classes/class_os.html#get-user-data-dir):
//   - macOS:   ~/Library/Application Support/Godot/app_userdata/<name>/
//   - Linux:   ${XDG_DATA_HOME:-~/.local/share}/godot/app_userdata/<name>/
//   - Windows: %APPDATA%/Godot/app_userdata/<name>/
// Custom user dir settings (`application/config/use_custom_user_dir` +
// `application/config/custom_user_dir_name`) drop the `Godot/app_userdata/`
// segment and use the custom name directly.
//
// No runtime deps beyond node built-ins (mcp-server/AGENTS.md).

import {
  existsSync,
  openSync,
  readSync,
  fstatSync,
  closeSync,
  statSync,
  readdirSync,
} from "node:fs";
import { homedir } from "node:os";
import { join, sep } from "node:path";

// ---------------------------------------------------------------------------
// Platform + env surface.
// ---------------------------------------------------------------------------

export type GodotLogPlatform = "win32" | "darwin" | "linux";

/** Operator-owned env override for the log path. Set when launching the MCP
 *  server (e.g. when Godot is launched with `--log-file`). NEVER a per-call
 *  tool argument — surfacing `log_path` would turn a diagnostic tool into a
 *  general local file reader. */
export const LOG_FILE_ENV_OVERRIDE = "GODOT_OPEN_MCP_LOG_FILE";

// ---------------------------------------------------------------------------
// project.godot settings subset.
// ---------------------------------------------------------------------------

/** Bounded subset of `project.godot` settings the log resolver consults. All
 *  fields are optional in a real project file; the resolver applies documented
 *  defaults when they are absent. */
export interface GodotProjectLogSettings {
  /** `application/config/name` — drives the default `app_userdata/<name>` user
   *  data dir. Falls back to `"Unnamed Project"` (Godot's own default) when the
   *  setting is absent/empty. */
  projectName: string;
  /** `application/config/use_custom_user_dir` — when true, the user data root
   *  drops the `Godot/app_userdata/` prefix and uses `custom_user_dir_name`
   *  directly under the OS data dir. */
  useCustomUserDir: boolean;
  /** `application/config/custom_user_dir_name` — replacement directory name.
   *  When `useCustomUserDir` is true but this is empty, Godot falls back to the
   *  project name. We mirror that. */
  customUserDirName: string;
  /** `debug/file_logging/enable_file_logging` — the master switch. False by
   *  default in Godot; the resolver surfaces a `logging_disabled` status rather
   *  than a missing-file error when it is off. */
  fileLoggingEnabled: boolean;
  /** `debug/file_logging/log_path` — explicit log path override. Godot treats
   *  this as a `user://`-relative path when it starts with `user://`, otherwise
   *  an absolute path. Null when the setting is absent (default
   *  `user://logs/godot.log` applies). */
  logPath: string | null;
  /** `debug/file_logging/max_log_files` — rotation cap Godot enforces. Used to
   *  bound the rotated-file scan (we never scan more candidates than Godot
   *  itself would keep). Default 5. */
  maxLogFiles: number;
}

/** Default values Godot applies when the corresponding `project.godot` key is
 *  absent (verified against the Godot 4.x docs). */
export const DEFAULT_PROJECT_NAME = "Unnamed Project";
export const DEFAULT_MAX_LOG_FILES = 5;
export const DEFAULT_LOG_PATH = "user://logs/godot.log";

/**
 * Parse the bounded `project.godot` settings subset the log resolver needs.
 * Lenient by design — unknown sections, comments, and malformed optional values
 * are skipped. Returns the documented defaults for any missing key so callers
 * can branch on the fields without null-checking each one.
 *
 * Godot's INI grammar: `[section]` headers, `key = value` lines, `;`/`#`
 * comments. Boolean values are `true`/`false`; integers are bare numbers;
 * string values may be quoted (`"My Game"`).
 */
export function parseProjectLogSettings(text: string): GodotProjectLogSettings {
  const out: GodotProjectLogSettings = {
    projectName: DEFAULT_PROJECT_NAME,
    useCustomUserDir: false,
    customUserDirName: "",
    fileLoggingEnabled: false,
    logPath: null,
    maxLogFiles: DEFAULT_MAX_LOG_FILES,
  };

  // Godot's project.godot uses INI sections + subsections. The file-logging
  // settings live under:
  //   [debug]
  //   [file_logging]
  //   enable_file_logging=true
  //   log_path="user://logs/godot.log"
  //   max_log_files=5
  // The keys are globally unique within the file, so we match on the bare key
  // (the last segment after `/`). This is robust to Godot's nested-section
  // convention AND to the `section/key=value` flat form some tools emit.
  for (const rawLine of text.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (line === "" || line.startsWith(";") || line.startsWith("#")) continue;

    // Skip section headers.
    if (/^\[.+\]$/.test(line)) continue;

    const eqIdx = line.indexOf("=");
    if (eqIdx <= 0) continue;
    const key = line.slice(0, eqIdx).trim();
    let value = line.slice(eqIdx + 1).trim();
    // Strip surrounding double quotes (Godot quotes string values).
    if (
      value.length >= 2 &&
      value.startsWith('"') &&
      value.endsWith('"')
    ) {
      value = value.slice(1, -1);
    }

    // The bare key is the last segment after `/` (e.g.
    // `debug/file_logging/enable_file_logging` → `enable_file_logging`). Godot
    // keys are globally unique, so the bare key is sufficient.
    const bareKey = key.includes("/") ? key.slice(key.lastIndexOf("/") + 1) : key;

    if (key === "config/name" && value !== "") {
      out.projectName = value;
    } else if (bareKey === "use_custom_user_dir") {
      out.useCustomUserDir = parseGodotBool(value);
    } else if (bareKey === "custom_user_dir_name") {
      out.customUserDirName = value;
    } else if (bareKey === "enable_file_logging") {
      out.fileLoggingEnabled = parseGodotBool(value);
    } else if (bareKey === "log_path") {
      out.logPath = value === "" ? null : value;
    } else if (bareKey === "max_log_files") {
      const n = parseInt(value, 10);
      if (Number.isFinite(n) && n >= 0) out.maxLogFiles = Math.trunc(n);
    }
  }
  return out;
}

/** Parse a Godot INI boolean (`true`/`false`, case-insensitive). Anything else
 *  is treated as `false` (matches Godot's ProjectSettings coercion for missing
 *  or unrecognized bool values). */
function parseGodotBool(raw: string): boolean {
  return raw.toLowerCase() === "true";
}

// ---------------------------------------------------------------------------
// User-data-dir resolution.
// ---------------------------------------------------------------------------

/**
 * Resolve the Godot user-data root for a platform + project settings. Mirrors
 * `OS.get_user_data_dir()` per the Godot 4.3+ docs.
 *
 *   - macOS:   ~/Library/Application Support/Godot/app_userdata/<name>/
 *   - Linux:   ${XDG_DATA_HOME:-~/.local/share}/godot/app_userdata/<name>/
 *   - Windows: %APPDATA%/Godot/app_userdata/<name>/
 *
 * When `useCustomUserDir` is true, the `Godot/app_userdata/` segment is dropped
 * and `customUserDirName` (falling back to the project name when empty) is used
 * directly under the OS data root.
 *
 * The result always ends in `sep` so a `logs/godot.log` join is clean. Pure —
 * no filesystem touch; injectable for tests via `homedir`/env reads.
 */
export function godotUserDataRoot(
  settings: GodotProjectLogSettings,
  platform: GodotLogPlatform,
  env: NodeJS.ProcessEnv = process.env,
): string {
  const base = osDataBaseDir(platform, env);
  let dirName: string;
  if (settings.useCustomUserDir) {
    const custom = settings.customUserDirName.trim();
    dirName = custom === "" ? settings.projectName : custom;
  } else {
    // macOS + Windows use a capitalized `Godot` segment; Linux follows the XDG
    // convention with lowercase `godot`. Verified against the Godot 4.3 docs
    // (https://docs.godotengine.org/en/4.3/classes/class_os.html#get-user-data-dir).
    const godotSegment = platform === "linux" ? "godot" : "Godot";
    dirName = join(godotSegment, "app_userdata", settings.projectName);
  }
  return join(base, dirName) + sep;
}

/**
 * Resolve the per-platform OS data base directory for Godot user data.
 *   - macOS:   ~/Library/Application Support
 *   - Linux:   ${XDG_DATA_HOME:-~/.local/share}
 *   - Windows: %APPDATA%
 *
 * On Linux Godot lowercases the `godot` segment (per XDG spec conventions);
 * `app_userdata` stays lowercase as Godot emits it. macOS / Windows use the
 * documented OS-provided env vars.
 */
export function osDataBaseDir(
  platform: GodotLogPlatform,
  env: NodeJS.ProcessEnv = process.env,
): string {
  switch (platform) {
    case "darwin":
      return join(homedir(), "Library", "Application Support");
    case "linux": {
      const xdg = env.XDG_DATA_HOME;
      if (xdg && xdg !== "") return xdg;
      return join(homedir(), ".local", "share");
    }
    case "win32": {
      const appdata = env.APPDATA;
      if (appdata && appdata !== "") return appdata;
      // APPDATA is effectively always set on Windows; fall back to the user
      // profile if it is somehow missing.
      return join(homedir(), "AppData", "Roaming");
    }
    default:
      return join(homedir(), "Library", "Application Support");
  }
}

// ---------------------------------------------------------------------------
// Log path resolution.
// ---------------------------------------------------------------------------

/** Default tail size. Bounded so a multi-MB log can't blow up the tool
 *  response; 256 KiB is ample for a compile-error burst (Godot writes the
 *  diagnostics in a contiguous block near the end of the log). */
export const DEFAULT_LOG_TAIL_BYTES = 256 * 1024;

/** Minimum + maximum tail sizes the tool accepts. The schema enforces the same
 *  bounds; these constants are reused by the resolver so the cap is applied in
 *  one place. */
export const MIN_LOG_TAIL_BYTES = 4096;
export const MAX_LOG_TAIL_BYTES = 1024 * 1024;

/**
 * Resolve the absolute log directory and the current log file's native path
 * from a bounded settings snapshot + the resolved project root.
 *
 * Resolution precedence:
 *   1. `GODOT_OPEN_MCP_LOG_FILE` env override (absolute path) — operator
 *      configuration supplied when launching the MCP server. Its directory is
 *      also used for rotated-log scanning.
 *   2. Parsed project `debug/file_logging/log_path` — treated as `user://`-
 *      relative when it starts with `user://`, otherwise as an absolute path.
 *   3. Default `user://logs/godot.log` under the user data root.
 *
 * Returns the absolute log DIRECTORY (for rotated-log scanning) and the
 * absolute current log file PATH. Never throws — a malformed env override
 * surfaces as `invalid_log_configuration` from the caller via the structured
 * error path.
 */
export interface ResolvedLogPaths {
  /** Absolute directory holding the current + rotated logs. */
  logDir: string;
  /** Absolute path to the current log file. */
  currentLogPath: string;
  /** Which resolution source won (for provenance). */
  source: "env_override" | "project_setting" | "default";
}

const USER_PREFIX = "user://";

export function resolveLogPaths(
  settings: GodotProjectLogSettings,
  userDataRoot: string,
  env: NodeJS.ProcessEnv = process.env,
): ResolvedLogPaths {
  // 1. Operator env override. Must be an absolute path; Godot's `--log-file`
  //    accepts an absolute or relative path but the operator is expected to
  //    supply an absolute path here (it is server-launch configuration).
  const envOverride = env[LOG_FILE_ENV_OVERRIDE];
  if (envOverride && envOverride.trim() !== "") {
    return {
      logDir: dirOf(envOverride),
      currentLogPath: envOverride,
      source: "env_override",
    };
  }

  // 2. Project setting (may be `user://`-relative or absolute).
  if (settings.logPath !== null) {
    if (settings.logPath.startsWith(USER_PREFIX)) {
      const rel = settings.logPath.slice(USER_PREFIX.length);
      const abs = join(userDataRoot, rel);
      return {
        logDir: dirOf(abs),
        currentLogPath: abs,
        source: "project_setting",
      };
    }
    // Absolute path in the project setting.
    return {
      logDir: dirOf(settings.logPath),
      currentLogPath: settings.logPath,
      source: "project_setting",
    };
  }

  // 3. Default `user://logs/godot.log`.
  const abs = join(userDataRoot, "logs", "godot.log");
  return {
    logDir: join(userDataRoot, "logs"),
    currentLogPath: abs,
    source: "default",
  };
}

/** Return the directory portion of a native path. `sep`-agnostic. */
function dirOf(p: string): string {
  const norm = p.endsWith(sep) ? p.slice(0, -1) : p;
  const i = norm.lastIndexOf(sep);
  if (i <= 0) return ".";
  return norm.slice(0, i);
}

// ---------------------------------------------------------------------------
// Bounded tail read.
// ---------------------------------------------------------------------------

/** Tail-read outcome. `exists: false` when the file is absent; `error` set when
 *  the file exists but could not be read. */
export interface ReadLogTailResult {
  /** Absolute path that was read. */
  path: string;
  /** Whether the file existed and was opened. */
  exists: boolean;
  /** The tail content (CRLF normalized to LF). Empty when missing/unreadable. */
  content: string;
  /** Bytes read (content length in UTF-8 bytes). 0 when missing. */
  bytes: number;
  /** Total file size in bytes (0 when missing). */
  size: number;
  /** File mtime in epoch ms (undefined when missing/unreadable). */
  mtimeMs?: number;
  /** Whether the read started mid-file (i.e. dropped a partial first line). */
  truncated: boolean;
  /** Error message when the file existed but could not be read. */
  error?: string;
}

/**
 * Read up to `maxBytes` from the END of a file, as a UTF-8 string. Returns
 * `{ exists: false }` when the file is absent. Never throws — read failures
 * (permissions, vanished mid-read) surface as `{ exists, error }`.
 *
 * The tail is read by seeking to `(size - readLen)` and reading forward, so a
 * multi-MB log is not loaded in full. When the read starts mid-file, the first
 * partial line is dropped (a diagnostic block split across the tail boundary
 * would otherwise surface a truncated first line that no parser matches).
 * CRLF is normalized to LF so downstream parsers only deal with one line
 * ending.
 */
export function readLogTail(
  path: string,
  maxBytes: number = DEFAULT_LOG_TAIL_BYTES,
): ReadLogTailResult {
  if (!existsSync(path)) {
    return {
      path,
      exists: false,
      content: "",
      bytes: 0,
      size: 0,
      truncated: false,
    };
  }
  let fd: number | undefined;
  try {
    fd = openSync(path, "r");
    const stat = fstatSync(fd);
    const size = stat.size;
    const readLen = Math.min(size, Math.max(0, maxBytes));
    const start = size - readLen;
    const buf = Buffer.alloc(readLen);
    // readSync may return fewer bytes than requested if the file is being
    // written concurrently; loop until the buffer is filled or we hit EOF.
    let read = 0;
    while (read < readLen) {
      const n = readSync(fd, buf, read, readLen - read, start + read);
      if (n === 0) break;
      read += n;
    }
    let content = buf.subarray(0, read).toString("utf8");
    // Normalize CRLF → LF so parsers only handle one line ending.
    content = content.replace(/\r\n/g, "\n");

    let truncated = false;
    if (start > 0) {
      // Started mid-file — drop the first partial line so a diagnostic split
      // across the tail boundary does not surface a phantom half-line. Only
      // drop when we can find a newline to advance past; if the window has no
      // newline at all, keep it verbatim (the whole window is one line, and
      // dropping it would surface an empty result for a valid read).
      const firstNl = content.indexOf("\n");
      if (firstNl >= 0 && firstNl < content.length - 1) {
        content = content.slice(firstNl + 1);
        truncated = true;
      } else {
        // Window started mid-file but has no newline boundary to align on.
        // Mark truncated so the caller knows the read started mid-file, but
        // keep the content (it may be the only signal).
        truncated = true;
      }
    }
    return {
      path,
      exists: true,
      content,
      bytes: Buffer.byteLength(content, "utf8"),
      size,
      mtimeMs: stat.mtimeMs,
      truncated,
    };
  } catch (err) {
    return {
      path,
      exists: true,
      content: "",
      bytes: 0,
      size: 0,
      truncated: false,
      error: err instanceof Error ? err.message : String(err),
    };
  } finally {
    if (fd !== undefined) {
      try {
        closeSync(fd);
      } catch {
        // best-effort
      }
    }
  }
}

// ---------------------------------------------------------------------------
// Rotated-log selection.
// ---------------------------------------------------------------------------

/** Godot's rotated-log filename pattern. The current log is `godot.log`;
 *  rotated logs are `godot.log.1`, `godot.log.2`, … up to `max_log_files`.
 *  We only consider files matching this strict prefix + numeric suffix. */
const ROTATED_RE = /^godot\.log\.(\d+)$/;

/** Selection result for the current-vs-rotated decision. */
export type SelectedLogKind = "current" | "rotated";

export interface SelectLogResult {
  /** Absolute path to the log we will read. */
  path: string;
  /** Whether we picked the current log or a rotated one. */
  kind: SelectedLogKind;
  /** True when the current log was missing/empty and a rotated one was used. */
  usedRotatedFallback: boolean;
  /** True when the file logging is disabled (no log expected to exist). */
  loggingDisabled: boolean;
  /** True when neither the current nor any rotated log exists. */
  notFound: boolean;
}

/**
 * Pick the authoritative log to read for compile-error extraction.
 *
 *   1. Prefer the current `godot.log` when it exists and is non-empty.
 *   2. If the current log is missing/empty AND `includeRotated`, inspect the
 *      configured log directory for rotated candidates (`godot.log.N`). Select
 *      the newest by mtime with a strict `godot.log.<digits>` filename policy.
 *      Never recursively scan outside the log directory.
 *   3. `maxLogFiles` bounds how many rotated candidates we consider (Godot
 *      itself caps rotation at this number; reading more would surface files
 *      Godot has already discarded).
 *
 * `loggingDisabled` is surfaced so the caller can emit `logging_disabled`
 * rather than `log_not_found` when file logging is off — the missing file is
 * expected in that case, not a failure. Never throws.
 */
export function selectLog(
  currentLogPath: string,
  logDir: string,
  opts: {
    includeRotated: boolean;
    maxLogFiles: number;
    loggingDisabled: boolean;
  },
): SelectLogResult {
  // Current log first.
  try {
    if (existsSync(currentLogPath)) {
      const st = statSync(currentLogPath);
      if (st.isFile() && st.size > 0) {
        return {
          path: currentLogPath,
          kind: "current",
          usedRotatedFallback: false,
          loggingDisabled: opts.loggingDisabled,
          notFound: false,
        };
      }
    }
  } catch {
    // Fall through to rotated scan; a stat failure is treated as "not usable".
  }

  if (!opts.includeRotated) {
    return {
      path: currentLogPath,
      kind: "current",
      usedRotatedFallback: false,
      loggingDisabled: opts.loggingDisabled,
      notFound: true,
    };
  }

  // Rotated candidates: strict `godot.log.<digits>` policy in the log dir.
  let candidates: { path: string; mtime: number }[] = [];
  try {
    const entries = readdirSync(logDir);
    for (const name of entries) {
      const m = ROTATED_RE.exec(name);
      if (!m) continue;
      const num = parseInt(m[1], 10);
      if (!Number.isFinite(num) || num < 1) continue;
      if (num > opts.maxLogFiles) continue;
      const abs = join(logDir, name);
      try {
        const st = statSync(abs);
        if (st.isFile() && st.size > 0) {
          candidates.push({ path: abs, mtime: st.mtimeMs });
        }
      } catch {
        // skip unreadable rotated candidates
      }
    }
  } catch {
    candidates = [];
  }

  if (candidates.length === 0) {
    return {
      path: currentLogPath,
      kind: "current",
      usedRotatedFallback: false,
      loggingDisabled: opts.loggingDisabled,
      notFound: true,
    };
  }

  // Newest by mtime.
  candidates.sort((a, b) => b.mtime - a.mtime);
  return {
    path: candidates[0].path,
    kind: "rotated",
    usedRotatedFallback: true,
    loggingDisabled: opts.loggingDisabled,
    notFound: false,
  };
}

// ---------------------------------------------------------------------------
// Stale-log detection (adapted from Unity's unity-log.ts).
// ---------------------------------------------------------------------------

export interface StaleLogResult {
  /** True when at least one cited source file is newer than the selected log. */
  staleLogSuspected: boolean;
  /** Selected log mtime in epoch ms, when readable. */
  logMtimeMs?: number;
  /**
   * Cited source files (project-relative) whose mtime is newer than the log.
   * Bounded — the agent does not need every offender, just enough to confirm
   * the diagnosis and start a recompile.
   */
  newerFiles: string[];
  /** One-line agent-facing recovery hint. Empty when not stale. */
  hint: string;
}

/** Upper bound on the number of newer files reported. The list is evidence,
 *  not an exhaustive roster — keeping it small avoids a giant payload when many
 *  files were touched together. */
const MAX_NEWER_FILES = 5;

/**
 * Decide whether the selected log at `logPath` is likely stale relative to the
 * on-disk source files cited by the parsed diagnostics.
 *
 * Conservative — only flags staleness when ALL of:
 *   - the log file exists and has a readable mtime,
 *   - at least one cited source file resolves under `projectRoot` and exists,
 *   - that source file's mtime is strictly newer than the log's mtime.
 *
 * Returns `staleLogSuspected: false` (no hint) whenever the comparison cannot
 * be made. Never throws. Excludes paths outside the project from mtime probes.
 *
 * `citedFiles` may carry Godot asset locators (`res://Foo.gd:12`) or bare paths
 * (`res://Foo.gd`); this helper strips a trailing `:line`/`:line,col` locator.
 */
export function detectStaleLog(
  logPath: string,
  citedFiles: ReadonlyArray<string>,
  projectRoot: string | null | undefined,
): StaleLogResult {
  const empty: StaleLogResult = {
    staleLogSuspected: false,
    newerFiles: [],
    hint: "",
  };
  if (!projectRoot) return empty;

  let logMtimeMs: number | undefined;
  try {
    if (!existsSync(logPath)) return empty;
    logMtimeMs = statSync(logPath).mtimeMs;
  } catch {
    return empty;
  }
  if (logMtimeMs === undefined) return empty;

  const newerFiles: string[] = [];
  for (const raw of citedFiles) {
    if (newerFiles.length >= MAX_NEWER_FILES) break;
    if (!raw) continue;
    // Normalize to a `res://`-style relative path and strip a trailing
    // `:line` / `:line,col` locator.
    const rel = stripResAndLocator(raw);
    if (rel === null || rel === "") continue;
    // Only inspect paths that resolve inside the project root. Logs sometimes
    // cite files under .godot/ or a package cache; their mtimes are not under
    // the agent's control and would produce noise.
    const abs = join(projectRoot, rel);
    if (!abs.startsWith(projectRoot + sep) && abs !== projectRoot) continue;
    let fileMtimeMs: number;
    try {
      if (!existsSync(abs)) continue;
      fileMtimeMs = statSync(abs).mtimeMs;
    } catch {
      continue;
    }
    // Strictly newer: an equal mtime means the log was rewritten at the same
    // instant the file was saved (a fresh compile just finished writing both)
    // — that is the OPPOSITE of stale, so the > keeps it out of the flag.
    if (fileMtimeMs > logMtimeMs) {
      newerFiles.push(rel);
    }
  }

  if (newerFiles.length === 0) {
    return { staleLogSuspected: false, logMtimeMs, newerFiles: [], hint: "" };
  }
  return {
    staleLogSuspected: true,
    logMtimeMs,
    newerFiles,
    hint:
      "Godot log appears stale — at least one cited source file was edited " +
      "after the log's most recent write (Godot did not re-run a compile that " +
      "rewrote the error block, so the diagnostics may no longer apply). " +
      "Force a fresh Godot editor reload / recompile before trusting these " +
      "errors: open the project in Godot, or trigger a script reload, then " +
      "call godot_open_mcp_read_compile_errors again.",
  };
}

/** Strip a leading `res://` (and `user://` defensively) and a trailing
 *  `:line` / `:line,col` locator from a cited path. Returns the project-
 *  relative path, or `null` when the input has an unsupported scheme. */
function stripResAndLocator(cited: string): string | null {
  let s = cited.trim();
  if (s.startsWith(USER_PREFIX)) {
    s = s.slice(USER_PREFIX.length);
  } else if (s.startsWith("res://")) {
    s = s.slice("res://".length);
  } else if (s.startsWith("/")) {
    // An absolute native path — only accept it when it is inside the project
    // root (the caller's join check enforces this). Keep it as-is.
  } else if (s === "") {
    return null;
  }
  // Strip a trailing `:line` or `:line,col` Godot locator. A path segment may
  // legitimately contain a colon on Windows (`C:\…`), so only strip when the
  // colon follows a path-like suffix (`.gd`, `.cs`, `.tscn`, `.tres`).
  const colonIdx = s.lastIndexOf(":");
  if (colonIdx > 0) {
    const before = s.slice(0, colonIdx);
    if (/\.(gd|cs|tscn|tres|gdshader|csproj)$/i.test(before)) {
      s = before;
    }
  }
  return s;
}
