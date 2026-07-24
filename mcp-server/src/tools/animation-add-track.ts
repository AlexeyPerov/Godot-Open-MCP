// `godot_open_mcp_animation_add_track` tool definition (P12.4).
//
// Adds a track to an Animation clip in a named library on a target AnimationPlayer,
// and returns the new track's index. The handler lives in the bridge
// (POST /tools/godot_open_mcp_animation_add_track); this file is the catalog
// metadata only.
//
// Adapted from Unity Open MCP's AnimationClip modify (adapt fidelity): Unity often
// folds track + key creation into a single clip-modify envelope. The Godot catalog
// surfaces add_track as its own verb because Godot's Animation API is track-then-
// key, and surfacing the two steps lets an agent recover from a bad track_index
// without rebuilding a clip. v1 supports four track types (value / position_3d /
// rotation_3d / scale_3d); other Godot track types (blend_shape / method / bezier /
// audio / animation) return unsupported_track_type.
//
// This is an `animation` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const animationAddTrack: Tool = {
  name: "godot_open_mcp_animation_add_track",
  description:
    "Add a track to an Animation clip in a named library on a target AnimationPlayer, and return " +
    "the new track's index (pass it to animation_insert_key). v1 supports four track types: " +
    "'value' (animate any property), 'position_3d' / 'rotation_3d' / 'scale_3d' (animate a Node3D " +
    "transform component; rotation is in radians). Other Godot track types (blend_shape / method " +
    "/ bezier / audio / animation) return unsupported_track_type. The track_path is a NodePath " +
    "string Godot resolves relative to the AnimationPlayer's root_node — e.g. " +
    "'Sprite2D:position' animates the position property of a node named Sprite2D. A wrong path is " +
    "accepted at authoring time but produces no playback effect; the result echoes the path so an " +
    "agent can verify. For value tracks an optional update_mode sets how Godot applies the value " +
    "(continuous / discrete / capture; continuous is the default). The scene is marked unsaved. " +
    "This is an `animation` group tool — activate the group with manage_tools first. Mutating: " +
    "runs the gate cycle by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "animation", "track_type", "track_path", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target AnimationPlayer owning the clip, relative to the edited " +
          "scene root. Must resolve to an AnimationPlayer; a different node type returns " +
          "wrong_node_type.",
      },
      library: {
        type: "string",
        description:
          "Name of the library holding the clip. Defaults to 'default' when omitted or empty.",
      },
      animation: {
        type: "string",
        description: "Clip name (the key the clip is registered under in the library).",
      },
      track_type: {
        type: "string",
        enum: ["value", "position_3d", "rotation_3d", "scale_3d"],
        description:
          "Track type. 'value' animates any property (number / bool / string / Vector2 / Vector3 " +
          "/ Color). 'position_3d' / 'rotation_3d' / 'scale_3d' animate the matching Node3D " +
          "transform component (rotation is in radians). Blend-shape / method / bezier / audio / " +
          "animation tracks are not supported in v1 and return unsupported_track_type.",
      },
      track_path: {
        type: "string",
        description:
          "NodePath string Godot resolves relative to the AnimationPlayer's root_node (an " +
          "AnimationMixer property; default is the player's parent). Format: " +
          "'NodeName:property' or 'NodeName:property:sub_component'. Examples: " +
          "'Sprite2D:position' (a Vector2), 'Sprite2D:position:x' (a float), " +
          "'Player:rotation' (a float in radians). A wrong path is accepted at authoring time " +
          "but produces no playback effect — Godot does not validate the path against the scene " +
          "tree at authoring. See the pack README for the working API sequence.",
      },
      value_type: {
        type: "string",
        description:
          "Optional hint for value tracks (e.g. 'float', 'bool', 'vector2'). Informational only " +
          "in v1 — Godot infers the value type from the inserted key Variant. Reserved for a " +
          "future validation pass.",
      },
      update_mode: {
        type: "string",
        enum: ["continuous", "discrete", "capture"],
        description:
          "Value tracks only: how Godot applies the animated value. 'continuous' (default): " +
          "update every frame. 'discrete': update only at keyframe times. 'capture': capture the " +
          "initial value on first evaluation (useful for one-shot blends). Ignored for " +
          "position_3d / rotation_3d / scale_3d tracks.",
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
