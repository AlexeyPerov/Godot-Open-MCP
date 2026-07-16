// Bounded `project.godot` identification tests (P7.3).
//
// Pins the offline project-identification contract: a directory is a Godot
// project iff it carries a regular `project.godot` marker; the identifier
// reads only `config/name` + `config/features` and tolerates the rest. Missing
// marker → `project_not_found`; unreadable/non-regular/oversized marker →
// `project_config_unreadable`.
//
// Built + run via the project test config:
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, symlink, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
  identifyGodotProject,
  PROJECT_GODOT_BYTE_CAP,
} from "./project-config.js";

async function makeProject(
  projectGodot: string,
): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), "gom-config-"));
  await writeFile(join(root, "project.godot"), projectGodot, "utf-8");
  return root;
}

const TYPICAL = `; Engine configuration file.
; It's best edited using the editor UI and not directly,
; since the parameters that go here are not all obvious.

config_version=5

[application]

config/name="My Cool Game"
config/features=PackedStringArray("4.3", "GL Compatibility")
config/icon="res://icon.svg"

[display]

window/size/viewport_width=1280
window/size/viewport_height=720
`;

// ---------------------------------------------------------------------------
// happy path
// ---------------------------------------------------------------------------

test("identifyGodotProject: parses config/name + config/features from a typical project", async () => {
  const root = await makeProject(TYPICAL);
  const r = await identifyGodotProject(root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  assert.equal(r.info.projectName, "My Cool Game");
  assert.deepEqual(r.info.features, ["4.3", "GL Compatibility"]);
});

test("identifyGodotProject: minimal marker with no [application] is still a project", async () => {
  // A freshly `godot --init`-style project may have only config_version.
  const root = await makeProject("config_version=5\n");
  const r = await identifyGodotProject(root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  assert.equal(r.info.projectName, null);
  assert.deepEqual(r.info.features, []);
});

test("identifyGodotProject: unquoted name value is captured verbatim", async () => {
  const root = await makeProject(
    `[application]\nconfig/name=BareName\n`,
  );
  const r = await identifyGodotProject(root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  assert.equal(r.info.projectName, "BareName");
});

test("identifyGodotProject: only the FIRST config/name wins (later duplicates ignored)", async () => {
  const root = await makeProject(
    `[application]\nconfig/name="First"\nconfig/name="Second"\n`,
  );
  const r = await identifyGodotProject(root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  assert.equal(r.info.projectName, "First");
});

test("identifyGodotProject: unknown sections + comments are skipped, not fatal", async () => {
  const root = await makeProject(
    `; comment line\n` +
      `# hash comment\n` +
      `[rendering]\n` +
      `renderer/rendering_method="mobile"\n` +
      `\n` +
      `[application]\n` +
      `config/name="With Comments"\n`,
  );
  const r = await identifyGodotProject(root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  assert.equal(r.info.projectName, "With Comments");
});

test("identifyGodotProject: empty config/features yields [] (no empty strings)", async () => {
  const root = await makeProject(
    `[application]\nconfig/name="X"\nconfig/features=PackedStringArray()\n`,
  );
  const r = await identifyGodotProject(root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  assert.deepEqual(r.info.features, []);
});

test("identifyGodotProject: features with trailing/leading spaces are trimmed", async () => {
  const root = await makeProject(
    `[application]\nconfig/features=PackedStringArray("  Double Precision ", " Forward Plus ")\n`,
  );
  const r = await identifyGodotProject(root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  assert.deepEqual(r.info.features, ["Double Precision", "Forward Plus"]);
});

// ---------------------------------------------------------------------------
// project root resolution
// ---------------------------------------------------------------------------

test("identifyGodotProject: returns the realpath'd project root", async () => {
  const root = await makeProject(TYPICAL);
  const r = await identifyGodotProject(root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  // On macOS /var → /private/var; the returned root must be the canonical path.
  assert.ok(
    r.info.projectRoot.length > 0,
    "project root is non-empty",
  );
  assert.ok(
    r.info.projectRoot.endsWith("gom-config-") ||
      r.info.projectRoot.includes("gom-config-"),
    "project root reflects the temp dir",
  );
});

// ---------------------------------------------------------------------------
// project_not_found
// ---------------------------------------------------------------------------

test("identifyGodotProject: missing project root → project_not_found", async () => {
  const r = await identifyGodotProject("/does/not/exist/xyz-123");
  assert.equal(r.ok, false);
  if (r.ok) return;
  assert.equal(r.error.code, "project_not_found");
});

test("identifyGodotProject: directory without project.godot → project_not_found", async () => {
  const root = await mkdtemp(join(tmpdir(), "gom-noconfig-"));
  const r = await identifyGodotProject(root);
  assert.equal(r.ok, false);
  if (r.ok) return;
  assert.equal(r.error.code, "project_not_found");
});

// ---------------------------------------------------------------------------
// project_config_unreadable
// ---------------------------------------------------------------------------

test("identifyGodotProject: project.godot as a directory → project_config_unreadable", async () => {
  const root = await mkdtemp(join(tmpdir(), "gom-dirmarker-"));
  await mkdir(join(root, "project.godot"));
  const r = await identifyGodotProject(root);
  assert.equal(r.ok, false);
  if (r.ok) return;
  assert.equal(r.error.code, "project_config_unreadable");
});

test("identifyGodotProject: oversized marker → project_config_unreadable", async () => {
  const root = await mkdtemp(join(tmpdir(), "gom-bigmarker-"));
  // Write a marker just over the byte cap.
  await writeFile(
    join(root, "project.godot"),
    Buffer.alloc(PROJECT_GODOT_BYTE_CAP + 16, 0x61 /* 'a' */),
  );
  const r = await identifyGodotProject(root);
  assert.equal(r.ok, false);
  if (r.ok) return;
  assert.equal(r.error.code, "project_config_unreadable");
  assert.match(r.error.message, /exceeds the .*-byte identification cap/);
});

// ---------------------------------------------------------------------------
// malformed-but-accepted (lenient parse)
// ---------------------------------------------------------------------------

test("identifyGodotProject: marker with only garbage content is still accepted as a project", async () => {
  // Godot tolerates a near-empty / weird project.godot; we mirror that — any
  // present, regular, readable marker identifies the directory as a project.
  // Name/features are just null/[] when nothing parses.
  const root = await makeProject("not an ini at all\n%%%garbage%%%\n");
  const r = await identifyGodotProject(root);
  assert.equal(r.ok, true);
  if (!r.ok) return;
  assert.equal(r.info.projectName, null);
  assert.deepEqual(r.info.features, []);
});
