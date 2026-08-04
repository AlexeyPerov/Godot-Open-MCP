// P15.3 — Godot editor process control: hang-signature detection + cross-
// platform kill for the `godot_open_mcp_restart_editor` tool.
//
// The reactive recovery half: when the Godot editor wedges mid-session the
// bridge's HTTP listener dies with it, and `bridge_status` reports
// `unreachable` (live PID, no /ping) or `dead_bridge`. `read_compile_errors`
// surfaces the failure from the on-disk Godot log, but neither tool can ACT
// on the wedge. This module is the acting half — terminating the hung Godot
// process so the operator/Hub can relaunch a fresh one. The relaunch itself
// is intentionally NOT here: the interactive Godot editor's launch recipe
// (the flags the Hub or operator used) is not knowable from the server, so a
// botched relaunch would leave the operator worse off. The shipped contract
// is "kill only; the response tells the operator to relaunch via the Hub."
//
// Adapted from Unity Open MCP's `mcp-server/src/editor-process-control.ts`
// (P15.3 porting-map fidelity tags):
//   - **copy** the cross-platform kill (SIGTERM → grace window → SIGKILL on
//     POSIX; `taskkill /T /F` on Windows), the `ProcessKiller` test seam, the
//     grace constants, and the never-throw contract. These are the same
//     contract Unity ships; Godot is also a long-lived native editor process
//     that must be killed cooperatively then forcibly.
//   - **adapt** the signature check. Unity keys on an fd-exhaustion string
//     (`Could not register to wait for file descriptor N`) — the Bee build-
//     driver hang. Godot has no equivalent single signature. The Godot hang
//     is detected from a crash marker in the on-disk Godot log OR a frozen
//     process (instance lock shows a live PID, /ping unreachable, log tail
//     shows no recent activity). Crucially, compile errors in the log are
//     treated as NOT a hang signature — those are FIXABLE failures whose
//     recovery is "fix the source", never "kill the editor".
//   - **adapt** PID resolution. Unity scans the OS process list
//     (`findUnityForProject`). Godot resolves the PID from the instance lock
//     (`instance-discovery.ts#readInstanceLock`) — the bridge writes its PID
//     there on startup, and the MCP server already trusts that file for port
//     resolution. No out-of-band process scan.
//
// PID liveness reuses `instance-discovery.ts#isPidAlive` (single source of
// truth for the `kill -0` probe) rather than duplicating it here. No runtime
// deps beyond node built-ins + the local instance-discovery module
// (mcp-server/AGENTS.md).

import { execFileSync } from "node:child_process";
import { isPidAlive } from "./instance-discovery.js";

// ---------------------------------------------------------------------------
// Kill-grace constants.
// ---------------------------------------------------------------------------

/**
 * Grace period (ms) between SIGTERM and the SIGKILL fallback on POSIX.
 * Generous enough for Godot to flush its log and release the instance lock
 * on a cooperative shutdown; short enough that a fully-hung editor does not
 * make the tool feel unresponsive. Tunable per-call via
 * `killEditorProcess({ graceMs })`.
 */
export const DEFAULT_KILL_GRACE_MS = 5_000;

/**
 * Hard upper bound on the total time `killEditorProcess` will wait. Guards
 * against a pathological POSIX kill where neither SIGTERM nor SIGKILL reaps
 * the process within a reasonable window (zombie / uninterruptible sleep).
 * The call returns `terminated: false` with a reason rather than hanging the
 * MCP request.
 */
export const MAX_KILL_WAIT_MS = 15_000;

// ---------------------------------------------------------------------------
// Kill result types.
// ---------------------------------------------------------------------------

/** How the process was actually terminated. */
export type KillMethod = "sigterm" | "sigkill" | "taskkill";

/** Successful kill result. */
export interface KillOk {
  terminated: true;
  /** The PID the kill targeted (echoed for correlation with the caller's scan). */
  pid: number;
  /** Which signal/method finally reaped the process. */
  method: KillMethod;
  /** Wall-clock ms from the first signal to confirmed termination. */
  elapsedMs: number;
}

/** Failed kill result. The process is still alive (or its state is unknown). */
export interface KillFailed {
  terminated: false;
  pid: number;
  /** Machine-readable reason code for branching. */
  reason:
    | "invalid_pid"
    | "signal_error"
    | "timeout"
    | "spawn_failed"
    | "not_found";
  /** Human-readable detail. */
  message: string;
}

export type KillResult = KillOk | KillFailed;

// ---------------------------------------------------------------------------
// Hang-signature detection (Godot-specific).
// ---------------------------------------------------------------------------

/**
 * Where the Godot hang signature was confirmed from. Mirrors Unity's
 * `signatureSource` ("editor_log" | "live_console") but splits the Godot log
 * signal into the two distinct shapes it can take — a crash marker vs a
 * frozen process — so the response can tell the operator which one fired.
 */
export type HangSignatureSource = "godot_log_crash" | "godot_log_frozen";

/** Result of {@link detectHangSignature}. */
export interface HangSignatureResult {
  /** True when a hang/crash signature is present and a kill is justified. */
  present: boolean;
  /** Which signal confirmed the hang, when present. `null` when absent. */
  source: HangSignatureSource | null;
  /**
   * Human-readable note. A WARNING (why the kill should NOT proceed) when
   * `present` is false; a CONFIRMATION when present. Surfaced verbatim in the
   * tool response so the operator/agent understands the verdict.
   */
  note: string;
}

/**
 * Godot crash-handler / OS-level crash markers seen in the on-disk Godot log
 * when the editor has segfaulted, aborted, or dumped a backtrace. Godot 4's
 * unix crash handler prints "Dumping the backtrace..." + "==BEGIN STACK
 * TRACE=="; the OS may append "Segmentation fault" / "Aborted". The Mono/.NET
 * integration emits its own fatal markers. None of these appear during a
 * normal reload or a fixable compile failure, so a match is a strong crash
 * signal.
 *
 * Kept conservative — every pattern names a definitive crash signature, never
 * a word that appears in healthy logs (e.g. the bare word "error" is NOT
 * here; that belongs to the compile-diagnostics layer).
 */
const CRASH_MARKER_RES: ReadonlyArray<RegExp> = [
  /Dumping the backtrace/i,
  /==BEGIN STACK TRACE==/,
  /Segmentation fault|SIGSEGV/i,
  /\bAborted\b\s*\(core dumped\)?/i,
  /\bFatal\b/i,
  /core dump/i,
  /CrashHandler/i,
  /Godot Engine[^\n]*crash/i,
];

/**
 * Inputs to {@link detectHangSignature}. The composition layer
 * (`ToolRouter.routeRestartEditor`) collects these from the Godot log read +
 * compile-diagnostics extraction + the instance lock + one `/ping` probe; the
 * detector is a pure function over them so the full signature matrix can be
 * unit-tested without a bridge stub or a Godot editor.
 */
export interface HangSignatureInput {
  /** Raw Godot log tail text (CRLF already normalized to LF). */
  logTail: string;
  /**
   * Whether a log file exists and was read. False when file logging is
   * disabled in project.godot OR no current/rotated log was found. The frozen
   * signature requires a log to exist (a missing log is ambiguous — logging
   * off vs frozen — so it does not by itself justify a kill).
   */
  logExists: boolean;
  /**
   * True when C#/GDScript compile errors were extracted from the log tail
   * (via `extractCompileDiagnostics`). These are FIXABLE failures — the
   * recovery is "fix the source", never "kill the editor". Their presence
   * forces the signature ABSENT regardless of any other signal.
   */
  hasCompileErrors: boolean;
  /**
   * True when the selected log's mtime is older than the staleness threshold
   * (no recent writes). A live Godot editor writes to its log periodically
   * (heartbeat, console output); a stale log + live PID + unreachable /ping
   * is the frozen-main-thread signature.
   */
  logStale: boolean;
  /** True when the `/ping` probe did not respond (bridge listener unreachable). */
  pingUnreachable: boolean;
  /** True when the instance-lock PID is still alive (Godot process exists). */
  pidAlive: boolean;
}

/**
 * Decide whether the Godot editor is hung/crashed from the collected signals.
 * Pure — no I/O. The verdict gates the kill: the tool refuses with
 * `restart_signature_absent` when this returns `present: false`.
 *
 * Decision order (first match wins):
 *
 *   1. **Compile errors → ABSENT.** A fixable failure. Killing the editor
 *      would discard unsaved work without addressing the cause. The recovery
 *      path is `read_compile_errors` → fix the source → reload.
 *   2. **Crash marker in the log → PRESENT (godot_log_crash).** A definitive
 *      crash signature (backtrace / segfault / abort). The process may still
 *      be technically alive (stuck in the crash handler) or already gone.
 *   3. **Frozen → PRESENT (godot_log_frozen).** Live PID + unreachable /ping
 *      + a stale log (exists but no recent writes). The editor's main thread
 *      is wedged and is not making forward progress.
 *   4. **Otherwise → ABSENT.** The signals do not confirm a hang. The note
 *      explains what was checked so the operator/agent can decide next steps.
 *
 * Note on the "sustained window" for /ping: the detector takes a single
 * boolean `pingUnreachable`. The operator is expected to have already
 * observed a sustained outage via `bridge_status` before calling this tool;
 * a single unreachable ping is the practical signal here.
 */
export function detectHangSignature(input: HangSignatureInput): HangSignatureResult {
  const { logTail, logExists, hasCompileErrors, logStale, pingUnreachable, pidAlive } = input;

  // 1. Compile errors are fixable — never restart on them.
  if (hasCompileErrors) {
    return {
      present: false,
      source: null,
      note:
        "The Godot log shows compile/parse errors — this is a FIXABLE failure, " +
        "not a hung editor. Killing the editor would discard any unsaved scene " +
        "work without addressing the cause. Call godot_open_mcp_read_compile_errors " +
        "to see the errors, fix the source, and let Godot reload. Only restart " +
        "when the editor is truly wedged (frozen main thread / crash) with no " +
        "compiling path forward.",
    };
  }

  // 2. Crash marker in the log tail → definitive crash signature.
  if (logTail && hasCrashMarker(logTail)) {
    return {
      present: true,
      source: "godot_log_crash",
      note:
        "A crash marker (backtrace / segfault / abort / fatal) is present in " +
        "the Godot log tail — the editor has crashed and will not recover on " +
        "its own. Terminating the process clears the wedge so a fresh editor " +
        "can be relaunched.",
    };
  }

  // 3. Frozen: live PID + unreachable /ping + stale log (exists, no recent
  //    writes). All three must hold — a reachable /ping means the editor is
  //    responsive, a dead PID means nothing to kill, and a fresh log means
  //    the editor is actively writing.
  if (pidAlive && pingUnreachable && logExists && logStale) {
    return {
      present: true,
      source: "godot_log_frozen",
      note:
        "The Godot process is alive but /ping is unreachable and the log shows " +
        "no recent activity — the editor's main thread is frozen (wedged). It " +
        "will not recover on its own. Terminating the process clears the wedge.",
    };
  }

  // 4. No confirmed hang signature.
  return {
    present: false,
    source: null,
    note: composeSignatureAbsentNote(input),
  };
}

/**
 * Scan a Godot log tail for any crash marker. Returns true on the first
 * match. Exported for unit tests; not part of the public tool surface.
 */
export function hasCrashMarker(logTail: string): boolean {
  if (!logTail) return false;
  for (const re of CRASH_MARKER_RES) {
    re.lastIndex = 0;
    if (re.test(logTail)) return true;
  }
  return false;
}

/** Compose the explanatory note when no hang signature was confirmed. Tells
 *  the operator/agent what was checked and what to do instead of killing. */
function composeSignatureAbsentNote(input: HangSignatureInput): string {
  const parts: string[] = [];
  parts.push("No hung-editor signature is present.");
  if (!input.logExists) {
    parts.push(
      "No Godot log file was found (file logging may be disabled in " +
        "project.godot — enable `debug/file_logging/enable_file_logging` and " +
        "reproduce the wedge so the crash/frozen signature is recorded).",
    );
  } else if (!input.logStale) {
    parts.push(
      "The Godot log shows recent activity — the editor appears to still be " +
        "writing to it (not frozen).",
    );
  }
  if (!input.pingUnreachable) {
    parts.push(
      "/ping is reachable — the bridge listener is responding (the editor is " +
        "not wedged).",
    );
  }
  if (!input.pidAlive) {
    parts.push(
      "No live Godot PID was found in the instance lock — there may be nothing " +
        "to kill, or the editor already exited.",
    );
  }
  parts.push(
    "Re-confirm with godot_open_mcp_bridge_status and godot_open_mcp_read_compile_errors " +
      "before killing. Only restart when the editor is truly wedged.",
  );
  return parts.join(" ");
}

// ---------------------------------------------------------------------------
// Cross-platform kill: SIGTERM → grace period → SIGKILL on POSIX; taskkill /T
// /F on Windows. Adapted (copy fidelity) from Unity's editor-process-control.
// ---------------------------------------------------------------------------

/** Cross-platform process killer. Tests inject a fake via the setter below. */
export interface ProcessKiller {
  /**
   * Terminate the given PID. Resolves once the process is confirmed gone or
   * the grace window expires. Never throws — failures surface as
   * `KillFailed`. `nowMs` is injected for deterministic elapsed-time tests.
   */
  kill(pid: number, graceMs: number, nowMs: () => number): Promise<KillResult>;
}

/** Sleep helper (kept as a parameter so tests can drive the clock). */
type Sleeper = (ms: number) => Promise<void>;
const realSleep: Sleeper = (ms) =>
  new Promise((resolve) => setTimeout(resolve, ms));

/** Default sleeper — production code path. */
const defaultSleeper: Sleeper = realSleep;

// ---------------------------------------------------------------------------
// POSIX kill: SIGTERM → grace period (polling liveness) → SIGKILL fallback.
// ---------------------------------------------------------------------------

async function killPosix(
  pid: number,
  graceMs: number,
  sleeper: Sleeper,
  nowMs: () => number,
): Promise<KillResult> {
  const start = nowMs();
  try {
    try {
      process.kill(pid, "SIGTERM");
    } catch (err) {
      const code = (err as NodeJS.ErrnoException).code;
      if (code === "ESRCH") {
        return {
          terminated: false,
          pid,
          reason: "not_found",
          message: `No such process for PID ${pid} (ESRCH on SIGTERM).`,
        };
      }
      return {
        terminated: false,
        pid,
        reason: "signal_error",
        message: `SIGTERM failed for PID ${pid}: ${
          err instanceof Error ? err.message : String(err)
        }`,
      };
    }

    // Poll for graceful exit across the grace window. 100ms cadence keeps the
    // tool responsive without burning CPU; we exit early the moment the PID is
    // reaped.
    const pollInterval = 100;
    let waited = 0;
    while (waited < graceMs) {
      await sleeper(Math.min(pollInterval, graceMs - waited));
      waited = nowMs() - start;
      if (!isPidAlive(pid)) {
        return {
          terminated: true,
          pid,
          method: "sigterm",
          elapsedMs: nowMs() - start,
        };
      }
      if (nowMs() - start >= MAX_KILL_WAIT_MS) {
        return {
          terminated: false,
          pid,
          reason: "timeout",
          message:
            `SIGTERM sent to PID ${pid} but it did not exit within the ` +
            `${MAX_KILL_WAIT_MS}ms hard cap.`,
        };
      }
    }

    // SIGKILL fallback. The process is still alive after the grace window.
    try {
      process.kill(pid, "SIGKILL");
    } catch (err) {
      const code = (err as NodeJS.ErrnoException).code;
      if (code === "ESRCH") {
        // Raced — exited between the last poll and the SIGKILL.
        return {
          terminated: true,
          pid,
          method: "sigterm",
          elapsedMs: nowMs() - start,
        };
      }
      return {
        terminated: false,
        pid,
        reason: "signal_error",
        message: `SIGKILL failed for PID ${pid}: ${
          err instanceof Error ? err.message : String(err)
        }`,
      };
    }

    // Give SIGKILL a brief window to reap. SIGKILL is async at the kernel
    // level — the process gets reaped on the next scheduler tick.
    await sleeper(Math.min(500, MAX_KILL_WAIT_MS - (nowMs() - start)));
    if (!isPidAlive(pid)) {
      return {
        terminated: true,
        pid,
        method: "sigkill",
        elapsedMs: nowMs() - start,
      };
    }

    return {
      terminated: false,
      pid,
      reason: "timeout",
      message:
        `Both SIGTERM and SIGKILL sent to PID ${pid} but it did not exit ` +
        `within the grace + fallback window.`,
    };
  } catch (err) {
    return {
      terminated: false,
      pid,
      reason: "signal_error",
      message: `Unexpected error killing PID ${pid}: ${
        err instanceof Error ? err.message : String(err)
      }`,
    };
  }
}

// ---------------------------------------------------------------------------
// Windows kill: `taskkill /PID <pid> /T /F`. /T kills the whole process tree
// (Godot spawns child processes — a hang often leaves those orphaned); /F
// forces termination (the hung main thread cannot honor a cooperative close).
// ---------------------------------------------------------------------------

async function killWindows(
  pid: number,
  nowMs: () => number,
): Promise<KillResult> {
  const start = nowMs();
  try {
    // taskkill exits 0 on success, non-zero when the PID was not found or the
    // kill failed. /F is forced — there is no graceful/forced escalation on
    // Windows like there is on POSIX.
    execFileSync("taskkill", ["/PID", String(pid), "/T", "/F"], {
      stdio: "ignore",
      windowsHide: true,
    });
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    if (/not found|no such|exit code: 128/i.test(message)) {
      return {
        terminated: false,
        pid,
        reason: "not_found",
        message: `taskkill reported no process for PID ${pid}.`,
      };
    }
    return {
      terminated: false,
      pid,
      reason: "spawn_failed",
      message: `taskkill failed for PID ${pid}: ${message}`,
    };
  }
  return {
    terminated: true,
    pid,
    method: "taskkill",
    elapsedMs: nowMs() - start,
  };
}

// ---------------------------------------------------------------------------
// Real killer — dispatches by platform.
// ---------------------------------------------------------------------------

const realKiller: ProcessKiller = {
  async kill(pid, graceMs, nowMs): Promise<KillResult> {
    if (!Number.isInteger(pid) || pid <= 0) {
      return {
        terminated: false,
        pid,
        reason: "invalid_pid",
        message: `Invalid PID ${pid}.`,
      };
    }
    if (process.platform === "win32") {
      // taskkill /F is forced — there is no grace window on Windows.
      return killWindows(pid, nowMs);
    }
    return killPosix(pid, graceMs, defaultSleeper, nowMs);
  },
};

// Mutable binding so tests can swap in a fake killer without threading a
// dependency through every caller. Default is the real OS killer.
let currentKiller: ProcessKiller = realKiller;

/** Read the active killer. Production callers use this via `killEditorProcess`. */
export function getProcessKiller(): ProcessKiller {
  return currentKiller;
}

/**
 * Install a fake killer for tests. Returns a restore function that re-binds
 * the previous killer (so concurrent test files don't leak state). Production
 * code MUST NOT call this.
 */
export function setProcessKillerForTest(
  fake: ProcessKiller | null,
): () => void {
  const prev = currentKiller;
  currentKiller = fake ?? realKiller;
  return () => {
    currentKiller = prev;
  };
}

/**
 * Terminate a Godot editor process by PID. The contract:
 *
 *   - SIGTERM first (POSIX) so Godot can flush its log + release the instance
 *     lock, then SIGKILL after `graceMs` if still alive.
 *   - `taskkill /T /F` on Windows (the hung main thread cannot honor a
 *     cooperative close; /T also reaps orphaned child processes).
 *   - Never throws. Failures (not found, signal error, timeout) surface as
 *     `KillFailed` with a machine-readable `reason` for branching.
 *
 * @param pid     Target Godot PID (from the instance lock).
 * @param graceMs SIGTERM→SIGKILL grace window on POSIX. Defaults to
 *                {@link DEFAULT_KILL_GRACE_MS}.
 */
export async function killEditorProcess(
  pid: number,
  graceMs: number = DEFAULT_KILL_GRACE_MS,
): Promise<KillResult> {
  return currentKiller.kill(
    pid,
    Math.max(0, Math.min(graceMs, MAX_KILL_WAIT_MS)),
    () => Date.now(),
  );
}
