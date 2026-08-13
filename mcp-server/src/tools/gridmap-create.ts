// `godot_open_mcp_gridmap_create` tool definition.
//
// First tool of the gridmap domain pack. Creates a Godot 4.3+ `GridMap` node (3D
// grid-based level construction) in the currently edited scene and returns its
// NodeData (same shape as node_create) so an agent can chain node_path straight
// into gridmap_set_mesh_library / gridmap_set_cell. The handler lives in the
// bridge (POST /tools/godot_open_mcp_gridmap_create); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Adapted from the tilemap pack's create surface (adapt fidelity): same
// mutating-tool shape (paths_hint required + gate default enforce), but the
// creation surface is Godot-native:
//   - The node is a `GridMap` (a Node3D), the 3D twin of TileMapLayer.
//   - `parent_node_path` matches node_create's resolver vocabulary.
//   - `position` is an "x,y,z" string because GridMap derives from Node3D.
//
// This is a `gridmap` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "gridmap" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const gridmapCreate: Tool = {
  name: "godot_open_mcp_gridmap_create",
  description:
    "Create a Godot 4.3+ GridMap node (3D grid-based level construction) in the currently edited " +
    "scene and return its NodeData (instanceId, name, path, type, scriptResourcePath, childCount) " +
    "so an agent can chain node_path straight into gridmap_set_mesh_library / gridmap_set_cell. " +
    "The new node's owner is set to the edited scene root so it persists in the .tscn on save; " +
    "the scene is marked unsaved. A GridMap is a Node3D, so position ('x,y,z') applies. Optionally " +
    "pass parent_node_path (defaults to the edited scene root) and name (defaults to the type's " +
    "auto-name). This is a `gridmap` group tool — activate the group with manage_tools first. " +
    "Mutating: runs the gate cycle by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      name: {
        type: "string",
        description:
          "Optional name for the new GridMap. When omitted, Godot assigns a default name " +
          "(e.g. 'GridMap').",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root. " +
          "Accepts 'Main', 'Main/Level', '/root/Main/Level', or '.' for the root itself " +
          "(same resolver as node_find / node_create). Defaults to the edited scene root.",
      },
      position: {
        type: "string",
        description:
          "Optional position as 'x,y,z'. Applied because GridMap derives from Node3D; " +
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
