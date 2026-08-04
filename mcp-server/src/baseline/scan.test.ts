// Offline baseline scanner tests (P15.1).
//
// Fixture-driven coverage for scanProjectOffline: a broken [ext_resource]
// (path + uid both unresolvable) surfaces broken_scene_reference; an attached
// script pointing at nothing additionally surfaces missing_script; a healthy
// scene surfaces nothing. Also verifies the categoriesRun / ciExcludedRules
// contract.
//
// Reuses the makeProject fixture idiom from references.test.ts so the scan
// tests share the same project-shape conventions.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { realpath } from "node:fs/promises";

import { scanProjectOffline, CI_EXCLUDED_RULES, OFFLINE_RULE_IDS } from "./scan.js";

// ---------------------------------------------------------------------------
// fixture builder
// ---------------------------------------------------------------------------

const PROJECT_GODOT = `[application]\nconfig/name="Scan Test"\nconfig/features=PackedStringArray("4.3")\n`;

interface Fixture {
  root: string;
  cleanup: () => Promise<void>;
}

async function makeProject(
  build?: (root: string) => Promise<void>,
): Promise<Fixture> {
  const root = await mkdtemp(join(tmpdir(), "gom-scan-"));
  await writeFile(join(root, "project.godot"), PROJECT_GODOT, "utf-8");
  if (build) await build(root);
  const realRoot = await realpath(root);
  const cleanup = () => rm(root, { recursive: true, force: true });
  return { root: realRoot, cleanup };
}

// ---------------------------------------------------------------------------
// constants
// ---------------------------------------------------------------------------

test("OFFLINE_RULE_IDS is broken_references + missing_scripts", () => {
  assert.deepEqual([...OFFLINE_RULE_IDS], ["broken_references", "missing_scripts"]);
});

test("CI_EXCLUDED_RULES lists the rules the offline scanner does not cover", () => {
  const excluded = [...CI_EXCLUDED_RULES];
  assert.ok(excluded.includes("import_health"));
  assert.ok(excluded.includes("project_health"));
  assert.ok(excluded.includes("scene_structure_health"));
  assert.ok(excluded.includes("materials_shader_health"));
  assert.ok(excluded.includes("script_audit"));
  assert.ok(excluded.includes("animation_analysis"));
});

// ---------------------------------------------------------------------------
// detection
// ---------------------------------------------------------------------------

test("a healthy project surfaces no issues", async () => {
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "Scenes"), { recursive: true });
    await mkdir(join(root, "Scripts"), { recursive: true });
    await writeFile(join(root, "Scripts", "Player.gd"), "extends Node\n", "utf-8");
    await writeFile(
      join(root, "Scenes", "Healthy.tscn"),
      `[gd_scene load_steps=2 format=3]

[ext_resource type="Script" path="res://Scripts/Player.gd" id="1_script"]

[node name="Player" type="Node"]
script = ExtResource("1_script")
`,
      "utf-8",
    );
  });
  try {
    const result = await scanProjectOffline({ projectRoot: fx.root });
    assert.equal(result.issues.length, 0);
    assert.deepEqual(result.categoriesRun, [...OFFLINE_RULE_IDS]);
    assert.equal(result.scannedFiles.length, 1);
    assert.ok(result.scannedFiles[0]!.includes("Healthy.tscn"));
  } finally {
    await fx.cleanup();
  }
});

test("a broken [ext_resource] (path + uid both unresolvable) surfaces broken_scene_reference", async () => {
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "Fixtures"), { recursive: true });
    await writeFile(
      join(root, "Fixtures", "Broken.tscn"),
      `[gd_scene load_steps=2 format=3]

[ext_resource type="Script" uid="uid://demobrokenref0000" path="res://Fixtures/DoesNotExist.gd" id="1_broken"]

[node name="Broken" type="Node"]
script = ExtResource("1_broken")
`,
      "utf-8",
    );
  });
  try {
    const result = await scanProjectOffline({ projectRoot: fx.root });
    assert.equal(result.issues.length, 2); // broken_scene_reference + missing_script
    const broken = result.issues.find((i) => i.ruleId === "broken_references")!;
    assert.equal(broken.issueCode, "broken_scene_reference");
    assert.equal(broken.severity, "Error");
    assert.ok(broken.assetPath.includes("Broken.tscn"));

    const missing = result.issues.find((i) => i.ruleId === "missing_scripts")!;
    assert.equal(missing.issueCode, "missing_script");
    assert.equal(missing.severity, "Error");
  } finally {
    await fx.cleanup();
  }
});

test("an unresolvable ext_resource that is NOT an attached script emits only broken_scene_reference", async () => {
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "Fixtures"), { recursive: true });
    await writeFile(
      join(root, "Fixtures", "BrokenResource.tscn"),
      `[gd_scene load_steps=2 format=3]

[ext_resource type="Texture2D" uid="uid://demobrokentex00000" path="res://Fixtures/Missing.png" id="1_tex"]

[node name="Root" type="Node"]
`,
      "utf-8",
    );
  });
  try {
    const result = await scanProjectOffline({ projectRoot: fx.root });
    assert.equal(result.issues.length, 1);
    assert.equal(result.issues[0]!.ruleId, "broken_references");
    assert.equal(result.issues[0]!.issueCode, "broken_scene_reference");
  } finally {
    await fx.cleanup();
  }
});

test("a resolvable uid heals a stale path (path does not exist but uid maps to a real file)", async () => {
  // The ext_resource path points at a non-existent location, but the uid maps
  // via the sidecar to a file that DOES exist → not broken.
  const UID = "uid://demotarget00001";
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "Scripts"), { recursive: true });
    await mkdir(join(root, "Scenes"), { recursive: true });
    // The real script lives here; its uid sidecar maps UID → this path.
    await writeFile(join(root, "Scripts", "Real.gd"), "extends Node\n", "utf-8");
    await writeFile(join(root, "Scripts", "Real.gd.uid"), UID + "\n", "utf-8");
    // The scene references it by uid but a STALE path.
    await writeFile(
      join(root, "Scenes", "UsesStale.tscn"),
      `[gd_scene load_steps=2 format=3]

[ext_resource type="Script" uid="${UID}" path="res://Scripts/StalePath.gd" id="1_s"]

[node name="N" type="Node"]
script = ExtResource("1_s")
`,
      "utf-8",
    );
  });
  try {
    const result = await scanProjectOffline({ projectRoot: fx.root });
    assert.equal(result.issues.length, 0); // uid healed the stale path
  } finally {
    await fx.cleanup();
  }
});

test("a resolvable path (file exists) is not broken even without a uid", async () => {
  const fx = await makeProject(async (root) => {
    await mkdir(join(root, "Scripts"), { recursive: true });
    await mkdir(join(root, "Scenes"), { recursive: true });
    await writeFile(join(root, "Scripts", "Real.gd"), "extends Node\n", "utf-8");
    await writeFile(
      join(root, "Scenes", "UsesPath.tscn"),
      `[gd_scene load_steps=2 format=3]

[ext_resource type="Script" path="res://Scripts/Real.gd" id="1_s"]

[node name="N" type="Node"]
script = ExtResource("1_s")
`,
      "utf-8",
    );
  });
  try {
    const result = await scanProjectOffline({ projectRoot: fx.root });
    assert.equal(result.issues.length, 0);
  } finally {
    await fx.cleanup();
  }
});

test("the scanner reports ciExcludedRules so a consumer knows the offline scope", async () => {
  const fx = await makeProject();
  try {
    const result = await scanProjectOffline({ projectRoot: fx.root });
    assert.deepEqual(result.ciExcludedRules, [...CI_EXCLUDED_RULES]);
    assert.deepEqual(result.categoriesRun, [...OFFLINE_RULE_IDS]);
    assert.ok(result.durationMs >= 0);
  } finally {
    await fx.cleanup();
  }
});

test("the scanner handles an empty project gracefully", async () => {
  const fx = await makeProject();
  try {
    const result = await scanProjectOffline({ projectRoot: fx.root });
    assert.equal(result.issues.length, 0);
    assert.equal(result.scannedFiles.length, 0);
  } finally {
    await fx.cleanup();
  }
});
