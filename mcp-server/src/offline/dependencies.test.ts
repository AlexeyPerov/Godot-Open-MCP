// Offline dependencies tests (P13.2).
//
// Fixture-driven coverage for dependenciesOffline: forward edges, broken
// targets, cycle detection, transitive impact + truncation, summary detail,
// and reverse-edge reuse via findReferencesOffline.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { realpath } from "node:fs/promises";

import { dependenciesOffline } from "./dependencies.js";

const PROJECT_GODOT = `[application]\nconfig/name="Dep Test"\nconfig/features=PackedStringArray("4.3")\n`;

const BASE_UID = "uid://depbase000001";
const SCRIPT_UID = "uid://depscript00001";
const CYCLE_A_UID = "uid://cyclea0000001";
const CYCLE_B_UID = "uid://cycleb0000001";

interface Fixture {
  root: string;
  cleanup: () => Promise<void>;
}

async function makeProject(
  build?: (root: string) => Promise<void>,
): Promise<Fixture> {
  const root = await mkdtemp(join(tmpdir(), "gom-deps-"));
  await writeFile(join(root, "project.godot"), PROJECT_GODOT, "utf-8");
  if (build) await build(root);
  const realRoot = await realpath(root);
  const cleanup = () => rm(root, { recursive: true, force: true });
  return { root: realRoot, cleanup };
}

async function seedStandardGraph(root: string): Promise<void> {
  await mkdir(join(root, "Resources"), { recursive: true });
  await mkdir(join(root, "Scenes"), { recursive: true });
  await mkdir(join(root, "Scripts"), { recursive: true });

  await writeFile(
    join(root, "Scripts", "DemoData.gd"),
    "extends Resource\n",
    "utf-8",
  );

  await writeFile(
    join(root, "Resources", "DemoData.tres"),
    `[gd_resource type="Resource" load_steps=2 format=3 uid="${BASE_UID}"]

[ext_resource type="Script" path="res://Scripts/DemoData.gd" id="1_script"]

[resource]
script = ExtResource("1_script")
`,
    "utf-8",
  );
  await writeFile(
    join(root, "Resources", "DemoData.tres.uid"),
    BASE_UID + "\n",
    "utf-8",
  );

  await writeFile(
    join(root, "Scripts", "Player.gd"),
    "extends Node\n",
    "utf-8",
  );
  await writeFile(
    join(root, "Scripts", "Player.gd.uid"),
    SCRIPT_UID + "\n",
    "utf-8",
  );

  await writeFile(
    join(root, "Scenes", "UsesData.tscn"),
    `[gd_scene load_steps=3 format=3]

[ext_resource type="Resource" uid="${BASE_UID}" path="res://Resources/DemoData.tres" id="1_data"]
[ext_resource type="Script" uid="${SCRIPT_UID}" path="res://Scripts/Player.gd" id="2_script"]

[node name="Root" type="Node"]
script = ExtResource("2_script")
`,
    "utf-8",
  );

  await writeFile(
    join(root, "Scenes", "UsesDataPathOnly.tscn"),
    `[gd_scene load_steps=2 format=3]

[ext_resource type="Resource" path="res://Resources/DemoData.tres" id="1_data"]

[node name="Root" type="Node"]
`,
    "utf-8",
  );
}

async function seedBrokenForward(root: string): Promise<void> {
  await mkdir(join(root, "Scenes"), { recursive: true });
  await writeFile(
    join(root, "Scenes", "Broken.tscn"),
    `[gd_scene load_steps=2 format=3]

[ext_resource type="Resource" uid="uid://missing000001" path="res://Missing/Gone.tres" id="1_gone"]

[node name="Root" type="Node"]
`,
    "utf-8",
  );
}

async function seedCycle(root: string): Promise<void> {
  await mkdir(join(root, "Resources"), { recursive: true });
  await writeFile(
    join(root, "Resources", "CycleA.tres"),
    `[gd_resource type="Resource" load_steps=2 format=3 uid="${CYCLE_A_UID}"]

[ext_resource type="Resource" uid="${CYCLE_B_UID}" path="res://Resources/CycleB.tres" id="1_b"]

[resource]
`,
    "utf-8",
  );
  await writeFile(
    join(root, "Resources", "CycleA.tres.uid"),
    CYCLE_A_UID + "\n",
    "utf-8",
  );
  await writeFile(
    join(root, "Resources", "CycleB.tres"),
    `[gd_resource type="Resource" load_steps=2 format=3 uid="${CYCLE_B_UID}"]

[ext_resource type="Resource" uid="${CYCLE_A_UID}" path="res://Resources/CycleA.tres" id="1_a"]

[resource]
`,
    "utf-8",
  );
  await writeFile(
    join(root, "Resources", "CycleB.tres.uid"),
    CYCLE_B_UID + "\n",
    "utf-8",
  );
}

async function seedImpactChain(root: string): Promise<void> {
  await mkdir(join(root, "Resources"), { recursive: true });
  await mkdir(join(root, "Scenes"), { recursive: true });

  const leafUid = "uid://impactleaf0001";
  await writeFile(
    join(root, "Resources", "Leaf.tres"),
    `[gd_resource type="Resource" load_steps=1 format=3 uid="${leafUid}"]

[resource]
`,
    "utf-8",
  );
  await writeFile(
    join(root, "Resources", "Leaf.tres.uid"),
    leafUid + "\n",
    "utf-8",
  );

  await writeFile(
    join(root, "Scenes", "Level1.tscn"),
    `[gd_scene load_steps=2 format=3]

[ext_resource type="Resource" uid="${leafUid}" path="res://Resources/Leaf.tres" id="1_leaf"]

[node name="Root" type="Node"]
`,
    "utf-8",
  );

  await writeFile(
    join(root, "Scenes", "Level2.tscn"),
    `[gd_scene load_steps=2 format=3]

[ext_resource type="Resource" path="res://Scenes/Level1.tscn" id="1_l1"]

[node name="Root" type="Node"]
`,
    "utf-8",
  );

  await writeFile(
    join(root, "Scenes", "Level3.tscn"),
    `[gd_scene load_steps=2 format=3]

[ext_resource type="Resource" path="res://Scenes/Level2.tscn" id="1_l2"]

[node name="Root" type="Node"]
`,
    "utf-8",
  );
}

test("dependenciesOffline forward edges from ext_resource headers", async () => {
  const f = await makeProject(seedStandardGraph);
  try {
    const result = await dependenciesOffline({
      assetPath: "res://Resources/DemoData.tres",
      detail: "normal",
      projectRoot: f.root,
    });
    assert.equal(result.forwardCount, 1);
    assert.equal(result.forwardDependencies[0]?.assetPath, "res://Scripts/DemoData.gd");
    assert.equal(result.forwardDependencies[0]?.resolved, true);
    assert.deepEqual(result.brokenForwardUids, []);
  } finally {
    await f.cleanup();
  }
});

test("dependenciesOffline reverse edges find referencing scenes", async () => {
  const f = await makeProject(seedStandardGraph);
  try {
    const result = await dependenciesOffline({
      assetPath: "res://Resources/DemoData.tres",
      detail: "normal",
      projectRoot: f.root,
    });
    assert.ok(result.reverseCount >= 2);
    const paths = result.reverseDependencies.map((e) => e.assetPath).sort();
    assert.ok(paths.includes("res://Scenes/UsesData.tscn"));
    assert.ok(paths.includes("res://Scenes/UsesDataPathOnly.tscn"));
  } finally {
    await f.cleanup();
  }
});

test("dependenciesOffline flags broken forward uids", async () => {
  const f = await makeProject(seedBrokenForward);
  try {
    const result = await dependenciesOffline({
      assetPath: "res://Scenes/Broken.tscn",
      detail: "normal",
      projectRoot: f.root,
    });
    assert.equal(result.forwardCount, 1);
    assert.equal(result.forwardDependencies[0]?.resolved, false);
    assert.deepEqual(result.brokenForwardUids, ["uid://missing000001"]);
  } finally {
    await f.cleanup();
  }
});

test("dependenciesOffline detects dependency cycle", async () => {
  const f = await makeProject(seedCycle);
  try {
    const result = await dependenciesOffline({
      assetPath: "res://Resources/CycleA.tres",
      detail: "normal",
      projectRoot: f.root,
    });
    assert.ok(result.cycles.length >= 1, "expected at least one cycle path");
    const cycle = result.cycles[0]!;
    assert.ok(cycle.includes("res://Resources/CycleA.tres"));
    assert.ok(cycle.includes("res://Resources/CycleB.tres"));
  } finally {
    await f.cleanup();
  }
});

test("dependenciesOffline include_impact returns hop depths", async () => {
  const f = await makeProject(seedImpactChain);
  try {
    const result = await dependenciesOffline({
      assetPath: "res://Resources/Leaf.tres",
      includeImpact: true,
      maxImpactDepth: 3,
      detail: "normal",
      projectRoot: f.root,
    });
    assert.ok(result.impact);
    assert.equal(result.impact!.affectedCount, 3);
    const depths = new Map(
      result.impact!.affected.map((e) => [e.assetPath, e.depth]),
    );
    assert.equal(depths.get("res://Scenes/Level1.tscn"), 1);
    assert.equal(depths.get("res://Scenes/Level2.tscn"), 2);
    assert.equal(depths.get("res://Scenes/Level3.tscn"), 3);
    // Frontier is non-empty at max depth (Level3) — closure may extend further.
    assert.equal(result.impact!.truncated, true);
  } finally {
    await f.cleanup();
  }
});

test("dependenciesOffline impact truncated at max_impact_depth", async () => {
  const f = await makeProject(seedImpactChain);
  try {
    const result = await dependenciesOffline({
      assetPath: "res://Resources/Leaf.tres",
      includeImpact: true,
      maxImpactDepth: 2,
      detail: "normal",
      projectRoot: f.root,
    });
    assert.ok(result.impact);
    assert.equal(result.impact!.affectedCount, 2);
    assert.equal(result.impact!.truncated, true);
  } finally {
    await f.cleanup();
  }
});

test("dependenciesOffline detail summary returns counts only", async () => {
  const f = await makeProject(seedStandardGraph);
  try {
    const result = await dependenciesOffline({
      assetPath: "res://Resources/DemoData.tres",
      detail: "summary",
      projectRoot: f.root,
    });
    assert.equal(result.forwardCount, 1);
    assert.equal(result.reverseCount, 2);
    assert.deepEqual(result.forwardDependencies, []);
    assert.deepEqual(result.reverseDependencies, []);
  } finally {
    await f.cleanup();
  }
});

test("dependenciesOffline caps reverse roster with max_results", async () => {
  const f = await makeProject(seedStandardGraph);
  try {
    const result = await dependenciesOffline({
      assetPath: "res://Resources/DemoData.tres",
      maxResults: 1,
      detail: "normal",
      projectRoot: f.root,
    });
    assert.equal(result.reverseDependencies.length, 1);
    assert.equal(result.reverseCount, 2);
    assert.equal(result.truncated, 1);
  } finally {
    await f.cleanup();
  }
});

test("dependenciesOffline tags _source offline", async () => {
  const f = await makeProject(seedStandardGraph);
  try {
    const result = await dependenciesOffline({
      assetPath: "res://Resources/DemoData.tres",
      projectRoot: f.root,
    });
    assert.equal(result._source, "offline");
  } finally {
    await f.cleanup();
  }
});
