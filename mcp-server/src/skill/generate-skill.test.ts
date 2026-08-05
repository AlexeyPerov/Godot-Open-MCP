// P15.5 — `godot_open_mcp_generate_skill` logic tests.
//
// Pins the project-state reader, the skill composer (merge + standalone), and
// the client write path. The router-level dispatch (route tag, write:true →
// written[], error envelope) is covered by tool-router.test.ts; this file
// pins the pure builder + the disk I/O of the orchestrator.
//
// The capability surface injected into the composer is a minimal hand-built
// `CapabilitiesResult` (one tool, one rule, one fix) so the tests do not
// depend on the full ALL_TOOLS roster or the rule-catalog drift tests. The
// shape mirrors `buildCapabilities` output.
//
// Built + run via the project test config:
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, rm, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
  readProjectState,
  generateSkillMarkdown,
  composeSkillMarkdown,
  writeSkillToClients,
  generateSkill,
  truncateForPreview,
} from "./generate-skill.js";
import {
  loadManifest,
  knownClientKeys,
  clientSkillRelativePath,
  resolveTemplateSkillPath,
  BUNDLED_MANIFEST,
  _clearClientPathsCacheForTests,
} from "./client-paths.js";
import type { CapabilitiesResult } from "../capabilities/build-capabilities.js";

// ---------------------------------------------------------------------------
// Fixture: a minimal capability surface (shape mirrors buildCapabilities).
// ---------------------------------------------------------------------------

const MIN_CAPS: CapabilitiesResult = {
  tools: [
    {
      name: "godot_open_mcp_ping",
      implemented: true,
      status: "implemented",
      description: "Connectivity probe. Returns ok when the bridge answers.",
      routePolicy: "live",
    },
  ],
  rules: [
    {
      id: "missing_scripts",
      title: "Missing scripts",
      description: "Detects nodes whose script resource is missing.",
      applicableAssetKinds: ["scene"],
      implemented: true,
      status: "implemented",
      issues: [
        {
          code: "missing_script",
          severity: "Error",
          rootCause: "missing_script_reference",
          fixIds: ["remove_missing_script"],
        },
      ],
    },
  ],
  fixes: [
    {
      id: "remove_missing_script",
      implemented: true,
      status: "implemented",
      rules: ["missing_scripts"],
      issueCodes: ["missing_script"],
      safe: true,
    },
  ],
  toolGroups: [],
  counts: {
    toolsImplemented: 1,
    toolsPlanned: 0,
    rulesImplemented: 1,
    rulesPlanned: 0,
    fixesImplemented: 1,
    fixesPlanned: 0,
  },
  routing: { liveDefault: true, policies: ["live", "local", "offline", "live-first"] },
};

const SAMPLE_PROJECT_GODOT = `
[application]

config/name="Demo Game"
config/features=PackedStringArray("4.3", "Forward Plus")
run/main_scene="res://Main.tscn"

[editor_plugins]

enabled=PackedStringArray("res://addons/godot_open_mcp/plugin.cfg", "res://addons/other/plugin.cfg")

[autoload]

Player="*res://player.gd"
Globals="*res://globals.gd"
`;

async function makeProjectDir(): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), "gom-genskill-"));
  await writeFile(join(root, "project.godot"), SAMPLE_PROJECT_GODOT, "utf-8");
  await mkdir(join(root, "scripts"), { recursive: true });
  await mkdir(join(root, "addons", "myaddon"), { recursive: true });
  // A @tool GDScript with a class_name.
  await writeFile(
    join(root, "scripts", "player.gd"),
    "@tool\nextends CharacterBody2D\nclass_name Player\n",
    "utf-8",
  );
  // A plain GDScript with a class_name (no @tool).
  await writeFile(
    join(root, "scripts", "enemy.gd"),
    "extends Node2D\nclass_name Enemy\n",
    "utf-8",
  );
  // A GDScript with no class_name — should be skipped.
  await writeFile(
    join(root, "scripts", "util.gd"),
    "extends Node\nfunc _ready(): pass\n",
    "utf-8",
  );
  // A C# script deriving from a Node subclass.
  await writeFile(
    join(root, "scripts", "CameraRig.cs"),
    "using Godot;\npublic partial class CameraRig : Camera3D { }\n",
    "utf-8",
  );
  // A C# script with no Godot base — should be skipped.
  await writeFile(
    join(root, "scripts", "Helper.cs"),
    "public class Helper { }\n",
    "utf-8",
  );
  return root;
}

// ---------------------------------------------------------------------------
// readProjectState
// ---------------------------------------------------------------------------

test("readProjectState parses project.godot name, version, plugins, autoloads", async () => {
  const root = await makeProjectDir();
  try {
    const state = await readProjectState(root);
    assert.equal(state.projectName, "Demo Game");
    assert.equal(state.godotVersion, "4.3");
    assert.ok(state.features.includes("4.3"));
    assert.ok(state.features.includes("Forward Plus"));
    assert.equal(state.bridgeInstalled, true);
    assert.equal(state.plugins.length, 2);
    assert.ok(
      state.plugins.some(
        (p) => p.enabled && p.path.includes("godot_open_mcp"),
      ),
    );
    assert.deepEqual(
      state.autoloads.map((a) => a.name).sort(),
      ["Globals", "Player"],
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("readProjectState surfaces @tool + plain class_name + C# Node subclass types", async () => {
  const root = await makeProjectDir();
  try {
    const state = await readProjectState(root);
    const names = state.types.map((t) => t.name).sort();
    // Player (@tool), Enemy (plain), CameraRig (C# Camera3D). util.gd and
    // Helper.cs have no class_name / Godot base and are skipped.
    assert.deepEqual(names, ["CameraRig", "Enemy", "Player"]);

    const player = state.types.find((t) => t.name === "Player");
    assert.equal(player?.kind, "gdscript");
    assert.equal(player?.role, "tool");

    const enemy = state.types.find((t) => t.name === "Enemy");
    assert.equal(enemy?.kind, "gdscript");
    assert.equal(enemy?.role, "script");

    const camera = state.types.find((t) => t.name === "CameraRig");
    assert.equal(camera?.kind, "csharp");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("readProjectState degrades gracefully when project.godot is missing", async () => {
  const root = await mkdtemp(join(tmpdir(), "gom-empty-"));
  try {
    const state = await readProjectState(root);
    assert.equal(state.godotVersion, "unknown");
    assert.equal(state.plugins.length, 0);
    assert.equal(state.bridgeInstalled, false);
    // Project name falls back to the directory basename.
    assert.equal(state.projectName, root.split(/[/\\]/).pop());
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// Skill composition
// ---------------------------------------------------------------------------

test("generateSkillMarkdown (standalone) includes inventory + capability + workflow blocks", async () => {
  const root = await makeProjectDir();
  try {
    const state = await readProjectState(root);
    const md = generateSkillMarkdown(state, MIN_CAPS);
    // Project environment block.
    assert.match(md, /Godot version:\*\* 4\.3/);
    assert.match(md, /Bridge addon:\*\* enabled/);
    // Plugins table.
    assert.match(md, /godot_open_mcp/);
    // Key project types — @tool tag present.
    assert.match(md, /\*\*Player\*\* `@tool`/);
    assert.match(md, /\*\*Enemy\*\*/);
    // C# section.
    assert.match(md, /\*\*CameraRig\*\*/);
    // Capability blocks.
    assert.match(md, /## Available tools/);
    assert.match(md, /godot_open_mcp_ping/);
    assert.match(md, /## Verify rules \(gate\)/);
    assert.match(md, /missing_script/);
    assert.match(md, /## Available fixes/);
    assert.match(md, /remove_missing_script/);
    // Workflow summary.
    assert.match(md, /## Core workflow: mutate → gate → fix/);
    // Footer.
    assert.match(md, /Auto-generated by `godot_open_mcp_generate_skill`/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("composeSkillMarkdown merges template verbatim + appends a Project inventory section", async () => {
  const root = await makeProjectDir();
  try {
    const state = await readProjectState(root);
    const template = "# Canonical playbook\n\nOperational rules go here.\n";
    const md = composeSkillMarkdown(state, MIN_CAPS, template, {
      includeWorkflow: true,
    });
    // Template appears verbatim at the top.
    assert.match(md, /^# Canonical playbook/);
    assert.match(md, /Operational rules go here\./);
    // Separator + project inventory section heading.
    assert.match(md, /^---$/m);
    assert.match(md, /# Project inventory — Demo Game/);
    // Inventory content present.
    assert.match(md, /Godot version:\*\* 4\.3/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("composeSkillMarkdown falls back to standalone when template is null", async () => {
  const root = await makeProjectDir();
  try {
    const state = await readProjectState(root);
    const md = composeSkillMarkdown(state, MIN_CAPS, null, {
      includeWorkflow: true,
    });
    // No template → standalone header + workflow summary.
    assert.match(md, /# Godot Open MCP — agent skill \(Demo Game\)/);
    assert.match(md, /## Core workflow: mutate → gate → fix/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("composeSkillMarkdown falls back to standalone when include_workflow is false", async () => {
  const root = await makeProjectDir();
  try {
    const state = await readProjectState(root);
    const template = "# Canonical playbook\n\nShould NOT appear.\n";
    const md = composeSkillMarkdown(state, MIN_CAPS, template, {
      includeWorkflow: false,
    });
    assert.doesNotMatch(md, /Should NOT appear\./);
    assert.match(md, /# Godot Open MCP — agent skill/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// Client write path
// ---------------------------------------------------------------------------

test("writeSkillToClients writes to the manifest-declared relative paths and creates parent dirs", async () => {
  const root = await mkdtemp(join(tmpdir(), "gom-write-"));
  try {
    const targets = await writeSkillToClients(root, "# test skill\n", [
      "claude",
      "cursor",
    ]);
    assert.equal(targets.length, 2);
    assert.equal(targets[0].written, true);
    // Claude path from the manifest: .claude/skills/godot-open-mcp/SKILL.md
    assert.match(targets[0].relativePath, /^\.claude\//);
    const written = await readFile(targets[0].absolutePath, "utf-8");
    assert.equal(written, "# test skill\n");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("writeSkillToClients skips unknown client keys without aborting the whole write", async () => {
  const root = await mkdtemp(join(tmpdir(), "gom-writebad-"));
  try {
    const targets = await writeSkillToClients(root, "# test\n", [
      "claude",
      "not-a-real-client",
    ]);
    // Only claude is written; the unknown key is skipped.
    assert.equal(targets.length, 1);
    assert.equal(targets[0].client, "claude");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("writeSkillToClients reports existed:true when overwriting a prior file", async () => {
  const root = await mkdtemp(join(tmpdir(), "gom-overwrite-"));
  try {
    await writeSkillToClients(root, "# first\n", ["claude"]);
    const second = await writeSkillToClients(root, "# second\n", ["claude"]);
    assert.equal(second[0].existed, true);
    const written = await readFile(second[0].absolutePath, "utf-8");
    assert.equal(written, "# second\n");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// Orchestrator
// ---------------------------------------------------------------------------

test("generateSkill write:false returns preview content + no written targets", async () => {
  const root = await makeProjectDir();
  try {
    const result = await generateSkill(root, MIN_CAPS, { write: false });
    assert.ok(result.skill.length > 0);
    assert.equal(result.written.length, 0);
    // The orchestrator reads the real template from the repo (the manifest
    // walk-up finds skills/godot-open-mcp/SKILL.md in this checkout), so the
    // merge path is exercised.
    assert.equal(typeof result.mergedWithTemplate, "boolean");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("generateSkill write:true persists to the default client (claude)", async () => {
  const root = await makeProjectDir();
  try {
    const result = await generateSkill(root, MIN_CAPS, { write: true });
    assert.equal(result.written.length, 1);
    assert.equal(result.written[0].client, "claude");
    const onDisk = await readFile(result.written[0].absolutePath, "utf-8");
    assert.equal(onDisk, result.skill);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("generateSkill respects an explicit clients list", async () => {
  const root = await makeProjectDir();
  try {
    const result = await generateSkill(root, MIN_CAPS, {
      write: true,
      clients: ["claude", "cursor", "opencode"],
    });
    const writtenClients = result.written.map((w) => w.client).sort();
    assert.deepEqual(writtenClients, ["claude", "cursor", "opencode"]);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// truncateForPreview
// ---------------------------------------------------------------------------

test("truncateForPreview returns short content unchanged", () => {
  const short = "# skill\n\nsmall content\n";
  assert.equal(truncateForPreview(short), short);
});

test("truncateForPreview truncates long content with a tail marker", () => {
  const long = "x".repeat(10_000);
  const out = truncateForPreview(long);
  assert.ok(out.length < long.length);
  assert.match(out, /more chars; full content written to disk\)/);
});

// ---------------------------------------------------------------------------
// client-paths manifest sync (anti-drift between BUNDLED_MANIFEST and disk)
// ---------------------------------------------------------------------------

test("the bundled client-keys roster is a subset of the on-disk manifest", () => {
  // The on-disk manifest is the source of truth. The bundled fallback must
  // carry the same client keys (a superset is fine for forward-compat; a
  // missing key is drift). This catches a manifest edit that forgot to update
  // BUNDLED_MANIFEST.
  _clearClientPathsCacheForTests();
  const disk = loadManifest();
  const diskKeys = new Set(Object.keys(disk.clients));
  const bundledKeys = Object.keys(BUNDLED_MANIFEST.clients);
  for (const key of bundledKeys) {
    assert.ok(
      diskKeys.has(key),
      `BUNDLED_MANIFEST carries client key '${key}' that is not in skills/client-paths.json — update the fallback`,
    );
  }
  _clearClientPathsCacheForTests();
});

test("knownClientKeys returns a non-empty, sorted, deduped roster", () => {
  _clearClientPathsCacheForTests();
  const keys = knownClientKeys();
  assert.ok(keys.length > 0);
  assert.ok(keys.includes("claude"));
  // Sorted + deduped.
  const sorted = [...keys].sort();
  assert.deepEqual(keys, sorted);
  assert.equal(new Set(keys).size, keys.length);
  _clearClientPathsCacheForTests();
});

test("clientSkillRelativePath returns the manifest path for a known key and throws for unknown", () => {
  _clearClientPathsCacheForTests();
  const claudePath = clientSkillRelativePath("claude");
  assert.match(claudePath, /^\.claude\/skills\/godot-open-mcp\/SKILL\.md$/);
  assert.throws(
    () => clientSkillRelativePath("not-a-real-client"),
    /Unknown client key/,
  );
  _clearClientPathsCacheForTests();
});

test("resolveTemplateSkillPath resolves to the on-disk template in this checkout", () => {
  _clearClientPathsCacheForTests();
  const p = resolveTemplateSkillPath();
  // In this dev checkout the repo root is discovered, so the template path is
  // non-null and points at the canonical playbook.
  assert.ok(p !== null, "expected the template path to resolve in dev");
  assert.match(p, /skills\/godot-open-mcp\/SKILL\.md$/);
  _clearClientPathsCacheForTests();
});
