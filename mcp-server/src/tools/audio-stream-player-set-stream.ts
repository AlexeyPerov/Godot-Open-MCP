// `godot_open_mcp_audio_stream_player_set_stream` tool definition (P16.4).
//
// Assigns an AudioStream resource (.wav / .ogg / .mp3 / .tres) to an existing
// audio player node (AudioStreamPlayer / AudioStreamPlayer2D /
// AudioStreamPlayer3D). Resolves the node, type-checks it, loads the resource,
// assigns it to the player's Stream property, and marks the scene unsaved. The
// handler lives in the bridge
// (POST /tools/godot_open_mcp_audio_stream_player_set_stream); this file is the
// catalog metadata only — name / description / input schema — advertised to AI
// clients over stdio ListTools.
//
// Adapted from Unity Open MCP's `audio_source_modify` (clip_path branch)
// (TypedTools/Extensions/Audio/AudioTools.cs — adapt fidelity): same
// assign-a-stream-to-a-source shape, but Godot players are Node / Node2D /
// Node3D subclasses and the resource is an AudioStream (Godot's audio asset
// type), not a Unity AudioClip. Unity mixer_group_path / volume / pitch /
// spatial_blend are NOT ported here (use audio_stream_player_create's starter
// scalars or node_modify for those).
//
// This is an `audio` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "audio" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const audioStreamPlayerSetStream: Tool = {
  name: "godot_open_mcp_audio_stream_player_set_stream",
  description:
    "Assign an AudioStream resource (.wav / .ogg / .mp3 / .tres) to an existing audio player node " +
    "(AudioStreamPlayer / AudioStreamPlayer2D / AudioStreamPlayer3D) and mark the scene unsaved. " +
    "Resolves the node, type-checks it against the three audio player families, loads the AudioStream " +
    "at stream_path, assigns it to the player's Stream property, and reports the assigned stream. A " +
    "non-player node surfaces wrong_node_type; a non-AudioStream resource surfaces wrong_resource_type.\n\n" +
    "Mutating — paths_hint is the edited scene path. This is an `audio` group tool — activate the " +
    "group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["node_path", "stream_path", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "The audio player node to mutate — a scene-tree path (same resolver as node_find). Must " +
          "resolve to an AudioStreamPlayer / AudioStreamPlayer2D / AudioStreamPlayer3D in the edited " +
          "scene (use audio_stream_player_create first if it does not exist). A non-player node " +
          "surfaces wrong_node_type.",
      },
      stream_path: {
        type: "string",
        description:
          "Required: a res:// AudioStream resource path (.wav / .ogg / .mp3 / .tres) to load and " +
          "assign. Must point at a saved AudioStream resource (Godot imports .wav / .ogg / .mp3 as " +
          "AudioStreamWAV / AudioStreamOggVorbis / AudioStreamMP3 respectively). A non-AudioStream " +
          "resource surfaces wrong_resource_type.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the edited scene res:// path. Mandatory even when gate is 'off' " +
          "(handler-level guard).",
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
