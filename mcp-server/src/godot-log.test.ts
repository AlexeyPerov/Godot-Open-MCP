// Godot project-log path resolution + bounded tail + rotated-log fallback +
// stale-log detection tests (P7.4).
//
// Pins the offline log-reader contract that backs
// `godot_open_mcp_read_compile_errors`:
//   - `parseProjectLogSettings` parses the `debug/file_logging/*` +
//     `application/config/*` subset with documented defaults.
//   - `godotUserDataRoot` resolves the per-platform user-data dir (macOS /
//     Linux / Windows + custom-user-dir override).
//   - `resolveLogPaths` honors the env override → project setting → default
//     precedence.
//   - `readLogTail` reads a bounded tail, drops a partial first line, and
//     normalizes CRLF.
//   - `selectLog` prefers the current log and falls back to the newest rotated
//     candidate by mtime with a strict filename policy.
//   - `detectStaleLog` flags staleness only when a cited source file is newer
//     than the log, ignoring files outside the project.
//
// Adapted from Unity Open MCP's `mcp-server/src/unity-log.test.ts` (copy
// fidelity for the bounded tail, not-found, unreadable, and stale-log patterns
// — the Godot deltas are the project-settings parser + platform user-data-dir
// resolver + rotated-log selection, all of which are new).
//
// Built + run via the project test config:
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  mkdtempSync,
  mkdirSync,
  writeFileSync,
  utimesSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join, sep } from "node:path";

import {
  parseProjectLogSettings,
  godotUserDataRoot,
  osDataBaseDir,
  resolveLogPaths,
  readLogTail,
  selectLog,
  detectStaleLog,
  DEFAULT_LOG_TAIL_BYTES,
  MIN_LOG_TAIL_BYTES,
  MAX_LOG_TAIL_BYTES,
  DEFAULT_MAX_LOG_FILES,
  DEFAULT_PROJECT_NAME,
  LOG_FILE_ENV_OVERRIDE,
  type GodotProjectLogSettings,
  type GodotLogPlatform,
} from "./godot-log.js";

// ---------------------------------------------------------------------------
// parseProjectLogSettings
// ---------------------------------------------------------------------------

const DEFAULTS: GodotProjectLogSettings = {
  projectName: DEFAULT_PROJECT_NAME,
  useCustomUserDir: false,
  customUserDirName: "",
  fileLoggingEnabled: false,
  logPath: null,
  maxLogFiles: DEFAULT_MAX_LOG_FILES,
};

function projectGodot(extra: string): string {
  return `config_version=5\n\n[application]\nconfig/name="My Game"\n${extra}\n`;
}

test("parseProjectLogSettings: returns documented defaults for an empty file", () => {
  assert.deepEqual(parseProjectLogSettings(""), { ...DEFAULTS });
});

test("parseProjectLogSettings: parses application/config/name", () => {
  const s = parseProjectLogSettings(
    `[application]\nconfig/name="Cool Game"\n`,
  );
  assert.equal(s.projectName, "Cool Game");
});

test("parseProjectLogSettings: parses debug/file_logging/enable_file_logging = true", () => {
  const s = parseProjectLogSettings(
    projectGodot(`[debug]\n[file_logging]\nenable_file_logging=true\n`),
  );
  assert.equal(s.fileLoggingEnabled, true);
});

test("parseProjectLogSettings: enable_file_logging defaults to false (Godot default)", () => {
  const s = parseProjectLogSettings(projectGodot(``));
  assert.equal(s.fileLoggingEnabled, false);
});

test("parseProjectLogSettings: parses debug/file_logging/log_path", () => {
  const s = parseProjectLogSettings(
    projectGodot(
      `[debug]\n[file_logging]\nlog_path="user://custom.log"\n`,
    ),
  );
  assert.equal(s.logPath, "user://custom.log");
});

test("parseProjectLogSettings: parses debug/file_logging/max_log_files", () => {
  const s = parseProjectLogSettings(
    projectGodot(`[debug]\n[file_logging]\nmax_log_files=10\n`),
  );
  assert.equal(s.maxLogFiles, 10);
});

test("parseProjectLogSettings: parses use_custom_user_dir + custom_user_dir_name", () => {
  const s = parseProjectLogSettings(
    projectGodot(
      `use_custom_user_dir=true\ncustom_user_dir_name="MyCompany/MyGame"\n`,
    ),
  );
  assert.equal(s.useCustomUserDir, true);
  assert.equal(s.customUserDirName, "MyCompany/MyGame");
});

test("parseProjectLogSettings: ignores comments + unknown sections", () => {
  const s = parseProjectLogSettings(
    `; a comment\n# another\n[unknown]\nfoo=bar\n[application]\nconfig/name="X"\n`,
  );
  assert.equal(s.projectName, "X");
});

// ---------------------------------------------------------------------------
// osDataBaseDir / godotUserDataRoot
// ---------------------------------------------------------------------------

test("osDataBaseDir: macOS resolves ~/Library/Application Support", () => {
  // We cannot assert the full path (homedir varies), only the suffix.
  const dir = osDataBaseDir("darwin", {});
  assert.ok(dir.endsWith(join("Library", "Application Support")), dir);
});

test("osDataBaseDir: Linux honors XDG_DATA_HOME", () => {
  const dir = osDataBaseDir("linux", { XDG_DATA_HOME: "/custom/xdg" });
  assert.equal(dir, "/custom/xdg");
});

test("osDataBaseDir: Linux falls back to ~/.local/share when XDG_DATA_HOME unset", () => {
  const dir = osDataBaseDir("linux", {});
  assert.ok(dir.endsWith(join(".local", "share")), dir);
});

test("osDataBaseDir: Windows honors APPDATA", () => {
  const dir = osDataBaseDir("win32", { APPDATA: "C:\\Users\\u\\AppData\\Roaming" });
  assert.equal(dir, "C:\\Users\\u\\AppData\\Roaming");
});

test("godotUserDataRoot: macOS default app_userdata/<name>", () => {
  const settings: GodotProjectLogSettings = {
    ...DEFAULTS,
    projectName: "MyGame",
  };
  const root = godotUserDataRoot(settings, "darwin", {});
  assert.ok(
    root.endsWith(join("Library", "Application Support", "Godot", "app_userdata", "MyGame") + sep),
    root,
  );
  assert.ok(root.endsWith(sep), "root must end with a separator for clean joins");
});

test("godotUserDataRoot: Linux default app_userdata/<name>", () => {
  const settings: GodotProjectLogSettings = {
    ...DEFAULTS,
    projectName: "MyGame",
  };
  const root = godotUserDataRoot(settings, "linux", { XDG_DATA_HOME: "/xdg" });
  assert.equal(root, join("/xdg", "godot", "app_userdata", "MyGame") + sep);
});

test("godotUserDataRoot: Windows default app_userdata/<name>", () => {
  const settings: GodotProjectLogSettings = {
    ...DEFAULTS,
    projectName: "MyGame",
  };
  const root = godotUserDataRoot(settings, "win32", {
    APPDATA: "C:\\Users\\u\\AppData\\Roaming",
  });
  assert.equal(
    root,
    join("C:\\Users\\u\\AppData\\Roaming", "Godot", "app_userdata", "MyGame") + sep,
  );
});

test("godotUserDataRoot: use_custom_user_dir drops Godot/app_userdata", () => {
  const settings: GodotProjectLogSettings = {
    ...DEFAULTS,
    projectName: "MyGame",
    useCustomUserDir: true,
    customUserDirName: "MyCompany/MyGame",
  };
  const root = godotUserDataRoot(settings, "darwin", {});
  assert.ok(
    root.endsWith(join("Application Support", "MyCompany", "MyGame") + sep),
    root,
  );
  assert.ok(
    !root.includes("app_userdata"),
    "custom user dir must drop the app_userdata segment",
  );
});

test("godotUserDataRoot: use_custom_user_dir with empty custom name falls back to project name", () => {
  const settings: GodotProjectLogSettings = {
    ...DEFAULTS,
    projectName: "MyGame",
    useCustomUserDir: true,
    customUserDirName: "",
  };
  const root = godotUserDataRoot(settings, "darwin", {});
  assert.ok(root.endsWith(join("Application Support", "MyGame") + sep), root);
});

// ---------------------------------------------------------------------------
// resolveLogPaths
// ---------------------------------------------------------------------------

test("resolveLogPaths: env override wins over project setting + default", () => {
  const settings: GodotProjectLogSettings = {
    ...DEFAULTS,
    logPath: "user://project.log",
  };
  const resolved = resolveLogPaths(settings, "/userdata", {
    [LOG_FILE_ENV_OVERRIDE]: "/custom/path/godot.log",
  });
  assert.equal(resolved.currentLogPath, "/custom/path/godot.log");
  assert.equal(resolved.source, "env_override");
  assert.equal(resolved.logDir, "/custom/path");
});

test("resolveLogPaths: project setting wins when no env override", () => {
  const settings: GodotProjectLogSettings = {
    ...DEFAULTS,
    logPath: "user://custom.log",
  };
  const resolved = resolveLogPaths(settings, "/userdata", {});
  assert.equal(resolved.currentLogPath, join("/userdata", "custom.log"));
  assert.equal(resolved.source, "project_setting");
});

test("resolveLogPaths: absolute project setting is honored as-is", () => {
  const settings: GodotProjectLogSettings = {
    ...DEFAULTS,
    logPath: "/abs/path/godot.log",
  };
  const resolved = resolveLogPaths(settings, "/userdata", {});
  assert.equal(resolved.currentLogPath, "/abs/path/godot.log");
  assert.equal(resolved.source, "project_setting");
});

test("resolveLogPaths: default user://logs/godot.log when no env + no setting", () => {
  const resolved = resolveLogPaths(DEFAULTS, "/userdata", {});
  assert.equal(resolved.currentLogPath, join("/userdata", "logs", "godot.log"));
  assert.equal(resolved.source, "default");
  assert.equal(resolved.logDir, join("/userdata", "logs"));
});

test("resolveLogPaths: empty env override is ignored", () => {
  const resolved = resolveLogPaths(DEFAULTS, "/userdata", {
    [LOG_FILE_ENV_OVERRIDE]: "   ",
  });
  assert.equal(resolved.source, "default");
});

// ---------------------------------------------------------------------------
// readLogTail
// ---------------------------------------------------------------------------

test("readLogTail: returns exists:false when the file is absent", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const result = readLogTail(join(dir, "nope.log"));
  assert.equal(result.exists, false);
  assert.equal(result.content, "");
  assert.equal(result.bytes, 0);
  assert.equal(result.truncated, false);
  assert.equal(result.error, undefined);
});

test("readLogTail: reads a small file in full", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const path = join(dir, "godot.log");
  const content = "hello godot log\n";
  writeFileSync(path, content);

  const result = readLogTail(path);
  assert.equal(result.exists, true);
  assert.equal(result.content, content);
  assert.equal(result.bytes, Buffer.byteLength(content));
  assert.equal(result.truncated, false);
  assert.ok(result.mtimeMs !== undefined);
});

test("readLogTail: returns only the last maxBytes of a large file", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const path = join(dir, "godot.log");
  const head = "A".repeat(3 * 1024);
  const tail = "B".repeat(1024);
  writeFileSync(path, head + tail);

  const result = readLogTail(path, 1024);
  assert.equal(result.exists, true);
  assert.equal(result.bytes, 1024);
  assert.equal(result.content, tail);
  assert.equal(result.truncated, true);
});

test("readLogTail: caps at DEFAULT_LOG_TAIL_BYTES by default", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const path = join(dir, "godot.log");
  const huge = "X".repeat(DEFAULT_LOG_TAIL_BYTES + 4096);
  writeFileSync(path, huge);

  const result = readLogTail(path);
  assert.equal(result.bytes, DEFAULT_LOG_TAIL_BYTES);
});

test("readLogTail: handles a file smaller than maxBytes", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const path = join(dir, "godot.log");
  writeFileSync(path, "tiny");
  const result = readLogTail(path, 1024 * 1024);
  assert.equal(result.content, "tiny");
  assert.equal(result.bytes, 4);
  assert.equal(result.truncated, false);
});

test("readLogTail: drops the partial first line when starting mid-file", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const path = join(dir, "godot.log");
  // The tail will start mid-first-line; the partial first line must be dropped.
  const head = "X".repeat(100);
  const tail = "\nsecond line\nthird line\n";
  writeFileSync(path, head + tail);

  const result = readLogTail(path, 50);
  assert.equal(result.truncated, true);
  assert.ok(!result.content.includes("X"), "partial first line dropped");
  assert.ok(result.content.includes("second line"));
  assert.ok(result.content.includes("third line"));
});

test("readLogTail: normalizes CRLF to LF", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const path = join(dir, "godot.log");
  writeFileSync(path, "line1\r\nline2\r\n");

  const result = readLogTail(path);
  assert.ok(!result.content.includes("\r\n"), "CRLF normalized to LF");
  assert.ok(result.content.includes("line1\nline2\n"));
});

// ---------------------------------------------------------------------------
// selectLog — current vs rotated
// ---------------------------------------------------------------------------

test("selectLog: prefers the current log when it exists and is non-empty", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const current = join(dir, "godot.log");
  writeFileSync(current, "current log content");

  const result = selectLog(current, dir, {
    includeRotated: true,
    maxLogFiles: 5,
    loggingDisabled: false,
  });
  assert.equal(result.kind, "current");
  assert.equal(result.usedRotatedFallback, false);
  assert.equal(result.notFound, false);
  assert.equal(result.path, current);
});

test("selectLog: falls back to the newest rotated log by mtime", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const current = join(dir, "godot.log"); // missing
  const r1 = join(dir, "godot.log.1");
  const r2 = join(dir, "godot.log.2");
  writeFileSync(r1, "older rotated");
  writeFileSync(r2, "newer rotated");
  setMtime(r1, 1000);
  setMtime(r2, 2000);

  const result = selectLog(current, dir, {
    includeRotated: true,
    maxLogFiles: 5,
    loggingDisabled: false,
  });
  assert.equal(result.kind, "rotated");
  assert.equal(result.usedRotatedFallback, true);
  assert.equal(result.path, r2, "newest rotated by mtime wins");
});

test("selectLog: notFound when current missing and no rotated candidates", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const current = join(dir, "godot.log");

  const result = selectLog(current, dir, {
    includeRotated: true,
    maxLogFiles: 5,
    loggingDisabled: false,
  });
  assert.equal(result.notFound, true);
  assert.equal(result.usedRotatedFallback, false);
});

test("selectLog: loggingDisabled surfaces when no log + file logging off", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const current = join(dir, "godot.log");

  const result = selectLog(current, dir, {
    includeRotated: true,
    maxLogFiles: 5,
    loggingDisabled: true,
  });
  // notFound is true (no log exists); the caller uses loggingDisabled to pick
  // the `logging_disabled` status rather than `log_not_found`.
  assert.equal(result.notFound, true);
  assert.equal(result.loggingDisabled, true);
});

test("selectLog: ignores rotated files beyond max_log_files", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const current = join(dir, "godot.log"); // missing
  // godot.log.10 is beyond the cap of 5; it must be ignored.
  const r10 = join(dir, "godot.log.10");
  writeFileSync(r10, "out of cap");

  const result = selectLog(current, dir, {
    includeRotated: true,
    maxLogFiles: 5,
    loggingDisabled: false,
  });
  assert.equal(result.notFound, true, "out-of-cap rotated file ignored");
});

test("selectLog: strict filename policy ignores non-matching files", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const current = join(dir, "godot.log"); // missing
  writeFileSync(join(dir, "godot.log.backup"), "wrong suffix");
  writeFileSync(join(dir, "other.log.1"), "wrong prefix");

  const result = selectLog(current, dir, {
    includeRotated: true,
    maxLogFiles: 5,
    loggingDisabled: false,
  });
  assert.equal(result.notFound, true, "non-matching files ignored");
});

test("selectLog: includeRotated=false skips the rotated scan", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const current = join(dir, "godot.log"); // missing
  writeFileSync(join(dir, "godot.log.1"), "rotated");

  const result = selectLog(current, dir, {
    includeRotated: false,
    maxLogFiles: 5,
    loggingDisabled: false,
  });
  assert.equal(result.notFound, true);
  assert.equal(result.usedRotatedFallback, false);
});

test("selectLog: empty current log falls back to rotated", () => {
  const dir = mkdtempSync(join(tmpdir(), "gl-"));
  const current = join(dir, "godot.log");
  writeFileSync(current, ""); // empty
  const r1 = join(dir, "godot.log.1");
  writeFileSync(r1, "rotated content");

  const result = selectLog(current, dir, {
    includeRotated: true,
    maxLogFiles: 5,
    loggingDisabled: false,
  });
  assert.equal(result.kind, "rotated");
  assert.equal(result.path, r1);
});

// ---------------------------------------------------------------------------
// detectStaleLog
// ---------------------------------------------------------------------------

function setMtime(path: string, epochSeconds: number): void {
  const t = new Date(epochSeconds * 1000);
  utimesSync(path, t, t);
}

test("detectStaleLog: flags staleness when a cited res:// source file is newer than the log", () => {
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "logs"));
  mkdirSync(join(project, "scripts"), { recursive: true });
  const logPath = join(project, "logs", "godot.log");
  const srcPath = join(project, "scripts", "player.gd");
  writeFileSync(logPath, "log content");
  writeFileSync(srcPath, "extends Node");
  setMtime(logPath, 1000);
  setMtime(srcPath, 2000);

  const result = detectStaleLog(logPath, ["res://scripts/player.gd"], project);
  assert.equal(result.staleLogSuspected, true);
  assert.equal(result.newerFiles.length, 1);
  assert.equal(result.newerFiles[0], "scripts/player.gd");
  assert.ok(result.hint.length > 0);
});

test("detectStaleLog: strips trailing :line locator from cited paths", () => {
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "logs"));
  mkdirSync(join(project, "scripts"), { recursive: true });
  const logPath = join(project, "logs", "godot.log");
  const srcPath = join(project, "scripts", "player.gd");
  writeFileSync(logPath, "log content");
  writeFileSync(srcPath, "extends Node");
  setMtime(logPath, 1000);
  setMtime(srcPath, 2000);

  const result = detectStaleLog(logPath, ["res://scripts/player.gd:42"], project);
  assert.equal(result.staleLogSuspected, true);
  assert.equal(result.newerFiles[0], "scripts/player.gd");
});

test("detectStaleLog: returns not-stale when the log is newer than every cited source", () => {
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "logs"));
  mkdirSync(join(project, "scripts"), { recursive: true });
  const logPath = join(project, "logs", "godot.log");
  const srcPath = join(project, "scripts", "player.gd");
  writeFileSync(logPath, "fresh log");
  writeFileSync(srcPath, "extends Node");
  setMtime(srcPath, 1000);
  setMtime(logPath, 2000);

  const result = detectStaleLog(logPath, ["res://scripts/player.gd"], project);
  assert.equal(result.staleLogSuspected, false);
  assert.equal(result.newerFiles.length, 0);
  assert.equal(result.hint, "");
});

test("detectStaleLog: equal mtimes are NOT stale", () => {
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "logs"));
  mkdirSync(join(project, "scripts"), { recursive: true });
  const logPath = join(project, "logs", "godot.log");
  const srcPath = join(project, "scripts", "player.gd");
  writeFileSync(logPath, "x");
  writeFileSync(srcPath, "x");
  setMtime(logPath, 1500);
  setMtime(srcPath, 1500);

  const result = detectStaleLog(logPath, ["res://scripts/player.gd"], project);
  assert.equal(result.staleLogSuspected, false);
});

test("detectStaleLog: ignores cited files that don't exist on disk", () => {
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "logs"));
  const logPath = join(project, "logs", "godot.log");
  writeFileSync(logPath, "x");
  setMtime(logPath, 1000);

  const result = detectStaleLog(logPath, ["res://scripts/gone.gd"], project);
  assert.equal(result.staleLogSuspected, false);
});

test("detectStaleLog: ignores paths that resolve outside the project root", () => {
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "logs"));
  const logPath = join(project, "logs", "godot.log");
  writeFileSync(logPath, "x");
  setMtime(logPath, 1000);

  const result = detectStaleLog(
    logPath,
    ["/etc/passwd", "../outside.gd"],
    project,
  );
  assert.equal(result.staleLogSuspected, false);
});

test("detectStaleLog: returns not-stale when no project root supplied", () => {
  const result = detectStaleLog("/dev/null/godot.log", ["res://x.gd"], null);
  assert.equal(result.staleLogSuspected, false);
});

test("detectStaleLog: returns not-stale when the log doesn't exist", () => {
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  const result = detectStaleLog(
    join(project, "logs", "missing.log"),
    ["res://x.gd"],
    project,
  );
  assert.equal(result.staleLogSuspected, false);
});

test("detectStaleLog: bounds newerFiles at 5 entries", () => {
  const project = mkdtempSync(join(tmpdir(), "proj-"));
  mkdirSync(join(project, "logs"));
  mkdirSync(join(project, "scripts"), { recursive: true });
  const logPath = join(project, "logs", "godot.log");
  writeFileSync(logPath, "x");
  setMtime(logPath, 1000);
  const cited: string[] = [];
  for (let i = 0; i < 10; i++) {
    const rel = `scripts/file${i}.gd`;
    writeFileSync(join(project, rel), "x");
    setMtime(join(project, rel), 2000 + i);
    cited.push(`res://${rel}`);
  }

  const result = detectStaleLog(logPath, cited, project);
  assert.equal(result.staleLogSuspected, true);
  assert.ok(result.newerFiles.length <= 5, "newerFiles must be bounded");
});

// ---------------------------------------------------------------------------
// Constants sanity
// ---------------------------------------------------------------------------

test("DEFAULT_LOG_TAIL_BYTES is 256 KiB", () => {
  assert.equal(DEFAULT_LOG_TAIL_BYTES, 256 * 1024);
});

test("MIN/MAX_LOG_TAIL_BYTES bounds match the schema", () => {
  assert.equal(MIN_LOG_TAIL_BYTES, 4096);
  assert.equal(MAX_LOG_TAIL_BYTES, 1024 * 1024);
});
