// Tests for editor-process-control.ts (P15.3 — Godot editor kill + hang
// signature).
//
// The kill path is cross-platform + destructive, so every kill test injects a
// fake `ProcessKiller` via `setProcessKillerForTest`. No real `process.kill` /
// `taskkill` runs in unit tests. The fake records the args it was called with
// and returns a canned `KillResult` so we can assert the dispatch + the
// `killEditorProcess` wrapper (grace clamping) without touching the OS.
//
// The hang-signature tests are pure-function assertions over `detectHangSignature`
// — no I/O. The detector's full matrix (compile errors → absent; crash marker →
// present; frozen triple → present; otherwise → absent) is pinned here.

import test from "node:test";
import assert from "node:assert/strict";

import {
  killEditorProcess,
  setProcessKillerForTest,
  detectHangSignature,
  hasCrashMarker,
  DEFAULT_KILL_GRACE_MS,
  MAX_KILL_WAIT_MS,
  type KillResult,
  type ProcessKiller,
} from "./editor-process-control.js";

// ---------------------------------------------------------------------------
// Kill dispatch + grace clamping (copy fidelity with Unity's tests).
// ---------------------------------------------------------------------------

/** Fake killer that records calls + returns a scripted result per call. */
function makeFakeKiller(
  results: KillResult[] | KillResult,
): ProcessKiller & {
  calls: Array<{ pid: number; graceMs: number }>;
} {
  const calls: Array<{ pid: number; graceMs: number }> = [];
  const queue = Array.isArray(results) ? [...results] : [results];
  return {
    calls,
    async kill(pid, graceMs) {
      calls.push({ pid, graceMs });
      const next = queue.shift();
      if (!next) throw new Error("fake killer exhausted");
      return next;
    },
  };
}

test("killEditorProcess dispatches to the active killer with the default grace", async () => {
  const fake = makeFakeKiller({
    terminated: true,
    pid: 1234,
    method: "sigterm",
    elapsedMs: 100,
  });
  const restore = setProcessKillerForTest(fake);
  try {
    const result = await killEditorProcess(1234);
    assert.equal(result.terminated, true);
    assert.deepEqual(fake.calls, [{ pid: 1234, graceMs: DEFAULT_KILL_GRACE_MS }]);
  } finally {
    restore();
  }
});

test("killEditorProcess honors an explicit grace window", async () => {
  const fake = makeFakeKiller({
    terminated: true,
    pid: 42,
    method: "sigkill",
    elapsedMs: 6000,
  });
  const restore = setProcessKillerForTest(fake);
  try {
    await killEditorProcess(42, 8000);
    assert.deepEqual(fake.calls, [{ pid: 42, graceMs: 8000 }]);
  } finally {
    restore();
  }
});

test("killEditorProcess clamps grace above MAX_KILL_WAIT_MS", async () => {
  const fake = makeFakeKiller({
    terminated: true,
    pid: 1,
    method: "sigterm",
    elapsedMs: 1,
  });
  const restore = setProcessKillerForTest(fake);
  try {
    await killEditorProcess(1, 999_999);
    assert.equal(
      fake.calls[0].graceMs,
      MAX_KILL_WAIT_MS,
      "grace clamped to the hard cap",
    );
  } finally {
    restore();
  }
});

test("killEditorProcess clamps negative grace to 0", async () => {
  const fake = makeFakeKiller({
    terminated: true,
    pid: 1,
    method: "sigterm",
    elapsedMs: 0,
  });
  const restore = setProcessKillerForTest(fake);
  try {
    await killEditorProcess(1, -100);
    assert.equal(fake.calls[0].graceMs, 0);
  } finally {
    restore();
  }
});

test("killEditorProcess surfaces a kill failure unchanged (no throw)", async () => {
  const fake = makeFakeKiller({
    terminated: false,
    pid: 7,
    reason: "timeout",
    message: "fake timeout",
  });
  const restore = setProcessKillerForTest(fake);
  try {
    const result = await killEditorProcess(7);
    assert.equal(result.terminated, false);
    if (!result.terminated) {
      assert.equal(result.reason, "timeout");
      assert.equal(result.message, "fake timeout");
    }
  } finally {
    restore();
  }
});

test("killEditorProcess surfaces not_found when the killer reports it", async () => {
  const fake = makeFakeKiller({
    terminated: false,
    pid: 999,
    reason: "not_found",
    message: "No such process.",
  });
  const restore = setProcessKillerForTest(fake);
  try {
    const result = await killEditorProcess(999);
    if (!result.terminated) {
      assert.equal(result.reason, "not_found");
    } else {
      assert.fail("expected not_found, got terminated");
    }
  } finally {
    restore();
  }
});

test("setProcessKillerForTest(null) restores the real killer binding", async () => {
  const fake = makeFakeKiller({
    terminated: true,
    pid: 1,
    method: "taskkill",
    elapsedMs: 1,
  });
  const r1 = setProcessKillerForTest(fake);
  r1();
  const r2 = setProcessKillerForTest(null);
  assert.equal(typeof r2, "function");
  r2();
});

test("DEFAULT_KILL_GRACE_MS is 5s and MAX_KILL_WAIT_MS is 15s (plan-stable constants)", () => {
  assert.equal(DEFAULT_KILL_GRACE_MS, 5_000);
  assert.equal(MAX_KILL_WAIT_MS, 15_000);
});

// ---------------------------------------------------------------------------
// Real killer smoke tests (POSIX branch only). These cover the SIGTERM→SIGKILL
// path against a real child process so the dispatch logic is not dead code.
// Skipped automatically on Windows.
// ---------------------------------------------------------------------------

test("real killer terminates a spawned child via SIGTERM within the grace window", async (t) => {
  if (process.platform === "win32") {
    t.skip("POSIX-only smoke test");
    return;
  }
  const restore = setProcessKillerForTest(null);
  try {
    const { spawn } = await import("node:child_process");
    const child = spawn("sleep", ["30"], { stdio: "ignore" });
    const pid = child.pid ?? -1;
    assert.ok(pid > 0, "child spawned with a pid");
    try {
      const result = await killEditorProcess(pid, 3_000);
      assert.equal(result.terminated, true, "child terminated");
      if (result.terminated) {
        assert.equal(result.method, "sigterm", "reaped by SIGTERM cooperatively");
      }
    } finally {
      try {
        process.kill(pid, "SIGKILL");
      } catch {
        // already gone — fine
      }
    }
  } finally {
    restore();
  }
});

// ---------------------------------------------------------------------------
// Hang-signature detection (Godot-specific — the detector is a pure function).
// ---------------------------------------------------------------------------

test("detectHangSignature: compile errors force ABSENT (never restart on a fixable failure)", () => {
  const result = detectHangSignature({
    logTail: "res://Foo.gd:12 - Parse Error: bad code",
    logExists: true,
    hasCompileErrors: true,
    logStale: true,
    pingUnreachable: true,
    pidAlive: true,
  });
  assert.equal(result.present, false);
  assert.equal(result.source, null);
  assert.match(result.note, /compile\/parse errors/i);
  assert.match(result.note, /FIXABLE/i);
});

test("detectHangSignature: compile errors force ABSENT even when a crash marker is also present", () => {
  // The compile-error guard runs FIRST — a fixable failure takes precedence.
  // Without this, a log that contains both a parse error AND a crash marker
  // would restart when the agent should fix the source instead.
  const result = detectHangSignature({
    logTail:
      "res://Foo.gd:12 - Parse Error: bad code\n==BEGIN STACK TRACE==\nSegmentation fault",
    logExists: true,
    hasCompileErrors: true,
    logStale: true,
    pingUnreachable: true,
    pidAlive: true,
  });
  assert.equal(result.present, false);
});

test("detectHangSignature: crash marker → PRESENT (godot_log_crash)", () => {
  const result = detectHangSignature({
    logTail:
      "Godot Engine v4.3\nDumping the backtrace...\nSegmentation fault (core dumped)",
    logExists: true,
    hasCompileErrors: false,
    logStale: false,
    pingUnreachable: true,
    pidAlive: true,
  });
  assert.equal(result.present, true);
  assert.equal(result.source, "godot_log_crash");
  assert.match(result.note, /crash marker/i);
});

test("detectHangSignature: frozen triple → PRESENT (godot_log_frozen)", () => {
  const result = detectHangSignature({
    logTail: "some old log content",
    logExists: true,
    hasCompileErrors: false,
    logStale: true,
    pingUnreachable: true,
    pidAlive: true,
  });
  assert.equal(result.present, true);
  assert.equal(result.source, "godot_log_frozen");
  assert.match(result.note, /frozen/i);
});

test("detectHangSignature: frozen requires live PID (dead PID → ABSENT)", () => {
  const result = detectHangSignature({
    logTail: "old log content",
    logExists: true,
    hasCompileErrors: false,
    logStale: true,
    pingUnreachable: true,
    pidAlive: false,
  });
  assert.equal(result.present, false);
  assert.match(result.note, /no live godot pid/i);
});

test("detectHangSignature: frozen requires unreachable /ping (reachable → ABSENT)", () => {
  const result = detectHangSignature({
    logTail: "old log content",
    logExists: true,
    hasCompileErrors: false,
    logStale: true,
    pingUnreachable: false,
    pidAlive: true,
  });
  assert.equal(result.present, false);
  assert.match(result.note, /\/ping is reachable/i);
});

test("detectHangSignature: frozen requires a stale log (fresh log → ABSENT)", () => {
  const result = detectHangSignature({
    logTail: "recent log content",
    logExists: true,
    hasCompileErrors: false,
    logStale: false,
    pingUnreachable: true,
    pidAlive: true,
  });
  assert.equal(result.present, false);
  assert.match(result.note, /recent activity/i);
});

test("detectHangSignature: frozen requires a log to exist (missing log → ABSENT)", () => {
  const result = detectHangSignature({
    logTail: "",
    logExists: false,
    hasCompileErrors: false,
    logStale: false,
    pingUnreachable: true,
    pidAlive: true,
  });
  assert.equal(result.present, false);
  assert.match(result.note, /no godot log file was found/i);
});

test("detectHangSignature: healthy editor → ABSENT with a combined note", () => {
  const result = detectHangSignature({
    logTail: "all good, editor writing normally",
    logExists: true,
    hasCompileErrors: false,
    logStale: false,
    pingUnreachable: false,
    pidAlive: true,
  });
  assert.equal(result.present, false);
  assert.equal(result.source, null);
});

// ---------------------------------------------------------------------------
// Crash-marker regex coverage — pin each pattern shape the detector must hit.
// ---------------------------------------------------------------------------

test("hasCrashMarker: detects Godot backtrace dump header", () => {
  assert.equal(hasCrashMarker("Dumping the backtrace. Blah"), true);
});

test("hasCrashMarker: detects BEGIN STACK TRACE", () => {
  assert.equal(hasCrashMarker("==BEGIN STACK TRACE=="), true);
});

test("hasCrashMarker: detects segfault", () => {
  assert.equal(hasCrashMarker("Received SIGSEGV (Segmentation fault)"), true);
});

test("hasCrashMarker: detects Aborted (core dumped)", () => {
  assert.equal(hasCrashMarker("Aborted (core dumped)"), true);
});

test("hasCrashMarker: detects Fatal marker", () => {
  assert.equal(hasCrashMarker("ERROR: FATAL: mono runtime died"), true);
});

test("hasCrashMarker: does NOT match the bare word 'error' (compile diagnostics are not a crash)", () => {
  assert.equal(hasCrashMarker("SCRIPT ERROR: something happened"), false);
  assert.equal(hasCrashMarker("res://Foo.gd:12 - Parse Error: bad"), false);
  assert.equal(hasCrashMarker("ERROR: a normal runtime warning"), false);
});

test("hasCrashMarker: empty / undefined tail → false", () => {
  assert.equal(hasCrashMarker(""), false);
});
