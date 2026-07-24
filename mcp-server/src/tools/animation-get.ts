// `godot_open_mcp_animation_get` tool definition (P12.4).
//
// Reads the libraries / animations / tracks of an AnimationPlayer in a bounded JSON
// envelope. Read-only (gate-free). The handler lives in the bridge
// (POST /tools/godot_open_mcp_animation_get); this file is the catalog metadata
// only.
//
// The read-only analog in Unity Open MCP is the AnimationClip inspect path (adapt
// fidelity): same bounded-inspect contract, but the Godot result walks the player →
// library → animation → track hierarchy (Unity has no library layer — clips live as
// assets). Does not dump every key by default; pass include_keys + an optional
// animation filter + max_keys (default 32, hard max 256) to dump keys for one clip.
//
// This is an `animation` group tool — activate the group with manage_tools first.
// Read-only — no paths_hint, no gate.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const animationGet: Tool = {
  name: "godot_open_mcp_animation_get",
  description:
    "Read the libraries / animations / tracks of an AnimationPlayer in a bounded JSON envelope. " +
    "Returns the full hierarchy: player → libraries[] → animations[] → tracks[] (each track " +
    "carries index / type / path / keyCount). Does NOT dump every key by default — pass " +
    "include_keys: true plus an optional animation filter and max_keys to dump keys for one clip " +
    "(capped at max_keys per track, default 32, hard max 256; a 'truncated' count is reported " +
    "when the cap bites). Optional library / animation filters restrict the walk to one library " +
    "or one clip (an animation filter without a library filter looks in 'default'). A filter that " +
    "does not resolve returns library_not_found / animation_not_found. Read-only (gate-free). " +
    "This is an `animation` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["node_path"],
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
          "Optional library name filter — restricts the walk to that one library. When absent, " +
          "all libraries on the player are listed. A name that does not exist returns " +
          "library_not_found.",
      },
      animation: {
        type: "string",
        description:
          "Optional clip name filter — restricts the walk to that one clip. When set without a " +
          "library filter, the handler looks in 'default'. A name that does not exist returns " +
          "animation_not_found.",
      },
      include_keys: {
        type: "boolean",
        default: false,
        description:
          "If true, each track also carries a bounded keys[] array (time / transition / value). " +
          "Defaults to false — the default response reports keyCount only, never key values. Set " +
          "to true when you need to inspect or echo back the keyframes.",
      },
      max_keys: {
        type: "integer",
        minimum: 0,
        maximum: 256,
        description:
          "Max keys per track when include_keys is true. Defaults to 32; clamped to [0, 256]. 0 " +
          "returns no keys (just the keyCount). When the cap bites, a 'truncated' field reports " +
          "how many keys were omitted.",
      },
    },
    additionalProperties: false,
  },
};
