// Offline generic asset reader tests (P17.1).
//
// Fixture-driven coverage for readAssetOffline across the four asset kinds
// (`.tscn` / `.tres` / `.gdshader` / `.import`), the compact/balanced/full
// profile axis, paging over the per-kind roster, and the three integrity
// signals (missing_reference / orphaned_import / parse_failure).
//
// Adapted from Unity Open MCP's readAssetOffline tests (adapt for Godot's
// INI-style `.tscn`/`.tres` grammar and `.gdshader` uniforms).

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { realpath } from "node:fs/promises";

import { readAssetOffline } from "./read-asset.js";

// ---------------------------------------------------------------------------
// fixture builder
// ---------------------------------------------------------------------------

const PROJECT_GODOT = `[application]\nconfig/name="Read Test"\nconfig/features=PackedStringArray("4.3")\n`;

interface Fixture {
  root: string;
  cleanup: () => Promise<void>;
}

async function makeProject(
  build?: (root: string) => Promise<void>,
): Promise<Fixture> {
  const root = await mkdtemp(join(tmpdir(), "gom-read-"));
  await writeFile(join(root, "project.godot"), PROJECT_GODOT, "utf-8");
  if (build) await build(root);
  const realRoot = await realpath(root);
  const cleanup = () => rm(root, { recursive: true, force: true });
  return { root: realRoot, cleanup };
}

async function seedAssets(root: string): Promise<void> {
  await mkdir(join(root, "Scenes"), { recursive: true });
  await mkdir(join(root, "Resources"), { recursive: true });
  await mkdir(join(root, "Shaders"), { recursive: true });
  await mkdir(join(root, "Icons"), { recursive: true });
  await mkdir(join(root, "Scripts"), { recursive: true });

  // The script the scene + resource reference (kept present so the clean
  // fixtures have no missing_reference signal).
  await writeFile(join(root, "Scripts", "Player.gd"), "extends Node3D\n", "utf-8");

  // Scene with two nodes + a script ext_resource.
  await writeFile(
    join(root, "Scenes", "Main.tscn"),
    `[gd_scene load_steps=3 format=3 uid="uid://scenemain00001"]

[ext_resource type="Script" path="res://Scripts/Player.gd" id="1_script"]

[node name="Root" type="Node"]

[node name="Player" type="CharacterBody3D" parent="."]
script = ExtResource("1_script")
`,
    "utf-8",
  );

  // Resource with one property + a missing ext_resource reference.
  await writeFile(
    join(root, "Resources", "Data.tres"),
    `[gd_resource type="DemoData" load_steps=2 format=3 uid="uid://datatres000001"]

[ext_resource type="Script" path="res://Scripts/Missing.gd" id="1_x"]

[resource]
script = ExtResource("1_x")
value = 42
label = "hello"
`,
    "utf-8",
  );

  // Shader with shader_type + render_mode + two uniforms (one with a hint).
  await writeFile(
    join(root, "Shaders", "Water.gdshader"),
    `shader_type spatial;
render_mode blend_mix, depth_draw_opaque;

uniform vec4 albedo : source_color = vec4(1.0);
uniform float speed = 1.0;
`,
    "utf-8",
  );

  // An .import sidecar with a present source.
  await writeFile(join(root, "Icons", "Icon.png"), "PNGDATA", "utf-8");
  await writeFile(
    join(root, "Icons", "Icon.png.import"),
    `[remap]

importer="image"
type="CompressedTexture2D"
uid="uid://iconpng000001"
path="res://.godot/imported/Icon.png-123.ctex"
source="res://Icons/Icon.png"
dest_files=["res://.godot/imported/Icon.png-123.ctex"]
`,
    "utf-8",
  );
}

// ---------------------------------------------------------------------------
// .tscn
// ---------------------------------------------------------------------------

test("read_asset parses a .tscn under compact (headline + counts)", async () => {
  const { root, cleanup } = await makeProject(seedAssets);
  try {
    const result = await readAssetOffline({
      assetPath: "res://Scenes/Main.tscn",
      detail: "summary",
      projectRoot: root,
    });
    assert.equal(result.kind, "scene");
    assert.equal(result.resourceType, "PackedScene");
    assert.equal(result.uid, "uid://scenemain00001");
    assert.equal(result.loadSteps, 3);
    assert.equal(result.format, 3);
    assert.equal(result.extResourceCount, 1);
    assert.equal(result.nodeCount, 2);
    // Compact: no expandable lists.
    assert.deepEqual(result.nodes, []);
    assert.deepEqual(result.extResources, []);
    assert.equal(result.integrity.length, 0);
  } finally {
    await cleanup();
  }
});

test("read_asset expands the node roster under balanced for .tscn", async () => {
  const { root, cleanup } = await makeProject(seedAssets);
  try {
    const result = await readAssetOffline({
      assetPath: "res://Scenes/Main.tscn",
      detail: "normal",
      projectRoot: root,
    });
    assert.equal(result.nodeCount, 2);
    assert.equal(result.nodes.length, 2);
    assert.equal(result.nodes[0]!.name, "Root");
    assert.equal(result.nodes[0]!.type, "Node");
    assert.equal(result.nodes[1]!.name, "Player");
    assert.equal(result.nodes[1]!.type, "CharacterBody3D");
    assert.equal(result.nodes[1]!.path, "Player");
    assert.equal(result.nodes[1]!.script, "res://Scripts/Player.gd");
    // ext_resources also expand under balanced.
    assert.equal(result.extResources.length, 1);
    assert.equal(result.extResources[0]!.path, "res://Scripts/Player.gd");
  } finally {
    await cleanup();
  }
});

test("read_asset pages the node roster under balanced + page_size", async () => {
  const { root, cleanup } = await makeProject(seedAssets);
  try {
    const first = await readAssetOffline({
      assetPath: "res://Scenes/Main.tscn",
      detail: "normal",
      pageSize: 1,
      projectRoot: root,
    });
    assert.equal(first.nodes.length, 1);
    assert.equal(first.pagination!.next_cursor, "read_asset:1");
    assert.equal(first.pagination!.truncated, 1);

    const second = await readAssetOffline({
      assetPath: "res://Scenes/Main.tscn",
      detail: "normal",
      pageSize: 1,
      cursor: first.pagination!.next_cursor!,
      projectRoot: root,
    });
    assert.equal(second.nodes.length, 1);
    assert.equal(second.nodes[0]!.name, "Player");
    assert.equal(second.pagination!.next_cursor, null);
  } finally {
    await cleanup();
  }
});

// ---------------------------------------------------------------------------
// .tres
// ---------------------------------------------------------------------------

test("read_asset surfaces a missing_reference integrity signal for .tres", async () => {
  const { root, cleanup } = await makeProject(seedAssets);
  try {
    const result = await readAssetOffline({
      assetPath: "res://Resources/Data.tres",
      detail: "summary",
      projectRoot: root,
    });
    assert.equal(result.kind, "resource");
    assert.equal(result.resourceType, "DemoData");
    assert.equal(result.uid, "uid://datatres000001");
    assert.equal(result.extResourceCount, 1);
    // The Missing.gd script does not exist on disk → missing_reference.
    const codes = result.integrity.map((i) => i.code);
    assert.ok(codes.includes("missing_reference"), codes.join(","));
  } finally {
    await cleanup();
  }
});

test("read_asset expands ext_resources + properties under balanced for .tres", async () => {
  const { root, cleanup } = await makeProject(seedAssets);
  try {
    const result = await readAssetOffline({
      assetPath: "res://Resources/Data.tres",
      detail: "normal",
      projectRoot: root,
    });
    assert.equal(result.extResources.length, 1);
    assert.equal(result.extResources[0]!.id, "1_x");
    assert.equal(result.extResources[0]!.missing, true);
    // value + label + script under the [resource] body.
    const names = result.properties.map((p) => p.name).sort();
    assert.deepEqual(names, ["label", "script", "value"]);
  } finally {
    await cleanup();
  }
});

// ---------------------------------------------------------------------------
// .gdshader
// ---------------------------------------------------------------------------

test("read_asset parses a .gdshader (shader_type + render_mode + uniforms)", async () => {
  const { root, cleanup } = await makeProject(seedAssets);
  try {
    const compact = await readAssetOffline({
      assetPath: "res://Shaders/Water.gdshader",
      detail: "summary",
      projectRoot: root,
    });
    assert.equal(compact.kind, "shader");
    assert.equal(compact.resourceType, "Shader");
    assert.equal(compact.uniformCount, 2);
    // shader_type/render_mode live in the properties list (headline).
    assert.equal(compact.properties.length, 2);
    const headline = Object.fromEntries(
      compact.properties.map((p) => [p.name, p.value]),
    );
    assert.equal(headline.shader_type, "spatial");
    assert.equal(headline.render_mode, "blend_mix, depth_draw_opaque");

    const balanced = await readAssetOffline({
      assetPath: "res://Shaders/Water.gdshader",
      detail: "normal",
      projectRoot: root,
    });
    assert.equal(balanced.uniforms.length, 2);
    assert.equal(balanced.uniforms[0]!.name, "albedo");
    assert.equal(balanced.uniforms[0]!.type, "vec4");
    assert.equal(balanced.uniforms[1]!.name, "speed");
    assert.equal(balanced.uniforms[1]!.type, "float");
    assert.equal(balanced.uniforms[1]!.hint, "");
  } finally {
    await cleanup();
  }
});

test("read_asset flags a parse_failure when a .gdshader has no shader_type", async () => {
  const { root, cleanup } = await makeProject(async (r) => {
    await mkdir(join(r, "Shaders"), { recursive: true });
    await writeFile(
      join(r, "Shaders", "Broken.gdshader"),
      `render_mode blend_mix;\nuniform float x;\n`,
      "utf-8",
    );
  });
  try {
    const result = await readAssetOffline({
      assetPath: "res://Shaders/Broken.gdshader",
      detail: "summary",
      projectRoot: root,
    });
    const codes = result.integrity.map((i) => i.code);
    assert.ok(codes.includes("parse_failure"), codes.join(","));
  } finally {
    await cleanup();
  }
});

// ---------------------------------------------------------------------------
// .import
// ---------------------------------------------------------------------------

test("read_asset parses a .import remap with a present source", async () => {
  const { root, cleanup } = await makeProject(seedAssets);
  try {
    const result = await readAssetOffline({
      assetPath: "res://Icons/Icon.png.import",
      detail: "summary",
      projectRoot: root,
    });
    assert.equal(result.kind, "import");
    assert.equal(result.resourceType, "CompressedTexture2D");
    assert.equal(result.uid, "uid://iconpng000001");
    assert.ok(result.importRemap);
    assert.equal(result.importRemap!.source, "res://Icons/Icon.png");
    assert.equal(result.importRemap!.sourceMissing, false);
    assert.equal(result.importRemap!.importer, "image");
    assert.equal(result.integrity.length, 0);
  } finally {
    await cleanup();
  }
});

test("read_asset flags an orphaned_import when the .import source is gone", async () => {
  const { root, cleanup } = await makeProject(async (r) => {
    await mkdir(join(r, "Icons"), { recursive: true });
    await writeFile(
      join(r, "Icons", "Gone.png.import"),
      `[remap]

importer="image"
type="CompressedTexture2D"
uid="uid://gonepng000001"
source="res://Icons/Gone.png"
dest_files=["res://.godot/imported/Gone.png-1.ctex"]
`,
      "utf-8",
    );
  });
  try {
    const result = await readAssetOffline({
      assetPath: "res://Icons/Gone.png.import",
      detail: "summary",
      projectRoot: root,
    });
    assert.equal(result.importRemap!.sourceMissing, true);
    const codes = result.integrity.map((i) => i.code);
    assert.ok(codes.includes("orphaned_import"), codes.join(","));
  } finally {
    await cleanup();
  }
});

// ---------------------------------------------------------------------------
// Error / fallback paths
// ---------------------------------------------------------------------------

test("read_asset returns a parse_failure for a missing asset path", async () => {
  const { root, cleanup } = await makeProject();
  try {
    const result = await readAssetOffline({
      assetPath: "res://Does/NotExist.tres",
      detail: "summary",
      projectRoot: root,
    });
    assert.equal(result.size, 0);
    assert.equal(result.lineCount, 0);
    const codes = result.integrity.map((i) => i.code);
    assert.ok(codes.includes("parse_failure"), codes.join(","));
  } finally {
    await cleanup();
  }
});

test("read_asset falls back to the text kind for an unknown extension", async () => {
  const { root, cleanup } = await makeProject(async (r) => {
    await writeFile(join(r, "notes.txt"), "first line\nsecond line", "utf-8");
  });
  try {
    const balanced = await readAssetOffline({
      assetPath: "res://notes.txt",
      detail: "normal",
      projectRoot: root,
    });
    assert.equal(balanced.kind, "text");
    assert.equal(balanced.lineCount, 2);
    assert.equal(balanced.properties[0]!.name, "first_line");
    assert.equal(balanced.properties[0]!.value, "first line");
  } finally {
    await cleanup();
  }
});

test("read_asset honors max_per_section cap + reports truncated", async () => {
  // Build a scene with 5 nodes; cap at 2 under balanced (no paging).
  const { root, cleanup } = await makeProject(async (r) => {
    await mkdir(join(r, "Scenes"), { recursive: true });
    let body = `[gd_scene load_steps=1 format=3]\n`;
    for (let i = 0; i < 5; i++) {
      body += `\n[node name="N${i}" type="Node" parent="."]\n`;
    }
    await writeFile(join(r, "Scenes", "Many.tscn"), body, "utf-8");
  });
  try {
    const result = await readAssetOffline({
      assetPath: "res://Scenes/Many.tscn",
      detail: "normal",
      maxPerSection: 2,
      projectRoot: root,
    });
    assert.equal(result.nodeCount, 5);
    assert.equal(result.nodes.length, 2);
    assert.equal(result.truncated, 3);
    assert.equal(result.pagination, undefined);
  } finally {
    await cleanup();
  }
});
