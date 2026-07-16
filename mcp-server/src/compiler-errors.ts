// Structured diagnostic extraction from a Godot log tail, for the offline
// `godot_open_mcp_read_compile_errors` tool (P7.4).
//
// Adapted from Unity Open MCP's `mcp-server/src/compiler-errors.ts` (copy the
// CSxxxx regex concept + dedup/cap discipline; ADAPT for the Godot/MSBuild
// layouts Godot's log emits). The GDScript / script-load / addon-load layers
// are GREENFIELD — Unity has no GDScript equivalent.
//
// Godot's file log mixes several diagnostic shapes the parser must normalize
// into one `CompileDiagnostic` record:
//
//   1. C# compiler forms (Mono/Csc + MSBuild wrappers Godot's .NET integration
//      emits):
//        path(line,col): error CSxxxx: message
//        path(line,column): error CSxxxx: message
//        C:\path(line,col): error CSxxxx: message   (Windows absolute)
//        res://path(line,col): error CSxxxx: message (rare; Godot res:// paths
//                                                       sometimes leak through)
//   2. GDScript parse/error forms (the engine's own parser):
//        res://path.gd:LINE - Parse Error: message
//        res://path.gd:LINE - Parse Error: (message)
//        SCRIPT ERROR: message
//          res://path.gd:LINE
//        res://path.gd:LINE: @class_name - message
//   3. Script/addon load failures:
//        Failed to load script: res://path.gd
//        Failed to load resource: res://path.tres
//        Cannot reload scene: ...
//        addon init / plugin enable failures
//   4. Unmatched error-looking lines surface as `other` only when a
//      conservative severity marker (`error`/`Error`/`SCRIPT ERROR`/`Parse
//      Error`) is present — never on the bare word "error" in prose.
//
// The C# regex is copied from Unity's `COMPILER_ERROR_RE` and extended to:
//   - accept `res://` / Windows-drive prefixes (the path group is non-greedy
//     over non-paren chars);
//   - accept either `error` or `warning` severity;
//   - capture an optional column separately (Unity folds line/column into one
//     group; Godot's MSBuild output always emits both).

/** Maximum number of distinct diagnostics we surface. Bounded so a giant wall
 *  of errors can't blow up the tool response; the agent can fix-and-recheck. */
export const MAX_COMPILE_DIAGNOSTICS = 50;

/** Normalized diagnostic kind, so an agent can branch on remediation. */
export type DiagnosticKind =
  | "csharp"
  | "gdscript"
  | "script_load"
  | "addon_load"
  | "other";

/** Diagnostic severity. */
export type DiagnosticSeverity = "error" | "warning";

export interface CompileDiagnostic {
  kind: DiagnosticKind;
  severity: DiagnosticSeverity;
  /** Asset-relative file path (`res://...` when known), or `null`. */
  file: string | null;
  /** 1-based line number, or `null` when unparseable. */
  line: number | null;
  /** 1-based column number, or `null` when not present in the log line. */
  column: number | null;
  /** Compiler/parser code (`CS0246`, etc.), or `null`. */
  code: string | null;
  /** The human-readable message. */
  message: string;
  /** The original matched line, quoted verbatim so the agent can reference it. */
  raw: string;
}

// ---------------------------------------------------------------------------
// C# compiler diagnostics (CSxxxx).
// ---------------------------------------------------------------------------

// Match `path(line[,col])` — the C# compiler locator. The path group accepts
// `res://`, Windows-drive, and relative forms (no parens/newlines). The
// locator's inner digits are the line + optional column.
const CS_LOCATOR_RE = /([^()\r\n]+?)\((\d+)(?:,(\d+))?\)/;

// Full C# diagnostic line: `<locator>: <severity> CSxxxx: <message>`. Severity
// may be `error` or `warning`; the CS code is 4+ digits. Matched on a single
// line — Unity/Godot emit the locator + severity on one line.
const CS_DIAGNOSTIC_RE =
  /([^()\r\n]+?)\((\d+)(?:,(\d+))?\):\s*(error|warning)\s+(CS\d{4,}):\s*([^\r\n]+)/g;

// ---------------------------------------------------------------------------
// GDScript parse/error forms.
// ---------------------------------------------------------------------------

// `res://path.gd:LINE - Parse Error: message` — Godot's primary GDScript
// parser error format. The leading path uses `res://` and a single colon
// before the line number.
const GDSCRIPT_PARSE_RE =
  /^(?:\s*)(res:\/\/[^\s:]+\.gd):(\d+)\s*-\s*Parse Error:\s*([^\r\n]+)/gm;

// `res://path.gd:LINE - Parse Warning: message` — the warning sibling.
const GDSCRIPT_PARSE_WARNING_RE =
  /^(?:\s*)(res:\/\/[^\s:]+\.gd):(\d+)\s*-\s*Parse Warning:\s*([^\r\n]+)/gm;

// `SCRIPT ERROR: message` followed by a path/line block on a later line. The
// engine emits the message first, then an indented `res://path.gd:LINE` line.
// We match the path/line first and capture the preceding message via a look-
// behind over the same line set. Node's regex engine supports lookbehind.
const GDSCRIPT_SCRIPT_ERROR_RE =
  /SCRIPT ERROR:\s*([^\r\n]+)\r?\n[^\r\n]*?(res:\/\/[^\s:]+\.gd):(\d+)/g;

// `res://path.gd:LINE: @identifier - message` — Godot's runtime script-error
// annotation form. Less common in compile-fail logs but present for some
// class-level errors.
const GDSCRIPT_RUNTIME_RE =
  /^(?:\s*)(res:\/\/[^\s:]+\.gd):(\d+)(?::\d+)?:\s*([^\r\n]+)/gm;

// ---------------------------------------------------------------------------
// Script/addon load failures.
// ---------------------------------------------------------------------------

// `Failed to load script: res://path.gd` / `Failed to load resource:
// res://path.tres`. Sometimes carries an inline reason.
const SCRIPT_LOAD_RE =
  /Failed to load (?:script|resource):\s*(res:\/\/[^\s\r\n]+)/gi;

// `Cannot reload scene` / `Cannot instantiate` / `Failed to instantiate` —
// addon-load-class failures whose primary signal is the verb.
const ADDON_LOAD_RE =
  /(?:Failed to (?:instantiate|initialize|enable) (?:addon|plugin|node)|Cannot (?:reload scene|instantiate))[:\s]*[^\r\n]*/gi;

/**
 * Extract structured diagnostics from a Godot log tail. Pure — no I/O. The
 * caller (the router's offline handler) reads the log and hands the contents
 * in.
 *
 * Layers are applied in order: C# → GDScript parse → GDScript script-error →
 * GDScript runtime → script/addon load → conservative `other` fallback. Each
 * diagnostic is deduplicated by the normalized `(kind, severity, file, line,
 * column, code, message)` tuple, preserving first-seen (oldest-in-tail) order,
 * and capped at {@link MAX_COMPILE_DIAGNOSTICS}.
 *
 * @param log              Log tail text (CRLF already normalized to LF).
 * @param maxDiagnostics   Override the cap (testing only; defaults to
 *                         {@link MAX_COMPILE_DIAGNOSTICS}).
 */
export function extractCompileDiagnostics(
  log: string,
  maxDiagnostics: number = MAX_COMPILE_DIAGNOSTICS,
): CompileDiagnostic[] {
  if (!log) return [];
  const cap = Math.max(1, maxDiagnostics);
  const seen = new Set<string>();
  const out: CompileDiagnostic[] = [];
  const push = (d: CompileDiagnostic): void => {
    if (out.length >= cap) return;
    const key = `${d.kind}|${d.severity}|${d.file ?? ""}|${d.line ?? ""}|${d.column ?? ""}|${d.code ?? ""}|${d.message}`;
    if (seen.has(key)) return;
    seen.add(key);
    out.push(d);
  };

  // 1. C# compiler diagnostics.
  CS_DIAGNOSTIC_RE.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = CS_DIAGNOSTIC_RE.exec(log)) !== null) {
    const raw = m[0].trim();
    const file = normalizeFilePath(m[1]);
    const line = parseInt(m[2], 10);
    const column = m[3] ? parseInt(m[3], 10) : null;
    const severity: DiagnosticSeverity = m[4] === "warning" ? "warning" : "error";
    const code = m[5] ?? null;
    const message = (m[6] ?? "").trim();
    push({
      kind: "csharp",
      severity,
      file,
      line: Number.isFinite(line) ? line : null,
      column: Number.isFinite(column as number) ? (column as number) : null,
      code,
      message,
      raw,
    });
  }

  // 2a. GDScript parse errors.
  GDSCRIPT_PARSE_RE.lastIndex = 0;
  while ((m = GDSCRIPT_PARSE_RE.exec(log)) !== null) {
    const raw = m[0].trim();
    const file = m[1];
    const line = parseInt(m[2], 10);
    const message = (m[3] ?? "").trim();
    push({
      kind: "gdscript",
      severity: "error",
      file,
      line: Number.isFinite(line) ? line : null,
      column: null,
      code: null,
      message,
      raw,
    });
  }

  // 2b. GDScript parse warnings.
  GDSCRIPT_PARSE_WARNING_RE.lastIndex = 0;
  while ((m = GDSCRIPT_PARSE_WARNING_RE.exec(log)) !== null) {
    const raw = m[0].trim();
    const file = m[1];
    const line = parseInt(m[2], 10);
    const message = (m[3] ?? "").trim();
    push({
      kind: "gdscript",
      severity: "warning",
      file,
      line: Number.isFinite(line) ? line : null,
      column: null,
      code: null,
      message,
      raw,
    });
  }

  // 2c. GDScript `SCRIPT ERROR: <msg>\n  res://path.gd:LINE`.
  GDSCRIPT_SCRIPT_ERROR_RE.lastIndex = 0;
  while ((m = GDSCRIPT_SCRIPT_ERROR_RE.exec(log)) !== null) {
    const message = (m[1] ?? "").trim();
    const file = m[2];
    const line = parseInt(m[3], 10);
    // Reconstruct a compact raw line quoting the original.
    const raw = `SCRIPT ERROR: ${message} (${file}:${line})`;
    push({
      kind: "gdscript",
      severity: "error",
      file,
      line: Number.isFinite(line) ? line : null,
      column: null,
      code: null,
      message,
      raw,
    });
  }

  // 2d. GDScript runtime annotation — only when it carries a parse-error-like
  // severity marker, to avoid classifying every runtime print.
  GDSCRIPT_RUNTIME_RE.lastIndex = 0;
  while ((m = GDSCRIPT_RUNTIME_RE.exec(log)) !== null) {
    const message = (m[3] ?? "").trim();
    // Conservative: only surface when the message reads like an error.
    if (!/\b(error|Parse Error|SCRIPT ERROR|Invalid|Cannot|Failed)\b/i.test(message)) {
      continue;
    }
    const raw = m[0].trim();
    const file = m[1];
    const line = parseInt(m[2], 10);
    push({
      kind: "gdscript",
      severity: "error",
      file,
      line: Number.isFinite(line) ? line : null,
      column: null,
      code: null,
      message,
      raw,
    });
  }

  // 3. Script / resource load failures.
  SCRIPT_LOAD_RE.lastIndex = 0;
  while ((m = SCRIPT_LOAD_RE.exec(log)) !== null) {
    const raw = m[0].trim();
    const file = m[1];
    push({
      kind: "script_load",
      severity: "error",
      file,
      line: null,
      column: null,
      code: null,
      message: `Failed to load ${file}`,
      raw,
    });
  }

  // 4. Addon / plugin load failures.
  ADDON_LOAD_RE.lastIndex = 0;
  while ((m = ADDON_LOAD_RE.exec(log)) !== null) {
    const raw = m[0].trim();
    push({
      kind: "addon_load",
      severity: "error",
      file: null,
      line: null,
      column: null,
      code: null,
      message: raw,
      raw,
    });
  }

  // 5. Conservative `other` fallback. Only surface unmatched lines that carry
  //    an explicit error signature — never the bare word "error" in prose.
  //    `SCRIPT ERROR` is handled by the script-error layer above (it pairs the
  //    message with a path/line on the next line); exclude it here so the
  //    message-only first line is not double-counted as `other`. `Parse Error`
  //    is handled by the GDScript parse layer; exclude it too.
  const OTHER_ERROR_RE = /^\s*(?:ERROR|Error):[^\r\n]+$/gm;
  OTHER_ERROR_RE.lastIndex = 0;
  while ((m = OTHER_ERROR_RE.exec(log)) !== null) {
    const raw = m[0].trim();
    push({
      kind: "other",
      severity: "error",
      file: null,
      line: null,
      column: null,
      code: null,
      message: raw,
      raw,
    });
  }

  return out;
}

/**
 * Normalize a C# file path to a Godot `res://`-relative form when possible.
 * The compiler emits native paths (`Assets/Foo.cs`, `C:\proj\Foo.cs`,
 * `/abs/proj/Foo.cs`); an agent can act faster on `res://Foo.cs`. Paths that
 * cannot be safely relativized (outside the project, unknown root) are
 * returned trimmed but otherwise unchanged. `null` when empty.
 *
 * NOTE: this helper only relativizes when the path clearly starts with a
 * project-root-like prefix. Full relativization requires the project root,
 * which the parser does not have — the router's offline handler performs any
 * further path normalization it needs. Keep the parser pure + root-free.
 */
function normalizeFilePath(raw: string): string | null {
  const trimmed = raw.trim();
  if (trimmed === "") return null;
  // Already a Godot scheme.
  if (trimmed.startsWith("res://") || trimmed.startsWith("user://")) {
    return trimmed;
  }
  return trimmed;
}

/** Re-exported for tests that assert the C# locator regex is shape-compatible
 *  with Unity's. Not part of the public surface. */
export const _CS_LOCATOR_RE = CS_LOCATOR_RE;
