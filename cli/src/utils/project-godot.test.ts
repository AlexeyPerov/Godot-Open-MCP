// Tests for the pure `project.godot` toggle helpers
// (src/utils/project-godot.ts). No I/O — every fixture is an inline string so
// the enable/disable transforms are exercised without touching the filesystem.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'
//
// Adapted from the Godot-MCP behavior reference's project-godot tests, with the
// plugin path renamed to `godot_open_mcp` and the toggle signature reordered to
// (text, pluginPath, enable).

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  GODOT_OPEN_MCP_PLUGIN_PATH,
  parseEnabledPlugins,
  togglePluginInText,
} from "./project-godot.js";

// ---------------------------------------------------------------------------
// parseEnabledPlugins
// ---------------------------------------------------------------------------

test("parseEnabledPlugins: empty array when no [editor_plugins] section", () => {
  assert.deepEqual(parseEnabledPlugins(""), []);
  assert.deepEqual(
    parseEnabledPlugins("[application]\n\nname=\"Hello\"\nconfig_version=5\n"),
    [],
  );
});

test("parseEnabledPlugins: empty array when section present but no enabled key", () => {
  const text = "[editor_plugins]\n\n[application]\n";
  assert.deepEqual(parseEnabledPlugins(text), []);
});

test("parseEnabledPlugins: single enabled plugin", () => {
  const text =
    '[editor_plugins]\n\nenabled=PackedStringArray("res://addons/godot_open_mcp/plugin.cfg")\n';
  assert.deepEqual(parseEnabledPlugins(text), [
    "res://addons/godot_open_mcp/plugin.cfg",
  ]);
});

test("parseEnabledPlugins: multiple enabled plugins preserve order", () => {
  const text =
    '[editor_plugins]\n\nenabled=PackedStringArray("res://addons/foo/plugin.cfg", "res://addons/godot_open_mcp/plugin.cfg")\n';
  assert.deepEqual(parseEnabledPlugins(text), [
    "res://addons/foo/plugin.cfg",
    "res://addons/godot_open_mcp/plugin.cfg",
  ]);
});

// ---------------------------------------------------------------------------
// togglePluginInText — enable
// ---------------------------------------------------------------------------

test("togglePluginInText: enable creates section + enabled line when both absent", () => {
  const before = "[application]\n\nname=\"Hello\"\n";
  const result = togglePluginInText(before, GODOT_OPEN_MCP_PLUGIN_PATH, true);
  assert.equal(result.kind, "changed");
  assert.deepEqual(result.enabled, [GODOT_OPEN_MCP_PLUGIN_PATH]);
  // Section + blank line + enabled line appended after existing content.
  assert.match(result.text, /\[editor_plugins\]/);
  assert.match(
    result.text,
    /enabled=PackedStringArray\("res:\/\/addons\/godot_open_mcp\/plugin.cfg"\)/,
  );
});

test("togglePluginInText: enable adds to existing enabled array", () => {
  const before =
    '[editor_plugins]\n\nenabled=PackedStringArray("res://addons/foo/plugin.cfg")\n';
  const result = togglePluginInText(before, GODOT_OPEN_MCP_PLUGIN_PATH, true);
  assert.equal(result.kind, "changed");
  assert.deepEqual(result.enabled, [
    "res://addons/foo/plugin.cfg",
    GODOT_OPEN_MCP_PLUGIN_PATH,
  ]);
  assert.match(
    result.text,
    /enabled=PackedStringArray\("res:\/\/addons\/foo\/plugin.cfg", "res:\/\/addons\/godot_open_mcp\/plugin.cfg"\)/,
  );
});

test("togglePluginInText: enable is idempotent when already enabled", () => {
  const before =
    '[editor_plugins]\n\nenabled=PackedStringArray("res://addons/godot_open_mcp/plugin.cfg")\n';
  const result = togglePluginInText(before, GODOT_OPEN_MCP_PLUGIN_PATH, true);
  assert.equal(result.kind, "unchanged");
  assert.equal(result.text, before);
  assert.deepEqual(result.enabled, [GODOT_OPEN_MCP_PLUGIN_PATH]);
});

test("togglePluginInText: enable preserves unrelated sections and ordering", () => {
  const before =
    '[application]\n\nname="Hello"\n\n[editor_plugins]\n\nenabled=PackedStringArray("res://addons/foo/plugin.cfg")\n\n[input]\n\nui_accept={"deadzone": 0.5}\n';
  const result = togglePluginInText(before, GODOT_OPEN_MCP_PLUGIN_PATH, true);
  assert.equal(result.kind, "changed");
  // Surrounding sections untouched.
  assert.ok(result.text.includes('[application]'));
  assert.ok(result.text.includes('[input]'));
  assert.ok(result.text.includes('name="Hello"'));
  // The other plugin remains enabled.
  assert.ok(result.text.includes("res://addons/foo/plugin.cfg"));
});

// ---------------------------------------------------------------------------
// togglePluginInText — disable
// ---------------------------------------------------------------------------

test("togglePluginInText: disable removes the plugin from the array", () => {
  const before =
    '[editor_plugins]\n\nenabled=PackedStringArray("res://addons/foo/plugin.cfg", "res://addons/godot_open_mcp/plugin.cfg")\n';
  const result = togglePluginInText(before, GODOT_OPEN_MCP_PLUGIN_PATH, false);
  assert.equal(result.kind, "changed");
  assert.deepEqual(result.enabled, ["res://addons/foo/plugin.cfg"]);
  assert.match(
    result.text,
    /enabled=PackedStringArray\("res:\/\/addons\/foo\/plugin.cfg"\)/,
  );
  // The godot_open_mcp entry is gone from the rendered line.
  assert.ok(!result.text.includes("godot_open_mcp"));
});

test("togglePluginInText: disable is idempotent when already absent", () => {
  const before =
    '[editor_plugins]\n\nenabled=PackedStringArray("res://addons/foo/plugin.cfg")\n';
  const result = togglePluginInText(before, GODOT_OPEN_MCP_PLUGIN_PATH, false);
  assert.equal(result.kind, "unchanged");
  assert.equal(result.text, before);
});

test("togglePluginInText: disable on a body with no section is unchanged", () => {
  const before = "[application]\n\nname=\"Hello\"\n";
  const result = togglePluginInText(before, GODOT_OPEN_MCP_PLUGIN_PATH, false);
  assert.equal(result.kind, "unchanged");
  assert.equal(result.text, before);
  assert.deepEqual(result.enabled, []);
});

// ---------------------------------------------------------------------------
// edge cases
// ---------------------------------------------------------------------------

test("togglePluginInText: handles arbitrary plugin path (forward-compat)", () => {
  // The helper is not hard-coded to godot_open_mcp — it toggles any path, so a
  // future sibling verify addon can reuse it.
  const verifyPath = "res://addons/godot_open_mcp_verify/plugin.cfg";
  const before =
    '[editor_plugins]\n\nenabled=PackedStringArray("res://addons/godot_open_mcp/plugin.cfg")\n';
  const result = togglePluginInText(before, verifyPath, true);
  assert.equal(result.kind, "changed");
  assert.deepEqual(result.enabled, [
    "res://addons/godot_open_mcp/plugin.cfg",
    verifyPath,
  ]);
});

test("togglePluginInText: enable into empty body creates a clean section", () => {
  const result = togglePluginInText("", GODOT_OPEN_MCP_PLUGIN_PATH, true);
  assert.equal(result.kind, "changed");
  assert.deepEqual(result.enabled, [GODOT_OPEN_MCP_PLUGIN_PATH]);
  assert.equal(
    result.text,
    '[editor_plugins]\n\nenabled=PackedStringArray("res://addons/godot_open_mcp/plugin.cfg")\n',
  );
});
