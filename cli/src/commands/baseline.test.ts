// Tests for the `baseline create|update` CLI command (P15.2).
//
// Fixture-driven coverage: create writes a schema-v1 baseline JSON + creates
// parent dirs; update is an alias (overwrite); the summary shape matches P15.1;
// default path is CI/godot-open-mcp-baseline.json; --platform-profile is tagged;
// baseline path resolves relative to project root vs absolute.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, rm, realpath, readFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, isAbsolute } from "node:path";

import { baselineCommand, resolveBaselinePath, DEFAULT_BASELINE_PATH } from "./baseline.js";

// ---------------------------------------------------------------------------
// fixture builder
// ---------------------------------------------------------------------------

const PROJECT_GODOT = `[application]\nconfig/name="Baseline Test"\nconfig/features=PackedStringArray("4.3")\n`;

interface Fixture {
  root: string;
  cleanup: () => Promise<void>;
}

async function makeProject(build?: (root: string) => Promise<void>): Promise<Fixture> {
  const root = await mkdtemp(join(tmpdir(), "gom-baseline-"));
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
// create
// ---------------------------------------------------------------------------

test("baseline create: writes a schema-v1 baseline JSON at the default path", async () => {
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "Scenes"), { recursive: true });
    await writeFile(join(root, "Scenes", "Broken.tscn"), BROKEN_SCENE, "utf-8");
  });
  try {
    const result = await baselineCommand({
      projectPath: fx.root,
      subcommand: "create",
    });
    assert.equal(result.exitCode, 0);
    const json = result.json as { baselinePath: string; schemaVersion: number; rules: Array<{ ruleId: string }> };
    assert.ok(isAbsolute(json.baselinePath), "baselinePath should be absolute");
    assert.ok(json.baselinePath.endsWith(DEFAULT_BASELINE_PATH));
    assert.equal(json.schemaVersion, 1);
    assert.deepEqual(json.rules.map((r) => r.ruleId), ["broken_references", "missing_scripts"]);

    // The file exists on disk and parses as schema v1.
    assert.ok(existsSync(json.baselinePath), "baseline file should exist");
    const written = JSON.parse(await readFile(json.baselinePath, "utf-8"));
    assert.equal(written.schemaVersion, 1);
    assert.equal(written.scanner, "offline");
    assert.deepEqual(written.categoriesRun ?? written.rules.map((r: { ruleId: string }) => r.ruleId), ["broken_references", "missing_scripts"]);
  } finally {
    await fx.cleanup();
  }
});

test("baseline create: creates parent directories when missing", async () => {
  const fx = await makeProject();
  try {
    const result = await baselineCommand({
      projectPath: fx.root,
      subcommand: "create",
      baselinePath: "CI/nested/deep/baseline.json",
    });
    assert.equal(result.exitCode, 0);
    const json = result.json as { baselinePath: string };
    assert.ok(existsSync(json.baselinePath), "nested baseline file should exist");
  } finally {
    await fx.cleanup();
  }
});

test("baseline create: --platform-profile mobile is tagged in the baseline", async () => {
  const fx = await makeProject();
  try {
    const result = await baselineCommand({
      projectPath: fx.root,
      subcommand: "create",
      platformProfile: "mobile",
    });
    assert.equal(result.exitCode, 0);
    const json = result.json as { platformProfile: string; baselinePath: string };
    assert.equal(json.platformProfile, "mobile");
    const written = JSON.parse(await readFile(json.baselinePath, "utf-8"));
    assert.equal(written.platformProfile, "mobile");
  } finally {
    await fx.cleanup();
  }
});

test("baseline create: invalid platform profile defaults to desktop", async () => {
  const fx = await makeProject();
  try {
    const result = await baselineCommand({
      projectPath: fx.root,
      subcommand: "create",
      platformProfile: "not-a-profile",
    });
    assert.equal(result.exitCode, 0);
    const json = result.json as { platformProfile: string };
    assert.equal(json.platformProfile, "desktop");
  } finally {
    await fx.cleanup();
  }
});

// ---------------------------------------------------------------------------
// update alias
// ---------------------------------------------------------------------------

test("baseline update: overwrites an existing baseline (alias for create)", async () => {
  const fx = await makeProject();
  try {
    const first = await baselineCommand({
      projectPath: fx.root,
      subcommand: "create",
    });
    assert.equal(first.exitCode, 0);
    const firstPath = (first.json as { baselinePath: string }).baselinePath;

    const second = await baselineCommand({
      projectPath: fx.root,
      subcommand: "update",
    });
    assert.equal(second.exitCode, 0);
    const secondJson = second.json as { baselinePath: string; subcommand: string };
    assert.equal(secondJson.subcommand, "update");
    assert.equal(secondJson.baselinePath, firstPath);
    assert.ok(existsSync(firstPath), "baseline still exists after update");
  } finally {
    await fx.cleanup();
  }
});

// ---------------------------------------------------------------------------
// human output
// ---------------------------------------------------------------------------

test("baseline create: human output reports 'created' verb + summary", async () => {
  const fx = await makeProject();
  try {
    const result = await baselineCommand({ projectPath: fx.root, subcommand: "create" });
    assert.match(result.human, /Baseline created:/);
    assert.match(result.human, /errors: 0/);
  } finally {
    await fx.cleanup();
  }
});

test("baseline update: human output reports 'updated' verb", async () => {
  const fx = await makeProject();
  try {
    const result = await baselineCommand({ projectPath: fx.root, subcommand: "update" });
    assert.match(result.human, /Baseline updated:/);
  } finally {
    await fx.cleanup();
  }
});

// ---------------------------------------------------------------------------
// resolveBaselinePath
// ---------------------------------------------------------------------------

test("resolveBaselinePath: relative path is joined to project root", () => {
  const resolved = resolveBaselinePath("CI/baseline.json", "/proj");
  assert.equal(resolved, join("/proj", "CI/baseline.json"));
});

test("resolveBaselinePath: absolute path passes through", () => {
  const abs = join("/", "tmp", "baseline.json");
  assert.equal(resolveBaselinePath(abs, "/proj"), abs);
});

test("resolveBaselinePath: empty string returns project root", () => {
  assert.equal(resolveBaselinePath("", "/proj"), "/proj");
});

test("DEFAULT_BASELINE_PATH is CI/godot-open-mcp-baseline.json", () => {
  assert.equal(DEFAULT_BASELINE_PATH, "CI/godot-open-mcp-baseline.json");
});
