// `godot_open_mcp_audio_stream_player_create` tool definition (P16.4).
//
// Creates a Godot audio player node — AudioStreamPlayer (non-positional global
// player), AudioStreamPlayer2D (2D positional), or AudioStreamPlayer3D (3D
// positional) — in the currently edited scene, with an optional stream +
// output bus + starter scalars (volume_db / pitch_scale / autoplay) applied
// through the same allow-listed + clamped path. The handler lives in the bridge
// (POST /tools/godot_open_mcp_audio_stream_player_create); this file is the
// catalog metadata only — name / description / input schema — advertised to AI
// clients over stdio ListTools.
//
// Adapted from Unity Open MCP's `audio_source_add`
// (TypedTools/Extensions/Audio/AudioTools.cs — adapt fidelity): same
// create-with-starter-scalars shape, but Godot audio players are Node / Node2D /
// Node3D subclasses (not Unity AudioSource components attached to a GameObject),
// and the dimension enum selects the concrete Godot player class. Unity
// spatial_blend / spatialize / min_distance / max_distance / doppler_level /
// spread / mixer_group_path are intentionally NOT ported in the typed surface
// (AudioStreamPlayer3D's attenuation surface is settable via node_modify; the
// per-player Bus is set at create via the `bus` arg).
//
// This is an `audio` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "audio" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const audioStreamPlayerCreate: Tool = {
  name: "godot_open_mcp_audio_stream_player_create",
  description:
    "Create a Godot audio player node in the currently edited scene by `dimension` and return its NodeData. " +
    "Dimensions: 'nonpositional' (AudioStreamPlayer — global, no positional attenuation), '2d' " +
    "(AudioStreamPlayer2D — 2D positional), '3d' (AudioStreamPlayer3D — 3D positional with attenuation " +
    "model + max distance). Optional `stream_path` loads + assigns an AudioStream resource " +
    "(.wav / .ogg / .mp3 / .tres) at create time; optional `bus` routes the player's output to a named " +
    "audio bus (validated against the live AudioServer layout). Optional starter scalars (volume_db / " +
    "pitch_scale / autoplay) apply at create time through the same allow-listed + clamped path; omit a " +
    "scalar to leave the engine default. The new node's owner is the edited scene root; the scene is " +
    "marked unsaved.\n\n" +
    "Mutating — runs the full gate cycle (checkpoint → save → validate → delta) by default. This is an " +
    "`audio` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["dimension", "paths_hint"],
    properties: {
      dimension: {
        type: "string",
        enum: ["nonpositional", "2d", "3d"],
        description:
          "Which audio player node to instantiate. 'nonpositional' → AudioStreamPlayer (global, no " +
          "positional attenuation — UI sounds, music). '2d' → AudioStreamPlayer2D (positional in 2D, " +
          "attenuates with distance from the Camera2D). '3d' → AudioStreamPlayer3D (positional in 3D, " +
          "attenuation model + max distance + emission angle — set those via node_modify after create).",
      },
      name: {
        type: "string",
        description:
          "Optional name for the new player node. When omitted, Godot assigns a default name for the " +
          "type (e.g. 'AudioStreamPlayer3D').",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root (same " +
          "resolver as node_find / node_create). Defaults to the edited scene root.",
      },
      position: {
        type: "string",
        description:
          "Optional position as 'x,y,z' (3D players) or 'x,y' (2D players). Applied to the Node3D / " +
          "Node2D transform. Ignored for nonpositional players (they carry no spatial transform). " +
          "Defaults to the origin.",
      },
      stream_path: {
        type: "string",
        description:
          "Optional res:// AudioStream resource path (.wav / .ogg / .mp3 / .tres) to load and assign " +
          "to the player's Stream property at create time. A missing file or a non-AudioStream resource " +
          "surfaces in the `warnings` array (the node is still created); use " +
          "audio_stream_player_set_stream to assign a stream after create.",
      },
      bus: {
        type: "string",
        description:
          "Optional output bus name (validated against the live AudioServer layout). A bus that does " +
          "not exist surfaces in the `warnings` array (the player keeps the 'Master' default). Add buses " +
          "via the Audio panel before referencing them here.",
      },
      volume_db: {
        type: "number",
        description:
          "Optional volume in decibels (float, passed through — Godot accepts the full dB range). " +
          "Applied via AudioStreamPlayer*.VolumeDb. A very negative value is effectively silent. " +
          "Defaults to 0 dB (unity).",
      },
      pitch_scale: {
        type: "number",
        description:
          "Optional pitch scale (float, clamped strictly positive — a non-positive value produces no " +
          "sound). Applied via AudioStreamPlayer*.PitchScale. Defaults to 1.0 (normal pitch).",
      },
      autoplay: {
        type: "boolean",
        description:
          "Optional autoplay toggle (bool). When true, the player starts playback as soon as the scene " +
          "is added to the tree (Godot's Autoplay property). Defaults to false.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the edited scene res:// path. The gate validates only these paths after " +
          "the mutation. Mandatory even when gate is 'off' (handler-level guard).",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → save → validate → delta; new errors " +
          "fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the cycle " +
          "(paths_hint is still required).",
      },
    },
    additionalProperties: false,
  },
};
