// Baseline schema tests (P15.1).
//
// Covers the issue-key format, baseline build/load/save round-trip, the
// load-result discrimination (missing vs invalid vs schema-version mismatch),
// and the severity-summary counting.
//
// Adapted from Unity Open MCP's BaselineStoreTests.cs (copy for the schema
// shape + load-failure contract; the TS-side loadBaseline discrimination is the
// intentional delta — Unity throws and the CLI sniffs exception types).

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { realpath } from "node:fs/promises";

import {
  BASELINE_SCHEMA_VERSION,
  buildBaseline,
  buildIssueKey,
  errorCountFor,
  loadBaseline,
  normalizeProfile,
  saveBaseline,
  type BaselineFile,
  type BaselineIssue,
} from "./baseline-schema.js";

// ---------------------------------------------------------------------------
// Issue key
// ---------------------------------------------------------------------------

test("buildIssueKey produces {ruleId}|{SEVERITY}|{assetPath}|{issueCode}", () => {
  assert.equal(
    buildIssueKey("broken_references", "Error", "res://Scenes/Main.tscn", "broken_scene_reference"),
    "broken_references|ERROR|res://Scenes/Main.tscn|broken_scene_reference",
  );
  assert.equal(
    buildIssueKey("missing_scripts", "Warning", "res://Foo.tscn", "missing_script"),
    "missing_scripts|WARN|res://Foo.tscn|missing_script",
  );
});

test("buildIssueKey sanitizes pipes in components to _", () => {
  // A pathological assetPath with a pipe must not split the key.
  const key = buildIssueKey("r", "Error", "res://a|b.tscn", "code");
  assert.equal(key, "r|ERROR|res://a_b.tscn|code");
});

// ---------------------------------------------------------------------------
// buildBaseline
// ---------------------------------------------------------------------------

const SAMPLE_ISSUES: BaselineIssue[] = [
  {
    ruleId: "broken_references",
    severity: "Error",
    assetPath: "res://A.tscn",
    issueCode: "broken_scene_reference",
    description: "dangling",
  },
  {
    ruleId: "broken_references",
    severity: "Warning",
    assetPath: "res://B.tscn",
    issueCode: "broken_scene_reference",
    description: "dangling warn",
  },
  {
    ruleId: "missing_scripts",
    severity: "Error",
    assetPath: "res://C.tscn",
    issueCode: "missing_script",
    description: "gone",
  },
];

test("buildBaseline counts severities and groups issue keys per rule", () => {
  const baseline = buildBaseline(
    SAMPLE_ISSUES,
    ["broken_references", "missing_scripts"],
    ["project_health"],
    "desktop",
  );

  assert.equal(baseline.schemaVersion, BASELINE_SCHEMA_VERSION);
  assert.equal(baseline.platformProfile, "desktop");
  assert.equal(baseline.scanner, "offline");
  assert.deepEqual(baseline.summary, { error: 2, warn: 1, info: 0 });
  assert.deepEqual(baseline.ciExcludedRules, ["project_health"]);

  const broken = baseline.rules.find((r) => r.ruleId === "broken_references")!;
  assert.equal(broken.error, 1);
  assert.equal(broken.warn, 1);
  assert.equal(broken.issueKeys.length, 2);
  assert.ok(broken.issueKeys.includes("broken_references|ERROR|res://A.tscn|broken_scene_reference"));
  assert.ok(broken.issueKeys.includes("broken_references|WARN|res://B.tscn|broken_scene_reference"));

  const missing = baseline.rules.find((r) => r.ruleId === "missing_scripts")!;
  assert.equal(missing.error, 1);
  assert.equal(missing.warn, 0);
  assert.equal(missing.issueKeys.length, 1);
});

test("buildBaseline records a rule with no issues as zero counts + empty keys", () => {
  const baseline = buildBaseline([], ["broken_references"], [], "mobile");
  assert.equal(baseline.rules.length, 1);
  assert.equal(baseline.rules[0]!.ruleId, "broken_references");
  assert.equal(baseline.rules[0]!.error, 0);
  assert.equal(baseline.rules[0]!.issueKeys.length, 0);
  assert.deepEqual(baseline.summary, { error: 0, warn: 0, info: 0 });
});

test("normalizeProfile defaults to desktop for unknown input", () => {
  assert.equal(normalizeProfile("mobile"), "mobile");
  assert.equal(normalizeProfile("desktop"), "desktop");
  assert.equal(normalizeProfile("unknown"), "desktop");
  assert.equal(normalizeProfile(undefined), "desktop");
  assert.equal(normalizeProfile(42), "desktop");
});

// ---------------------------------------------------------------------------
// errorCountFor
// ---------------------------------------------------------------------------

test("errorCountFor returns 0 for an absent rule and the count for a present one", () => {
  const baseline = buildBaseline(SAMPLE_ISSUES, ["broken_references", "missing_scripts"], [], "desktop");
  assert.equal(errorCountFor(baseline, "broken_references"), 1);
  assert.equal(errorCountFor(baseline, "missing_scripts"), 1);
  assert.equal(errorCountFor(baseline, "nonexistent"), 0);
  assert.equal(errorCountFor(null, "broken_references"), 0);
  assert.equal(errorCountFor(undefined, "broken_references"), 0);
});

// ---------------------------------------------------------------------------
// save + load round-trip
// ---------------------------------------------------------------------------

test("saveBaseline + loadBaseline round-trip preserves the baseline", async () => {
  const dir = await mkdtemp(join(tmpdir(), "gom-baseline-"));
  const realDir = await realpath(dir);
  try {
    const path = join(realDir, "CI", "godot-open-mcp-baseline.json");
    const original = buildBaseline(SAMPLE_ISSUES, ["broken_references", "missing_scripts"], ["project_health"], "desktop");
    await saveBaseline(original, path);

    const loaded = await loadBaseline(path);
    assert.equal(loaded.ok, true);
    if (loaded.ok) {
      assert.equal(loaded.baseline.schemaVersion, BASELINE_SCHEMA_VERSION);
      assert.equal(loaded.baseline.platformProfile, "desktop");
      assert.equal(loaded.baseline.scanner, "offline");
      assert.deepEqual(loaded.baseline.summary, original.summary);
      assert.equal(loaded.baseline.rules.length, 2);
      assert.deepEqual(loaded.baseline.ciExcludedRules, ["project_health"]);
    }
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("saveBaseline creates parent directories", async () => {
  const dir = await mkdtemp(join(tmpdir(), "gom-baseline-"));
  const realDir = await realpath(dir);
  try {
    const path = join(realDir, "nested", "deep", "baseline.json");
    await saveBaseline(buildBaseline([], [], [], "desktop"), path);
    const loaded = await loadBaseline(path);
    assert.equal(loaded.ok, true);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// load failure discrimination
// ---------------------------------------------------------------------------

test("loadBaseline returns {ok:false, reason:'missing'} for an absent file", async () => {
  const loaded = await loadBaseline("/nonexistent/path/baseline.json");
  assert.equal(loaded.ok, false);
  if (!loaded.ok) {
    assert.equal(loaded.reason, "missing");
    assert.equal(loaded.path, "/nonexistent/path/baseline.json");
  }
});

test("loadBaseline returns {ok:false, reason:'invalid'} for unparseable JSON", async () => {
  const dir = await mkdtemp(join(tmpdir(), "gom-baseline-"));
  const realDir = await realpath(dir);
  try {
    const path = join(realDir, "baseline.json");
    await writeFile(path, "{ not valid json", "utf-8");
    const loaded = await loadBaseline(path);
    assert.equal(loaded.ok, false);
    if (!loaded.ok) {
      assert.equal(loaded.reason, "invalid");
      assert.ok(loaded.message.includes("not valid JSON"));
    }
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("loadBaseline returns {ok:false, reason:'invalid'} for a non-object root", async () => {
  const dir = await mkdtemp(join(tmpdir(), "gom-baseline-"));
  const realDir = await realpath(dir);
  try {
    const path = join(realDir, "baseline.json");
    await writeFile(path, "[]", "utf-8");
    const loaded = await loadBaseline(path);
    assert.equal(loaded.ok, false);
    if (!loaded.ok) assert.equal(loaded.reason, "invalid");
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("loadBaseline returns {ok:false, reason:'invalid'} for a schema-version mismatch", async () => {
  const dir = await mkdtemp(join(tmpdir(), "gom-baseline-"));
  const realDir = await realpath(dir);
  try {
    const path = join(realDir, "baseline.json");
    const stale: BaselineFile = {
      schemaVersion: 999,
      platformProfile: "desktop",
      generatedAt: "2026-01-01T00:00:00Z",
      scanner: "offline",
      summary: { error: 0, warn: 0, info: 0 },
      rules: [],
      ciExcludedRules: [],
    };
    await writeFile(path, JSON.stringify(stale), "utf-8");
    const loaded = await loadBaseline(path);
    assert.equal(loaded.ok, false);
    if (!loaded.ok) {
      assert.equal(loaded.reason, "invalid");
      assert.ok(loaded.message.includes("schema version mismatch"));
      assert.ok(loaded.message.includes(String(BASELINE_SCHEMA_VERSION)));
    }
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("loadBaseline returns {ok:false, reason:'invalid'} when rules is not an array", async () => {
  const dir = await mkdtemp(join(tmpdir(), "gom-baseline-"));
  const realDir = await realpath(dir);
  try {
    const path = join(realDir, "baseline.json");
    await writeFile(
      path,
      JSON.stringify({
        schemaVersion: BASELINE_SCHEMA_VERSION,
        platformProfile: "desktop",
        rules: "not-an-array",
      }),
      "utf-8",
    );
    const loaded = await loadBaseline(path);
    assert.equal(loaded.ok, false);
    if (!loaded.ok) assert.equal(loaded.reason, "invalid");
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("loadBaseline coerces a minimally-shaped valid file", async () => {
  const dir = await mkdtemp(join(tmpdir(), "gom-baseline-"));
  const realDir = await realpath(dir);
  try {
    const path = join(realDir, "baseline.json");
    await writeFile(
      path,
      JSON.stringify({
        schemaVersion: BASELINE_SCHEMA_VERSION,
        rules: [{ ruleId: "broken_references", error: 2, issueKeys: ["broken_references|ERROR|res://X.tscn|broken_scene_reference"] }],
      }),
      "utf-8",
    );
    const loaded = await loadBaseline(path);
    assert.equal(loaded.ok, true);
    if (loaded.ok) {
      assert.equal(loaded.baseline.platformProfile, "desktop"); // defaulted
      assert.equal(loaded.baseline.scanner, "offline");
      assert.equal(loaded.baseline.rules.length, 1);
      assert.equal(loaded.baseline.rules[0]!.error, 2);
    }
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});
