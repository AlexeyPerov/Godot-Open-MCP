// Tests for process-diagnostics.ts (P15.4 — fd/handle pressure + trend).
//
// The probe paths are cross-platform + I/O-bound, so every probe test injects
// a fake `FdProbe` / `FdCeilingProbe` via the `set*ForTest` seams. No real
// `lsof` / `/proc` / `launchctl` / PowerShell runs in unit tests. The fakes
// return canned `FdCountResult` / `FdCeilingResult` so the dispatch + the
// `countFileDescriptors` / `probeFdCeiling` wrappers can be asserted without
// touching the OS.
//
// The headroom + trend tests are pure-function assertions over
// `computeFdHeadroom` / `analyzeFdTrend` — no I/O. The headroom matrix (ok /
// warn / critical / unknown) and the trend matrix (stable / rising / leaking /
// no_history / mixed-PID) are pinned here.
//
// Adapted from Unity Open MCP's process-diagnostics.test.ts (copy for the
// fake-probe seam + the headroom/threshold test shape); Godot-specific deltas:
//   - The ceiling is a PARAMETER (probed per-OS), not the fixed Unity 1024, so
//     the headroom tests pass `ceiling` explicitly and cover the `null`
//     (unknown ceiling) branch Unity does not have.
//   - The trend detector takes the probed ceiling and falls back to an
//     absolute leak threshold when the ceiling is null (Windows) — pinned here.

import test from "node:test";
import assert from "node:assert/strict";

import {
  countFileDescriptors,
  probeFdCeiling,
  computeFdHeadroom,
  analyzeFdTrend,
  setFdProbeForTest,
  setFdCeilingProbeForTest,
  FD_WARN_RATIO,
  FD_CRITICAL_RATIO,
  FD_LEAK_FALLBACK_THRESHOLD,
  FD_SAMPLE_RING_CAPACITY,
  LAUNCH_CONTEXT_CAVEAT,
  type FdProbe,
  type FdCeilingProbe,
  type FdCountResult,
  type FdCeilingResult,
  type FdSample,
} from "./process-diagnostics.js";

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

test("FD_WARN_RATIO and FD_CRITICAL_RATIO are the conventional pressure bands", () => {
  assert.equal(FD_WARN_RATIO, 0.8);
  assert.equal(FD_CRITICAL_RATIO, 0.9);
  assert.ok(FD_WARN_RATIO < FD_CRITICAL_RATIO);
});

test("FD_LEAK_FALLBACK_THRESHOLD is a positive integer (the no-ceiling leak floor)", () => {
  assert.equal(FD_LEAK_FALLBACK_THRESHOLD, 50);
});

test("FD_SAMPLE_RING_CAPACITY is 20", () => {
  assert.equal(FD_SAMPLE_RING_CAPACITY, 20);
});

test("LAUNCH_CONTEXT_CAVEAT mentions the trend as the actionable signal", () => {
  assert.ok(LAUNCH_CONTEXT_CAVEAT.length > 50);
  assert.match(LAUNCH_CONTEXT_CAVEAT, /TREND/i);
  assert.match(LAUNCH_CONTEXT_CAVEAT, /per-OS|launchctl|limits/i);
});

// ---------------------------------------------------------------------------
// computeFdHeadroom — the pure headroom math.
// ---------------------------------------------------------------------------

test("computeFdHeadroom: count null → unknown (regardless of ceiling)", () => {
  const h = computeFdHeadroom(null, 1024);
  assert.equal(h.state, "unknown");
  assert.equal(h.reliable, false);
  assert.equal(h.pressureRatio, 0);
  assert.equal(h.ceiling, 1024);
});

test("computeFdHeadroom: known count + null ceiling → unknown (Windows / probe failure)", () => {
  const h = computeFdHeadroom(500, null);
  assert.equal(h.state, "unknown");
  assert.equal(h.reliable, true);
  assert.equal(h.pressureRatio, 0);
  assert.equal(h.headroom, 0);
  assert.equal(h.ceiling, null);
});

test("computeFdHeadroom: known count + null ceiling + approximate → unknown + unreliable", () => {
  const h = computeFdHeadroom(500, null, true);
  assert.equal(h.state, "unknown");
  assert.equal(h.reliable, false);
});

test("computeFdHeadroom: ok band (< 80%)", () => {
  const h = computeFdHeadroom(100, 1024);
  assert.equal(h.state, "ok");
  assert.equal(h.reliable, true);
  assert.equal(h.pressureRatio, 100 / 1024);
  assert.equal(h.headroom, 924);
  assert.equal(h.ceiling, 1024);
});

test("computeFdHeadroom: warn band at exactly 80%", () => {
  const h = computeFdHeadroom(800, 1000);
  assert.equal(h.state, "warn");
  assert.equal(h.pressureRatio, 0.8);
  assert.equal(h.headroom, 200);
});

test("computeFdHeadroom: warn band just below 90%", () => {
  const h = computeFdHeadroom(890, 1000);
  assert.equal(h.state, "warn");
});

test("computeFdHeadroom: critical band at exactly 90%", () => {
  const h = computeFdHeadroom(900, 1000);
  assert.equal(h.state, "critical");
  assert.equal(h.pressureRatio, 0.9);
});

test("computeFdHeadroom: critical band at 100%", () => {
  const h = computeFdHeadroom(1024, 1024);
  assert.equal(h.state, "critical");
  assert.equal(h.pressureRatio, 1);
  assert.equal(h.headroom, 0);
});

test("computeFdHeadroom: count above ceiling clamps pressureRatio to 1", () => {
  const h = computeFdHeadroom(2000, 1024);
  assert.equal(h.state, "critical");
  assert.equal(h.pressureRatio, 1);
  assert.equal(h.headroom, 0);
});

test("computeFdHeadroom: negative count is clamped to 0 (defense in depth)", () => {
  const h = computeFdHeadroom(-5, 1024);
  assert.equal(h.state, "ok");
  assert.equal(h.pressureRatio, 0);
  assert.equal(h.headroom, 1024);
});

test("computeFdHeadroom: approximate (Windows) — never warns, only critical", () => {
  // Windows HandleCount is naturally higher than Unix fds. At 85% an
  // approximate count is still "ok" (no false warn), and only flips to
  // critical at ≥90%.
  assert.equal(computeFdHeadroom(850, 1000, true).state, "ok");
  assert.equal(computeFdHeadroom(900, 1000, true).state, "critical");
  assert.equal(computeFdHeadroom(850, 1000, true).reliable, false);
});

// ---------------------------------------------------------------------------
// analyzeFdTrend — the monotonic-climb trend detector.
// ---------------------------------------------------------------------------

/** Helper: build a same-PID sample list with known counts. */
function samples(
  pid: number,
  counts: number[],
  baseTs = 1_000,
): FdSample[] {
  return counts.map((count, i) => ({
    ts: baseTs + i * 1000,
    pid,
    count,
  }));
}

test("analyzeFdTrend: empty samples → no_history", () => {
  const t = analyzeFdTrend([], 1024);
  assert.equal(t.state, "no_history");
  assert.equal(t.delta, null);
  assert.equal(t.sampleCount, 0);
});

test("analyzeFdTrend: single sample → no_history", () => {
  const t = analyzeFdTrend(samples(1, [100]), 1024);
  assert.equal(t.state, "no_history");
  assert.equal(t.delta, null);
  assert.equal(t.sampleCount, 1);
});

test("analyzeFdTrend: two identical samples → stable", () => {
  const t = analyzeFdTrend(samples(1, [100, 100]), 1024);
  assert.equal(t.state, "stable");
  assert.equal(t.delta, 0);
  assert.equal(t.sampleCount, 2);
});

test("analyzeFdTrend: non-monotonic climb below leak threshold → rising", () => {
  // 100 → 95 → 105: climbs overall but dips in the middle, so not monotonic.
  const t = analyzeFdTrend(samples(1, [100, 95, 105]), 1024);
  assert.equal(t.state, "rising");
  assert.equal(t.delta, 5);
  assert.equal(t.sampleCount, 3);
});

test("analyzeFdTrend: monotonic climb below 10% of ceiling → rising (not a leak yet)", () => {
  // 100 → 105 → 110: monotonic but only +10 on a 1024 ceiling (10.24 → ~1%).
  const t = analyzeFdTrend(samples(1, [100, 105, 110]), 1024);
  assert.equal(t.state, "rising");
  assert.equal(t.delta, 10);
});

test("analyzeFdTrend: monotonic climb ≥ 10% of ceiling → leaking", () => {
  // 600 → 750 → 900: monotonic, +300 on a 1024 ceiling (~29%).
  const t = analyzeFdTrend(samples(1, [600, 750, 900]), 1024);
  assert.equal(t.state, "leaking");
  assert.equal(t.delta, 300);
  assert.equal(t.sampleCount, 3);
});

test("analyzeFdTrend: leaking threshold is exactly 10% of ceiling", () => {
  // 1000 ceiling → leak threshold is 100. A monotonic +100 is leaking.
  const t = analyzeFdTrend(samples(1, [0, 50, 100]), 1000);
  assert.equal(t.state, "leaking");
  assert.equal(t.delta, 100);
  // A monotonic +99 is rising (just under the 10% threshold).
  const t2 = analyzeFdTrend(samples(1, [0, 50, 99]), 1000);
  assert.equal(t2.state, "rising");
});

test("analyzeFdTrend: null ceiling → leak threshold falls back to FD_LEAK_FALLBACK_THRESHOLD", () => {
  // Windows: ceiling is null. A monotonic climb of exactly the fallback
  // threshold is leaking; one below it is rising.
  const leak = analyzeFdTrend(samples(1, [0, 25, FD_LEAK_FALLBACK_THRESHOLD]), null);
  assert.equal(leak.state, "leaking");
  assert.equal(leak.delta, FD_LEAK_FALLBACK_THRESHOLD);

  const rising = analyzeFdTrend(samples(1, [0, 25, FD_LEAK_FALLBACK_THRESHOLD - 1]), null);
  assert.equal(rising.state, "rising");
});

test("analyzeFdTrend: decreasing counts → stable (delta negative)", () => {
  const t = analyzeFdTrend(samples(1, [500, 400, 300]), 1024);
  assert.equal(t.state, "stable");
  assert.equal(t.delta, -200);
});

test("analyzeFdTrend: mixes only samples for the LAST pid (a restart changes the PID)", () => {
  // Samples 1-3 are PID 1, samples 4-5 are PID 2 (a restart). The trend must
  // only consider PID 2's tail — mixing pre- and post-restart would be a false
  // leak signal.
  const mixed: FdSample[] = [
    { ts: 1000, pid: 1, count: 100 },
    { ts: 2000, pid: 1, count: 200 },
    { ts: 3000, pid: 1, count: 300 },
    { ts: 4000, pid: 2, count: 50 },
    { ts: 5000, pid: 2, count: 55 },
  ];
  const t = analyzeFdTrend(mixed, 1024);
  assert.equal(t.sampleCount, 2);
  assert.equal(t.delta, 5);
  assert.equal(t.state, "rising");
});

test("analyzeFdTrend: null-count samples are filtered out", () => {
  // A probe failure (count: null) should NOT be interpolated across.
  const withGap: FdSample[] = [
    { ts: 1000, pid: 1, count: 100 },
    { ts: 2000, pid: 1, count: null },
    { ts: 3000, pid: 1, count: 110 },
  ];
  const t = analyzeFdTrend(withGap, 1024);
  assert.equal(t.sampleCount, 2);
  assert.equal(t.delta, 10);
});

test("analyzeFdTrend: last PID with only null counts → no_history", () => {
  const allNull: FdSample[] = [
    { ts: 1000, pid: 1, count: 100 },
    { ts: 2000, pid: 2, count: null },
  ];
  const t = analyzeFdTrend(allNull, 1024);
  assert.equal(t.state, "no_history");
  assert.equal(t.sampleCount, 0);
});

// ---------------------------------------------------------------------------
// countFileDescriptors + probeFdCeiling — probe dispatch via injectable seams.
// ---------------------------------------------------------------------------

/** Fake fd-count probe that returns a scripted result. */
function makeFakeFdProbe(result: FdCountResult): FdProbe & {
  calls: number[];
} {
  const calls: number[] = [];
  return {
    calls,
    count(pid) {
      calls.push(pid);
      return result;
    },
  };
}

test("countFileDescriptors dispatches to the active fd probe", () => {
  const fake = makeFakeFdProbe({
    count: 42,
    method: "proc",
    approximate: false,
  });
  const restore = setFdProbeForTest(fake);
  try {
    const result = countFileDescriptors(1234);
    assert.equal(result.count, 42);
    assert.deepEqual(fake.calls, [1234]);
  } finally {
    restore();
  }
});

test("countFileDescriptors: restore re-binds the previous probe", () => {
  const fake1 = makeFakeFdProbe({ count: 1, method: "proc", approximate: false });
  const fake2 = makeFakeFdProbe({ count: 2, method: "lsof", approximate: false });
  const restore1 = setFdProbeForTest(fake1);
  const restore2 = setFdProbeForTest(fake2);
  try {
    assert.equal(countFileDescriptors(1).count, 2);
    restore2();
    // After restoring fake2, the active probe is fake1 again.
    assert.equal(countFileDescriptors(1).count, 1);
  } finally {
    restore1();
  }
});

test("setFdProbeForTest(null) re-binds the real OS probe", () => {
  const fake = makeFakeFdProbe({ count: 99, method: "proc", approximate: false });
  const restore = setFdProbeForTest(fake);
  restore();
  const restore2 = setFdProbeForTest(null);
  try {
    // The real probe for an invalid PID returns not_found (no OS call for
    // non-positive pids).
    const result = countFileDescriptors(-1);
    assert.equal(result.count, null);
    assert.equal(result.method, "lsof");
  } finally {
    restore2();
  }
});

/** Fake ceiling probe that returns a scripted result. */
function makeFakeCeilingProbe(result: FdCeilingResult): FdCeilingProbe & {
  calls: number[];
} {
  const calls: number[] = [];
  return {
    calls,
    ceiling(pid) {
      calls.push(pid);
      return result;
    },
  };
}

test("probeFdCeiling dispatches to the active ceiling probe", () => {
  const fake = makeFakeCeilingProbe({
    ceiling: 1024,
    method: "proc_limits",
    approximate: false,
  });
  const restore = setFdCeilingProbeForTest(fake);
  try {
    const result = probeFdCeiling(1234);
    assert.equal(result.ceiling, 1024);
    assert.deepEqual(fake.calls, [1234]);
  } finally {
    restore();
  }
});

test("probeFdCeiling: restore re-binds the previous probe", () => {
  const fake1 = makeFakeCeilingProbe({
    ceiling: 1024,
    method: "proc_limits",
    approximate: false,
  });
  const fake2 = makeFakeCeilingProbe({
    ceiling: null,
    method: "none",
    reason: "windows",
  });
  const restore1 = setFdCeilingProbeForTest(fake1);
  const restore2 = setFdCeilingProbeForTest(fake2);
  try {
    assert.equal(probeFdCeiling(1).ceiling, null);
    restore2();
    assert.equal(probeFdCeiling(1).ceiling, 1024);
  } finally {
    restore1();
  }
});

test("probeFdCeiling: real probe for an invalid PID returns unknown", () => {
  const restore = setFdCeilingProbeForTest(null);
  try {
    const result = probeFdCeiling(-1);
    assert.equal(result.ceiling, null);
    assert.equal(result.method, "none");
  } finally {
    restore();
  }
});
