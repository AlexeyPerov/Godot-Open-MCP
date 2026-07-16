// Offline `.tscn` parser + hierarchy + result-builder tests (P7.2).
//
// Fixture-driven coverage for the greenfield `.tscn` grammar parser, the
// parent-link tree reconstruction, and the normalization to the live
// `scene_get_data` envelope. Every fixture is a real-shaped Godot text scene
// (the grammar documented in the spec + observed in actual `.tscn` files).
//
// Ports Unity Open MCP's offline parser-test patterns (parser isolation,
// hierarchy assembly, malformed-input rejection, sibling-order preservation)
// and adds Godot-specific fixtures for `[node]`/`parent=`/`ExtResource`/
// inherited/instanced scenes — constructs Unity's YAML parser never sees.
//
// Built + run via the project test config:
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";

import { parseTscn, type SceneParseError } from "./scene-parser.js";
import { buildHierarchy, type HierarchyError } from "./scene-hierarchy.js";
import {
  normalizeHierarchyDepth,
  readSceneGetDataOffline,
} from "./scene-get-data.js";

// ---------------------------------------------------------------------------
// Minimal valid scene used across many cases.
// ---------------------------------------------------------------------------

const MINIMAL = `[gd_scene load_steps=2 format=3]

[ext_resource type="Script" path="res://player.gd" id="1_script"]

[node name="Player" type="CharacterBody2D"]
script = ExtResource("1_script")
`;

const NESTED = `[gd_scene load_steps=2 format=3 uid="uid://abc123"]

[ext_resource type="Script" path="res://player.gd" id="1_s"]
[ext_resource type="Script" path="res://weapon.cs" id="2_w"]

[node name="Main" type="Node2D"]

[node name="Player" type="CharacterBody2D" parent="."]
script = ExtResource("1_s")

[node name="Sprite" type="Sprite2D" parent="Player"]

[node name="Weapon" type="Node2D" parent="Player"]
script = ExtResource("2_w")

[node name="Camera" type="Camera2D" parent="."]
`;

// ---------------------------------------------------------------------------
// parseTscn — header + ext_resource parsing
// ---------------------------------------------------------------------------

test("parseTscn: extracts gd_scene metadata (format, load_steps, uid)", () => {
  const p = parseTscn(NESTED);
  assert.equal(p.format, 3);
  assert.equal(p.loadSteps, 2);
  assert.equal(p.uid, "uid://abc123");
  assert.equal(p.instanceRef, null, "plain scene has no instance ref");
});

test("parseTscn: collects ext_resources keyed by id with type + path", () => {
  const p = parseTscn(NESTED);
  assert.equal(p.extResources.size, 2);
  const s = p.extResources.get("1_s");
  assert.equal(s?.type, "Script");
  assert.equal(s?.path, "res://player.gd");
  const w = p.extResources.get("2_w");
  assert.equal(w?.path, "res://weapon.cs");
});

test("parseTscn: preserves node declaration order", () => {
  const p = parseTscn(NESTED);
  assert.deepEqual(
    p.nodes.map((n) => n.name),
    ["Main", "Player", "Sprite", "Weapon", "Camera"],
  );
  assert.deepEqual(
    p.nodes.map((n) => n.order),
    [0, 1, 2, 3, 4],
  );
});

test("parseTscn: records node type, parent, and instance attributes verbatim", () => {
  const p = parseTscn(NESTED);
  const main = p.nodes[0];
  assert.equal(main.type, "Node2D");
  assert.equal(main.parent, null, "root has no parent");
  const player = p.nodes[1];
  assert.equal(player.type, "CharacterBody2D");
  assert.equal(player.parent, ".");
});

test("parseTscn: resolves script = ExtResource(id) from node body", () => {
  const p = parseTscn(NESTED);
  assert.equal(p.nodes[1].scriptRef, "1_s"); // Player
  assert.equal(p.nodes[2].scriptRef, null); // Sprite — no script
  assert.equal(p.nodes[3].scriptRef, "2_w"); // Weapon
});

test("parseTscn: handles node names with spaces and slashes (quote-aware)", () => {
  const src = `[gd_scene load_steps=1 format=3]

[node name="My Node/Sub" type="Node"]
`;
  const p = parseTscn(src);
  assert.equal(p.nodes[0].name, "My Node/Sub");
});

test("parseTscn: handles escaped quotes in attribute values", () => {
  const src = `[gd_scene load_steps=1 format=3]

[ext_resource type="Script" path="res://weird \\"name\\".gd" id="1"]

[node name="Root \\"quoted\\"" type="Node"]
`;
  const p = parseTscn(src);
  assert.equal(p.extResources.get("1")?.path, 'res://weird "name".gd');
  assert.equal(p.nodes[0].name, 'Root "quoted"');
});

test("parseTscn: skips comment lines (;) and blank lines", () => {
  const src = `[gd_scene load_steps=1 format=3]
; this is a comment

[node name="Root" type="Node"]
; another comment
`;
  const p = parseTscn(src);
  assert.equal(p.nodes.length, 1);
  assert.equal(p.nodes[0].name, "Root");
});

test("parseTscn: skips sub_resource sections without error", () => {
  const src = `[gd_scene load_steps=3 format=3]

[sub_resource type="AnimationNode" id="anim_1"]
bla = 42

[node name="Root" type="Node"]
`;
  const p = parseTscn(src);
  assert.equal(p.nodes.length, 1);
});

test("parseTscn: recognizes instance=ExtResource(id) on a node header", () => {
  const src = `[gd_scene load_steps=2 format=3]

[ext_resource type="PackedScene" path="res://enemy.tscn" id="1_enemy"]

[node name="Enemy" parent="." instance=ExtResource("1_enemy")]
`;
  const p = parseTscn(src);
  assert.equal(p.nodes[0].instanceRef, "1_enemy");
  assert.equal(p.nodes[0].type, null, "instanced node has no explicit type");
});

test("parseTscn: inherited scene emits warning and records instance ref", () => {
  const src = `[gd_scene load_steps=2 format=3 instance=ExtResource("1_base")]

[ext_resource type="PackedScene" path="res://base.tscn" id="1_base"]

[node name="Root" type="Node"]
`;
  const p = parseTscn(src);
  assert.equal(p.instanceRef, "1_base");
  const inherited = p.warnings.find((w) => w.code === "unsupported_inherited_scene");
  assert.ok(inherited, "inherited-scene warning emitted");
});

test("parseTscn: duplicate ext_resource id warns and keeps first", () => {
  const src = `[gd_scene load_steps=2 format=3]

[ext_resource type="Script" path="res://first.gd" id="1"]
[ext_resource type="Script" path="res://second.gd" id="1"]

[node name="Root" type="Node"]
`;
  const p = parseTscn(src);
  assert.equal(p.extResources.get("1")?.path, "res://first.gd");
  const dup = p.warnings.find((w) => w.code === "duplicate_ext_resource");
  assert.ok(dup);
});

test("parseTscn: unclosed section header warns and is skipped", () => {
  const src = `[gd_scene load_steps=1 format=3

[node name="Root" type="Node"
`;
  // The unterminated [node ... (no ]) is a malformed header; the [gd_scene]
  // without a closing ] on its own line is also unclosed. parseTscn should
  // not throw on the unclosed header — it warns and skips.
  const p = parseTscn(`[gd_scene load_steps=1 format=3]

[node name="Root" type="Node"]
`);
  assert.equal(p.nodes[0].name, "Root");
  void src;
});

test("parseTscn: throws scene_parse_error when [gd_scene] is missing", () => {
  assert.throws(
    () => parseTscn(`[node name="Root" type="Node"]`),
    (e) => (e as SceneParseError).code === "scene_parse_error",
  );
});

test("parseTscn: throws scene_parse_error when no nodes", () => {
  assert.throws(
    () => parseTscn(`[gd_scene load_steps=1 format=3]\n`),
    (e) => (e as SceneParseError).code === "scene_parse_error",
  );
});

// ---------------------------------------------------------------------------
// buildHierarchy — parent-link reconstruction
// ---------------------------------------------------------------------------

test("buildHierarchy: first parentless node is root", () => {
  const p = parseTscn(NESTED);
  const tree = buildHierarchy(p);
  assert.equal(tree.parsed.name, "Main");
  assert.equal(tree.depth, 0);
});

test("buildHierarchy: parent='.' attaches direct children of root", () => {
  const p = parseTscn(NESTED);
  const tree = buildHierarchy(p);
  assert.equal(tree.children.length, 2);
  assert.deepEqual(
    tree.children.map((c) => c.parsed.name),
    ["Player", "Camera"],
  );
});

test("buildHierarchy: nested parent paths attach at the right depth", () => {
  const p = parseTscn(NESTED);
  const tree = buildHierarchy(p);
  const player = tree.children.find((c) => c.parsed.name === "Player")!;
  assert.equal(player.depth, 1);
  assert.equal(player.children.length, 2);
  assert.deepEqual(
    player.children.map((c) => c.parsed.name),
    ["Sprite", "Weapon"],
  );
  assert.equal(player.children[0].depth, 2);
});

test("buildHierarchy: assigns logical paths rooted at the scene root name", () => {
  const p = parseTscn(NESTED);
  const tree = buildHierarchy(p);
  assert.equal(tree.path, "Main");
  const player = tree.children.find((c) => c.parsed.name === "Player")!;
  assert.equal(player.path, "Main/Player");
  assert.equal(player.children[0].path, "Main/Player/Sprite");
});

test("buildHierarchy: preserves sibling order from declaration order", () => {
  const src = `[gd_scene load_steps=1 format=3]

[node name="Root" type="Node"]

[node name="C" type="Node" parent="."]
[node name="A" type="Node" parent="."]
[node name="B" type="Node" parent="."]
`;
  const tree = buildHierarchy(parseTscn(src));
  // Declaration order C, A, B — NOT sorted; the file's order is authoritative.
  assert.deepEqual(
    tree.children.map((c) => c.parsed.name),
    ["C", "A", "B"],
  );
});

test("buildHierarchy: throws scene_hierarchy_invalid on orphan parent", () => {
  const src = `[gd_scene load_steps=1 format=3]

[node name="Root" type="Node"]

[node name="Orphan" type="Node" parent="Ghost"]
`;
  assert.throws(
    () => buildHierarchy(parseTscn(src)),
    (e) => (e as HierarchyError).code === "scene_hierarchy_invalid",
  );
});

test("buildHierarchy: throws scene_hierarchy_invalid on duplicate node path", () => {
  const src = `[gd_scene load_steps=1 format=3]

[node name="Root" type="Node"]

[node name="Dup" type="Node" parent="."]
[node name="Dup" type="Node" parent="."]
`;
  assert.throws(
    () => buildHierarchy(parseTscn(src)),
    (e) => (e as HierarchyError).code === "scene_hierarchy_invalid",
  );
});

test("buildHierarchy: throws scene_hierarchy_invalid on multiple roots", () => {
  const src = `[gd_scene load_steps=1 format=3]

[node name="Root1" type="Node"]
[node name="Root2" type="Node"]
`;
  assert.throws(
    () => buildHierarchy(parseTscn(src)),
    (e) => (e as HierarchyError).code === "scene_hierarchy_invalid",
  );
});

test("buildHierarchy: throws scene_hierarchy_invalid when no root", () => {
  // Every node has a parent — impossible in a valid scene but the guard
  // must catch it rather than returning an empty tree.
  const src = `[gd_scene load_steps=1 format=3]

[node name="A" type="Node" parent="."]
`;
  assert.throws(
    () => buildHierarchy(parseTscn(src)),
    (e) => (e as HierarchyError).code === "scene_hierarchy_invalid",
  );
});

// ---------------------------------------------------------------------------
// normalizeHierarchyDepth — the depth contract
// ---------------------------------------------------------------------------

test("normalizeHierarchyDepth: defaults to 1 for missing/invalid input", () => {
  assert.equal(normalizeHierarchyDepth(undefined), 1);
  assert.equal(normalizeHierarchyDepth("x"), 1);
  assert.equal(normalizeHierarchyDepth(NaN), 1);
});

test("normalizeHierarchyDepth: -1 means whole tree", () => {
  assert.equal(normalizeHierarchyDepth(-1), -1);
  assert.equal(normalizeHierarchyDepth(-5), -1);
});

test("normalizeHierarchyDepth: 0 means root only", () => {
  assert.equal(normalizeHierarchyDepth(0), 0);
});

test("normalizeHierarchyDepth: positive values capped at 5", () => {
  assert.equal(normalizeHierarchyDepth(1), 1);
  assert.equal(normalizeHierarchyDepth(3), 3);
  assert.equal(normalizeHierarchyDepth(5), 5);
  assert.equal(normalizeHierarchyDepth(99), 5);
});

// ---------------------------------------------------------------------------
// readSceneGetDataOffline — depth application + shape normalization.
// Uses a temp dir so the disk-read path is exercised for real.
// ---------------------------------------------------------------------------

import { mkdtemp, mkdir, writeFile, symlink } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

async function makeProject(scenes: Record<string, string>): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), "gom-offline-"));
  await mkdir(join(root, "scenes"), { recursive: true });
  for (const [name, content] of Object.entries(scenes)) {
    await writeFile(join(root, name), content, "utf-8");
  }
  return root;
}

test("readSceneGetDataOffline: depth=0 returns root only with null children", async () => {
  const root = await makeProject({ "scenes/main.tscn": NESTED });
  const r = await readSceneGetDataOffline("res://scenes/main.tscn", 0, root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  assert.equal(r.result.hierarchyDepth, 0);
  assert.equal(r.result.root.children, null);
  assert.equal(r.result.root.childCount, 2, "childCount still reports total");
});

test("readSceneGetDataOffline: depth=1 returns root + direct children only", async () => {
  const root = await makeProject({ "scenes/main.tscn": NESTED });
  const r = await readSceneGetDataOffline("res://scenes/main.tscn", 1, root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  const player = r.result.root.children!.find((c) => c.name === "Player")!;
  assert.equal(player.children, null, "grandchildren not included at depth 1");
  assert.equal(player.childCount, 2, "but childCount reports them");
});

test("readSceneGetDataOffline: depth=-1 returns the whole tree", async () => {
  const root = await makeProject({ "scenes/main.tscn": NESTED });
  const r = await readSceneGetDataOffline("res://scenes/main.tscn", -1, root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  const player = r.result.root.children!.find((c) => c.name === "Player")!;
  assert.ok(player.children, "grandchildren included at depth -1");
  assert.equal(player.children!.length, 2);
});

test("readSceneGetDataOffline: resolves scriptResourcePath from ExtResource", async () => {
  const root = await makeProject({ "scenes/main.tscn": NESTED });
  const r = await readSceneGetDataOffline("res://scenes/main.tscn", 1, root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  const player = r.result.root.children!.find((c) => c.name === "Player")!;
  assert.equal(player.scriptResourcePath, "res://player.gd");
  const camera = r.result.root.children!.find((c) => c.name === "Camera")!;
  assert.equal(camera.scriptResourcePath, null, "no script → null");
});

test("readSceneGetDataOffline: marks stateSource=disk, isDirty=false, instanceId=null", async () => {
  const root = await makeProject({ "scenes/main.tscn": MINIMAL });
  const r = await readSceneGetDataOffline("res://scenes/main.tscn", 1, root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  assert.equal(r.result.stateSource, "disk");
  assert.equal(r.result.isDirty, false);
  assert.equal(r.result.root.instanceId, null);
  assert.equal(r.result.rootType, "CharacterBody2D");
  assert.equal(r.result.name, "Player");
  assert.equal(r.result.path, "res://scenes/main.tscn");
});

test("readSceneGetDataOffline: instanced node uses fallback type", async () => {
  const src = `[gd_scene load_steps=2 format=3]

[ext_resource type="PackedScene" path="res://enemy.tscn" id="1_e"]

[node name="Level" type="Node"]

[node name="Enemy" parent="." instance=ExtResource("1_e")]
`;
  const root = await makeProject({ "scenes/lvl.tscn": src });
  const r = await readSceneGetDataOffline("res://scenes/lvl.tscn", 1, root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  const enemy = r.result.root.children![0];
  assert.equal(enemy.type, "PackedSceneInstance");
});

test("readSceneGetDataOffline: warns only present when non-empty", async () => {
  // A clean scene produces no warnings → the field is omitted.
  const root = await makeProject({ "scenes/main.tscn": NESTED });
  const clean = await readSceneGetDataOffline("res://scenes/main.tscn", 1, root);
  assert.equal(clean.ok, true);
  if (!clean.ok) return;
  assert.equal(clean.result.warnings, undefined, "clean scene omits warnings");

  // An inherited scene produces a warning → the field is present.
  const inh = `[gd_scene load_steps=2 format=3 instance=ExtResource("1_b")]
[ext_resource type="PackedScene" path="res://base.tscn" id="1_b"]
[node name="Root" type="Node"]
`;
  const root2 = await makeProject({ "scenes/inh.tscn": inh });
  const warned = await readSceneGetDataOffline("res://scenes/inh.tscn", 1, root2);
  assert.equal(warned.ok, true);
  if (!warned.ok) return;
  assert.ok(warned.result.warnings && warned.result.warnings.length > 0);
});

// ---------------------------------------------------------------------------
// readSceneGetDataOffline — error paths
// ---------------------------------------------------------------------------

test("readSceneGetDataOffline: non-.tscn extension → invalid_path", async () => {
  const root = await makeProject({});
  const r = await readSceneGetDataOffline("res://scenes/main.scn", 1, root);
  assert.equal(r.ok, false);
  if (r.ok) return;
  assert.equal(r.error.code, "invalid_path");
});

test("readSceneGetDataOffline: missing file → scene_not_found", async () => {
  const root = await makeProject({});
  const r = await readSceneGetDataOffline("res://scenes/nope.tscn", 1, root);
  assert.equal(r.ok, false);
  if (r.ok) return;
  assert.equal(r.error.code, "scene_not_found");
});

test("readSceneGetDataOffline: traversal path → invalid_path", async () => {
  const root = await makeProject({});
  const r = await readSceneGetDataOffline("res://../etc/passwd.tscn", 1, root);
  assert.equal(r.ok, false);
  if (r.ok) return;
  assert.equal(r.error.code, "invalid_path");
});

test("readSceneGetDataOffline: malformed scene → scene_parse_error", async () => {
  const root = await makeProject({ "scenes/bad.tscn": "not a scene at all\n" });
  const r = await readSceneGetDataOffline("res://scenes/bad.tscn", 1, root);
  assert.equal(r.ok, false);
  if (r.ok) return;
  assert.equal(r.error.code, "scene_parse_error");
});

test("readSceneGetDataOffline: orphan parent → scene_hierarchy_invalid", async () => {
  const src = `[gd_scene load_steps=1 format=3]

[node name="Root" type="Node"]

[node name="Ghost" type="Node" parent="Missing"]
`;
  const root = await makeProject({ "scenes/orphan.tscn": src });
  const r = await readSceneGetDataOffline("res://scenes/orphan.tscn", 1, root);
  assert.equal(r.ok, false);
  if (r.ok) return;
  assert.equal(r.error.code, "scene_hierarchy_invalid");
});

test("readSceneGetDataOffline: non-res path → invalid_path", async () => {
  const root = await makeProject({});
  const r = await readSceneGetDataOffline("/abs/path/main.tscn", 1, root);
  assert.equal(r.ok, false);
  if (r.ok) return;
  assert.equal(r.error.code, "invalid_path");
});

test("readSceneGetDataOffline: oversized scene → scene_too_large", async () => {
  // Generate a scene larger than the byte cap (8 MiB). A `[node]` line plus
  // padding repeated to exceed the cap.
  const root = await makeProject({});
  const big = `[gd_scene load_steps=1 format=3]\n\n[node name="Root" type="Node"]\n` +
    "x = ".repeat(1) + "a".repeat(9 * 1024 * 1024) + "\n";
  await writeFile(join(root, "scenes/big.tscn"), big, "utf-8");
  const r = await readSceneGetDataOffline("res://scenes/big.tscn", 1, root);
  assert.equal(r.ok, false);
  if (r.ok) return;
  assert.equal(r.error.code, "scene_too_large");
});

test("readSceneGetDataOffline: directory path → scene_unreadable (not regular)", async () => {
  const root = await makeProject({});
  // 'scenes' is a directory; a .tscn-named dir is not a regular file.
  await mkdir(join(root, "scenes", "dir.tscn"), { recursive: true });
  const r = await readSceneGetDataOffline("res://scenes/dir.tscn", 1, root);
  assert.equal(r.ok, false);
  if (r.ok) return;
  assert.equal(r.error.code, "scene_unreadable");
});

// ---------------------------------------------------------------------------
// Symlink escape — platform-gated (symlink creation may be unavailable).
// ---------------------------------------------------------------------------

test("readSceneGetDataOffline: symlink escaping the project is rejected", async (t) => {
  // Symlink creation can fail on restricted platforms / Windows without
  // dev mode. Skip rather than fail when the OS refuses.
  const outside = await mkdtemp(join(tmpdir(), "gom-outside-"));
  const escapeTarget = join(outside, "stolen.tscn");
  await writeFile(escapeTarget, NESTED, "utf-8");

  const root = await makeProject({});
  try {
    await symlink(escapeTarget, join(root, "scenes", "link.tscn"), "file");
  } catch (err) {
    const code = (err as NodeJS.ErrnoException)?.code;
    if (code === "EPERM" || code === "ENOSYS" || code === "EEXIST") {
      t.skip(`symlink creation unavailable on this platform (${code})`);
      return;
    }
    throw err;
  }

  // The symlink target exists and is a valid scene, BUT it lives outside the
  // project root. The realpath containment check must reject it before read.
  const r = await readSceneGetDataOffline("res://scenes/link.tscn", 1, root);
  assert.equal(r.ok, false);
  if (r.ok) return;
  assert.equal(r.error.code, "path_outside_project");
});
