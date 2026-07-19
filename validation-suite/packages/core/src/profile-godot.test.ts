/**
 * Loads the real bundled `engine-profiles/godot.json` and asserts it
 * validates against the EngineProfile shape with the expected Godot
 * conventions (id, markers, placeholder tokens, tool prefix, companions).
 * Run with: `npm run test:core`.
 */

import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

import { parseProfile } from "./loader.ts";

const here = dirname(fileURLToPath(import.meta.url));
// packages/core/src → validation-suite/engine-profiles/godot.json
const profilePath = join(here, "..", "..", "..", "engine-profiles", "godot.json");

test("godot.json parses and validates against EngineProfile", () => {
  const raw = JSON.parse(readFileSync(profilePath, "utf8"));
  const p = parseProfile(raw);

  assert.equal(p.id, "godot");
  assert.equal(p.displayName, "Godot Open MCP");
  assert.equal(p.mcpCliBinary, "godot-open-mcp");
  assert.equal(p.toolNamePrefix, "godot_open_mcp_");
});

test("godot.json markers detect a project by project.godot with no required dirs", () => {
  const raw = JSON.parse(readFileSync(profilePath, "utf8"));
  const p = parseProfile(raw);

  // Empty `dirs` must be accepted; a single `project.godot` marker suffices.
  assert.deepEqual(p.markers.dirs, []);
  assert.deepEqual(p.markers.files, ["project.godot"]);
});

test("godot.json declares both placeholder tokens", () => {
  const raw = JSON.parse(readFileSync(profilePath, "utf8"));
  const p = parseProfile(raw);

  assert.ok(p.placeholders.includes("{fixtureRoot}"));
  assert.ok(p.placeholders.includes("{projectRoot}"));
});

test("godot.json uses Godot state/fixture path conventions", () => {
  const raw = JSON.parse(readFileSync(profilePath, "utf8"));
  const p = parseProfile(raw);

  assert.equal(p.paths.fixtureRoot, "_ValidationSuite/<test-id>/");
  assert.equal(p.paths.stateRoot, ".godot-open-mcp/ValidationSuite/");
  assert.equal(p.paths.stateFile, ".godot-open-mcp/ValidationSuite/.state.json");
});

test("godot.json companions use Godot sidecars (.import / .uid), never .meta", () => {
  const raw = JSON.parse(readFileSync(profilePath, "utf8"));
  const p = parseProfile(raw);

  assert.ok(p.companions.length > 0);
  for (const c of p.companions) {
    assert.ok(
      c.companion.endsWith(".import") || c.companion.endsWith(".uid"),
      `unexpected companion: ${c.companion}`,
    );
    assert.ok(!c.companion.endsWith(".meta"), "must not use Unity .meta");
  }
});
