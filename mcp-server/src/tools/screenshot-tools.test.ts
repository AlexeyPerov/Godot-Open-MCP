// `godot_open_mcp_screenshot_*` tool-definition tests (P4.8). Pins the catalog metadata the MCP
// ListTools response advertises to AI clients for the three screenshot tools — name prefix per
// ADR-003, non-empty descriptions, the Godot-adapted property sets, the `additionalProperties:
// false` guards, and the read-only surface (no paths_hint/gate — screenshots are gate-free). The
// live round-trip (POST /tools/godot_open_mcp_screenshot_{viewport,camera,isolated} → bridge
// handler → editor viewport / SubViewport / PNG encode → image envelope) is exercised against a
// local HTTP stub + headless Godot smoke; this file only asserts the contracts advertised over
// stdio.
//
// Adapted from editor-selection.test.ts (copy fidelity for the catalog-metadata test shape), with
// property-set assertions specific to each P4.8 schema. The key contrast: all three screenshot
// tools are read-only (no paths_hint/gate), unlike the mutating editor_selection_set.

import { test } from "node:test";
import assert from "node:assert/strict";
import { screenshotViewport } from "./screenshot-viewport.js";
import { screenshotCamera } from "./screenshot-camera.js";
import { screenshotIsolated } from "./screenshot-isolated.js";
import { ALL_TOOLS } from "./index.js";

// ---------------------------------------------------------------------------
// screenshot_viewport — read-only, mode 2d/3d.
// ---------------------------------------------------------------------------

test("screenshot_viewport tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(screenshotViewport.name, "godot_open_mcp_screenshot_viewport");
  assert.match(screenshotViewport.name, /^godot_open_mcp_/);
});

test("screenshot_viewport tool has a non-empty description", () => {
  assert.ok(typeof screenshotViewport.description === "string");
  assert.ok((screenshotViewport.description ?? "").length > 0);
});

test("screenshot_viewport declares an object schema with additionalProperties:false", () => {
  assert.equal(screenshotViewport.inputSchema.type, "object");
  assert.equal(screenshotViewport.inputSchema.additionalProperties, false);
});

test("screenshot_viewport exposes only the mode property", () => {
  const props = screenshotViewport.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["mode"]);
});

test("screenshot_viewport mode defaults to 3d and accepts 2d/3d only", () => {
  const props = screenshotViewport.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.equal(props.mode.default, "3d");
  assert.deepEqual(props.mode.enum, ["2d", "3d"]);
});

test("screenshot_viewport is read-only (no paths_hint/gate)", () => {
  const props = screenshotViewport.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined, "read-only tool — no gate surface");
  assert.equal(props.gate, undefined, "read-only tool — no gate surface");
});

// ---------------------------------------------------------------------------
// screenshot_camera — read-only, node_ref + width/height.
// ---------------------------------------------------------------------------

test("screenshot_camera tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(screenshotCamera.name, "godot_open_mcp_screenshot_camera");
});

test("screenshot_camera declares an object schema with additionalProperties:false", () => {
  assert.equal(screenshotCamera.inputSchema.type, "object");
  assert.equal(screenshotCamera.inputSchema.additionalProperties, false);
});

test("screenshot_camera exposes node_ref + width + height", () => {
  const props = screenshotCamera.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), ["height", "node_ref", "width"]);
});

test("screenshot_camera node_ref carries instance_id + node_path", () => {
  const props = screenshotCamera.inputSchema.properties as Record<
    string,
    { properties?: Record<string, unknown>; additionalProperties?: boolean }
  >;
  const refProps = props.node_ref.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(refProps).sort(), ["instance_id", "node_path"]);
  assert.equal(props.node_ref.additionalProperties, false);
});

test("screenshot_camera defaults width=1920 height=1080", () => {
  const props = screenshotCamera.inputSchema.properties as Record<
    string,
    { default?: number }
  >;
  assert.equal(props.width.default, 1920);
  assert.equal(props.height.default, 1080);
});

test("screenshot_camera is read-only (no paths_hint/gate)", () => {
  const props = screenshotCamera.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined);
  assert.equal(props.gate, undefined);
});

// ---------------------------------------------------------------------------
// screenshot_isolated — read-only, node_ref + view + background + optics + resolution.
// ---------------------------------------------------------------------------

test("screenshot_isolated tool name follows the godot_open_mcp_* convention", () => {
  assert.equal(screenshotIsolated.name, "godot_open_mcp_screenshot_isolated");
});

test("screenshot_isolated declares an object schema with additionalProperties:false", () => {
  assert.equal(screenshotIsolated.inputSchema.type, "object");
  assert.equal(screenshotIsolated.inputSchema.additionalProperties, false);
});

test("screenshot_isolated exposes the full optics + framing property set", () => {
  const props = screenshotIsolated.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(Object.keys(props).sort(), [
    "background",
    "background_color",
    "camera_view",
    "far_clip_plane",
    "field_of_view",
    "near_clip_plane",
    "node_ref",
    "padding",
    "resolution",
  ]);
});

test("screenshot_isolated camera_view defaults to front with all six views", () => {
  const props = screenshotIsolated.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.equal(props.camera_view.default, "front");
  assert.deepEqual(props.camera_view.enum, [
    "front",
    "back",
    "left",
    "right",
    "top",
    "bottom",
  ]);
});

test("screenshot_isolated background defaults to solid_color with transparent option", () => {
  const props = screenshotIsolated.inputSchema.properties as Record<
    string,
    { default?: string; enum?: string[] }
  >;
  assert.equal(props.background.default, "solid_color");
  assert.deepEqual(props.background.enum, ["solid_color", "transparent"]);
});

test("screenshot_isolated optics defaults match the plan contract", () => {
  const props = screenshotIsolated.inputSchema.properties as Record<
    string,
    { default?: number }
  >;
  assert.equal(props.background_color.default, "#404040");
  assert.equal(props.field_of_view.default, 60);
  assert.equal(props.near_clip_plane.default, 0.05);
  assert.equal(props.far_clip_plane.default, 4000);
  assert.equal(props.padding.default, 1.2);
  assert.equal(props.resolution.default, 512);
});

test("screenshot_isolated is read-only (no paths_hint/gate)", () => {
  const props = screenshotIsolated.inputSchema.properties as Record<string, unknown>;
  assert.equal(props.paths_hint, undefined);
  assert.equal(props.gate, undefined);
});

// ---------------------------------------------------------------------------
// Registration.
// ---------------------------------------------------------------------------

test("ALL_TOOLS registers all three P4.8 screenshot tools", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(
    names.includes("godot_open_mcp_screenshot_viewport"),
    "screenshot_viewport missing from ALL_TOOLS",
  );
  assert.ok(
    names.includes("godot_open_mcp_screenshot_camera"),
    "screenshot_camera missing from ALL_TOOLS",
  );
  assert.ok(
    names.includes("godot_open_mcp_screenshot_isolated"),
    "screenshot_isolated missing from ALL_TOOLS",
  );
});
