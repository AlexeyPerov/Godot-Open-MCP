// `godot_open_mcp_tilemap_set_tileset` tool definition (P12.1).
//
// Assigns an existing Godot TileSet resource to a TileMapLayer. No TileSet
// authoring in v1 — agents point this at an existing .tres/.res TileSet (use
// resource_create to build one if needed). The handler lives in the bridge
// (POST /tools/godot_open_mcp_tilemap_set_tileset); this file is the catalog
// metadata only.
//
// Adapted from Unity Open MCP's tilemap-create tile-assignment surface (adapt
// fidelity): same mutating-tool shape (paths_hint required + gate default
// enforce), but Godot addresses a TileSet as a res:// resource path rather than
// a Unity TileBase asset path, and assigns it to the layer's TileSet property
// directly.
//
// This is a `tilemap` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const tilemapSetTileset: Tool = {
  name: "godot_open_mcp_tilemap_set_tileset",
  description:
    "Assign an existing Godot TileSet resource to a TileMapLayer. The tileset_path must be a " +
    "res:// path to an existing .tres/.res TileSet (use resource_create to build one first if " +
    "needed — no TileSet authoring is bundled with this tool). After assignment the layer can be " +
    "painted with tilemap_set_cell. The scene is marked unsaved. This is a `tilemap` group tool — " +
    "activate the group with manage_tools first. Mutating: runs the gate cycle by default; " +
    "paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "tileset_path", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target TileMapLayer, relative to the edited scene root " +
          "('Main/Ground', '/root/Main/Ground', or '.' for the root). Must resolve to a " +
          "TileMapLayer node (Godot 4.3+); the deprecated TileMap node is rejected with " +
          "wrong_node_type.",
      },
      tileset_path: {
        type: "string",
        description:
          "res:// path (or uid://) to an existing TileSet resource (.tres/.res). Must exist and " +
          "be a TileSet — a non-TileSet resource at the path returns resource_load_failed.",
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
