// `godot_open_mcp_animation_library_add` tool definition (P12.4).
//
// Adds an empty AnimationLibrary registered under a name on a target AnimationPlayer.
// The handler lives in the bridge
// (POST /tools/godot_open_mcp_animation_library_add); this file is the catalog
// metadata only.
//
// Adapted from Unity Open MCP's animation envelope (adapt fidelity): Unity ships no
// separate library step (AnimationClip assets live in the project, not on a
// container). The Godot catalog surfaces it because Godot's AnimationPlayer owns
// libraries as named slots, and an agent must create the slot before adding clips.
// The library name defaults to \"default\" (Godot's convention). A duplicate name
// returns already_exists — no silent overwrite.
//
// This is an `animation` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const animationLibraryAdd: Tool = {
  name: "godot_open_mcp_animation_library_add",
  description:
    "Add an empty AnimationLibrary registered under a name on a target AnimationPlayer. The " +
    "library is a named slot on the player that will hold Animation clips (added afterwards via " +
    "animation_create). The library name defaults to 'default' when omitted (Godot's convention). " +
    "A library already registered under that name returns already_exists — no silent overwrite. " +
    "The scene is marked unsaved. This is an `animation` group tool — activate the group with " +
    "manage_tools first. Mutating: runs the gate cycle by default; paths_hint is the edited " +
    "scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target AnimationPlayer, relative to the edited scene root. " +
          "Must resolve to an AnimationPlayer; a different node type returns wrong_node_type.",
      },
      library: {
        type: "string",
        description:
          "Name to register the library under on the player. Defaults to 'default' when omitted " +
          "or empty. Godot's internal default library key is the empty string, but this pack " +
          "names libraries 'default' by convention so an agent does not have to know about the " +
          "empty-string special case. Use a distinct name (e.g. 'player', 'ui') to hold " +
          "multiple libraries on one player.",
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
