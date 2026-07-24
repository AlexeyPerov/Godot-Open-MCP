// `godot_open_mcp_animation_insert_key` tool definition (P12.4).
//
// Inserts a keyframe at a given time on a track in an Animation clip, and returns
// the key index Godot assigned. The handler lives in the bridge
// (POST /tools/godot_open_mcp_animation_insert_key); this file is the catalog
// metadata only.
//
// Adapted from Unity Open MCP's AnimationClip modify (adapt fidelity): Unity folds
// key insertion into the clip-modify envelope. The Godot catalog surfaces it as its
// own verb (same reason as add_track — Godot's API is track-then-key, and a bad
// track_index should be recoverable without rebuilding a clip). The value is a
// typed JSON token the handler converts to a Godot Variant; the supported shapes
// are number / bool / string / {x,y} / {x,y,z} / {r,g,b[,a]}.
//
// This is an `animation` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const animationInsertKey: Tool = {
  name: "godot_open_mcp_animation_insert_key",
  description:
    "Insert a keyframe at the given time on a track in an Animation clip, and return the key " +
    "index Godot assigned (plus the track's new key count). The value is a typed JSON token the " +
    "handler converts to a Godot Variant — supported shapes: number (e.g. 1.5, 42), bool " +
    "(true/false), string, {x,y} (Vector2), {x,y,z} (Vector3), {r,g,b[,a]} (Color, a defaults to " +
    "1). A type-tagged object (e.g. {type:'vector3',x:1,y:2,z:3}) is also accepted. Godot checks " +
    "the Variant type against the track type at insertion — a value track accepts the property's " +
    "type, a position_3d / rotation_3d / scale_3d track requires a Vector3 (rotation in radians). " +
    "A mismatch returns invalid_parameter. An optional interpolation overrides the per-key easing " +
    "(nearest / linear / cubic; absent leaves the Godot default linear). An optional transition " +
    "sets the easing curve weight (default 1.0). The scene is marked unsaved. This is an " +
    "`animation` group tool — activate the group with manage_tools first. Mutating: runs the gate " +
    "cycle by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "animation", "track_index", "time", "value", "paths_hint"],
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
      track_index: {
        type: "integer",
        minimum: 0,
        description:
          "Track index to key — the value returned by animation_add_track. An out-of-range index " +
          "returns track_not_found with the valid range in the message.",
      },
      time: {
        type: "number",
        description:
          "Keyframe time in seconds. Godot sorts keys by time, so the insertion order does not " +
          "matter; passing times out of order is fine. A time beyond the clip's length extends " +
          "the effective length (Godot's behavior) — call animation_create again with a larger " +
          "length only if you want the reported length to match.",
      },
      value: {
        description:
          "Keyframe value. A number becomes a float Variant; true/false a bool; a plain string " +
          "a string; {x,y} a Vector2; {x,y,z} a Vector3; {r,g,b[,a]} a Color (a defaults to 1). " +
          "A type-tagged object ({type:'vector3',...}) overrides component inference. Godot " +
          "checks the Variant type against the track type at insertion — a mismatch returns " +
          "invalid_parameter. Whole numbers become floats (value tracks treat numbers as floats).",
        // One-of: number | boolean | string | object (vector2/vector3/color). The handler parser
        // discriminates by shape.
        type: ["number", "boolean", "string", "object"],
      },
      interpolation: {
        type: "string",
        enum: ["nearest", "linear", "cubic"],
        description:
          "Optional per-key interpolation override. 'nearest': step (no easing). 'linear': " +
          "straight-line (Godot's default for most tracks). 'cubic': smooth easing. Absent " +
          "leaves the Godot default. Angle-variant interpolations (linear_angle / cubic_angle) " +
          "are deferred from v1.",
      },
      transition: {
        type: "number",
        minimum: 0,
        description:
          "Optional easing curve weight (Godot's transition parameter). Default 1.0 (linear " +
          "easing curve). Higher values ease in more sharply; lower values ease out.",
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
