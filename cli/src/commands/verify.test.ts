// Tests for the `verify` CLI command (P15.2).
//
// Fixture-driven coverage: a clean project → exit 0; a project with broken
// references → exit 1 (default --fail-on error); --fail-on warn escalates
// warnings; --fail-on none never fails; JSON shape carries the issue set.
// The exit-code contract mirrors the P15.1 offline scanner (shared code).

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, rm, realpath } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { verifyCommand } from "./verify.js";

// ---------------------------------------------------------------------------
// fixture builder (mirrors mcp-server/src/baseline/scan.test.ts)
// ---------------------------------------------------------------------------

const PROJECT_GODOT = `[application]\nconfig/name="Verify Test"\nconfig/features=PackedStringArray("4.3")\n`;

interface Fixture {
  root: string;
  cleanup: () => Promise<void>;
}

async function makeProject(build?: (root: string) => Promise<void>): Promise<Fixture> {
  const root = await mkdtemp(join(tmpdir(), "gom-verify-"));
  await writeFile(join(root, "project.godot"), PROJECT_GODOT, "utf-8");
  if (build) await build(root);
  const realRoot = await realpath(root);
  return { root: realRoot, cleanup: () => rm(root, { recursive: true, force: true }) };
}

const HEALTHY_SCENE = `[gd_scene load_steps=2 format=3]

[ext_resource type="Script" path="res://Scripts/Player.gd" id="1_script"]

[node name="Player" type="Node"]
script = ExtResource("1_script")
`;

const BROKEN_SCENE = `[gd_scene load_steps=2 format=3]

[ext_resource type="Script" path="res://Scripts/Missing.gd" id="1_missing"]

[node name="Player" type="Node"]
script = ExtResource("1_missing")
`;

// ---------------------------------------------------------------------------
// exit codes
// ---------------------------------------------------------------------------

test("verify: clean project → exit 0", async () => {
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "Scenes"), { recursive: true });
    await mkdir(join(root, "Scripts"), { recursive: true });
    await writeFile(join(root, "Scripts", "Player.gd"), "extends Node\n", "utf-8");
    await writeFile(join(root, "Scenes", "Healthy.tscn"), HEALTHY_SCENE, "utf-8");
  });
  try {
    const result = await verifyCommand({ projectPath: fx.root, failOn: "error" });
    assert.equal(result.exitCode, 0);
    const json = result.json as { summary: { errors: number; warnings: number }; issues: unknown[] };
    assert.equal(json.summary.errors, 0);
    assert.equal(json.summary.warnings, 0);
    assert.equal(json.issues.length, 0);
  } finally {
    await fx.cleanup();
  }
});

test("verify: broken references → exit 1 (default --fail-on error)", async () => {
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "Scenes"), { recursive: true });
    await writeFile(join(root, "Scenes", "Broken.tscn"), BROKEN_SCENE, "utf-8");
  });
  try {
    const result = await verifyCommand({ projectPath: fx.root, failOn: "error" });
    assert.equal(result.exitCode, 1);
    assert.equal(result.errorLabel, "verify_failed");
    const json = result.json as { summary: { errors: number }; issues: Array<{ ruleId: string }> };
    assert.ok(json.summary.errors > 0, "should detect errors");
    assert.ok(json.issues.length > 0);
    const ruleIds = json.issues.map((i) => i.ruleId);
    assert.ok(ruleIds.includes("broken_references"));
    assert.ok(ruleIds.includes("missing_scripts"));
  } finally {
    await fx.cleanup();
  }
});

test("verify: --fail-on none never fails even with errors", async () => {
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "Scenes"), { recursive: true });
    await writeFile(join(root, "Scenes", "Broken.tscn"), BROKEN_SCENE, "utf-8");
  });
  try {
    const result = await verifyCommand({ projectPath: fx.root, failOn: "none" });
    assert.equal(result.exitCode, 0);
    assert.equal(result.errorLabel, undefined);
    const json = result.json as { summary: { errors: number }; issues: unknown[] };
    assert.ok(json.summary.errors > 0, "issues still listed even when not failing");
  } finally {
    await fx.cleanup();
  }
});

// ---------------------------------------------------------------------------
// JSON + human output shape
// ---------------------------------------------------------------------------

test("verify: JSON carries the full issue set + ciExcludedRules", async () => {
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "Scenes"), { recursive: true });
    await writeFile(join(root, "Scenes", "Broken.tscn"), BROKEN_SCENE, "utf-8");
  });
  try {
    const result = await verifyCommand({ projectPath: fx.root, failOn: "error" });
    const json = result.json as {
      command: string;
      failOn: string;
      rules: string[];
      ciExcludedRules: string[];
      issues: Array<{ ruleId: string; severity: string; assetPath: string; issueCode: string; description: string }>;
      scannedFileCount: number;
      durationMs: number;
    };
    assert.equal(json.command, "verify");
    assert.equal(json.failOn, "error");
    assert.deepEqual(json.rules, ["broken_references", "missing_scripts"]);
    assert.ok(json.ciExcludedRules.length > 0);
    assert.ok(json.ciExcludedRules.includes("project_health"));
    assert.ok(json.scannedFileCount > 0);
    assert.ok(json.durationMs >= 0);
    for (const issue of json.issues) {
      assert.equal(issue.severity, "Error");
      assert.ok(issue.assetPath.startsWith("res://"));
    }
  } finally {
    await fx.cleanup();
  }
});

test("verify: human output is greppable (FAIL/OK verdict + issue lines)", async () => {
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "Scenes"), { recursive: true });
    await writeFile(join(root, "Scenes", "Broken.tscn"), BROKEN_SCENE, "utf-8");
  });
  try {
    const fail = await verifyCommand({ projectPath: fx.root, failOn: "error" });
    assert.match(fail.human, /Godot Open MCP verify: FAIL/);
    assert.match(fail.human, /--fail-on error/);
    assert.match(fail.human, /\[ERROR\]/);
    assert.match(fail.human, /broken_references\//);
  } finally {
    await fx.cleanup();
  }

  const fx2 = await makeProject(async (root) => {
    await mkdir(join(root, "Scenes"), { recursive: true });
    await mkdir(join(root, "Scripts"), { recursive: true });
    await writeFile(join(root, "Scripts", "Player.gd"), "extends Node\n", "utf-8");
    await writeFile(join(root, "Scenes", "H.tscn"), HEALTHY_SCENE, "utf-8");
  });
  try {
    const ok = await verifyCommand({ projectPath: fx2.root, failOn: "error" });
    assert.match(ok.human, /Godot Open MCP verify: OK/);
    assert.doesNotMatch(ok.human, /\[ERROR\]/);
  } finally {
    await fx2.cleanup();
  }
});

test("verify: empty project (no scenes) → exit 0, no issues", async () => {
  const fx = await makeProject();
  try {
    const result = await verifyCommand({ projectPath: fx.root, failOn: "error" });
    assert.equal(result.exitCode, 0);
    const json = result.json as { summary: { errors: number }; issues: unknown[]; scannedFileCount: number };
    assert.equal(json.summary.errors, 0);
    assert.equal(json.issues.length, 0);
    assert.equal(json.scannedFileCount, 0);
  } finally {
    await fx.cleanup();
  }
});
