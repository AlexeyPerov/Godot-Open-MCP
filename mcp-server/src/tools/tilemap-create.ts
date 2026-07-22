// `godot_open_mcp_tilemap_create` tool definition (P12.1).
//
// First tool of the tilemap domain pack. Creates a Godot 4.3+ `TileMapLayer` node
// in the currently edited scene and returns its NodeData (same shape as
// node_create) so an agent can chain node_path straight into tilemap_set_tileset
// / tilemap_set_cell. The handler lives in the bridge
// (POST /tools/godot_open_mcp_tilemap_create); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/tilemap-create.ts (adapt
// fidelity): same mutating-tool shape (paths_hint required + gate default enforce),
// but the creation surface is Godot-native:
//   - The node is a single `TileMapLayer` (Godot 4.3+), NOT Unity's Grid + Tilemap
//     hierarchy and NOT Godot's deprecated `TileMap` multi-layer node.
//   - No separate grid parent — TileMapLayer is a self-contained 2D tile layer.
//   - `parent_node_path` matches node_create's resolver vocabulary.
//   - `position` applies because TileMapLayer derives from Node2D.
//
// This is a `tilemap` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "tilemap" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const tilemapCreate: Tool = {
  name: "godot_open_mcp_tilemap_create",
  description:
    "Create a Godot 4.3+ TileMapLayer node in the currently edited scene and return its NodeData " +
    "(instanceId, name, path, type, scriptResourcePath, childCount) so an agent can chain " +
    "node_path straight into tilemap_set_tileset / tilemap_set_cell. The new node's owner is set " +
    "to the edited scene root so it persists in the .tscn on save; the scene is marked unsaved. " +
    "TileMapLayer is the Godot 4.3 replacement for the deprecated multi-layer TileMap node — use " +
    "one TileMapLayer per layer. Optionally pass parent_node_path (defaults to the edited scene " +
    "root) and name (defaults to the type's auto-name). A position field ('x,y') applies because " +
    "TileMapLayer derives from Node2D. This is a `tilemap` group tool — activate the group with " +
    "manage_tools first. Mutating: runs the gate cycle by default; paths_hint is the edited scene " +
    "res:// path.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      name: {
        type: "string",
        description:
          "Optional name for the new TileMapLayer. When omitted, Godot assigns a default name " +
          "(e.g. 'TileMapLayer').",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root. " +
          "Accepts 'Main', 'Main/Player', '/root/Main/Player', or '.' for the root itself " +
          "(same resolver as node_find / node_create). Defaults to the edited scene root.",
      },
      position: {
        type: "string",
        description:
          "Optional position as 'x,y'. Applied because TileMapLayer derives from Node2D; " +
          "defaults to the origin.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the edited scene res:// path (e.g. res://levels/level_1.tscn). " +
          "Mandatory even when gate is 'off' (handler-level guard).",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → mutate → validate → delta; new " +
          "errors fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the " +
          "cycle (paths_hint is still required).",
      },
    },
    additionalProperties: false,
  },
};
