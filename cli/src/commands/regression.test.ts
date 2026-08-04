// Tests for the `regression check` CLI command (P15.2).
//
// Fixture-driven coverage of the full exit-code contract:
//   0 — no regression (current errors ≤ baseline + threshold)
//   1 — regression (error-count delta > threshold, or per-category breach)
//   2 — baseline missing
//   3 — baseline invalid (unreadable / unparseable / schema-version mismatch)
//
// Reuses baseline create to seed the baseline, then mutates the fixture to
// introduce (or remove) issues and re-checks.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, rm, realpath } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { baselineCommand } from "./baseline.js";
import { regressionCommand, resolveBaselinePath } from "./regression.js";
import { BASELINE_SCHEMA_VERSION } from "../baseline/baseline-schema.js";

// ---------------------------------------------------------------------------
// fixture builder
// ---------------------------------------------------------------------------

const PROJECT_GODOT = `[application]\nconfig/name="Regression Test"\nconfig/features=PackedStringArray("4.3")\n`;

interface Fixture {
  root: string;
  cleanup: () => Promise<void>;
}

async function makeProject(build?: (root: string) => Promise<void>): Promise<Fixture> {
  const root = await mkdtemp(join(tmpdir(), "gom-regression-"));
  await writeFile(join(root, "project.godot"), PROJECT_GODOT, "utf-8");
  if (build) await build(root);
  const realRoot = await realpath(root);
  return { root: realRoot, cleanup: () => rm(root, { recursive: true, force: true }) };
}

const BROKEN_SCENE = `[gd_scene load_steps=2 format=3]

[ext_resource type="Script" path="res://Scripts/Missing.gd" id="1_missing"]

[node name="Player" type="Node"]
script = ExtResource("1_missing")
`;

// ---------------------------------------------------------------------------
// exit 2 — baseline missing
// ---------------------------------------------------------------------------

test("regression check: exit 2 when the baseline file does not exist", async () => {
  const fx = await makeProject();
  try {
    const result = await regressionCommand({
      projectPath: fx.root,
      subcommand: "check",
      baselinePath: "CI/does-not-exist.json",
    });
    assert.equal(result.exitCode, 2);
    assert.equal(result.errorLabel, "baseline_missing");
    const json = result.json as { exitCode: number; error: { code: string } };
    assert.equal(json.exitCode, 2);
    assert.equal(json.error.code, "baseline_missing");
    assert.match(result.human, /baseline_missing/);
  } finally {
    await fx.cleanup();
  }
});

// ---------------------------------------------------------------------------
// exit 3 — baseline invalid
// ---------------------------------------------------------------------------

test("regression check: exit 3 when the baseline is unparseable JSON", async () => {
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "CI"), { recursive: true });
    await writeFile(join(root, "CI", "bad.json"), "{ not valid json", "utf-8");
  });
  try {
    const result = await regressionCommand({
      projectPath: fx.root,
      subcommand: "check",
      baselinePath: "CI/bad.json",
    });
    assert.equal(result.exitCode, 3);
    assert.equal(result.errorLabel, "baseline_invalid");
    const json = result.json as { exitCode: number; error: { code: string } };
    assert.equal(json.exitCode, 3);
    assert.equal(json.error.code, "baseline_invalid");
  } finally {
    await fx.cleanup();
  }
});

test("regression check: exit 3 when the baseline schema version mismatches", async () => {
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "CI"), { recursive: true });
    const stale = {
      schemaVersion: 999,
      platformProfile: "desktop",
      generatedAt: "2024-01-01T00:00:00Z",
      scanner: "offline",
      summary: { error: 0, warn: 0, info: 0 },
      rules: [],
      ciExcludedRules: [],
    };
    await writeFile(join(root, "CI", "stale.json"), JSON.stringify(stale), "utf-8");
  });
  try {
    const result = await regressionCommand({
      projectPath: fx.root,
      subcommand: "check",
      baselinePath: "CI/stale.json",
    });
    assert.equal(result.exitCode, 3);
    const json = result.json as { error: { message: string } };
    assert.match(json.error.message, /schema version mismatch/);
  } finally {
    await fx.cleanup();
  }
});

// ---------------------------------------------------------------------------
// exit 0 — no regression
// ---------------------------------------------------------------------------

test("regression check: exit 0 when the project matches the baseline", async () => {
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "Scenes"), { recursive: true });
    await writeFile(join(root, "Scenes", "Broken.tscn"), BROKEN_SCENE, "utf-8");
  });
  try {
    // Seed baseline from the current (broken) state.
    const seed = await baselineCommand({
      projectPath: fx.root,
      subcommand: "create",
    });
    assert.equal(seed.exitCode, 0);

    // Re-check against the same state → no regression.
    const result = await regressionCommand({
      projectPath: fx.root,
      subcommand: "check",
    });
    assert.equal(result.exitCode, 0);
    const json = result.json as { regressed: boolean; exitCode: number };
    assert.equal(json.regressed, false);
    assert.equal(json.exitCode, 0);
  } finally {
    await fx.cleanup();
  }
});

test("regression check: exit 0 when errors decreased (improvement)", async () => {
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "Scenes"), { recursive: true });
    await writeFile(join(root, "Scenes", "Broken.tscn"), BROKEN_SCENE, "utf-8");
  });
  try {
    const seed = await baselineCommand({ projectPath: fx.root, subcommand: "create" });
    assert.equal(seed.exitCode, 0);

    // Remove the broken scene → fewer errors than baseline.
    await rm(join(fx.root, "Scenes", "Broken.tscn"));

    const result = await regressionCommand({
      projectPath: fx.root,
      subcommand: "check",
    });
    assert.equal(result.exitCode, 0);
    const json = result.json as { regressed: boolean };
    assert.equal(json.regressed, false);
  } finally {
    await fx.cleanup();
  }
});

// ---------------------------------------------------------------------------
// exit 1 — regression
// ---------------------------------------------------------------------------

test("regression check: exit 1 when a new broken scene appears (delta > threshold 0)", async () => {
  const fx = await makeProject();
  try {
    // Seed a clean baseline.
    const seed = await baselineCommand({ projectPath: fx.root, subcommand: "create" });
    assert.equal(seed.exitCode, 0);

    // Introduce a broken scene → errors increased.
    await mkdir(join(fx.root, "Scenes"), { recursive: true });
    await writeFile(join(fx.root, "Scenes", "Broken.tscn"), BROKEN_SCENE, "utf-8");

    const result = await regressionCommand({
      projectPath: fx.root,
      subcommand: "check",
    });
    assert.equal(result.exitCode, 1);
    assert.equal(result.errorLabel, "regression_detected");
    const json = result.json as { regressed: boolean; exitCode: number; summary: string };
    assert.equal(json.regressed, true);
    assert.equal(json.exitCode, 1);
    assert.match(json.summary, /REGRESSION/);
  } finally {
    await fx.cleanup();
  }
});

test("regression check: delta equal to threshold is TOLERATED (strict >)", async () => {
  // Baseline has 0 errors; we introduce exactly 1 broken scene (2 issues:
  // broken_references + missing_scripts). With --threshold 2 the delta equals
  // the threshold → tolerated (exit 0). With --threshold 1 → exit 1.
  const fx = await makeProject();
  try {
    const seed = await baselineCommand({ projectPath: fx.root, subcommand: "create" });
    assert.equal(seed.exitCode, 0);
    const seedJson = seed.json as { summary: { error: number } };
    const baselineErrors = seedJson.summary.error;
    assert.equal(baselineErrors, 0, "fixture should start clean");

    await mkdir(join(fx.root, "Scenes"), { recursive: true });
    await writeFile(join(fx.root, "Scenes", "Broken.tscn"), BROKEN_SCENE, "utf-8");

    // First measure the actual delta.
    const measure = await regressionCommand({
      projectPath: fx.root,
      subcommand: "check",
      threshold: 999, // high so it passes and we can read the delta
    });
    const measureJson = measure.json as { regression: { errorDelta: number } };
    const delta = measureJson.regression.errorDelta;
    assert.ok(delta > 0, `expected positive delta, got ${delta}`);

    // delta == threshold → tolerated (exit 0)
    const atThreshold = await regressionCommand({
      projectPath: fx.root,
      subcommand: "check",
      threshold: delta,
    });
    assert.equal(atThreshold.exitCode, 0);

    // delta > threshold → regression (exit 1)
    const belowThreshold = await regressionCommand({
      projectPath: fx.root,
      subcommand: "check",
      threshold: delta - 1,
    });
    assert.equal(belowThreshold.exitCode, 1);
  } finally {
    await fx.cleanup();
  }
});

// ---------------------------------------------------------------------------
// per-category thresholds
// ---------------------------------------------------------------------------

test("regression check: per-category threshold can tolerate a global breach", async () => {
  // Baseline 0 errors. Introduce 1 broken scene. Global threshold 0 would
  // normally fail, but set a per-category threshold of 5 for broken_references
  // AND missing_scripts. However the global gate is OR'd with per-rule — so the
  // global gate (delta > 0) still fires. To truly test per-category tolerance
  // we set the global threshold high enough that only the per-rule gate matters.
  const fx = await makeProject();
  try {
    await baselineCommand({ projectPath: fx.root, subcommand: "create" });
    await mkdir(join(fx.root, "Scenes"), { recursive: true });
    await writeFile(join(fx.root, "Scenes", "Broken.tscn"), BROKEN_SCENE, "utf-8");

    const perCat = new Map<string, number>([
      ["broken_references", 5],
      ["missing_scripts", 5],
    ]);

    // Global threshold 0 → global gate fires (regression).
    const fail = await regressionCommand({
      projectPath: fx.root,
      subcommand: "check",
      threshold: 0,
      perCategoryThresholds: perCat,
    });
    assert.equal(fail.exitCode, 1, "global gate should fire regardless of per-category");

    // Global threshold high → only per-category gates matter, all tolerated.
    const pass = await regressionCommand({
      projectPath: fx.root,
      subcommand: "check",
      threshold: 999,
      perCategoryThresholds: perCat,
    });
    assert.equal(pass.exitCode, 0, "per-category thresholds tolerate the delta");
    const passJson = pass.json as { regression: { perRule: Array<{ ruleId: string; regressed: boolean }> | null } };
    assert.ok(passJson.regression.perRule !== null, "per-rule detail should be present");
    for (const r of passJson.regression.perRule!) {
      assert.equal(r.regressed, false);
    }
  } finally {
    await fx.cleanup();
  }
});

test("regression check: per-category fallback to global threshold", async () => {
  // A rule with no explicit per-category entry falls back to the global threshold.
  const fx = await makeProject();
  try {
    await baselineCommand({ projectPath: fx.root, subcommand: "create" });
    await mkdir(join(fx.root, "Scenes"), { recursive: true });
    await writeFile(join(fx.root, "Scenes", "Broken.tscn"), BROKEN_SCENE, "utf-8");

    // Per-category entry for a rule that has no issues; the real rules fall back
    // to global threshold 0 → regression.
    const perCat = new Map<string, number>([["nonexistent_rule", 99]]);
    const result = await regressionCommand({
      projectPath: fx.root,
      subcommand: "check",
      threshold: 0,
      perCategoryThresholds: perCat,
    });
    assert.equal(result.exitCode, 1);
    const json = result.json as { regression: { perRule: Array<{ ruleId: string; errorThreshold: number; regressed: boolean }> } };
    const brokenRefs = json.regression.perRule.find((r) => r.ruleId === "broken_references");
    assert.ok(brokenRefs, "broken_references should appear in per-rule detail");
    assert.equal(brokenRefs!.errorThreshold, 0, "should fall back to global threshold");
    assert.equal(brokenRefs!.regressed, true);
  } finally {
    await fx.cleanup();
  }
});

// ---------------------------------------------------------------------------
// JSON shape + human summary
// ---------------------------------------------------------------------------

test("regression check: JSON carries schemaVersion, summary, regression detail", async () => {
  const fx = await makeProject();
  try {
    await baselineCommand({ projectPath: fx.root, subcommand: "create" });
    const result = await regressionCommand({ projectPath: fx.root, subcommand: "check" });
    const json = result.json as {
      command: string;
      subcommand: string;
      baselinePath: string;
      schemaVersion: number;
      regressed: boolean;
      summary: string;
      regression: { errorDelta: number; errorThreshold: number; perRule: unknown };
    };
    assert.equal(json.command, "regression");
    assert.equal(json.subcommand, "check");
    assert.equal(json.schemaVersion, BASELINE_SCHEMA_VERSION);
    assert.equal(typeof json.summary, "string");
    assert.equal(typeof json.regression.errorDelta, "number");
  } finally {
    await fx.cleanup();
  }
});

test("regression check: human summary is greppable (REGRESSION / OK)", async () => {
  const fx = await makeProject();
  try {
    await baselineCommand({ projectPath: fx.root, subcommand: "create" });

    const ok = await regressionCommand({ projectPath: fx.root, subcommand: "check" });
    assert.match(ok.human, /Godot Open MCP regression: OK/);

    await mkdir(join(fx.root, "Scenes"), { recursive: true });
    await writeFile(join(fx.root, "Scenes", "Broken.tscn"), BROKEN_SCENE, "utf-8");

    const fail = await regressionCommand({ projectPath: fx.root, subcommand: "check" });
    assert.match(fail.human, /Godot Open MCP regression: REGRESSION/);
  } finally {
    await fx.cleanup();
  }
});

// ---------------------------------------------------------------------------
// resolveBaselinePath parity with baseline command
// ---------------------------------------------------------------------------

test("resolveBaselinePath: relative path joined to project root", () => {
  assert.equal(resolveBaselinePath("CI/baseline.json", "/proj"), join("/proj", "CI/baseline.json"));
});

test("resolveBaselinePath: absolute path passes through", () => {
  const abs = join("/", "tmp", "baseline.json");
  assert.equal(resolveBaselinePath(abs, "/proj"), abs);
});
