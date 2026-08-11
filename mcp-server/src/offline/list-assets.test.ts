// Offline compressed `res://` listing tests (P17.1).
//
// Fixture-driven coverage for listAssetsOffline: folder → kind → count rollup,
// sidecar folding (.import/.uid never listed), the type filter, the compact/
// balanced/full sample cap, paging over folders, and the kindSummary rollup.
//
// Adapted from Unity Open MCP's listAssetsOffline tests (adapt for Godot kinds
// + sidecar folding).

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { realpath } from "node:fs/promises";

import { listAssetsOffline } from "./list-assets.js";

// ---------------------------------------------------------------------------
// fixture builder
// ---------------------------------------------------------------------------

const PROJECT_GODOT = `[application]\nconfig/name="List Test"\nconfig/features=PackedStringArray("4.3")\n`;

interface Fixture {
  root: string;
  cleanup: () => Promise<void>;
}

async function makeProject(
  build?: (root: string) => Promise<void>,
): Promise<Fixture> {
  const root = await mkdtemp(join(tmpdir(), "gom-list-"));
  await writeFile(join(root, "project.godot"), PROJECT_GODOT, "utf-8");
  if (build) await build(root);
  const realRoot = await realpath(root);
  const cleanup = () => rm(root, { recursive: true, force: true });
  return { root: realRoot, cleanup };
}

async function seedProject(root: string): Promise<void> {
  await mkdir(join(root, "Scenes"), { recursive: true });
  await mkdir(join(root, "Resources"), { recursive: true });
  await mkdir(join(root, "Scripts"), { recursive: true });
  await mkdir(join(root, "Icons"), { recursive: true });

  await writeFile(
    join(root, "Scenes", "Main.tscn"),
    `[gd_scene load_steps=1 format=3]\n`,
    "utf-8",
  );
  await writeFile(
    join(root, "Scenes", "Level.tscn"),
    `[gd_scene load_steps=1 format=3]\n`,
    "utf-8",
  );
  await writeFile(
    join(root, "Resources", "Data.tres"),
    `[gd_resource type="Resource" format=3]\n`,
    "utf-8",
  );
  await writeFile(join(root, "Scripts", "Player.gd"), "extends Node\n", "utf-8");
  await writeFile(join(root, "Icons", "Icon.png"), "PNG", "utf-8");
  // Sidecars — must be folded into the parent, never listed on their own.
  await writeFile(join(root, "Icons", "Icon.png.import"), `[remap]\n`, "utf-8");
  await writeFile(join(root, "Scripts", "Player.gd.uid"), "uid://x\n", "utf-8");
}

// ---------------------------------------------------------------------------
// Listing shape
// ---------------------------------------------------------------------------

test("list_assets returns folder → kind → count with a kindSummary rollup", async () => {
  const { root, cleanup } = await makeProject(seedProject);
  try {
    const result = await listAssetsOffline({
      detail: "normal",
      projectRoot: root,
    });
    assert.equal(result.root, "res://");
    assert.equal(result.totalFolders, 4);
    assert.equal(result.totalFiles, 5);
    // kindSummary is a project-wide rollup.
    assert.equal(result.kindSummary.scene, 2);
    assert.equal(result.kindSummary.resource, 1);
    assert.equal(result.kindSummary.script, 1);
    assert.equal(result.kindSummary.texture, 1);
    // No sidecar kinds in the rollup.
    assert.equal(result.kindSummary.other, undefined);
  } finally {
    await cleanup();
  }
});

test("list_assets folds .import/.uid sidecars into their parent (never lists them)", async () => {
  const { root, cleanup } = await makeProject(seedProject);
  try {
    const result = await listAssetsOffline({
      detail: "normal",
      projectRoot: root,
    });
    const allFiles = result.folders.flatMap((f) =>
      Object.values(f.kinds).flatMap((b) => b.sample),
    );
    // No sample is a sidecar file name.
    for (const name of allFiles) {
      assert.ok(!name.endsWith(".import"), `${name} should not be listed`);
      assert.ok(!name.endsWith(".uid"), `${name} should not be listed`);
    }
    // The Icons folder lists exactly one texture (Icon, not its sidecar).
    const icons = result.folders.find((f) => f.folder === "res://Icons/");
    assert.ok(icons);
    assert.equal(icons!.fileCount, 1);
    assert.deepEqual(icons!.kinds.texture!.sample, ["Icon"]);
  } finally {
    await cleanup();
  }
});

// ---------------------------------------------------------------------------
// Folder + type filters
// ---------------------------------------------------------------------------

test("list_assets scopes to a folder subtree", async () => {
  const { root, cleanup } = await makeProject(seedProject);
  try {
    const result = await listAssetsOffline({
      folder: "res://Scenes/",
      detail: "normal",
      projectRoot: root,
    });
    assert.equal(result.totalFolders, 1);
    assert.equal(result.totalFiles, 2);
    assert.equal(result.folders[0]!.folder, "res://Scenes/");
    assert.equal(result.folders[0]!.kinds.scene!.count, 2);
  } finally {
    await cleanup();
  }
});

test("list_assets type filter narrows the kind breakdown", async () => {
  const { root, cleanup } = await makeProject(seedProject);
  try {
    const result = await listAssetsOffline({
      type: "scene",
      detail: "normal",
      projectRoot: root,
    });
    assert.equal(result.totalFiles, 2);
    assert.deepEqual(result.kindSummary, { scene: 2 });
    // Only the Scenes folder survives the filter.
    assert.deepEqual(
      result.folders.map((f) => f.folder),
      ["res://Scenes/"],
    );
  } finally {
    await cleanup();
  }
});

// ---------------------------------------------------------------------------
// Profile sample caps + paging
// ---------------------------------------------------------------------------

test("list_assets compact drops sample lists; balanced carries them", async () => {
  const { root, cleanup } = await makeProject(seedProject);
  try {
    const compact = await listAssetsOffline({
      detail: "summary",
      projectRoot: root,
    });
    for (const f of compact.folders) {
      for (const k of Object.keys(f.kinds)) {
        assert.deepEqual(f.kinds[k]!.sample, []);
      }
    }
    const balanced = await listAssetsOffline({
      detail: "normal",
      projectRoot: root,
    });
    const scenes = balanced.folders.find((f) => f.folder === "res://Scenes/")!;
    assert.deepEqual(scenes.kinds.scene!.sample.sort(), ["Level", "Main"]);
  } finally {
    await cleanup();
  }
});

test("list_assets marks a folder truncated when the sample cap is exceeded", async () => {
  const { root, cleanup } = await makeProject(async (r) => {
    await mkdir(join(r, "Scenes"), { recursive: true });
    for (let i = 0; i < 5; i++) {
      await writeFile(
        join(r, "Scenes", `S${i}.tscn`),
        `[gd_scene load_steps=1 format=3]\n`,
        "utf-8",
      );
    }
  });
  try {
    const result = await listAssetsOffline({
      maxPerFolder: 2,
      detail: "normal",
      projectRoot: root,
    });
    // One folder truncated (Scenes), counts still complete.
    assert.equal(result.truncated, 1);
    const scenes = result.folders[0]!;
    assert.equal(scenes.kinds.scene!.count, 5);
    assert.equal(scenes.kinds.scene!.sample.length, 2);
  } finally {
    await cleanup();
  }
});

test("list_assets pages the folder list under page_size", async () => {
  const { root, cleanup } = await makeProject(seedProject);
  try {
    const first = await listAssetsOffline({
      detail: "summary",
      pageSize: 2,
      projectRoot: root,
    });
    assert.equal(first.folders.length, 2);
    assert.ok(first.pagination!.next_cursor);
    assert.equal(first.pagination!.truncated, 2);

    const second = await listAssetsOffline({
      detail: "summary",
      pageSize: 2,
      cursor: first.pagination!.next_cursor!,
      projectRoot: root,
    });
    assert.equal(second.folders.length, 2);
    assert.equal(second.pagination!.next_cursor, null);
  } finally {
    await cleanup();
  }
});
