// `godot_open_mcp_filesystem_list` / `filesystem_reimport` tool-definition tests (P4.4).
// Pins the catalog metadata the MCP ListTools response advertises to AI clients for the filesystem
// tools — name prefix per ADR-003, non-empty descriptions, the Godot-adapted property sets, the
// `additionalProperties: false` guards, and the gate surface (paths_hint + gate for the mutator).
// The live round-trip (POST /tools/godot_open_mcp_filesystem_{list,reimport} → bridge handler →
// EditorFileSystem → listing/settle envelope) is exercised against a local HTTP stub + headless
// Godot smoke; this file only asserts the contracts advertised over stdio.
//
// Adapted from resource-tools.test.ts / resource-file-operations.test.ts (copy fidelity for the
// catalog-metadata test shape), with property-set assertions specific to each P4.4 schema. The key
// contrast: filesystem_list is read-only (no paths_hint/gate), filesystem_reimport is mutating.

import { test } from "node:test";
import assert from "node:assert/strict";
import { filesystemList } from "./filesystem-list.js";
import { filesystemReimport } from "./filesystem-reimport.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// filesystem_list — read-only (no paths_hint / gate).
// ---------------------------------------------------------------------------

test("filesystem_list tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(filesystemList.name, "godot_open_mcp_filesystem_list");
  assert.match(filesystemList.name, /^godot_open_mcp_/);
});

test("filesystem_list tool has a non-empty description", () => {
  assert.ok(typeof filesystemList.description === "string");
  assert.ok((filesystemList.description ?? "").length > 0);
});

test("filesystem_list tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(filesystemList.inputSchema.type, "object");
  assert.equal(filesystemList.inputSchema.additionalProperties, false);
});

test("filesystem_list exposes the Godot-adapted property set", () => {
  // path (optional directory) + page_size + cursor (paging) + include_hidden. Unity's folder/kind
  // listing becomes the EditorFileSystemDirectory one-level walk.
  const props = filesystemList.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "cursor",
    "include_hidden",
    "page_size",
    "path",
  ]);
});

test("filesystem_list does NOT expose Unity-only fields or gate surface", () => {
  // Guards against an accidental copy-paste from Unity's list-assets schema.
  const props = filesystemList.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "read-only tool — no gate surface");
  assert.equal(props.gate, undefined, "read-only tool — no gate surface");
  assert.equal(props.guid, undefined, "Unity GUID replaced by uid in file entries");
  assert.equal(props.recursive, undefined, "one-level listing only — recursive deferred to P7.3");
  assert.equal(props.folder, undefined, "renamed to path");
});

test("filesystem_list page_size defaults to 100 with bounds", () => {
  const props = filesystemList.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number; maximum?: number }
  >;
  assert.equal(props.page_size.default, 100);
  assert.equal(props.page_size.minimum, 1);
  assert.equal(props.page_size.maximum, 500);
});

test("filesystem_list include_hidden defaults to false", () => {
  const props = filesystemList.inputSchema.properties as Record<
    string,
    { default?: boolean }
  >;
  assert.equal(props.include_hidden.default, false);
});

test("filesystem_list has no required array (path is optional)", () => {
  assert.equal(filesystemList.inputSchema.required, undefined);
});

// ---------------------------------------------------------------------------
// filesystem_reimport — mutating (has paths_hint + gate, default enforce).
// ---------------------------------------------------------------------------

test("filesystem_reimport tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(filesystemReimport.name, "godot_open_mcp_filesystem_reimport");
  assert.match(filesystemReimport.name, /^godot_open_mcp_/);
});

test("filesystem_reimport tool has a non-empty description", () => {
  assert.ok(typeof filesystemReimport.description === "string");
  assert.ok((filesystemReimport.description ?? "").length > 0);
});

test("filesystem_reimport tool declares an object input schema with additionalProperties:false", () => {
  assert.equal(filesystemReimport.inputSchema.type, "object");
  assert.equal(filesystemReimport.inputSchema.additionalProperties, false);
});

test("filesystem_reimport exposes the Godot-adapted property set", () => {
  // files (exact reimport) + timeout_ms (settle bound) + paths_hint (gate scope) + gate (mode).
  const props = filesystemReimport.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "files",
    "gate",
    "paths_hint",
    "timeout_ms",
  ]);
});

test("filesystem_reimport DOES expose paths_hint and gate (mutating tool)", () => {
  // Contrast with the P4.4 read-only filesystem_list that explicitly asserts these are absent.
  const props = filesystemReimport.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.paths_hint !== undefined, "paths_hint required for mutating tool");
  assert.ok(props.gate !== undefined, "gate surface required for mutating tool");
});

test("filesystem_reimport gate defaults to enforce", () => {
  const props = filesystemReimport.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.equal(props.gate.default, "enforce");
  assert.deepEqual(props.gate.enum, ["enforce", "warn", "off"]);
});

test("filesystem_reimport requires paths_hint", () => {
  // files is optional (omitted → full scan); paths_hint is always mandatory.
  assert.deepEqual(filesystemReimport.inputSchema.required, ["paths_hint"]);
});

test("filesystem_reimport timeout_ms defaults to 5000 with bounds", () => {
  const props = filesystemReimport.inputSchema.properties as Record<
    string,
    { default?: number; minimum?: number; maximum?: number }
  >;
  assert.equal(props.timeout_ms.default, 5000);
  assert.equal(props.timeout_ms.minimum, 1000);
  assert.equal(props.timeout_ms.maximum, 60000);
});

test("filesystem_reimport does NOT expose Unity-only fields", () => {
  // Guards against accidental copy-paste from Unity's assets-refresh schema.
  const props = filesystemReimport.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.asset_path, undefined, "Unity asset_path replaced by files[]");
  assert.equal(props.paths, undefined, "no paths[] array — use files[]");
  assert.equal(props.import_assets, undefined, "no Unity import-assets flag");
  assert.equal(props.recursive, undefined, "no recursive flag — mode is implicit (files vs scan)");
});

// ---------------------------------------------------------------------------
// Registration.
// ---------------------------------------------------------------------------

test("ALL_TOOLS registers both P4.4 filesystem tools", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(names.includes("godot_open_mcp_filesystem_list"), "filesystem_list missing from ALL_TOOLS");
  assert.ok(names.includes("godot_open_mcp_filesystem_reimport"), "filesystem_reimport missing from ALL_TOOLS");
});
