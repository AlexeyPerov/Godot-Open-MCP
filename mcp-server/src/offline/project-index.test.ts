// Offline one-level `res://` directory listing tests (P7.3).
//
// Fixture-driven coverage for the disk-backed `filesystem_list` fallback:
// deterministic ordering (dirs-first, ordinal sort), internal/hidden
// filtering, paging + cursor consistency, resource-type mapping, and the
// security contract (traversal refusal, symlink-escape exclusion).
//
// Ports Unity Open MCP's offline listing test patterns (deterministic walk,
// skip-directory, type-filter, bounded output) and adds Godot-specific
// `project.godot` + `.godot/` + UID-null + P4.4 paging fixtures.
//
// Built + run via the project test config:
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, symlink, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { realpath } from "node:fs/promises";

import {
  listProjectDirectoryOffline,
  resourceTypeForExtension,
  MAX_LISTING_ENTRIES,
} from "./project-index.js";

// ---------------------------------------------------------------------------
// fixture builder
// ---------------------------------------------------------------------------

const PROJECT_GODOT = `[application]\nconfig/name="Test Project"\nconfig/features=PackedStringArray("4.3")\n`;

interface Fixture {
  root: string;
  realRoot: string;
  cleanup: () => Promise<void>;
}

async function makeProject(
  build?: (root: string) => Promise<void>,
): Promise<Fixture> {
  const root = await mkdtemp(join(tmpdir(), "gom-index-"));
  await writeFile(join(root, "project.godot"), PROJECT_GODOT, "utf-8");
  if (build) await build(root);
  const realRoot = await realpath(root);
  const cleanup = () => rm(root, { recursive: true, force: true });
  return { root, realRoot, cleanup };
}

function entryNames(result: { entries: { name: string }[] }): string[] {
  return result.entries.map((e) => e.name);
}

// ---------------------------------------------------------------------------
// root listing + ordering
// ---------------------------------------------------------------------------

test("listProjectDirectoryOffline: bare res:// lists the project root", async () => {
  const f = await makeProject(async (root) => {
    await writeFile(join(root, "a.tscn"), "x");
    await mkdir(join(root, "scenes"));
    await writeFile(join(root, "b.gd"), "x");
  });
  try {
    const r = await listProjectDirectoryOffline("res://", f.root);
    assert.equal(r.ok, true);
    if (!r.ok) return;
    assert.equal(r.result.path, "res://");
    assert.equal(r.result.directoryCount, 1);
    assert.equal(r.result.fileCount, 2);
    // Directories first, then files, each group sorted by name.
    assert.deepEqual(entryNames(r.result), ["scenes", "a.tscn", "b.gd"]);
    assert.equal(r.result.stateSource, "disk");
  } finally {
    await f.cleanup();
  }
});

test("listProjectDirectoryOffline: empty res:// and 'res://' are equivalent (root)", async () => {
  const f = await makeProject();
  try {
    const a = await listProjectDirectoryOffline("", f.root);
    const b = await listProjectDirectoryOffline("res://", f.root);
    assert.equal(a.ok && b.ok, true);
    if (!a.ok || !b.ok) return;
    assert.equal(a.result.path, b.result.path);
  } finally {
    await f.cleanup();
  }
});

test("listProjectDirectoryOffline: directories sort before files; each group ordinal-sorted", async () => {
  const f = await makeProject(async (root) => {
    await mkdir(join(root, "z_dir"));
    await mkdir(join(root, "a_dir"));
    await writeFile(join(root, "z_file.tres"), "x");
    await writeFile(join(root, "a_file.tres"), "x");
    await writeFile(join(root, "M_file.tres"), "x");
  });
  try {
    const r = await listProjectDirectoryOffline("res://", f.root);
    assert.equal(r.ok, true);
    if (!r.ok) return;
    // dirs first (a_dir, z_dir), then files — ORDINAL (uppercase < lowercase in
    // charCode), NOT locale case-insensitive. M_file (0x4D) precedes a_file
    // (0x61) and z_file (0x7A).
    assert.deepEqual(entryNames(r.result), [
      "a_dir",
      "z_dir",
      "M_file.tres",
      "a_file.tres",
      "z_file.tres",
    ]);
  } finally {
    await f.cleanup();
  }
});

test("listProjectDirectoryOffline: nested res:// directory lists its immediate children", async () => {
  const f = await makeProject(async (root) => {
    await mkdir(join(root, "scenes", "levels"), { recursive: true });
    await writeFile(join(root, "scenes", "level1.tscn"), "x");
    await writeFile(join(root, "scenes", "level2.tscn"), "x");
  });
  try {
    const r = await listProjectDirectoryOffline("res://scenes/", f.root);
    assert.equal(r.ok, true);
    if (!r.ok) return;
    assert.equal(r.result.path, "res://scenes/");
    assert.equal(r.result.directoryCount, 1);
    assert.equal(r.result.fileCount, 2);
    assert.deepEqual(entryNames(r.result), [
      "levels",
      "level1.tscn",
      "level2.tscn",
    ]);
  } finally {
    await f.cleanup();
  }
});

test("listProjectDirectoryOffline: directory entries carry trailing slash; files never do", async () => {
  const f = await makeProject(async (root) => {
    await mkdir(join(root, "scenes"));
    await writeFile(join(root, "main.tscn"), "x");
  });
  try {
    const r = await listProjectDirectoryOffline("res://", f.root);
    assert.equal(r.ok, true);
    if (!r.ok) return;
    const dir = r.result.entries.find((e) => e.isDirectory);
    const file = r.result.entries.find((e) => !e.isDirectory);
    assert.ok(dir);
    assert.ok(file);
    assert.equal(dir!.path, "res://scenes/");
    assert.equal(file!.path, "res://main.tscn");
  } finally {
    await f.cleanup();
  }
});

// ---------------------------------------------------------------------------
// filtering — internals + hidden
// ---------------------------------------------------------------------------

test("listProjectDirectoryOffline: .godot, VCS, node_modules are excluded regardless of include_hidden", async () => {
  const f = await makeProject(async (root) => {
    await mkdir(join(root, ".godot"));
    await mkdir(join(root, ".git"));
    await mkdir(join(root, "node_modules"));
    await mkdir(join(root, "scenes"));
  });
  try {
    for (const includeHidden of [false, true]) {
      const r = await listProjectDirectoryOffline("res://", f.root, {
        includeHidden,
      });
      assert.equal(r.ok, true);
      if (!r.ok) return;
      const names = entryNames(r.result);
      assert.ok(!names.includes(".godot"), `.godot excluded (hidden=${includeHidden})`);
      assert.ok(!names.includes(".git"), `.git excluded (hidden=${includeHidden})`);
      assert.ok(
        !names.includes("node_modules"),
        `node_modules excluded (hidden=${includeHidden})`,
      );
      assert.ok(names.includes("scenes"), `user dir present (hidden=${includeHidden})`);
    }
  } finally {
    await f.cleanup();
  }
});

test("listProjectDirectoryOffline: hidden user files are excluded by default, included with include_hidden", async () => {
  const f = await makeProject(async (root) => {
    await writeFile(join(root, ".eslintrc"), "x");
    await writeFile(join(root, ".env"), "x");
    await writeFile(join(root, "visible.gd"), "x");
  });
  try {
    const hidden = await listProjectDirectoryOffline("res://", f.root);
    assert.equal(hidden.ok, true);
    if (!hidden.ok) return;
    const hiddenNames = entryNames(hidden.result);
    assert.ok(!hiddenNames.includes(".eslintrc"));
    assert.ok(!hiddenNames.includes(".env"));
    assert.ok(hiddenNames.includes("visible.gd"));

    const shown = await listProjectDirectoryOffline("res://", f.root, {
      includeHidden: true,
    });
    assert.equal(shown.ok, true);
    if (!shown.ok) return;
    const shownNames = entryNames(shown.result);
    assert.ok(shownNames.includes(".eslintrc"));
    assert.ok(shownNames.includes(".env"));
    assert.ok(shownNames.includes("visible.gd"));
  } finally {
    await f.cleanup();
  }
});

// ---------------------------------------------------------------------------
// resource-type mapping (best-effort, extension-based)
// ---------------------------------------------------------------------------

test("listProjectDirectoryOffline: file resourceType is best-effort by extension; uid is always null", async () => {
  const f = await makeProject(async (root) => {
    await writeFile(join(root, "scene.tscn"), "x");
    await writeFile(join(root, "res.tres"), "x");
    await writeFile(join(root, "script.gd"), "x");
    await writeFile(join(root, "csharp.cs"), "x");
    await writeFile(join(root, "shader.gdshader"), "x");
    await writeFile(join(root, "image.png"), "x");
    await writeFile(join(root, "noext"), "x");
  });
  try {
    const r = await listProjectDirectoryOffline("res://", f.root);
    assert.equal(r.ok, true);
    if (!r.ok) return;
    const byName = new Map(r.result.entries.map((e) => [e.name, e]));
    assert.equal(byName.get("scene.tscn")!.resourceType, "PackedScene");
    assert.equal(byName.get("res.tres")!.resourceType, "Resource");
    assert.equal(byName.get("script.gd")!.resourceType, "GDScript");
    assert.equal(byName.get("csharp.cs")!.resourceType, "CSharpScript");
    assert.equal(byName.get("shader.gdshader")!.resourceType, "Shader");
    // Unknown extensions surface null — offline never overclaims importer type.
    assert.equal(byName.get("image.png")!.resourceType, null);
    assert.equal(byName.get("noext")!.resourceType, null);
    // UID is ALWAYS null offline (the UID table lives in .godot import state).
    for (const e of r.result.entries) {
      assert.equal(e.uid, null);
    }
    // Directories never carry a resourceType.
    assert.equal(byName.get("noext")!.isDirectory, false);
  } finally {
    await f.cleanup();
  }
});

test("resourceTypeForExtension: pinned mapping (case-insensitive ext)", () => {
  assert.equal(resourceTypeForExtension(".tscn"), "PackedScene");
  assert.equal(resourceTypeForExtension(".TSCN"), "PackedScene");
  assert.equal(resourceTypeForExtension(".tres"), "Resource");
  assert.equal(resourceTypeForExtension(".gd"), "GDScript");
  assert.equal(resourceTypeForExtension(".cs"), "CSharpScript");
  assert.equal(resourceTypeForExtension(".gdshader"), "Shader");
  // .gdshaderinc is a shader include fragment, not a standalone Shader.
  assert.equal(resourceTypeForExtension(".gdshaderinc"), null);
  assert.equal(resourceTypeForExtension(".png"), null);
  assert.equal(resourceTypeForExtension(""), null);
});

// ---------------------------------------------------------------------------
// paging + cursor
// ---------------------------------------------------------------------------

test("listProjectDirectoryOffline: large directory is paged via cursor with no dup/gap", async () => {
  const f = await makeProject(async (root) => {
    // 10 files + 0 dirs; page_size 4 → 3 pages (4, 4, 2).
    for (let i = 0; i < 10; i++) {
      await writeFile(join(root, `f${i}.gd`), "x");
    }
  });
  try {
    const seen: string[] = [];
    let cursor: string | undefined;
    let pages = 0;
    for (;;) {
      const r = await listProjectDirectoryOffline("res://", f.root, {
        pageSize: 4,
        cursor,
      });
      assert.equal(r.ok, true);
      if (!r.ok) return;
      pages++;
      seen.push(...entryNames(r.result));
      // Full count is reported on EVERY page (not page-bounded).
      assert.equal(r.result.fileCount, 10);
      assert.equal(r.result.directoryCount, 0);
      cursor = r.result.pagination.nextCursor ?? undefined;
      if (!cursor) break;
      if (pages > 10) throw new Error("paging did not terminate");
    }
    // No duplicates, no gaps, ordinal-sorted.
    assert.deepEqual(seen, [
      "f0.gd",
      "f1.gd",
      "f2.gd",
      "f3.gd",
      "f4.gd",
      "f5.gd",
      "f6.gd",
      "f7.gd",
      "f8.gd",
      "f9.gd",
    ]);
    assert.equal(pages, 3);
  } finally {
    await f.cleanup();
  }
});

test("listProjectDirectoryOffline: paging keeps directories-first ordering across pages", async () => {
  const f = await makeProject(async (root) => {
    await mkdir(join(root, "d0"));
    await mkdir(join(root, "d1"));
    await writeFile(join(root, "f0.gd"), "x");
    await writeFile(join(root, "f1.gd"), "x");
    await writeFile(join(root, "f2.gd"), "x");
  });
  try {
    // page_size 2 → page1 = [d0, d1], page2 = [f0, f1], page3 = [f2].
    const p1 = await listProjectDirectoryOffline("res://", f.root, {
      pageSize: 2,
    });
    assert.equal(p1.ok, true);
    if (!p1.ok) return;
    assert.deepEqual(entryNames(p1.result), ["d0", "d1"]);

    const p2 = await listProjectDirectoryOffline("res://", f.root, {
      pageSize: 2,
      cursor: p1.result.pagination.nextCursor ?? undefined,
    });
    assert.equal(p2.ok, true);
    if (!p2.ok) return;
    assert.deepEqual(entryNames(p2.result), ["f0.gd", "f1.gd"]);

    const p3 = await listProjectDirectoryOffline("res://", f.root, {
      pageSize: 2,
      cursor: p2.result.pagination.nextCursor ?? undefined,
    });
    assert.equal(p3.ok, true);
    if (!p3.ok) return;
    assert.deepEqual(entryNames(p3.result), ["f2.gd"]);
    assert.equal(p3.result.pagination.nextCursor, null);
  } finally {
    await f.cleanup();
  }
});

test("listProjectDirectoryOffline: page_size defaults to 100, clamped to [1,500]", async () => {
  const f = await makeProject(async (root) => {
    for (let i = 0; i < 3; i++) {
      await writeFile(join(root, `f${i}.gd`), "x");
    }
  });
  try {
    // No page_size → default 100 → all 3 fit on one page.
    const def = await listProjectDirectoryOffline("res://", f.root);
    assert.equal(def.ok, true);
    if (!def.ok) return;
    assert.equal(def.result.entries.length, 3);
    assert.equal(def.result.pagination.nextCursor, null);

    // page_size 0 → clamped to 1.
    const zero = await listProjectDirectoryOffline("res://", f.root, {
      pageSize: 0,
    });
    assert.equal(zero.ok, true);
    if (!zero.ok) return;
    assert.equal(zero.result.entries.length, 1);

    // page_size 99999 → clamped to 500 (all 3 fit).
    const huge = await listProjectDirectoryOffline("res://", f.root, {
      pageSize: 99999,
    });
    assert.equal(huge.ok, true);
    if (!huge.ok) return;
    assert.equal(huge.result.entries.length, 3);
  } finally {
    await f.cleanup();
  }
});

test("listProjectDirectoryOffline: directory mutation between pages → stale_cursor", async () => {
  const f = await makeProject(async (root) => {
    for (let i = 0; i < 6; i++) {
      await writeFile(join(root, `f${i}.gd`), "x");
    }
  });
  try {
    const p1 = await listProjectDirectoryOffline("res://", f.root, {
      pageSize: 3,
    });
    assert.equal(p1.ok, true);
    if (!p1.ok) return;
    const cursor = p1.result.pagination.nextCursor;
    assert.ok(cursor);

    // Add a file between pages — the fingerprint must change.
    await writeFile(join(f.root, "fNEW.gd"), "x");

    const p2 = await listProjectDirectoryOffline("res://", f.root, {
      pageSize: 3,
      cursor: cursor ?? undefined,
    });
    assert.equal(p2.ok, false);
    if (p2.ok) return;
    assert.equal(p2.error.code, "stale_cursor");
  } finally {
    await f.cleanup();
  }
});

test("listProjectDirectoryOffline: cursor from another path → invalid_cursor", async () => {
  const f = await makeProject(async (root) => {
    await mkdir(join(root, "a"));
    await mkdir(join(root, "b"));
    await writeFile(join(root, "a", "f1.gd"), "x");
    await writeFile(join(root, "a", "f2.gd"), "x");
    await writeFile(join(root, "b", "g1.gd"), "x");
  });
  try {
    // Grab a cursor for res://a/.
    const a = await listProjectDirectoryOffline("res://a/", f.root, {
      pageSize: 1,
    });
    assert.equal(a.ok, true);
    if (!a.ok) return;
    const cursor = a.result.pagination.nextCursor;
    assert.ok(cursor);

    // Reuse it against res://b/ — path mismatch.
    const b = await listProjectDirectoryOffline("res://b/", f.root, {
      pageSize: 1,
      cursor: cursor ?? undefined,
    });
    assert.equal(b.ok, false);
    if (b.ok) return;
    assert.equal(b.error.code, "invalid_cursor");
  } finally {
    await f.cleanup();
  }
});

test("listProjectDirectoryOffline: malformed cursor → invalid_cursor", async () => {
  const f = await makeProject();
  try {
    const r = await listProjectDirectoryOffline("res://", f.root, {
      cursor: "!!!not-base64-cursor!!!",
    });
    assert.equal(r.ok, false);
    if (r.ok) return;
    assert.equal(r.error.code, "invalid_cursor");
  } finally {
    await f.cleanup();
  }
});

// ---------------------------------------------------------------------------
// error paths
// ---------------------------------------------------------------------------

test("listProjectDirectoryOffline: traversal segment → invalid_path", async () => {
  const f = await makeProject();
  try {
    const r = await listProjectDirectoryOffline("res://../escape/", f.root);
    assert.equal(r.ok, false);
    if (r.ok) return;
    assert.equal(r.error.code, "invalid_path");
  } finally {
    await f.cleanup();
  }
});

test("listProjectDirectoryOffline: non-res scheme → invalid_path", async () => {
  const f = await makeProject();
  try {
    const r = await listProjectDirectoryOffline("user://saves", f.root);
    assert.equal(r.ok, false);
    if (r.ok) return;
    assert.equal(r.error.code, "invalid_path");
  } finally {
    await f.cleanup();
  }
});

test("listProjectDirectoryOffline: missing nested directory → directory_not_found", async () => {
  const f = await makeProject();
  try {
    const r = await listProjectDirectoryOffline("res://nope/", f.root);
    assert.equal(r.ok, false);
    if (r.ok) return;
    assert.equal(r.error.code, "directory_not_found");
  } finally {
    await f.cleanup();
  }
});

test("listProjectDirectoryOffline: path naming a file → invalid_path", async () => {
  const f = await makeProject(async (root) => {
    await writeFile(join(root, "main.tscn"), "x");
  });
  try {
    const r = await listProjectDirectoryOffline("res://main.tscn/", f.root);
    assert.equal(r.ok, false);
    if (r.ok) return;
    assert.equal(r.error.code, "invalid_path");
  } finally {
    await f.cleanup();
  }
});

// ---------------------------------------------------------------------------
// symlink containment
// ---------------------------------------------------------------------------

test("listProjectDirectoryOffline: in-project symlinked directory appears as a directory", async (t) => {
  const f = await makeProject(async (root) => {
    await mkdir(join(root, "real_dir"));
    await writeFile(join(root, "real_dir", "inside.gd"), "x");
    try {
      await symlink(join(root, "real_dir"), join(root, "link_dir"), "dir");
    } catch (err) {
      const code = (err as NodeJS.ErrnoException)?.code;
      if (code === "EPERM" || code === "ENOSYS" || code === "EEXIST") {
        t.skip(`symlink creation unavailable (${code})`);
        return;
      }
      throw err;
    }
  });
  try {
    const r = await listProjectDirectoryOffline("res://", f.root);
    assert.equal(r.ok, true);
    if (!r.ok) return;
    const link = r.result.entries.find((e) => e.name === "link_dir");
    assert.ok(link, "symlinked dir is listed");
    assert.equal(link!.isDirectory, true);
    assert.equal(link!.path, "res://link_dir/");
  } finally {
    await f.cleanup();
  }
});

test("listProjectDirectoryOffline: symlink escaping the project is excluded", async (t) => {
  const outside = await mkdtemp(join(tmpdir(), "gom-outside-idx-"));
  const f = await makeProject(async (root) => {
    await writeFile(join(outside, "secret.txt"), "x");
    try {
      await symlink(outside, join(root, "escape"), "dir");
    } catch (err) {
      const code = (err as NodeJS.ErrnoException)?.code;
      if (code === "EPERM" || code === "ENOSYS" || code === "EEXIST") {
        t.skip(`symlink creation unavailable (${code})`);
        return;
      }
      throw err;
    }
  });
  try {
    const r = await listProjectDirectoryOffline("res://", f.root);
    assert.equal(r.ok, true);
    if (!r.ok) return;
    const names = entryNames(r.result);
    assert.ok(!names.includes("escape"), "escaping symlink is excluded");
  } finally {
    await f.cleanup();
    await rm(outside, { recursive: true, force: true });
  }
});

test("listProjectDirectoryOffline: broken symlink is excluded", async (t) => {
  const f = await makeProject(async (root) => {
    try {
      await symlink(join(root, "missing"), join(root, "broken"), "dir");
    } catch (err) {
      const code = (err as NodeJS.ErrnoException)?.code;
      if (code === "EPERM" || code === "ENOSYS" || code === "EEXIST") {
        t.skip(`symlink creation unavailable (${code})`);
        return;
      }
      throw err;
    }
  });
  try {
    const r = await listProjectDirectoryOffline("res://", f.root);
    assert.equal(r.ok, true);
    if (!r.ok) return;
    const names = entryNames(r.result);
    assert.ok(!names.includes("broken"), "broken symlink is excluded");
  } finally {
    await f.cleanup();
  }
});

// ---------------------------------------------------------------------------
// hard cap
// ---------------------------------------------------------------------------

test("listProjectDirectoryOffline: directory beyond the entry cap is capped + paged", async () => {
  // Use an injected small cap (maxEntries) so the test proves the cap behavior
  // without materializing thousands of real files (the production cap is
  // MAX_LISTING_ENTRIES; the override is a test-only seam).
  const cap = 5;
  const over = cap + 3;
  const f = await makeProject(async (root) => {
    for (let i = 0; i < over; i++) {
      await writeFile(join(root, `f${String(i).padStart(3, "0")}.gd`), "x");
    }
  });
  try {
    const r = await listProjectDirectoryOffline("res://", f.root, {
      pageSize: 100,
      maxEntries: cap,
    });
    assert.equal(r.ok, true);
    if (!r.ok) return;
    // Only `cap` entries are reachable across ALL pages; fileCount still
    // reflects the on-disk total (over).
    assert.equal(r.result.entries.length, cap);
    assert.equal(r.result.fileCount, over, "fileCount reflects on-disk total");
  } finally {
    await f.cleanup();
  }
});

test(`listProjectDirectoryOffline: production cap is ${MAX_LISTING_ENTRIES}`, () => {
  // Pin the production default so an accidental bump is caught.
  assert.equal(MAX_LISTING_ENTRIES, 5000);
});
