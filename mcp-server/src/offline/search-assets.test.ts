// Offline project-wide asset search tests (P17.1).
//
// Fixture-driven coverage for searchAssetsOffline: by_name / by_kind /
// by_node_type / by_script / references_uid reason tags, compact/balanced/full
// profile axis, paging, and byKind rollup.
//
// Adapted from Unity Open MCP's searchAssetsOffline tests (adapt for Godot
// criteria + reason-tag set).

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { realpath } from "node:fs/promises";

import { searchAssetsOffline } from "./search-assets.js";

// ---------------------------------------------------------------------------
// fixture builder
// ---------------------------------------------------------------------------

const PROJECT_GODOT = `[application]\nconfig/name="Search Test"\nconfig/features=PackedStringArray("4.3")\n`;
const PLAYER_UID = "uid://playerscript01";
const TARGET_UID = "uid://demotarget0001";

interface Fixture {
  root: string;
  cleanup: () => Promise<void>;
}

async function makeProject(
  build?: (root: string) => Promise<void>,
): Promise<Fixture> {
  const root = await mkdtemp(join(tmpdir(), "gom-search-"));
  await writeFile(join(root, "project.godot"), PROJECT_GODOT, "utf-8");
  if (build) await build(root);
  const realRoot = await realpath(root);
  const cleanup = () => rm(root, { recursive: true, force: true });
  return { root: realRoot, cleanup };
}

async function seedProject(root: string): Promise<void> {
  await mkdir(join(root, "Scenes"), { recursive: true });
  await mkdir(join(root, "Scripts"), { recursive: true });
  await mkdir(join(root, "Resources"), { recursive: true });
  await mkdir(join(root, "Shaders"), { recursive: true });

  await writeFile(join(root, "Scripts", "Player.gd"), "extends Node3D\n", "utf-8");
  await writeFile(join(root, "Scripts", "Player.gd.uid"), PLAYER_UID + "\n", "utf-8");

  await writeFile(
    join(root, "Scenes", "PlayerScene.tscn"),
    `[gd_scene load_steps=2 format=3]

[ext_resource type="Script" path="res://Scripts/Player.gd" id="1_s"]

[node name="Player" type="CharacterBody3D" parent="."]
script = ExtResource("1_s")

[node name="Cam" type="Camera3D" parent="Player"]
`,
    "utf-8",
  );

  await writeFile(
    join(root, "Resources", "Data.tres"),
    `[gd_resource type="DemoData" load_steps=2 format=3 uid="${TARGET_UID}"]

[ext_resource type="Script" path="res://Scripts/Player.gd" id="1_s"]

[resource]
script = ExtResource("1_s")
`,
    "utf-8",
  );

  await writeFile(
    join(root, "Shaders", "Water.gdshader"),
    "shader_type spatial;\nuniform float speed;\n",
    "utf-8",
  );
}

// ---------------------------------------------------------------------------
// by_name
// ---------------------------------------------------------------------------

test("search_assets by_name matches file basenames (case-insensitive)", async () => {
  const { root, cleanup } = await makeProject(seedProject);
  try {
    const result = await searchAssetsOffline({
      name: "player",
      detail: "normal",
      projectRoot: root,
    });
    const paths = result.matches.map((m) => m.assetPath).sort();
    // Player.gd + PlayerScene.tscn both contain "player" in the basename.
    assert.ok(paths.includes("res://Scripts/Player.gd"), paths.join(","));
    assert.ok(paths.includes("res://Scenes/PlayerScene.tscn"), paths.join(","));
    for (const m of result.matches) {
      assert.ok(m.reasons.includes("by_name"));
    }
  } finally {
    await cleanup();
  }
});

// ---------------------------------------------------------------------------
// by_kind
// ---------------------------------------------------------------------------

test("search_assets by_kind filters to the requested kinds", async () => {
  const { root, cleanup } = await makeProject(seedProject);
  try {
    const result = await searchAssetsOffline({
      kind: "shader",
      detail: "normal",
      projectRoot: root,
    });
    assert.equal(result.matchCount, 1);
    assert.equal(result.matches[0]!.kind, "shader");
    assert.deepEqual(result.byKind, { shader: 1 });
  } finally {
    await cleanup();
  }
});

// ---------------------------------------------------------------------------
// by_node_type
// ---------------------------------------------------------------------------

test("search_assets by_node_type matches scene node types + returns hits under balanced", async () => {
  const { root, cleanup } = await makeProject(seedProject);
  try {
    const result = await searchAssetsOffline({
      nodeType: "camera",
      detail: "normal",
      projectRoot: root,
    });
    assert.equal(result.matchCount, 1);
    const m = result.matches[0]!;
    assert.equal(m.assetPath, "res://Scenes/PlayerScene.tscn");
    assert.ok(m.reasons.includes("by_node_type"));
    assert.ok(m.nodes && m.nodes.length === 1);
    assert.equal(m.nodes![0]!.type, "Camera3D");
  } finally {
    await cleanup();
  }
});

// ---------------------------------------------------------------------------
// by_script
// ---------------------------------------------------------------------------

test("search_assets by_script matches scenes/resources with that script attached", async () => {
  const { root, cleanup } = await makeProject(seedProject);
  try {
    const result = await searchAssetsOffline({
      script: "player.gd",
      detail: "normal",
      projectRoot: root,
    });
    const paths = result.matches.map((m) => m.assetPath).sort();
    assert.deepEqual(paths, [
      "res://Resources/Data.tres",
      "res://Scenes/PlayerScene.tscn",
    ]);
    for (const m of result.matches) {
      assert.ok(m.reasons.includes("by_script"));
      assert.ok(m.scripts && m.scripts.includes("res://Scripts/Player.gd"));
    }
  } finally {
    await cleanup();
  }
});

// ---------------------------------------------------------------------------
// references_uid
// ---------------------------------------------------------------------------

test("search_assets references_uid matches assets referencing the uid (own uid excluded)", async () => {
  const { root, cleanup } = await makeProject(seedProject);
  try {
    // TARGET_UID is Data.tres's own uid; no one references it → no matches.
    const own = await searchAssetsOffline({
      uid: TARGET_UID,
      detail: "normal",
      projectRoot: root,
    });
    assert.equal(own.matchCount, 0);

    // PLAYER_UID is Player.gd's uid; the scene + resource reference it.
    const refs = await searchAssetsOffline({
      uid: PLAYER_UID,
      detail: "normal",
      projectRoot: root,
    });
    const paths = refs.matches.map((m) => m.assetPath).sort();
    assert.deepEqual(paths, [
      "res://Resources/Data.tres",
      "res://Scenes/PlayerScene.tscn",
    ]);
    for (const m of refs.matches) {
      assert.ok(m.reasons.includes("references_uid"));
    }
  } finally {
    await cleanup();
  }
});

// ---------------------------------------------------------------------------
// compact + paging
// ---------------------------------------------------------------------------

test("search_assets compact returns counts + byKind only (no per-asset list)", async () => {
  const { root, cleanup } = await makeProject(seedProject);
  try {
    const result = await searchAssetsOffline({
      detail: "summary",
      projectRoot: root,
    });
    assert.deepEqual(result.matches, []);
    assert.ok(result.matchCount >= 4);
    assert.equal(result.truncated, result.matchCount);
    // byKind rolls up every file.
    assert.ok((result.byKind.scene ?? 0) >= 1);
    assert.ok((result.byKind.script ?? 0) >= 1);
  } finally {
    await cleanup();
  }
});

test("search_assets pages the match list under balanced + page_size", async () => {
  const { root, cleanup } = await makeProject(seedProject);
  try {
    const first = await searchAssetsOffline({
      detail: "normal",
      pageSize: 2,
      projectRoot: root,
    });
    assert.equal(first.matches.length, 2);
    assert.ok(first.pagination!.next_cursor);
    assert.equal(first.pagination!.truncated, first.matchCount - 2);

    const second = await searchAssetsOffline({
      detail: "normal",
      pageSize: 2,
      cursor: first.pagination!.next_cursor!,
      projectRoot: root,
    });
    // Page 2 carries a different first path than page 1.
    assert.notEqual(
      second.matches[0]!.assetPath,
      first.matches[0]!.assetPath,
    );
  } finally {
    await cleanup();
  }
});
