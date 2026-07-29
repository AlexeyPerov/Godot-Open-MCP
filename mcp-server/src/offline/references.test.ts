// Offline reverse-reference lookup tests (P13.1).
//
// Fixture-driven coverage for findReferencesOffline: ext_resource path/uid
// edges, script attachment via ExtResource, uid↔path query parity, compact/
// balanced/full profiles, paging cursors, and unresolved_uid surfacing.
//
// Adapted from Unity Open MCP's findReferencesOffline tests in offline.test.ts
// (adapt for Godot `[ext_resource]` / `uid://` tokens).

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { realpath } from "node:fs/promises";

import { findReferencesOffline } from "./references.js";
import { buildUidPathIndex } from "./project-index.js";

// ---------------------------------------------------------------------------
// fixture builder
// ---------------------------------------------------------------------------

const PROJECT_GODOT = `[application]\nconfig/name="Ref Test"\nconfig/features=PackedStringArray("4.3")\n`;

const TARGET_UID = "uid://demotarget00001";
const SCRIPT_UID = "uid://demoscript00001";

interface Fixture {
  root: string;
  cleanup: () => Promise<void>;
}

async function makeProject(
  build?: (root: string) => Promise<void>,
): Promise<Fixture> {
  const root = await mkdtemp(join(tmpdir(), "gom-refs-"));
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

  // Target resource with its own uid in the header.
  await writeFile(
    join(root, "Resources", "DemoData.tres"),
    `[gd_resource type="Resource" load_steps=2 format=3 uid="${TARGET_UID}"]

[ext_resource type="Script" path="res://Scripts/DemoData.gd" id="1_script"]

[resource]
script = ExtResource("1_script")
`,
    "utf-8",
  );
  await writeFile(join(root, "Resources", "DemoData.tres.uid"), TARGET_UID + "\n", "utf-8");

  await writeFile(
    join(root, "Scripts", "DemoData.gd"),
    `extends Resource\n`,
    "utf-8",
  );
  await writeFile(
    join(root, "Scripts", "Player.gd"),
    `extends Node\n`,
    "utf-8",
  );
  await writeFile(join(root, "Scripts", "Player.gd.uid"), SCRIPT_UID + "\n", "utf-8");

  // Scene that references the .tres via ext_resource path + uid.
  await writeFile(
    join(root, "Scenes", "UsesData.tscn"),
    `[gd_scene load_steps=3 format=3]

[ext_resource type="Resource" uid="${TARGET_UID}" path="res://Resources/DemoData.tres" id="1_data"]
[ext_resource type="Script" uid="${SCRIPT_UID}" path="res://Scripts/Player.gd" id="2_script"]

[node name="Root" type="Node"]

[node name="Holder" type="Node" parent="."]
script = ExtResource("2_script")
`,
    "utf-8",
  );

  // Second scene that only path-references the .tres (no uid on the decl).
  await writeFile(
    join(root, "Scenes", "UsesDataPathOnly.tscn"),
    `[gd_scene load_steps=2 format=3]

[ext_resource type="Resource" path="res://Resources/DemoData.tres" id="1_data"]

[node name="Root" type="Node"]
`,
    "utf-8",
  );
}

// ---------------------------------------------------------------------------
// core edges
// ---------------------------------------------------------------------------

test("findReferencesOffline finds .tscn referencing a .tres via ext_resource", async () => {
  const f = await makeProject(seedStandardGraph);
  try {
    const result = await findReferencesOffline({
      assetPath: "res://Resources/DemoData.tres",
      detail: "normal",
      projectRoot: f.root,
    });
    assert.equal(result.unresolvedUid, false);
    assert.equal(result.queriedAssetPath, "res://Resources/DemoData.tres");
    assert.equal(result.queriedAssetUid, TARGET_UID);
    assert.ok(result.totalCount >= 2, `expected >=2 hits, got ${result.totalCount}`);
    const paths = result.referencedBy.map((h) => h.assetPath).sort();
    assert.ok(paths.includes("res://Scenes/UsesData.tscn"));
    assert.ok(paths.includes("res://Scenes/UsesDataPathOnly.tscn"));
    assert.ok(result.byKind.scene >= 2);
    assert.ok(result.byFolder["res://Scenes/"] >= 2);
  } finally {
    await f.cleanup();
  }
});

test("findReferencesOffline finds script attachment via ExtResource", async () => {
  const f = await makeProject(seedStandardGraph);
  try {
    const result = await findReferencesOffline({
      assetPath: "res://Scripts/Player.gd",
      detail: "verbose",
      projectRoot: f.root,
    });
    assert.equal(result.totalCount, 1);
    assert.equal(result.referencedBy[0]?.assetPath, "res://Scenes/UsesData.tscn");
    const locs = result.referencedBy[0]?.locations ?? [];
    assert.ok(
      locs.some((l) => l.includes("ext_resource") || l.includes("script")),
      `expected script/ext_resource location, got ${JSON.stringify(locs)}`,
    );
  } finally {
    await f.cleanup();
  }
});

test("findReferencesOffline uid query matches path query set", async () => {
  const f = await makeProject(seedStandardGraph);
  try {
    const byPath = await findReferencesOffline({
      assetPath: "res://Resources/DemoData.tres",
      detail: "normal",
      projectRoot: f.root,
    });
    const byUid = await findReferencesOffline({
      uid: TARGET_UID,
      detail: "normal",
      projectRoot: f.root,
    });
    assert.equal(byUid.queriedAssetPath, "res://Resources/DemoData.tres");
    assert.equal(byUid.unresolvedUid, false);
    const pathsA = byPath.referencedBy.map((h) => h.assetPath).sort();
    const pathsB = byUid.referencedBy.map((h) => h.assetPath).sort();
    assert.deepEqual(pathsB, pathsA);
  } finally {
    await f.cleanup();
  }
});

// ---------------------------------------------------------------------------
// profiles + paging
// ---------------------------------------------------------------------------

test("findReferencesOffline compact profile returns counts only", async () => {
  const f = await makeProject(seedStandardGraph);
  try {
    const result = await findReferencesOffline({
      assetPath: "res://Resources/DemoData.tres",
      detail: "summary",
      projectRoot: f.root,
    });
    assert.equal(result.referencedBy.length, 0);
    assert.ok(result.totalCount >= 2);
    assert.ok(Object.keys(result.byKind).length > 0);
    assert.ok(Object.keys(result.byFolder).length > 0);
  } finally {
    await f.cleanup();
  }
});

test("findReferencesOffline balanced returns paths; full returns locations", async () => {
  const f = await makeProject(seedStandardGraph);
  try {
    const balanced = await findReferencesOffline({
      assetPath: "res://Resources/DemoData.tres",
      detail: "normal",
      projectRoot: f.root,
    });
    assert.ok(balanced.referencedBy.length >= 2);
    assert.equal(balanced.referencedBy[0]?.locations, undefined);

    const full = await findReferencesOffline({
      assetPath: "res://Resources/DemoData.tres",
      detail: "verbose",
      projectRoot: f.root,
    });
    assert.ok(full.referencedBy.length >= 2);
    assert.ok(
      (full.referencedBy[0]?.locations?.length ?? 0) > 0,
      "full profile should include locations",
    );
  } finally {
    await f.cleanup();
  }
});

test("findReferencesOffline paging resumes via next_cursor", async () => {
  const f = await makeProject(seedStandardGraph);
  try {
    const page1 = await findReferencesOffline({
      assetPath: "res://Resources/DemoData.tres",
      detail: "normal",
      pageSize: 1,
      projectRoot: f.root,
    });
    assert.equal(page1.referencedBy.length, 1);
    assert.ok(page1.pagination);
    assert.ok(page1.pagination!.next_cursor);
    assert.ok(page1.pagination!.truncated >= 1);

    const page2 = await findReferencesOffline({
      assetPath: "res://Resources/DemoData.tres",
      detail: "normal",
      pageSize: 1,
      cursor: page1.pagination!.next_cursor!,
      projectRoot: f.root,
    });
    assert.equal(page2.referencedBy.length, 1);
    assert.notEqual(
      page2.referencedBy[0]?.assetPath,
      page1.referencedBy[0]?.assetPath,
    );
    // Combined pages cover the full set without dupes.
    const combined = new Set([
      page1.referencedBy[0]!.assetPath,
      page2.referencedBy[0]!.assetPath,
    ]);
    assert.equal(combined.size, 2);
  } finally {
    await f.cleanup();
  }
});

// ---------------------------------------------------------------------------
// unresolved uid
// ---------------------------------------------------------------------------

test("findReferencesOffline surfaces unresolvedUid when uid has no path", async () => {
  const f = await makeProject(async (root) => {
    await mkdir(join(root, "Scenes"), { recursive: true });
    // Scene references a ghost uid that has no on-disk path mapping.
    await writeFile(
      join(root, "Scenes", "Ghost.tscn"),
      `[gd_scene load_steps=2 format=3]

[ext_resource type="Resource" uid="uid://ghostnopath0001" path="res://Missing/Gone.tres" id="1_ghost"]

[node name="Root" type="Node"]
`,
      "utf-8",
    );
  });
  try {
    const result = await findReferencesOffline({
      uid: "uid://ghostnopath0001",
      detail: "normal",
      projectRoot: f.root,
    });
    assert.equal(result.unresolvedUid, true);
    assert.equal(result.queriedAssetPath, "");
    assert.equal(result.queriedAssetUid, "uid://ghostnopath0001");
    // Still finds the referencing scene by uid token.
    assert.equal(result.totalCount, 1);
    assert.equal(result.referencedBy[0]?.assetPath, "res://Scenes/Ghost.tscn");
  } finally {
    await f.cleanup();
  }
});

test("buildUidPathIndex maps .uid sidecar and resource header", async () => {
  const f = await makeProject(seedStandardGraph);
  try {
    const index = await buildUidPathIndex(f.root);
    assert.equal(index.uidToPath.get(TARGET_UID), "res://Resources/DemoData.tres");
    assert.equal(index.pathToUid.get("res://Resources/DemoData.tres"), TARGET_UID);
    assert.equal(index.uidToPath.get(SCRIPT_UID), "res://Scripts/Player.gd");
  } finally {
    await f.cleanup();
  }
});

test("findReferencesOffline excludes self-reference", async () => {
  const f = await makeProject(async (root) => {
    await mkdir(join(root, "Scenes"), { recursive: true });
    // A scene that somehow mentions its own path in an ext_resource would be
    // unusual; more typically self appears only as the file being scanned.
    await writeFile(
      join(root, "Scenes", "Lonely.tscn"),
      `[gd_scene load_steps=1 format=3 uid="uid://lonely000000001"]

[node name="Root" type="Node"]
`,
      "utf-8",
    );
  });
  try {
    const result = await findReferencesOffline({
      assetPath: "res://Scenes/Lonely.tscn",
      detail: "normal",
      projectRoot: f.root,
    });
    assert.equal(result.totalCount, 0);
    assert.deepEqual(result.referencedBy, []);
  } finally {
    await f.cleanup();
  }
});

test("findReferencesOffline include_scripts finds preload literals", async () => {
  const f = await makeProject(async (root) => {
    await mkdir(join(root, "Resources"), { recursive: true });
    await mkdir(join(root, "Scripts"), { recursive: true });
    await writeFile(
      join(root, "Resources", "DemoData.tres"),
      `[gd_resource type="Resource" format=3 uid="${TARGET_UID}"]\n\n[resource]\n`,
      "utf-8",
    );
    await writeFile(
      join(root, "Scripts", "Loader.gd"),
      `extends Node\nconst DATA = preload("res://Resources/DemoData.tres")\n`,
      "utf-8",
    );
  });
  try {
    const off = await findReferencesOffline({
      assetPath: "res://Resources/DemoData.tres",
      detail: "normal",
      projectRoot: f.root,
    });
    assert.equal(off.totalCount, 0);

    const on = await findReferencesOffline({
      assetPath: "res://Resources/DemoData.tres",
      detail: "normal",
      includeScripts: true,
      projectRoot: f.root,
    });
    assert.equal(on.totalCount, 1);
    assert.equal(on.referencedBy[0]?.assetPath, "res://Scripts/Loader.gd");
    assert.equal(on.referencedBy[0]?.kind, "script");
  } finally {
    await f.cleanup();
  }
});
