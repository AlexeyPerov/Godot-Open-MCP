// `godot_open_mcp_audio_bus_set_volume` tool definition (P16.4).
//
// Sets an audio bus's volume via Godot's AudioServer. Accepts volume_db (Godot
// native — written directly) or volume_linear (0–1, converted to dB via Godot's
// linear_to_db). volume_db wins when both are present. Resolves the bus by name
// via AudioServer.GetBusIndex (a typo surfaces bus_not_found), writes the value
// via AudioServer.SetBusVolumeDb, and reads it back. The handler lives in the
// bridge (POST /tools/godot_open_mcp_audio_bus_set_volume); this file is the
// catalog metadata only — name / description / input schema — advertised to AI
// clients over stdio ListTools.
//
// Adapted from Unity Open MCP's `audio_mixer_set_parameter`
// (TypedTools/Extensions/Audio/AudioTools.cs — adapt fidelity): same
// set-a-bus/mixer-volume shape, but Godot's AudioServer bus model replaces
// Unity's AudioMixer exposed-float parameter surface — this tool writes a bus
// volume directly (no exposed-parameter indirection, no .mix asset, no
// `normalize` flag — the linear→dB conversion is built in via volume_linear).
// Unity mixer_group_path routing is NOT ported (Godot's per-player Bus property
// is a string bus name set via audio_stream_player_create or node_modify).
//
// This is an `audio` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "audio" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const audioBusSetVolume: Tool = {
  name: "godot_open_mcp_audio_bus_set_volume",
  description:
    "Set an audio bus's volume via Godot's AudioServer and read it back. Accepts `volume_db` " +
    "(Godot native — written directly) OR `volume_linear` (0–1, converted to dB via linear_to_db); " +
    "`volume_db` wins when both are present (the ignored `volume_linear` is reported in `warnings`). " +
    "Resolves the bus by name (validated against the live AudioServer layout — a typo surfaces " +
    "bus_not_found), writes the value, and reads it back so the caller can confirm.\n\n" +
    "The bus layout is project-level state (Godot saves it into project.godot via the Audio panel); " +
    "this tool writes to the running AudioServer — call Project → Save to persist the bus layout to " +
    "disk afterwards.\n\n" +
    "Mutating — paths_hint is res://project.godot (the bus layout's home). This is an `audio` group " +
    "tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["bus", "paths_hint"],
    properties: {
      bus: {
        type: "string",
        description:
          "Required: the audio bus name to mutate (validated against the live AudioServer layout). " +
          "The default layout has 'Master' only until more buses are added in the Audio panel. A bus " +
          "that does not exist surfaces bus_not_found.",
      },
      volume_db: {
        type: "number",
        description:
          "Volume in decibels (Godot native — written directly via AudioServer.SetBusVolumeDb). " +
          "Wins over volume_linear when both are present. 0 dB is unity; -80 dB is effectively " +
          "silent; positive values amplify. Godot accepts the full dB range (no clamp).",
      },
      volume_linear: {
        type: "number",
        description:
          "Optional linear volume (0–1, converted to dB via Godot's linear_to_db). Used only when " +
          "volume_db is absent. 0 maps to -80 dB (silent floor); 1 maps to 0 dB (unity); >1 " +
          "amplifies. A value ≤ 0 is clamped to the -80 dB silent floor.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — res://project.godot (the bus layout's home). Mandatory even when gate " +
          "is 'off' (handler-level guard).",
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
