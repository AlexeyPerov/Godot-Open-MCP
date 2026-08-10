// P16.4 audio pack — schema + group-assignment parity tests.
//
// Pins the catalog metadata the MCP ListTools response advertises to AI clients
// (name prefix, non-empty description, mutating-tool shape with paths_hint
// required + gate default enforce + enum vocabularies) and the group assignment
// (all three tools map to `audio`). The live round-trip (POST
// /tools/godot_open_mcp_audio_* → bridge handler → result envelope) is exercised
// by the headless Godot smoke; this file only asserts the contract advertised
// over stdio.
//
// Adapted from lighting-tools.test.ts (copy fidelity for the catalog-metadata
// test shape), with assertions specific to the audio pack's mutating-only
// surface (no read-only tools in this family) and the player-dimension /
// set-stream / bus-volume input shapes.

import { test } from "node:test";
import assert from "node:assert/strict";

import { audioStreamPlayerCreate } from "./audio-stream-player-create.js";
import { audioStreamPlayerSetStream } from "./audio-stream-player-set-stream.js";
import { audioBusSetVolume } from "./audio-bus-set-volume.js";
import { groupFor, toolsInGroup } from "../capabilities/tool-groups.js";

const ALL_AUDIO_TOOLS = [audioStreamPlayerCreate, audioStreamPlayerSetStream, audioBusSetVolume];

// ---------------------------------------------------------------------------
// Names + group assignment
// ---------------------------------------------------------------------------

test("every audio tool name follows the godot_open_mcp_audio_* convention", () => {
  for (const t of ALL_AUDIO_TOOLS) {
    assert.match(t.name, /^godot_open_mcp_audio_[a-z0-9_]+$/);
  }
});

test("every audio tool is assigned to the audio group", () => {
  for (const t of ALL_AUDIO_TOOLS) {
    assert.equal(groupFor(t.name), "audio", `${t.name} must map to audio`);
  }
});

test("toolsInGroup(audio) returns exactly the three pack tools", () => {
  assert.deepEqual(toolsInGroup("audio"), [
    "godot_open_mcp_audio_bus_set_volume",
    "godot_open_mcp_audio_stream_player_create",
    "godot_open_mcp_audio_stream_player_set_stream",
  ]);
});

// ---------------------------------------------------------------------------
// Shared catalog-metadata invariants
// ---------------------------------------------------------------------------

test("every audio tool has a non-empty description", () => {
  for (const t of ALL_AUDIO_TOOLS) {
    assert.ok(typeof t.description === "string");
    assert.ok((t.description ?? "").length > 0, `${t.name} description is empty`);
  }
});

test("every audio tool declares an object input schema with additionalProperties:false", () => {
  for (const t of ALL_AUDIO_TOOLS) {
    assert.equal(t.inputSchema.type, "object", `${t.name} schema type`);
    assert.equal(t.inputSchema.additionalProperties, false, `${t.name} additionalProperties`);
  }
});

test("every audio tool description tells the agent to activate the audio group", () => {
  // The group is default-off; the description must surface the activation step so an
  // agent does not call a hidden tool blind. All three tools are in the group — they
  // must mention activation.
  for (const t of ALL_AUDIO_TOOLS) {
    assert.match(
      t.description ?? "",
      /activate the group with manage_tools/,
      `${t.name} description must mention manage_tools activation`,
    );
  }
});

// ---------------------------------------------------------------------------
// Shared mutating shape — every audio tool is mutating
// ---------------------------------------------------------------------------

test("every audio tool requires paths_hint", () => {
  for (const t of ALL_AUDIO_TOOLS) {
    assert.ok(
      (t.inputSchema.required ?? []).includes("paths_hint"),
      `${t.name} must require paths_hint`,
    );
  }
});

test("every audio tool gate default is enforce", () => {
  for (const t of ALL_AUDIO_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { default?: string }>).gate;
    assert.equal(gate?.default, "enforce", `${t.name} gate default must be enforce`);
  }
});

test("every audio tool gate enum is [enforce, warn, off]", () => {
  for (const t of ALL_AUDIO_TOOLS) {
    const gate = (t.inputSchema.properties as Record<string, { enum?: string[] }>).gate;
    assert.deepEqual(gate?.enum, ["enforce", "warn", "off"], `${t.name} gate enum`);
  }
});

test("every audio tool paths_hint is an array of strings", () => {
  for (const t of ALL_AUDIO_TOOLS) {
    const ph = (t.inputSchema.properties as Record<string, { type?: string; items?: { type?: string } }>).paths_hint;
    assert.equal(ph?.type, "array", `${t.name} paths_hint type`);
    assert.equal(ph?.items?.type, "string", `${t.name} paths_hint items type`);
  }
});

// ---------------------------------------------------------------------------
// audio_stream_player_create — mutating shape
// ---------------------------------------------------------------------------

test("audio_stream_player_create requires dimension + paths_hint", () => {
  assert.deepEqual(
    (audioStreamPlayerCreate.inputSchema.required ?? []).sort(),
    ["dimension", "paths_hint"],
  );
});

test("audio_stream_player_create dimension enum is exactly the three player families", () => {
  const dimension = (audioStreamPlayerCreate.inputSchema.properties as Record<string, { enum?: string[] }>).dimension;
  assert.deepEqual(
    dimension?.enum,
    ["nonpositional", "2d", "3d"],
    "create dimension enum",
  );
});

test("audio_stream_player_create exposes the mutating property set", () => {
  const props = audioStreamPlayerCreate.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(
    Object.keys(props).sort(),
    [
      "autoplay",
      "bus",
      "dimension",
      "gate",
      "name",
      "parent_node_path",
      "paths_hint",
      "pitch_scale",
      "position",
      "stream_path",
      "volume_db",
    ],
  );
});

// ---------------------------------------------------------------------------
// audio_stream_player_set_stream — mutating shape
// ---------------------------------------------------------------------------

test("audio_stream_player_set_stream requires node_path + stream_path + paths_hint", () => {
  assert.deepEqual(
    (audioStreamPlayerSetStream.inputSchema.required ?? []).sort(),
    ["node_path", "paths_hint", "stream_path"],
  );
});

test("audio_stream_player_set_stream exposes the mutating property set", () => {
  const props = audioStreamPlayerSetStream.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(
    Object.keys(props).sort(),
    ["gate", "node_path", "paths_hint", "stream_path"],
  );
});

// ---------------------------------------------------------------------------
// audio_bus_set_volume — mutating shape
// ---------------------------------------------------------------------------

test("audio_bus_set_volume requires bus + paths_hint", () => {
  assert.deepEqual(
    (audioBusSetVolume.inputSchema.required ?? []).sort(),
    ["bus", "paths_hint"],
  );
});

test("audio_bus_set_volume exposes the mutating property set", () => {
  const props = audioBusSetVolume.inputSchema.properties as Record<string, unknown>;
  assert.deepEqual(
    Object.keys(props).sort(),
    ["bus", "gate", "paths_hint", "volume_db", "volume_linear"],
  );
});
