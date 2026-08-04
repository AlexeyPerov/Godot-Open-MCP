// P15.4 — live Godot process fd/handle pressure sampling + trend detection.
//
// Proactive counterpart to `restart_editor` (the reactive kill half, P15.3):
// where the kill tool acts AFTER the editor is wedged, this module samples the
// live Godot process's file-descriptor / handle count BEFORE exhaustion so an
// agent can warn the operator to save and restart while the bridge is still
// healthy. The bridge is the thing that dies on resource exhaustion, so the
// probe MUST NOT depend on it — it runs server-side against the OS, exactly
// like the process scan in `editor-process-control.ts`.
//
// Adapted from Unity Open MCP's `mcp-server/src/process-diagnostics.ts`
// (P15.4 porting-map fidelity tags):
//   - **copy** the cross-platform fd-count probes (macOS `lsof -p <pid>`;
//     Linux `readdirSync(/proc/<pid>/fd)`; Windows `Get-Process -Id <pid>
//     .HandleCount`), the injectable `FdProbe` test seam, the never-throw
//     contract, and the monotonic-climb trend detector shape.
//   - **adapt** the ceiling. Unity hard-codes Mono's ~1024 fd ceiling (the Bee
//     build-driver hang trip point). That Mono-specific constant does NOT apply
//     to Godot — Godot's ceiling depends on the OS and the .NET runtime
//     version, so the ceiling is PROBED per-OS (Linux `/proc/<pid>/limits`,
//     macOS `launchctl limit maxfiles`; Windows has no Unix fd ceiling →
//     `null`). The actionable signal is therefore the TREND (rising/leaking),
//     not the absolute count. See design decision #1 in P15.4.md.
//   - **adapt** the headroom math to take the probed ceiling as a parameter
//     (Unity bakes `FD_CEILING` into `computeFdHeadroom`).
//   - **adapt** the trend's leak threshold: Unity keys on `>= 10% of the fixed
//     ceiling`; Godot uses `>= 10% of the probed ceiling` when known, and a
//     fixed absolute fallback (`FD_LEAK_FALLBACK_THRESHOLD`) when the ceiling
//     is unknown (Windows) so a leak is still detectable without a ceiling.
//   - **adapt** PID resolution: Godot resolves the PID from the instance lock
//     (`instance-discovery.ts`), not an OS process scan. That happens in the
//     router handler, not here — this module is pure probe + math.
//
// No runtime deps beyond node built-ins (mcp-server/AGENTS.md).

import { execFileSync } from "node:child_process";
import { readdirSync, readFileSync } from "node:fs";

// ---------------------------------------------------------------------------
// Thresholds + constants.
//
// The state thresholds (warn ≥80%, critical ≥90%) are copied from Unity — they
// are conventional pressure bands, not Unity-specific. The ceiling they are
// measured against is the difference: Unity's fixed 1024 vs Godot's probed
// per-OS value.
// ---------------------------------------------------------------------------

/**
 * Headroom fraction (of the probed ceiling) at or above which the state is
 * `warn`: the editor is approaching its fd ceiling and an agent should surface
 * the trend so the operator can save + restart before a hang. ≥80% usage.
 */
export const FD_WARN_RATIO = 0.8;

/**
 * Headroom fraction at or above which the state is `critical`: the editor is
 * close to exhausting its fd budget. ≥90% usage.
 */
export const FD_CRITICAL_RATIO = 0.9;

/** execFileSync timeout for the macOS `lsof` + ceiling probes (ms). */
export const FD_PROBE_TIMEOUT_MS = 3_000;

/**
 * Capacity of the session-scoped sample ring (P15.4 §"Session-scoped ring, no
 * disk cache"). Samples live in `ToolSessionState`; a server restart clears
 * history. 20 is enough to see a monotonic climb across several recompiles
 * without unbounded growth.
 */
export const FD_SAMPLE_RING_CAPACITY = 20;

/**
 * Absolute fd-climb threshold used to classify a monotonic climb as `leaking`
 * when the per-OS ceiling could not be probed (Windows has no Unix fd ceiling).
 * When the ceiling IS known, the leak threshold is `ceiling * 0.1` (10% of the
 * ceiling), matching Unity's "meaningful leak, not measurement noise" rule.
 * 50 fds is a conservative floor — a monotonic 50-fd climb across samples is a
 * real leak, not noise, regardless of the absolute ceiling.
 */
export const FD_LEAK_FALLBACK_THRESHOLD = 50;

// ---------------------------------------------------------------------------
// fd-count probe types.
// ---------------------------------------------------------------------------

/** OS method used to count file descriptors for a PID. */
export type FdCountMethod = "lsof" | "proc" | "handle_count";

/** Successful fd-count probe. */
export interface FdCountOk {
  /** The raw fd count (or Windows handle count — see `approximate`). */
  count: number;
  method: FdCountMethod;
  /**
   * `true` when the count is a Windows HandleCount approximation rather than
   * a Unix fd count. Windows handles cover more than just fds (kernel + GDI +
   * user objects), so the headroom math is looser there. The response flags
   * this so an agent does not over-react to a naturally-higher number.
   */
  approximate: boolean;
}

/** Failed fd-count probe. The count is unknown — callers must NOT treat this
 *  as "low pressure" (it is "unknown pressure"). */
export interface FdCountFailed {
  count: null;
  method: FdCountMethod;
  /**
   * Machine-readable reason for branching. Each reason is paired with the
   * matching {@link FdCountMethod} — `lsof_failed` only comes from the macOS
   * probe, `proc_unreadable` only from the Linux probe, and
   * `handle_count_failed` only from the Windows probe.
   */
  reason:
    | "not_found"
    | "proc_unreadable"
    | "lsof_failed"
    | "handle_count_failed"
    | "unsupported_platform";
  message: string;
}

export type FdCountResult = FdCountOk | FdCountFailed;

// ---------------------------------------------------------------------------
// Ceiling probe types.
//
// Unity has no equivalent — it bakes `FD_CEILING = 1024`. The per-OS ceiling
// probe is the Godot-specific delta: the relevant fd ceiling depends on the OS
// and runtime, so it is probed (Linux `/proc/<pid>/limits`, macOS
// `launchctl limit maxfiles`; Windows has no Unix fd ceiling → `null`).
// ---------------------------------------------------------------------------

/** OS method used to resolve the fd ceiling for a PID. */
export type FdCeilingMethod = "proc_limits" | "launchctl" | "none";

/** Successful ceiling probe (the ceiling is known). */
export interface FdCeilingOk {
  ceiling: number;
  method: FdCeilingMethod;
  /**
   * `true` when the ceiling is a system-wide limit rather than a per-process
   * soft limit. macOS `launchctl limit maxfiles` is system-wide (a GUI-launched
   * Godot inherits it); Linux `/proc/<pid>/limits` is authoritative per-process.
   */
  approximate: boolean;
}

/** Ceiling could not be resolved (Windows, or a probe failure). The headroom
 *  state degrades to `unknown`; the trend is still computed. */
export interface FdCeilingUnknown {
  ceiling: null;
  method: FdCeilingMethod;
  /** Why the ceiling is unknown — surfaced so the response can explain it. */
  reason: string;
}

export type FdCeilingResult = FdCeilingOk | FdCeilingUnknown;

// ---------------------------------------------------------------------------
// Headroom + state types.
// ---------------------------------------------------------------------------

/** Coarse pressure state derived from {@link computeFdHeadroom}. */
export type FdPressureState = "ok" | "warn" | "critical" | "unknown";

/** Headroom metric against the probed fd ceiling. */
export interface FdHeadroom {
  /**
   * Open fds / ceiling, clamped to [0, 1] when both count and ceiling are
   * known. `0` when either is unknown (the state is `unknown` in that case).
   */
  pressureRatio: number;
  /** ceiling - count, clamped at 0. `0` when the ceiling is unknown. */
  headroom: number;
  /** The probed ceiling, or `null` when it could not be resolved. */
  ceiling: number | null;
  state: FdPressureState;
  /** `false` when the underlying count was an approximation (Windows). */
  reliable: boolean;
}

// ---------------------------------------------------------------------------
// Injectable probe seams (tests swap in fakes; production uses the real OS
// probes). Same pattern as `editor-process-control.ts` ProcessKiller.
// ---------------------------------------------------------------------------

/** Injectable fd-count probe. */
export interface FdProbe {
  count(pid: number): FdCountResult;
}

/** Injectable ceiling probe. */
export interface FdCeilingProbe {
  ceiling(pid: number): FdCeilingResult;
}

// ---------------------------------------------------------------------------
// Platform-specific fd-count probe implementations (adapted — copy fidelity —
// from Unity's process-diagnostics.ts).
// ---------------------------------------------------------------------------

/** macOS `lsof -p <pid>` scanner. Returns the line count (one line per fd). */
function probeMacos(pid: number): FdCountResult {
  let stdout: string;
  try {
    stdout = execFileSync("lsof", ["-p", String(pid)], {
      encoding: "utf8",
      timeout: FD_PROBE_TIMEOUT_MS,
      stdio: ["ignore", "pipe", "ignore"],
    });
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    // lsof exits non-zero when the PID is gone. Treat that as not_found.
    if (/no such file|exit code: 1/i.test(message)) {
      return {
        count: null,
        method: "lsof",
        reason: "not_found",
        message: `lsof reported no process for PID ${pid}.`,
      };
    }
    return {
      count: null,
      method: "lsof",
      reason: "lsof_failed",
      message: `lsof failed for PID ${pid}: ${message}`,
    };
  }
  // `lsof -p <pid>` prints a header line ("COMMAND PID USER FD TYPE ...") then
  // one line per open fd. Subtract the header. An empty/blank stdout means the
  // PID vanished between the spawn and the read — treat as not_found.
  const lines = stdout.split(/\r?\n/).filter((l) => l.length > 0);
  if (lines.length === 0) {
    return {
      count: null,
      method: "lsof",
      reason: "not_found",
      message: `lsof produced no output for PID ${pid}.`,
    };
  }
  return {
    count: Math.max(0, lines.length - 1),
    method: "lsof",
    approximate: false,
  };
}

/** Linux `/proc/<pid>/fd` directory entry count (no shell-out). */
function probeLinux(pid: number): FdCountResult {
  let entries: string[];
  try {
    entries = readdirSync(`/proc/${pid}/fd`);
  } catch (err) {
    const code = (err as NodeJS.ErrnoException).code;
    if (code === "ENOENT") {
      return {
        count: null,
        method: "proc",
        reason: "not_found",
        message: `No /proc/${pid}/fd (process gone).`,
      };
    }
    return {
      count: null,
      method: "proc",
      reason: "proc_unreadable",
      message: `Could not read /proc/${pid}/fd: ${
        err instanceof Error ? err.message : String(err)
      }`,
    };
  }
  return {
    count: entries.length,
    method: "proc",
    approximate: false,
  };
}

/** Windows `Get-Process -Id <pid>.HandleCount` (approximate). */
function probeWindows(pid: number): FdCountResult {
  let stdout: string;
  try {
    stdout = execFileSync(
      "powershell",
      [
        "-NoProfile",
        "-NonInteractive",
        "-Command",
        `(Get-Process -Id ${pid} -ErrorAction SilentlyContinue).HandleCount`,
      ],
      {
        encoding: "utf8",
        windowsHide: true,
        timeout: FD_PROBE_TIMEOUT_MS,
      },
    );
  } catch (err) {
    return {
      count: null,
      method: "handle_count",
      reason: "handle_count_failed",
      message: `PowerShell handle-count probe failed for PID ${pid}: ${
        err instanceof Error ? err.message : String(err)
      }`,
    };
  }
  const trimmed = stdout.trim();
  if (trimmed.length === 0) {
    return {
      count: null,
      method: "handle_count",
      reason: "not_found",
      message: `Get-Process reported no process for PID ${pid}.`,
    };
  }
  const parsed = Number.parseInt(trimmed, 10);
  if (!Number.isFinite(parsed) || parsed < 0) {
    return {
      count: null,
      method: "handle_count",
      reason: "handle_count_failed",
      message: `Unparseable HandleCount '${trimmed}' for PID ${pid}.`,
    };
  }
  return {
    count: parsed,
    method: "handle_count",
    // Windows HandleCount includes kernel + GDI + user handles — broader
    // than Unix fds. The headroom math still applies as a loose signal when a
    // ceiling is known; on Windows the ceiling is null so the state degrades
    // to `unknown` and the trend carries the signal.
    approximate: true,
  };
}

// ---------------------------------------------------------------------------
// Platform-specific ceiling probe implementations (Godot delta — Unity has no
// equivalent; it bakes FD_CEILING = 1024).
// ---------------------------------------------------------------------------

/**
 * Linux `/proc/<pid>/limits` `Max open files` soft limit. Authoritative
 * per-process ceiling. The file looks like:
 *   "Max open files     1024   4096   files"
 * First number = soft limit (the ceiling), second = hard limit.
 */
function probeCeilingLinux(pid: number): FdCeilingResult {
  let raw: string;
  try {
    raw = readFileSync(`/proc/${pid}/limits`, "utf8");
  } catch (err) {
    const code = (err as NodeJS.ErrnoException).code;
    return {
      ceiling: null,
      method: "proc_limits",
      reason:
        code === "ENOENT"
          ? `No /proc/${pid}/limits (process gone or not Linux).`
          : `Could not read /proc/${pid}/limits: ${
              err instanceof Error ? err.message : String(err)
            }`,
    };
  }
  const match = raw.match(/^Max open files\s+(\d+)\s+(\d+)/m);
  if (!match) {
    return {
      ceiling: null,
      method: "proc_limits",
      reason: `/proc/${pid}/limits has no parseable 'Max open files' line.`,
    };
  }
  const soft = Number.parseInt(match[1], 10);
  if (!Number.isFinite(soft) || soft <= 0) {
    return {
      ceiling: null,
      method: "proc_limits",
      reason: `'Max open files' soft limit '${match[1]}' is not a positive integer.`,
    };
  }
  return {
    ceiling: soft,
    method: "proc_limits",
    // Per-process soft limit — authoritative for this PID.
    approximate: false,
  };
}

/**
 * macOS `launchctl limit maxfiles` — the system-wide soft/hard file-descriptor
 * limit. A GUI-launched Godot inherits this (NOT the MCP server's shell
 * `ulimit -n`), so it is the relevant ceiling for a Godot editor started from
 * the dock/Finder/Hub. Output looks like `maxfiles    256    unlimited` (soft
 * then hard); the soft value is the ceiling. Flagged `approximate` because it
 * is system-wide, not per-process.
 */
function probeCeilingMacos(): FdCeilingResult {
  let stdout: string;
  try {
    stdout = execFileSync("launchctl", ["limit", "maxfiles"], {
      encoding: "utf8",
      timeout: FD_PROBE_TIMEOUT_MS,
      stdio: ["ignore", "pipe", "ignore"],
    });
  } catch (err) {
    return {
      ceiling: null,
      method: "launchctl",
      reason: `launchctl limit maxfiles failed: ${
        err instanceof Error ? err.message : String(err)
      }`,
    };
  }
  // `launchctl limit maxfiles` prints a header-ish line: the soft then hard
  // limit, possibly `unlimited`. Take the soft value; treat `unlimited` as no
  // finite ceiling.
  const match = stdout.match(/maxfiles\s+(\S+)\s+(\S+)/i);
  if (!match) {
    return {
      ceiling: null,
      method: "launchctl",
      reason: `'launchctl limit maxfiles' output did not parse: '${stdout.trim()}'.`,
    };
  }
  if (/unlimited/i.test(match[1])) {
    return {
      ceiling: null,
      method: "launchctl",
      reason: "macOS maxfiles soft limit is 'unlimited' — no finite ceiling.",
    };
  }
  const soft = Number.parseInt(match[1], 10);
  if (!Number.isFinite(soft) || soft <= 0) {
    return {
      ceiling: null,
      method: "launchctl",
      reason: `macOS maxfiles soft limit '${match[1]}' is not a positive integer.`,
    };
  }
  return {
    ceiling: soft,
    method: "launchctl",
    // System-wide, not per-process — a GUI Godot inherits it but a custom
    // launch could raise/lower it. Flagged so the response explains the nuance.
    approximate: true,
  };
}

/**
 * Windows has no Unix-style per-process fd ceiling — handles are pooled and
 * bounded by available memory / desktop heap, not a fixed soft limit. There is
 * no truthful finite number to report, so the ceiling is `null`. The headroom
 * state degrades to `unknown` and the trend (rising/leaking) carries the only
 * actionable signal. This is the documented Windows delta (P15.4 §"Ceiling is
 * per-OS").
 */
function probeCeilingWindows(): FdCeilingResult {
  return {
    ceiling: null,
    method: "none",
    reason:
      "Windows has no Unix-style fd ceiling (handles are memory-bounded); " +
      "pressure state is unknown — rely on the trend.",
  };
}

// ---------------------------------------------------------------------------
// Real probes — dispatch by platform.
// ---------------------------------------------------------------------------

const realFdProbe: FdProbe = {
  count(pid: number): FdCountResult {
    if (!Number.isInteger(pid) || pid <= 0) {
      return {
        count: null,
        method: "lsof",
        reason: "not_found",
        message: `Invalid PID ${pid}.`,
      };
    }
    if (process.platform === "darwin") return probeMacos(pid);
    if (process.platform === "linux") return probeLinux(pid);
    if (process.platform === "win32") return probeWindows(pid);
    return {
      count: null,
      method: "lsof",
      reason: "unsupported_platform",
      message: `fd-count probe is not implemented for platform '${process.platform}'.`,
    };
  },
};

const realCeilingProbe: FdCeilingProbe = {
  ceiling(pid: number): FdCeilingResult {
    if (!Number.isInteger(pid) || pid <= 0) {
      return {
        ceiling: null,
        method: "none",
        reason: `Invalid PID ${pid}.`,
      };
    }
    if (process.platform === "linux") return probeCeilingLinux(pid);
    if (process.platform === "darwin") return probeCeilingMacos();
    if (process.platform === "win32") return probeCeilingWindows();
    return {
      ceiling: null,
      method: "none",
      reason: `ceiling probe is not implemented for platform '${process.platform}'.`,
    };
  },
};

// Mutable bindings so tests can swap in fakes.
let currentFdProbe: FdProbe = realFdProbe;
let currentCeilingProbe: FdCeilingProbe = realCeilingProbe;

/** Read the active fd-count probe. */
export function getFdProbe(): FdProbe {
  return currentFdProbe;
}

/** Read the active ceiling probe. */
export function getFdCeilingProbe(): FdCeilingProbe {
  return currentCeilingProbe;
}

/**
 * Install a fake fd-count probe for tests. Returns a restore function that
 * re-binds the previous probe. Production code MUST NOT call this.
 */
export function setFdProbeForTest(fake: FdProbe | null): () => void {
  const prev = currentFdProbe;
  currentFdProbe = fake ?? realFdProbe;
  return () => {
    currentFdProbe = prev;
  };
}

/**
 * Install a fake ceiling probe for tests. Returns a restore function that
 * re-binds the previous probe. Production code MUST NOT call this.
 */
export function setFdCeilingProbeForTest(fake: FdCeilingProbe | null): () => void {
  const prev = currentCeilingProbe;
  currentCeilingProbe = fake ?? realCeilingProbe;
  return () => {
    currentCeilingProbe = prev;
  };
}

/**
 * Count the open file descriptors for a live Godot PID. Never throws —
 * failures (process gone, lsof/proc unreadable, unsupported platform) surface
 * as `FdCountFailed`. The result's `method` records how the count was obtained
 * so an agent can interpret the headroom math correctly (Windows
 * `handle_count` is approximate).
 *
 * @param pid the live Godot PID (from the instance lock).
 */
export function countFileDescriptors(pid: number): FdCountResult {
  return currentFdProbe.count(pid);
}

/**
 * Probe the per-OS fd ceiling for a live Godot PID. Never throws — failures
 * surface as `FdCeilingUnknown`. The ceiling is the relevant soft limit:
 *   - Linux: `/proc/<pid>/limits` `Max open files` soft value (authoritative).
 *   - macOS: `launchctl limit maxfiles` soft value (system-wide; a GUI Godot
 *     inherits it). Flagged `approximate`.
 *   - Windows: `null` (no Unix fd ceiling; rely on the trend).
 *
 * @param pid the live Godot PID (from the instance lock).
 */
export function probeFdCeiling(pid: number): FdCeilingResult {
  return currentCeilingProbe.ceiling(pid);
}

// ---------------------------------------------------------------------------
// Pure headroom + trend math.
// ---------------------------------------------------------------------------

/**
 * Pure headroom math against a probed fd ceiling. The ceiling is a parameter
 * (Godot delta vs Unity, which bakes `FD_CEILING`): callers pass the value
 * from {@link probeFdCeiling}.
 *
 * `null` count (fd probe failed) OR `null` ceiling (Windows / probe failure) →
 * `state: "unknown"` with `reliable: false`. An agent must NOT treat "unknown"
 * as "ok"; it should surface the trend (if any prior samples exist) and tell
 * the operator the live count/ceiling could not be read.
 *
 * `approximate` counts (Windows HandleCount) only ever produce `critical` /
 * `unknown`, never `warn`, so an agent does not over-react to a naturally-
 * higher handle count.
 */
export function computeFdHeadroom(
  count: number | null,
  ceiling: number | null,
  approximate = false,
): FdHeadroom {
  if (count === null || !Number.isFinite(count)) {
    return {
      pressureRatio: 0,
      headroom: 0,
      ceiling,
      state: "unknown",
      reliable: false,
    };
  }
  if (ceiling === null || !Number.isFinite(ceiling) || ceiling <= 0) {
    // Count is known but the ceiling is not (Windows, or a ceiling-probe
    // failure). The absolute pressure state is unknowable; the trend is the
    // only actionable signal.
    return {
      pressureRatio: 0,
      headroom: 0,
      ceiling: null,
      state: "unknown",
      reliable: !approximate,
    };
  }
  const clampedCount = Math.max(0, count);
  const pressureRatio = Math.min(1, clampedCount / ceiling);
  const headroom = Math.max(0, ceiling - clampedCount);
  let state: FdPressureState;
  if (approximate) {
    // Windows HandleCount is naturally higher than Unix fds — only flag
    // critical, never warn, so an agent does not over-react.
    state = pressureRatio >= FD_CRITICAL_RATIO ? "critical" : "ok";
  } else if (pressureRatio >= FD_CRITICAL_RATIO) {
    state = "critical";
  } else if (pressureRatio >= FD_WARN_RATIO) {
    state = "warn";
  } else {
    state = "ok";
  }
  return {
    pressureRatio,
    headroom,
    ceiling,
    state,
    reliable: !approximate,
  };
}

// ---------------------------------------------------------------------------
// Session-scoped sample ring + trend detection.
// ---------------------------------------------------------------------------

/** One fd sample recorded in the session-scoped ring. */
export interface FdSample {
  /** Wall-clock timestamp (ms since epoch). */
  ts: number;
  /** The Godot PID the sample was taken against. */
  pid: number;
  /** Open fd count at sample time (`null` when the probe failed). */
  count: number | null;
}

/**
 * Trend classification across successive fd samples. A monotonic climb across
 * recompiles / editor reloads is the leak signature — the agent should warn
 * the operator to save + restart BEFORE the count crosses the ceiling.
 * Absolute count is NOT enough: a stable 600 fds is healthy; a climb
 * 600 → 800 → 950 across three samples is a leak in progress. When the ceiling
 * is unknown (Windows), the trend is the PRIMARY actionable signal.
 */
export type FdTrendState =
  | "no_history"
  | "stable"
  | "rising"
  | "leaking"
  | "unknown";

export interface FdTrend {
  state: FdTrendState;
  /**
   * First-to-last delta in fd count (positive for growth). `null` when there
   * are fewer than two samples with known counts for the same PID.
   */
  delta: number | null;
  /** Number of samples used for the trend (same-PID, known-count). */
  sampleCount: number;
}

/**
 * Pure trend detector over a sample list. Only considers samples with a known
 * count for the SAME pid (a Godot restart changes the PID; the trend must not
 * mix pre- and post-restart samples). A monotonic climb of ≥10% of the ceiling
 * across ≥3 samples is `leaking`; a smaller non-monotonic climb is `rising`;
 * otherwise `stable`. When the ceiling is unknown (Windows), the leak
 * threshold falls back to {@link FD_LEAK_FALLBACK_THRESHOLD} so a leak is
 * still detectable. Fewer than two usable samples → `no_history`.
 *
 * @param samples the session ring (oldest-first).
 * @param ceiling the probed ceiling, or `null` when unknown.
 */
export function analyzeFdTrend(
  samples: readonly FdSample[],
  ceiling: number | null,
): FdTrend {
  if (samples.length === 0) {
    return { state: "no_history", delta: null, sampleCount: 0 };
  }
  // Take the tail for the most-recent PID (a restart changes the PID).
  const lastPid = samples[samples.length - 1].pid;
  const usable = samples.filter(
    (s) => s.pid === lastPid && s.count !== null && Number.isFinite(s.count),
  ) as Array<{ ts: number; pid: number; count: number }>;
  if (usable.length < 2) {
    return { state: "no_history", delta: null, sampleCount: usable.length };
  }
  const first = usable[0].count;
  const last = usable[usable.length - 1].count;
  const delta = last - first;

  // `rising` = strictly non-decreasing across all usable samples (each step
  // ≥ the previous). `leaking` = rising AND the total climb is meaningful —
  // ≥10% of the ceiling when known, else the absolute fallback threshold.
  let monotonic = true;
  for (let i = 1; i < usable.length; i++) {
    if (usable[i].count < usable[i - 1].count) {
      monotonic = false;
      break;
    }
  }
  const leakThreshold =
    ceiling !== null && Number.isFinite(ceiling) && ceiling > 0
      ? ceiling * 0.1
      : FD_LEAK_FALLBACK_THRESHOLD;
  if (monotonic && delta >= leakThreshold) {
    return { state: "leaking", delta, sampleCount: usable.length };
  }
  if (delta > 0) {
    return { state: "rising", delta, sampleCount: usable.length };
  }
  return { state: "stable", delta, sampleCount: usable.length };
}

/**
 * Caveat string surfaced in the resource_pressure response so an agent (and
 * the operator) understand the per-OS ceiling nuance: the actionable signal is
 * the TREND (rising/leaking), not the absolute count, because the fd ceiling
 * depends on the OS and runtime and is only probed as a best-effort reference
 * (system-wide on macOS, per-process on Linux, none on Windows).
 */
export const LAUNCH_CONTEXT_CAVEAT =
  "Headroom is measured against the per-OS fd ceiling probed at sample time " +
  "(Linux /proc/<pid>/limits 'Max open files' soft limit; macOS " +
  "'launchctl limit maxfiles' system-wide soft limit — a GUI-launched Godot " +
  "inherits it, not the MCP server's shell ulimit; Windows has no Unix fd " +
  "ceiling). The ceiling is a best-effort reference; the actionable signal is " +
  "the TREND (rising/leaking) across successive samples, not the absolute " +
  "count. A monotonic climb across recompiles/reloads is the leak signature.";
