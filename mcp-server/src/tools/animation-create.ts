// `godot_open_mcp_animation_create` tool definition (P12.4).
//
// Creates an Animation clip in a named library on a target AnimationPlayer, and
// returns the clip's name + length + loop mode. The handler lives in the bridge
// (POST /tools/godot_open_mcp_animation_create); this file is the catalog metadata
// only.
//
// Adapted from Unity Open MCP's AnimationClip create (adapt fidelity): same clip-
// centric create concept, but the Godot clip lives inside a named library on an
// AnimationPlayer (not as a standalone asset). The library defaults to 'default'
// and is auto-created when missing (catalog convention). A duplicate clip name
// returns already_exists. No AnimatorController equivalent — Godot plays clips by
// name from script.
//
// This is an `animation` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const animationCreate: Tool = {
  name: "godot_open_mcp_animation_create",
  description:
    "Create an Animation clip in a named library on a target AnimationPlayer, and return the " +
    "clip's name + length + loop mode. The library defaults to 'default' and is auto-created when " +
    "missing — you do not need to call animation_library_add first unless you want a non-default " +
    "library name or an explicit empty library. A clip already registered under that name in that " +
    "library returns already_exists — no silent overwrite. An explicit-but-unrecognized loop_mode " +
    "returns invalid_parameter; an absent loop_mode leaves the Godot default (none). After " +
    "creating the clip, call animation_add_track to add tracks, then animation_insert_key to " +
    "keyframe. The scene is marked unsaved. This is an `animation` group tool — activate the " +
    "group with manage_tools first. Mutating: runs the gate cycle by default; paths_hint is the " +
    "edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "animation", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target AnimationPlayer that will own the clip, relative to the " +
          "edited scene root. Must resolve to an AnimationPlayer; a different node type returns " +
          "wrong_node_type.",
      },
      library: {
        type: "string",
        description:
          "Name of the library to add the clip to. Defaults to 'default' when omitted or empty. " +
          "Auto-created when missing on the player (so this tool can be the first animation call).",
      },
      animation: {
        type: "string",
        description:
          "Clip name — the key under which the Animation registers in the library (e.g. 'Idle', " +
          "'Run'). Played back by passing this name (optionally library-prefixed) to the player's " +
          "play method from script.",
      },
      length: {
        type: "number",
        exclusiveMinimum: 0,
        description:
          "Clip length in seconds. Defaults to 1.0 (Godot's default). Clamped to strictly " +
          "positive so a 0/negative length does not produce a clip that ends at its first key. " +
          "Godot extends a clip's effective length to the last key's time when you insert keys " +
          "beyond it, so this is a starting length, not a hard cap.",
      },
      loop_mode: {
        type: "string",
        enum: ["none", "linear", "pingpong"],
        description:
          "Loop behavior. 'none' (default when absent): play once and stop. 'linear': loop " +
          "seamlessly. 'pingpong': loop forward then backward. Names mirror Godot's " +
          "Animation.LoopMode enum (LoopNone / LoopLinear / LoopPingpong) minus the redundant " +
          "'Loop' prefix. An explicit-but-unrecognized token returns invalid_parameter.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the edited scene res:// path. Mandatory even when gate is 'off'.",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description: "Gate mode. 'enforce' (default), 'warn', or 'off' (paths_hint still required).",
      },
    },
    additionalProperties: false,
  },
};
