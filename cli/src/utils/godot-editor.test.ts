// Tests for Godot editor binary discovery + name matching
// (src/utils/godot-editor.ts).
//
// Pure tests for the name-ranking + binary-name recognition helpers (no I/O),
// plus filesystem-backed tests for `findGodotBinary` with an injected temp
// tree. The launch helper is exercised structurally (it spawns detached; a real
// spawn test would require a Godot binary on the test host).
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

import {
  isGodotBinaryName,
  godotBinaryRank,
  scanForGodotBinaries,
  findGodotBinary,
  launchEditor,
  GODOT_BIN_ENV_VARS,
} from "./godot-editor.js";

// ---------------------------------------------------------------------------
// isGodotBinaryName
// ---------------------------------------------------------------------------

test("isGodotBinaryName: fixed names on linux", () => {
  assert.equal(isGodotBinaryName("godot", "linux"), true);
  assert.equal(isGodotBinaryName("godot_mono", "linux"), true);
  assert.equal(isGodotBinaryName("Godot", "linux"), true);
  assert.equal(isGodotBinaryName("notgodot", "linux"), false);
});

test("isGodotBinaryName: fixed names on win32 (case-insensitive)", () => {
  assert.equal(isGodotBinaryName("godot.exe", "win32"), true);
  assert.equal(isGodotBinaryName("GODOT.EXE", "win32"), true);
  assert.equal(isGodotBinaryName("godot_mono.exe", "win32"), true);
  assert.equal(isGodotBinaryName("other.exe", "win32"), false);
});

test("isGodotBinaryName: version-stamped release names", () => {
  assert.equal(
    isGodotBinaryName("Godot_v4.5.1-stable_mono_win64.exe", "win32"),
    true,
  );
  assert.equal(
    isGodotBinaryName("Godot_v4.5.1-stable_mono_win64_console.exe", "win32"),
    true,
  );
  assert.equal(
    isGodotBinaryName("Godot_v4.5.1-stable_macos.universal", "darwin"),
    true,
  );
  assert.equal(
    isGodotBinaryName("Godot_v4.3-stable_mono_linux.x86_64", "linux"),
    true,
  );
});

test("isGodotBinaryName: unrelated version-stamped names are rejected", () => {
  assert.equal(
    isGodotBinaryName("Godot_v4.5.1-stable_mono_win64.txt", "win32"),
    false,
  );
  assert.equal(isGodotBinaryName("GodotEditor.exe", "win32"), false);
});

// ---------------------------------------------------------------------------
// godotBinaryRank
// ---------------------------------------------------------------------------

test("godotBinaryRank: mono _console ranks highest", () => {
  assert.equal(godotBinaryRank("Godot_v4.5.1-stable_mono_win64_console.exe"), 3);
});

test("godotBinaryRank: mono ranks above plain _console", () => {
  assert.equal(godotBinaryRank("Godot_v4.5.1-stable_mono_win64.exe"), 2);
  assert.equal(godotBinaryRank("Godot_v4.5.1-stable_win64_console.exe"), 1);
});

test("godotBinaryRank: plain build ranks lowest", () => {
  assert.equal(godotBinaryRank("godot"), 0);
  assert.equal(godotBinaryRank("Godot_v4.5.1-stable_win64.exe"), 0);
});

// ---------------------------------------------------------------------------
// scanForGodotBinaries (filesystem-backed)
// ---------------------------------------------------------------------------

test("scanForGodotBinaries: finds and ranks candidates in a temp tree", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "godot-editor-scan-"));
  try {
    // Plant a plain build and a mono build at depth 1; mono should win.
    fs.mkdirSync(path.join(dir, "v4"), { recursive: true });
    fs.writeFileSync(path.join(dir, "v4", "godot"), "");
    fs.writeFileSync(path.join(dir, "v4", "godot_mono"), "");
    const hits = scanForGodotBinaries(dir, "linux", 3);
    assert.equal(hits.length, 2);
    // mono ranks first.
    assert.equal(path.basename(hits[0]), "godot_mono");
    assert.equal(path.basename(hits[1]), "godot");
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("scanForGodotBinaries: respects the max-depth bound", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "godot-editor-scan-"));
  try {
    // Plant a binary three levels deep.
    fs.mkdirSync(path.join(dir, "a", "b", "c"), { recursive: true });
    fs.writeFileSync(path.join(dir, "a", "b", "c", "godot"), "");
    // depth 2 excludes the binary (it sits at depth 3).
    assert.equal(scanForGodotBinaries(dir, "linux", 2).length, 0);
    // depth 3 includes it.
    assert.equal(scanForGodotBinaries(dir, "linux", 3).length, 1);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("scanForGodotBinaries: prefers newer versions when rank ties", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "godot-editor-scan-"));
  try {
    fs.mkdirSync(path.join(dir, "rel"), { recursive: true });
    fs.writeFileSync(path.join(dir, "rel", "Godot_v4.3-stable_linux.x86_64"), "");
    fs.writeFileSync(path.join(dir, "rel", "Godot_v4.5.1-stable_linux.x86_64"), "");
    const hits = scanForGodotBinaries(dir, "linux", 3);
    assert.equal(hits.length, 2);
    // Both are plain builds (rank 0) → newest version first.
    assert.match(path.basename(hits[0]), /v4\.5\.1/);
    assert.match(path.basename(hits[1]), /v4\.3/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// findGodotBinary resolution
// ---------------------------------------------------------------------------

test("findGodotBinary: explicit editorPath wins when the file exists", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "godot-editor-find-"));
  try {
    const bin = path.join(dir, "godot");
    fs.writeFileSync(bin, "");
    assert.equal(findGodotBinary(bin), bin);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("findGodotBinary: explicit editorPath returns null when missing", () => {
  assert.equal(findGodotBinary("/no/such/godot/binary"), null);
});

test("findGodotBinary: empty-string editorPath returns null", () => {
  assert.equal(findGodotBinary("   "), null);
});

test("findGodotBinary: env var override resolves when the file exists", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "godot-editor-env-"));
  const bin = path.join(dir, "godot");
  fs.writeFileSync(bin, "");
  const prev = process.env["GODOT"];
  process.env["GODOT"] = bin;
  try {
    assert.equal(findGodotBinary(undefined), bin);
  } finally {
    if (prev === undefined) delete process.env["GODOT"];
    else process.env["GODOT"] = prev;
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("findGodotBinary: GODOT_EDITOR env var also resolves", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "godot-editor-env2-"));
  const bin = path.join(dir, "godot");
  fs.writeFileSync(bin, "");
  const prevGodot = process.env["GODOT"];
  const prevEditor = process.env["GODOT_EDITOR"];
  delete process.env["GODOT"];
  process.env["GODOT_EDITOR"] = bin;
  try {
    assert.equal(findGodotBinary(undefined), bin);
  } finally {
    if (prevGodot === undefined) delete process.env["GODOT"];
    else process.env["GODOT"] = prevGodot;
    if (prevEditor === undefined) delete process.env["GODOT_EDITOR"];
    else process.env["GODOT_EDITOR"] = prevEditor;
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("findGodotBinary: returns null when nothing resolves (no env, no path hit)", () => {
  // Clear the Godot env vars and point PATH somewhere empty so discovery falls
  // through to the common-install scan (which we also expect to miss on a CI
  // host without Godot installed). This test is best-effort: if Godot happens
  // to be installed on the host, the assertion is skipped.
  const saved: Record<string, string | undefined> = {};
  for (const v of GODOT_BIN_ENV_VARS) {
    saved[v] = process.env[v];
    delete process.env[v];
  }
  const savedPath = process.env["PATH"];
  process.env["PATH"] = "";
  try {
    const result = findGodotBinary(undefined);
    if (result !== null) {
      // Godot is installed on this host — skip rather than fail.
      return;
    }
    assert.equal(result, null);
  } finally {
    for (const [v, val] of Object.entries(saved)) {
      if (val === undefined) delete process.env[v];
      else process.env[v] = val;
    }
    process.env["PATH"] = savedPath;
  }
});

// ---------------------------------------------------------------------------
// launchEditor (structural — no real Godot spawn)
// ---------------------------------------------------------------------------

test("launchEditor: rejects a non-existent binary via the error callback", async () => {
  // Spawning a path that does not exist fires the 'error' event (ENOENT). The
  // child is still returned (detached + unref'd); we assert the callback fires.
  let captured: Error | undefined;
  const child = launchEditor("/no/such/godot/binary/here", "/tmp", {
    onError: (err) => {
      captured = err;
    },
  });
  // Wait briefly for the error event to fire.
  await new Promise((resolve) => setTimeout(resolve, 100));
  try {
    assert.ok(captured, "expected the error callback to fire for a missing binary");
  } finally {
    // The child may have already exited; best-effort cleanup.
    try {
      child.kill();
    } catch {
      // already gone
    }
  }
});

// ---------------------------------------------------------------------------
// macOS .app-bundle ranking (regression)
// ---------------------------------------------------------------------------
//
// On macOS the edition (.NET/mono) and version markers live in the `.app` BUNDLE DIRECTORY name —
// the inner executable is always literally `Godot`
// (`Godot_v4.5-stable_mono_macos.universal.app/Contents/MacOS/Godot`). Ranking by basename made
// both godotBinaryRank and godotVersionKey constant, so selection collapsed to a path
// localeCompare: the non-.NET build won over the mono build, and 4.3 won over 4.5. The addon is C#,
// so a non-.NET editor cannot load it at all.

/** Lay out macOS-style .app bundles and return the scan root. */
function macAppTree(bundleNames: string[]): { root: string; cleanup: () => void } {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "godot-mac-apps-"));
  for (const name of bundleNames) {
    const macos = path.join(root, name, "Contents", "MacOS");
    fs.mkdirSync(macos, { recursive: true });
    fs.writeFileSync(path.join(macos, "Godot"), "");
  }
  return { root, cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}

/** The winning candidate's `.app` bundle directory name. */
function winningBundle(root: string, ranked: string[]): string {
  return path.relative(root, ranked[0]!).split(path.sep)[0]!;
}

test("scanForGodotBinaries: macOS prefers the .NET (mono) bundle over the plain one", () => {
  const fx = macAppTree([
    "Godot_v4.5-stable_macos.universal.app",
    "Godot_v4.5-stable_mono_macos.universal.app",
  ]);
  try {
    const hits = scanForGodotBinaries(fx.root, "darwin", 3);
    assert.equal(hits.length, 2, "both bundles must be discovered");
    assert.equal(winningBundle(fx.root, hits), "Godot_v4.5-stable_mono_macos.universal.app");
  } finally {
    fx.cleanup();
  }
});

test("scanForGodotBinaries: macOS prefers the newer version bundle", () => {
  const fx = macAppTree([
    "Godot_v4.3-stable_mono_macos.universal.app",
    "Godot_v4.5-stable_mono_macos.universal.app",
  ]);
  try {
    const hits = scanForGodotBinaries(fx.root, "darwin", 3);
    assert.equal(winningBundle(fx.root, hits), "Godot_v4.5-stable_mono_macos.universal.app");
  } finally {
    fx.cleanup();
  }
});

test("scanForGodotBinaries: macOS ranks edition above version (mono 4.3 beats plain 4.5)", () => {
  // Edition is the harder constraint: a plain build cannot load the C# addon at any version.
  const fx = macAppTree([
    "Godot_v4.5-stable_macos.universal.app",
    "Godot_v4.3-stable_mono_macos.universal.app",
  ]);
  try {
    const hits = scanForGodotBinaries(fx.root, "darwin", 3);
    assert.equal(winningBundle(fx.root, hits), "Godot_v4.3-stable_mono_macos.universal.app");
  } finally {
    fx.cleanup();
  }
});

test("scanForGodotBinaries: linux/windows name-based ranking still holds", () => {
  // The relative-path keying must not regress the platforms where the markers are in the file name.
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "godot-lin-"));
  try {
    fs.writeFileSync(path.join(root, "Godot_v4.5-stable_linux.x86_64"), "");
    fs.writeFileSync(path.join(root, "Godot_v4.5-stable_mono_linux.x86_64"), "");
    const hits = scanForGodotBinaries(root, "linux", 1);
    assert.equal(path.basename(hits[0]!), "Godot_v4.5-stable_mono_linux.x86_64");
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
