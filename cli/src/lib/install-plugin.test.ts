// Integration tests for the `install-plugin` library (src/lib/install-plugin.ts).
//
// Each test builds a temp Godot project + a temp addon source on disk, runs the
// installer, and asserts on the resulting file tree + `project.godot` state.
// The temp dirs are cleaned up in a finally block. No real Godot editor is
// involved — the installer is pure Node fs.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";

import { installPlugin } from "./install-plugin.js";
import { GODOT_OPEN_MCP_PLUGIN_PATH } from "../utils/project-godot.js";

// ---------------------------------------------------------------------------
// temp-fixture helpers
// ---------------------------------------------------------------------------

/**
 * Build a minimal addon source dir at <root>/addon-src containing a plugin.cfg
 * + a couple of nested files. Mirrors the shape of packages/bridge/ (plugin.cfg
 * at the root, code under Editor/). Optionally drops a Tests/ subtree to prove
 * it is excluded from the install.
 */
function buildAddonSource(root: string, withTests: boolean): string {
  const src = path.join(root, "addon-src");
  fs.mkdirSync(path.join(src, "Editor"), { recursive: true });
  fs.writeFileSync(
    path.join(src, "plugin.cfg"),
    '[plugin]\n\nname="Godot Open MCP"\nscript="Editor/GodotOpenMcpPlugin.cs"\n',
  );
  fs.writeFileSync(
    path.join(src, "Editor", "GodotOpenMcpPlugin.cs"),
    "// stub\n",
  );
  if (withTests) {
    fs.mkdirSync(path.join(src, "Tests"), { recursive: true });
    fs.writeFileSync(path.join(src, "Tests", "Smoke.cs"), "// test stub\n");
    // Build junk dirs that must also be excluded.
    fs.mkdirSync(path.join(src, "obj"), { recursive: true });
    fs.writeFileSync(path.join(src, "obj", "junk.txt"), "obj\n");
    fs.mkdirSync(path.join(src, "bin"), { recursive: true });
    fs.writeFileSync(path.join(src, "bin", "junk.dll"), "bin\n");
  }
  return src;
}

/** Build a minimal Godot project at <root>/project with a project.godot. */
function buildGodotProject(root: string, body: string): string {
  const project = path.join(root, "project");
  fs.mkdirSync(project, { recursive: true });
  fs.writeFileSync(path.join(project, "project.godot"), body);
  return project;
}

/** Per-test scratch dir; cleaned up by the caller via cleanup(). */
function scratchDir(): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), "godot-open-mcp-install-"));
}

function cleanup(dir: string): void {
  fs.rmSync(dir, { recursive: true, force: true });
}

const MINIMAL_PROJECT_GODOT = [
  "; Engine configuration file.",
  "; It's best edited using the editor UI and not via hand.",
 "",
  "[application]",
  "",
  'name="TestGame"',
  "config_version=5",
  "",
].join("\n");

// ---------------------------------------------------------------------------
// happy path
// ---------------------------------------------------------------------------

test("installPlugin: copies addon + enables plugin in project.godot", async () => {
  const dir = scratchDir();
  try {
    const source = buildAddonSource(dir, /* withTests */ true);
    const project = buildGodotProject(dir, MINIMAL_PROJECT_GODOT);

    const result = await installPlugin({
      godotProjectPath: project,
      source,
    });

    assert.equal(result.kind, "success");
    if (result.kind !== "success") return; // narrowing
    assert.equal(result.changed, true);
    assert.equal(result.pluginPath, GODOT_OPEN_MCP_PLUGIN_PATH);
    assert.deepEqual(result.enabledPlugins, [GODOT_OPEN_MCP_PLUGIN_PATH]);
    assert.equal(result.materialize.source, "local");
    assert.equal(result.materialize.sourceDir, path.resolve(source));

    // Addon files landed at addons/godot_open_mcp/.
    const addonDir = path.join(project, "addons", "godot_open_mcp");
    assert.ok(fs.existsSync(path.join(addonDir, "plugin.cfg")));
    assert.ok(
      fs.existsSync(path.join(addonDir, "Editor", "GodotOpenMcpPlugin.cs")),
    );

    // project.godot lists the plugin.
    const text = fs.readFileSync(path.join(project, "project.godot"), "utf-8");
    assert.ok(text.includes("[editor_plugins]"));
    assert.ok(text.includes("godot_open_mcp/plugin.cfg"));
    // Unrelated content preserved.
    assert.ok(text.includes('name="TestGame"'));
  } finally {
    cleanup(dir);
  }
});

test("installPlugin: excludes Tests/, obj/, bin/ + dev files from the addon copy", async () => {
  const dir = scratchDir();
  try {
    const source = buildAddonSource(dir, /* withTests */ true);
    // Drop dev files at the addon root that must not ship into a consumer.
    fs.writeFileSync(path.join(source, ".gitkeep"), "");
    fs.writeFileSync(path.join(source, "AGENTS.md"), "# dev rules\n");
    const project = buildGodotProject(dir, MINIMAL_PROJECT_GODOT);

    await installPlugin({ godotProjectPath: project, source });

    const addonDir = path.join(project, "addons", "godot_open_mcp");
    assert.ok(!fs.existsSync(path.join(addonDir, "Tests")));
    assert.ok(!fs.existsSync(path.join(addonDir, "obj")));
    assert.ok(!fs.existsSync(path.join(addonDir, "bin")));
    assert.ok(!fs.existsSync(path.join(addonDir, ".gitkeep")));
    assert.ok(!fs.existsSync(path.join(addonDir, "AGENTS.md")));
    // The real addon files ARE present.
    assert.ok(fs.existsSync(path.join(addonDir, "plugin.cfg")));
  } finally {
    cleanup(dir);
  }
});

// ---------------------------------------------------------------------------
// verify bundling (P9.3 packaging audit)
// ---------------------------------------------------------------------------

test("installPlugin: bundles sibling verify Editor/ source into addons/godot_open_mcp/Verify/", async () => {
  const dir = scratchDir();
  try {
    // Mirror the monorepo layout: <root>/packages/bridge + <root>/packages/verify.
    const packagesDir = path.join(dir, "packages");
    const bridgeDir = path.join(packagesDir, "bridge");
    const verifyDir = path.join(packagesDir, "verify");
    fs.mkdirSync(path.join(bridgeDir, "Editor"), { recursive: true });
    fs.writeFileSync(
      path.join(bridgeDir, "plugin.cfg"),
      '[plugin]\n\nname="Godot Open MCP"\nscript="Editor/GodotOpenMcpPlugin.cs"\n',
    );
    fs.writeFileSync(
      path.join(bridgeDir, "Editor", "GodotOpenMcpPlugin.cs"),
      "// stub\n",
    );
    // Sibling verify package with an Editor/ subtree (the bridge addon's
    // `using GodotOpenMcp.Verify.*` resolves against this).
    fs.mkdirSync(path.join(verifyDir, "Editor", "Core"), { recursive: true });
    fs.mkdirSync(path.join(verifyDir, "Tests"), { recursive: true });
    fs.writeFileSync(
      path.join(verifyDir, "Editor", "Core", "VerifyRunner.cs"),
      "namespace GodotOpenMcp.Verify.Core { class VerifyRunner {} }\n",
    );
    fs.writeFileSync(
      path.join(verifyDir, "Tests", "Smoke.cs"),
      "// test stub — must NOT ship into the consumer\n",
    );

    const project = buildGodotProject(dir, MINIMAL_PROJECT_GODOT);
    const result = await installPlugin({
      godotProjectPath: project,
      source: bridgeDir,
    });
    assert.equal(result.kind, "success");

    const addonDir = path.join(project, "addons", "godot_open_mcp");
    // The verify Core source is bundled at <addon>/Verify/Core/.
    assert.ok(
      fs.existsSync(path.join(addonDir, "Verify", "Core", "VerifyRunner.cs")),
    );
    // Verify's Tests/ subtree is excluded by the same COPY_EXCLUDE_DIRS rule
    // that scrubs the bridge's Tests/.
    assert.ok(!fs.existsSync(path.join(addonDir, "Verify", "Tests")));
    // No verify-missing warning — the sibling package was found.
    if (result.kind === "success") {
      assert.ok(
        !result.warnings.some((w) => w.includes("verify package")),
        `expected no verify-missing warning, got: ${result.warnings.join("; ")}`,
      );
    }
  } finally {
    cleanup(dir);
  }
});

test("installPlugin: warns when sibling verify package is absent (addon will not compile)", async () => {
  const dir = scratchDir();
  try {
    // Addon source with NO sibling verify package.
    const source = buildAddonSource(dir, /* withTests */ false);
    const project = buildGodotProject(dir, MINIMAL_PROJECT_GODOT);

    const result = await installPlugin({
      godotProjectPath: project,
      source,
    });
    assert.equal(result.kind, "success");
    if (result.kind !== "success") return;
    // The install succeeds (bridge addon ships) but a warning flags that the
    // verify-coupled code paths will not compile until verify is bundled.
    assert.ok(
      result.warnings.some((w) => w.includes("verify package")),
      `expected a verify-missing warning, got: ${result.warnings.join("; ")}`,
    );
    // No Verify/ dir was created.
    assert.ok(
      !fs.existsSync(path.join(project, "addons", "godot_open_mcp", "Verify")),
    );
  } finally {
    cleanup(dir);
  }
});

// ---------------------------------------------------------------------------
// idempotency
// ---------------------------------------------------------------------------

test("installPlugin: re-run reports changed:false and makes no duplicate writes", async () => {
  const dir = scratchDir();
  try {
    const source = buildAddonSource(dir, /* withTests */ false);
    const project = buildGodotProject(dir, MINIMAL_PROJECT_GODOT);

    const first = await installPlugin({ godotProjectPath: project, source });
    assert.equal(first.kind, "success");
    if (first.kind !== "success") return;
    assert.equal(first.changed, true);

    const textAfterFirst = fs.readFileSync(
      path.join(project, "project.godot"),
      "utf-8",
    );

    const second = await installPlugin({ godotProjectPath: project, source });
    assert.equal(second.kind, "success");
    if (second.kind !== "success") return;
    assert.equal(second.changed, false);
    assert.deepEqual(second.enabledPlugins, [GODOT_OPEN_MCP_PLUGIN_PATH]);

    // project.godot byte-identical to the first run's output — no duplicate
    // enabled entries, no churn.
    const textAfterSecond = fs.readFileSync(
      path.join(project, "project.godot"),
      "utf-8",
    );
    assert.equal(textAfterFirst, textAfterSecond);
  } finally {
    cleanup(dir);
  }
});

test("installPlugin: preserves other enabled plugins across installs", async () => {
  const dir = scratchDir();
  try {
    const source = buildAddonSource(dir, /* withTests */ false);
    const otherPlugin = "res://addons/other/plugin.cfg";
    const bodyWithOther =
      MINIMAL_PROJECT_GODOT +
      `[editor_plugins]\n\nenabled=PackedStringArray("${otherPlugin}")\n`;
    const project = buildGodotProject(dir, bodyWithOther);

    const result = await installPlugin({ godotProjectPath: project, source });
    assert.equal(result.kind, "success");
    if (result.kind !== "success") return;
    assert.deepEqual(result.enabledPlugins, [
      otherPlugin,
      GODOT_OPEN_MCP_PLUGIN_PATH,
    ]);

    const text = fs.readFileSync(path.join(project, "project.godot"), "utf-8");
    assert.ok(text.includes(otherPlugin));
    assert.ok(text.includes(GODOT_OPEN_MCP_PLUGIN_PATH));
  } finally {
    cleanup(dir);
  }
});

// ---------------------------------------------------------------------------
// failure modes
// ---------------------------------------------------------------------------

test("installPlugin: not_godot_project when project.godot absent", async () => {
  const dir = scratchDir();
  try {
    const source = buildAddonSource(dir, /* withTests */ false);
    const notAProject = path.join(dir, "no-project");
    fs.mkdirSync(notAProject, { recursive: true });

    const result = await installPlugin({
      godotProjectPath: notAProject,
      source,
    });
    assert.equal(result.kind, "failure");
    if (result.kind !== "failure") return;
    assert.equal(result.errorLabel, "not_godot_project");
    assert.match(result.error.message, /project\.godot/);
  } finally {
    cleanup(dir);
  }
});

test("installPlugin: source_missing when --source has no plugin.cfg", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir, MINIMAL_PROJECT_GODOT);
    const badSource = path.join(dir, "empty-source");
    fs.mkdirSync(badSource, { recursive: true });

    const result = await installPlugin({
      godotProjectPath: project,
      source: badSource,
    });
    assert.equal(result.kind, "failure");
    if (result.kind !== "failure") return;
    assert.equal(result.errorLabel, "source_missing");
    assert.match(result.error.message, /plugin\.cfg/);
  } finally {
    cleanup(dir);
  }
});

test("installPlugin: skipMaterialize only toggles project.godot + warns", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir, MINIMAL_PROJECT_GODOT);

    const result = await installPlugin({
      godotProjectPath: project,
      skipMaterialize: true,
    });
    assert.equal(result.kind, "success");
    if (result.kind !== "success") return;
    assert.equal(result.materialize.source, "skipped");
    assert.equal(result.changed, true); // manifest toggle still counts as a change
    // Warned that no addon files are present.
    assert.ok(result.warnings.some((w) => w.includes("plugin.cfg") && w.includes("absent")));

    // Addon dir was NOT created.
    assert.ok(!fs.existsSync(path.join(project, "addons")));
    // But the plugin IS enabled in project.godot.
    const text = fs.readFileSync(path.join(project, "project.godot"), "utf-8");
    assert.ok(text.includes(GODOT_OPEN_MCP_PLUGIN_PATH));
  } finally {
    cleanup(dir);
  }
});

test("installPlugin: rejects empty godotProjectPath", async () => {
  const result = await installPlugin({ godotProjectPath: "" });
  assert.equal(result.kind, "failure");
  if (result.kind !== "failure") return;
  assert.equal(result.errorLabel, "not_godot_project");
});

// ---------------------------------------------------------------------------
// source resolver shapes
// ---------------------------------------------------------------------------

test("installPlugin: source pointing at a parent of addons/godot_open_mcp resolves", async () => {
  const dir = scratchDir();
  try {
    // Build a source shaped like a checkout: <root>/packages/bridge containing
    // addons/godot_open_mcp/plugin.cfg (the nested form).
    const checkout = path.join(dir, "checkout");
    const nestedAddon = path.join(
      checkout,
      "packages",
      "bridge",
      "addons",
      "godot_open_mcp",
    );
    fs.mkdirSync(nestedAddon, { recursive: true });
    fs.writeFileSync(path.join(nestedAddon, "plugin.cfg"), "[plugin]\n\nname=\"x\"\n");

    const project = buildGodotProject(dir, MINIMAL_PROJECT_GODOT);
    // Point --source at the bridge dir that CONTAINS addons/godot_open_mcp.
    const bridgeDir = path.join(checkout, "packages", "bridge");

    const result = await installPlugin({
      godotProjectPath: project,
      source: bridgeDir,
    });
    assert.equal(result.kind, "success");
    if (result.kind !== "success") return;
    assert.equal(result.materialize.sourceDir, path.resolve(nestedAddon));
    assert.ok(
      fs.existsSync(
        path.join(project, "addons", "godot_open_mcp", "plugin.cfg"),
      ),
    );
  } finally {
    cleanup(dir);
  }
});
