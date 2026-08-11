// P16.5 UI pack — schema + group-assignment parity tests.
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// (name prefix, non-empty description, mutating-tool shape with paths_hint
// required + gate default enforce + enum vocabularies) and the group assignment
// (all five tools map to `ui`). The live round-trip (POST
// /tools/godot_open_mcp_control_* / container_* / theme_apply → bridge handler
// → result envelope) is exercised by the headless Godot smoke; this file only
// asserts the contract advertised over stdio.
//
// Adapted from lighting-tools.test.ts (copy fidelity for the catalog-metadata
// test shape), with assertions specific to the UI pack's mutating-only surface
// (no read-only tools in this family) and the control-kind / container-kind /
// theme-apply input shapes.

import { test } from "node:test";
import assert from "node:assert/strict";

import { controlCreate } from "./control-create.js";
import { controlModify } from "./control-modify.js";
import { containerAdd } from "./container-add.js";
import { containerSetLayout } from "./container-set-layout.js";
import { themeApply } from "./theme-apply.js";
import { groupFor, toolsInGroup } from "../capabilities/tool-groups.js";

const ALL_UI_TOOLS = [controlCreate, controlModify, containerAdd, containerSetLayout, themeApply];

// ---------------------------------------------------------------------------
// Names + group assignment
// ---------------------------------------------------------------------------

test("every ui tool name follows the godot_open_mcp_(control|container|theme)_[a-z0-9_]+ convention", () => {
  for (const t of ALL_UI_TOOLS) {
    assert.match(t.name, /^godot_open_mcp_(control|container|theme)_[a-z0-9_]+$/);
  }
});

test("every ui tool is assigned to the ui group", () => {
  for (const t of ALL_UI_TOOLS) {
    assert.equal(groupFor(t.name), "ui", `${t.name} must map to ui`);
  }
});

test("toolsInGroup(ui) returns exactly the five pack tools", () => {
  assert.deepEqual(toolsInGroup("ui"), [
    "godot_open_mcp_container_add",
    "godot_open_mcp_container_set_layout",
    "godot_open_mcp_control_create",
    "godot_open_mcp_control_modify",
    "godot_open_mcp_theme_apply",
  ]);
});

// ---------------------------------------------------------------------------
// Shared catalog-metadata invariants
// ---------------------------------------------------------------------------

test("every ui tool has a non-empty description", () => {
  for (const t of ALL_UI_TOOLS) {
    assert.ok(typeof t.description === "string");
    assert.ok((t.description ?? "").length > 0, `${t.name} description is empty`);
  }
});

test("every ui tool declares an object input schema with additionalProperties:false", () => {
  for (const t of ALL_UI_TOOLS) {
    assert.equal(t.inputSchema.type, "object", `${t.name} schema type`);
    assert.equal(t.inputSchema.additionalProperties, false, `${t.name} additionalProperties`);
  }
});

test("every ui tool description tells the agent to activate the ui group", () => {
  // The group is default-off; the description must surface the activation step so an
  // agent does not call a hidden tool blind. All five tools are in the group — they
  // must mention activation.
  for (const t of ALL_UI_TOOLS) {
    assert.match(
      t.description ?? "",
      /activate the group with manage_tools/,
      `${t.name} description must mention manage_tools activation`,
    );
  }
});

// ---------------------------------------------------------------------------
// Shared mutating shape — every ui tool is mutating
// ---------------------------------------------------------------------------

test("every ui tool requires paths_hint", () => {
  for (const t of ALL_UI_TOOLS) {
    assert.ok(
      (t.inputSchema.required ?? []).includes("paths_hint"),
      `${t.name} must require paths_hint`,
    );
  }
});

test("every ui tool gate default is enforce", () => {
  for (const t of ALL_UI_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { default?: string }>).gate;
    assert.equal(gate?.default, "enforce", `${t.name} gate default must be enforce`);
  }
});

test("every ui tool gate enum is [enforce, warn, off]", () => {
  for (const t of ALL_UI_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { enum?: string[] }>).gate;
    assert.deepEqual(gate?.enum, ["enforce", "warn", "off"], `${t.name} gate enum`);
  }
});

test("every ui tool paths_hint is an array of strings", () => {
  for (const t of ALL_UI_TOOLS) {
    const ph = (t.inputSchema.properties as Record<string, { type?: string; items?: { type?: string } }>).paths_hint;
    assert.equal(ph?.type, "array", `${t.name} paths_hint type`);
    assert.equal(ph?.items?.type, "string", `${t.name} paths_hint items type`);
  }
});

// ---------------------------------------------------------------------------
// control_create — mutating shape
// ---------------------------------------------------------------------------

test("control_create requires type + paths_hint", () => {
  assert.deepEqual(
    (controlCreate.inputSchema.required ?? []).sort(),
    ["paths_hint", "type"],
  );
});

test("control_create type enum is exactly the fifteen creatable control families", () => {
  const type = (controlCreate.inputSchema.properties as Record<string, { enum?: string[] }>).type;
  assert.deepEqual(
    type?.enum,
    [
      "button",
      "label",
      "lineedit",
      "textedit",
      "texturerect",
      "colorrect",
      "progressbar",
      "checkbox",
      "checkbutton",
      "slider",
      "spinbox",
      "optionbutton",
      "separator",
      "ninepatchrect",
      "richtextlabel",
    ],
    "create type enum",
  );
});

test("control_create exposes the mutating property set", () => {
  const props = controlCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(
    Object.keys(props).sort(),
    ["anchors_preset", "gate", "name", "parent_node_path", "paths_hint", "text", "type"],
  );
});

// ---------------------------------------------------------------------------
// control_modify — mutating shape
// ---------------------------------------------------------------------------

test("control_modify requires node_path + fields + paths_hint", () => {
  assert.deepEqual(
    (controlModify.inputSchema.required ?? []).sort(),
    ["fields", "node_path", "paths_hint"],
  );
});

test("control_modify fields is an object with additionalProperties:true (free-form map)", () => {
  // fields is a free-form {field → value} map; the handler validates each key
  // against the allow-list. additionalProperties:true so any field name is
  // accepted at the schema layer (the handler surfaces unsupported_field).
  const fields = (controlModify.inputSchema.properties as Record<string, { type?: string; additionalProperties?: boolean }>).fields;
  assert.equal(fields?.type, "object", "modify fields type");
  assert.equal(fields?.additionalProperties, true, "modify fields additionalProperties");
});

test("control_modify exposes the mutating property set", () => {
  const props = controlModify.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(
    Object.keys(props).sort(),
    ["fields", "gate", "node_path", "paths_hint"],
  );
});

// ---------------------------------------------------------------------------
// container_add — mutating shape
// ---------------------------------------------------------------------------

test("container_add requires type + paths_hint", () => {
  assert.deepEqual(
    (containerAdd.inputSchema.required ?? []).sort(),
    ["paths_hint", "type"],
  );
});

test("container_add type enum is exactly the five creatable container families", () => {
  const type = (containerAdd.inputSchema.properties as Record<string, { enum?: string[] }>).type;
  assert.deepEqual(
    type?.enum,
    ["vbox", "hbox", "grid", "margin", "scroll"],
    "container_add type enum",
  );
});

test("container_add exposes the mutating property set", () => {
  const props = containerAdd.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(
    Object.keys(props).sort(),
    ["anchors_preset", "gate", "name", "parent_node_path", "paths_hint", "type"],
  );
});

// ---------------------------------------------------------------------------
// container_set_layout — mutating shape
// ---------------------------------------------------------------------------

test("container_set_layout requires node_path + fields + paths_hint", () => {
  assert.deepEqual(
    (containerSetLayout.inputSchema.required ?? []).sort(),
    ["fields", "node_path", "paths_hint"],
  );
});

test("container_set_layout fields is an object with additionalProperties:true (free-form map)", () => {
  const fields = (containerSetLayout.inputSchema.properties as Record<string, { type?: string; additionalProperties?: boolean }>).fields;
  assert.equal(fields?.type, "object", "container_set_layout fields type");
  assert.equal(fields?.additionalProperties, true, "container_set_layout fields additionalProperties");
});

test("container_set_layout exposes the mutating property set", () => {
  const props = containerSetLayout.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(
    Object.keys(props).sort(),
    ["fields", "gate", "node_path", "paths_hint"],
  );
});

// ---------------------------------------------------------------------------
// theme_apply — mutating shape
// ---------------------------------------------------------------------------

test("theme_apply requires node_path + theme_path + paths_hint", () => {
  assert.deepEqual(
    (themeApply.inputSchema.required ?? []).sort(),
    ["node_path", "paths_hint", "theme_path"],
  );
});

test("theme_apply recursive defaults to false", () => {
  const recursive = (themeApply.inputSchema.properties as Record<string, { default?: boolean }>).recursive;
  assert.equal(recursive?.default, false, "theme_apply recursive default");
});

test("theme_apply exposes the mutating property set", () => {
  const props = themeApply.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(
    Object.keys(props).sort(),
    ["gate", "node_path", "paths_hint", "recursive", "theme_path"],
  );
});
