// Safe `res://` path resolution + containment tests (P7.2).
//
// Pins the path-safety contract the offline reader relies on: only `res://`
// paths, no traversal, no escape via symlink, no NUL/control chars, no
// backslashes, and the byte-cap probe. The scene-parser tests exercise the
// higher-level read path; this file pins the resolver in isolation.
//
// Built + run via the project test config:
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, symlink, realpath } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
  probeFile,
  requireTscn,
  resolveResPath,
  SCENE_BYTE_CAP,
} from "./project-paths.js";

async function makeRoot(): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), "gom-paths-"));
  await mkdir(join(root, "scenes"), { recursive: true });
  await writeFile(join(root, "scenes", "a.tscn"), "x", "utf-8");
  return root;
}

// ---------------------------------------------------------------------------
// resolveResPath — happy path + scheme/character rejection
// ---------------------------------------------------------------------------

test("resolveResPath: valid res:// path resolves under the project root", async () => {
  const root = await makeRoot();
  const r = resolveResPath("res://scenes/a.tscn", root);
  assert.equal(r.kind, "ok");
  if (r.kind !== "ok") return;
  // Compare against the canonical (realpath'd) root: on macOS the temp dir
  // is itself a symlink (/var → /private/var), so nativePath follows it.
  const realRoot = await realpath(root);
  assert.ok(r.nativePath.startsWith(realRoot), "native path is under project root");
  assert.ok(r.nativePath.endsWith(join("scenes", "a.tscn")));
});

test("resolveResPath: empty path → invalid_path", async () => {
  const root = await makeRoot();
  const r = resolveResPath("", root);
  assert.equal(r.kind, "invalid_path");
});

test("resolveResPath: non-res scheme → invalid_path", async () => {
  const root = await makeRoot();
  assert.equal(resolveResPath("/abs/x.tscn", root).kind, "invalid_path");
  assert.equal(resolveResPath("user://x.tscn", root).kind, "invalid_path");
  assert.equal(resolveResPath("file:///x.tscn", root).kind, "invalid_path");
  assert.equal(resolveResPath("relative.tscn", root).kind, "invalid_path");
});

test("resolveResPath: traversal segment → invalid_path", async () => {
  const root = await makeRoot();
  assert.equal(resolveResPath("res://../escape.tscn", root).kind, "invalid_path");
  assert.equal(resolveResPath("res://scenes/../escape.tscn", root).kind, "invalid_path");
  assert.equal(resolveResPath("res://a/b/../../c.tscn", root).kind, "invalid_path");
});

test("resolveResPath: NUL byte → invalid_path", async () => {
  const root = await makeRoot();
  assert.equal(resolveResPath("res://a\0b.tscn", root).kind, "invalid_path");
});

test("resolveResPath: backslash → invalid_path", async () => {
  const root = await makeRoot();
  assert.equal(resolveResPath("res://a\\b.tscn", root).kind, "invalid_path");
});

test("resolveResPath: control character → invalid_path", async () => {
  const root = await makeRoot();
  assert.equal(resolveResPath("res://a\x07b.tscn", root).kind, "invalid_path");
});

test("resolveResPath: URL-like authority component → invalid_path", async () => {
  const root = await makeRoot();
  // res://evil.com/... has a host segment Godot never emits.
  assert.equal(resolveResPath("res://evil.com/x.tscn", root).kind, "invalid_path");
});

test("resolveResPath: redundant // and . segments are normalized (still valid)", async () => {
  const root = await makeRoot();
  const r = resolveResPath("res://scenes//./a.tscn", root);
  assert.equal(r.kind, "ok");
});

// ---------------------------------------------------------------------------
// resolveResPath — symlink containment
// ---------------------------------------------------------------------------

test("resolveResPath: symlink escaping the project → path_outside_project", async (t) => {
  const outside = await mkdtemp(join(tmpdir(), "gom-outside2-"));
  const root = await makeRoot();
  try {
    await symlink(outside, join(root, "scenes", "escape"), "dir");
  } catch (err) {
    const code = (err as NodeJS.ErrnoException)?.code;
    if (code === "EPERM" || code === "ENOSYS" || code === "EEXIST") {
      t.skip(`symlink creation unavailable (${code})`);
      return;
    }
    throw err;
  }
  const r = resolveResPath("res://scenes/escape/whatever.tscn", root);
  assert.equal(r.kind, "path_outside_project");
});

test("resolveResPath: missing project root → path_outside_project", () => {
  const r = resolveResPath("res://a.tscn", "/does/not/exist/xyz");
  assert.equal(r.kind, "path_outside_project");
});

// ---------------------------------------------------------------------------
// probeFile — existence, regular-file, size cap
// ---------------------------------------------------------------------------

test("probeFile: existing regular file → ok with size", async () => {
  const root = await makeRoot();
  const r = resolveResPath("res://scenes/a.tscn", root);
  if (r.kind !== "ok") throw new Error("resolve failed");
  const p = probeFile(r.nativePath);
  assert.equal(p.kind, "ok");
  if (p.kind !== "ok") return;
  assert.equal(p.size, 1);
});

test("probeFile: missing file → not_found", async () => {
  const root = await makeRoot();
  const r = resolveResPath("res://scenes/missing.tscn", root);
  if (r.kind !== "ok") throw new Error("resolve failed");
  const p = probeFile(r.nativePath);
  assert.equal(p.kind, "not_found");
});

test("probeFile: directory → not_regular", async () => {
  const root = await makeRoot();
  const r = resolveResPath("res://scenes", root);
  if (r.kind !== "ok") throw new Error("resolve failed");
  const p = probeFile(r.nativePath);
  assert.equal(p.kind, "not_regular");
});

test("probeFile: oversized file → too_large with size + cap", async () => {
  const root = await makeRoot();
  const bigPath = join(root, "big.tscn");
  await writeFile(bigPath, Buffer.alloc(SCENE_BYTE_CAP + 10), "utf-8");
  const r = resolveResPath("res://big.tscn", root);
  if (r.kind !== "ok") throw new Error("resolve failed");
  const p = probeFile(r.nativePath);
  assert.equal(p.kind, "too_large");
  if (p.kind !== "too_large") return;
  assert.equal(p.size, SCENE_BYTE_CAP + 10);
  assert.equal(p.cap, SCENE_BYTE_CAP);
});

test("probeFile: custom cap is honored", async () => {
  const root = await makeRoot();
  const r = resolveResPath("res://scenes/a.tscn", root);
  if (r.kind !== "ok") throw new Error("resolve failed");
  // 1-byte file against a 0-byte cap → too_large.
  const p = probeFile(r.nativePath, 0);
  assert.equal(p.kind, "too_large");
});

// ---------------------------------------------------------------------------
// requireTscn — extension gating
// ---------------------------------------------------------------------------

test("requireTscn: accepts .tscn (any case)", () => {
  assert.equal(requireTscn("res://a.tscn"), true);
  assert.equal(requireTscn("res://a.TSCN"), true);
  assert.equal(requireTscn("res://a.Tscn"), true);
});

test("requireTscn: rejects binary .scn and other extensions", () => {
  assert.equal(requireTscn("res://a.scn"), false);
  assert.equal(requireTscn("res://a.tres"), false);
  assert.equal(requireTscn("res://a.gd"), false);
  assert.equal(requireTscn("res://a"), false);
});
