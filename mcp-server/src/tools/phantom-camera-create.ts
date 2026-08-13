// `godot_open_mcp_phantom_camera_create` tool definition.
//
// First tool of the phantom_camera domain pack. Creates a PhantomCamera3D
// (default) or PhantomCamera2D node — the phantom-camera addon's virtual
// cameras — in the currently edited scene via ClassDB.Instantiate and returns
// its NodeData (same shape as node_create). The handler lives in the bridge
// (POST /tools/godot_open_mcp_phantom_camera_create); this file is the catalog
// metadata only.
//
// Greenfield fidelity — there is no Unity twin inside this repo's porting scope,
// and the addon is GDScript (not statically available to the C# bridge). The
// mutating-tool shape (paths_hint required + gate default enforce) is copied
// from the Phase 12 domain packs. Runtime addon gate: if the addon is not
// enabled the handler returns addon_not_found (the architecture-faithful
// equivalent of a compile gate — Godot Open MCP has no per-pack compile
// inventory).
//
// This is a `phantom_camera` group tool — it is hidden from ListTools until an
// agent activates the group via `godot_open_mcp_manage_tools({ action:
// "activate", group: "phantom_camera" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const phantomCameraCreate: Tool = {
  name: "godot_open_mcp_phantom_camera_create",
  description:
    "Create a phantom-camera addon virtual camera node (PhantomCamera3D by default, or " +
    "PhantomCamera2D when dimension is '2d') in the currently edited scene and return its NodeData " +
    "(instanceId, name, path, type, scriptResourcePath, childCount) so an agent can chain node_path " +
    "straight into phantom_camera_set_priority / set_target / set_follow / set_look_at. The new " +
    "node's owner is set to the edited scene root so it persists in the .tscn on save; the scene is " +
    "marked unsaved. Requires the phantom-camera addon enabled (returns addon_not_found otherwise). " +
    "A PhantomCamera is inert until a PhantomCameraHost exists under a Camera3D/Camera2D in the " +
    "scene and the camera has a priority — follow up with phantom_camera_set_priority. Optionally " +
    "pass parent_node_path (defaults to the edited scene root) and name. position is 'x,y,z' for 3D " +
    "(default) or 'x,y' for 2D. This is a `phantom_camera` group tool — activate the group with " +
    "manage_tools first. Mutating: runs the gate cycle by default; paths_hint is the edited scene " +
    "res:// path.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      name: {
        type: "string",
        description:
          "Optional name for the new PhantomCamera. When omitted, Godot assigns a default name " +
          "(e.g. 'PhantomCamera3D').",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root. " +
          "Accepts 'Main', 'Main/Player', '/root/Main/Player', or '.' for the root itself " +
          "(same resolver as node_find / node_create). Defaults to the edited scene root.",
      },
      dimension: {
        type: "string",
        enum: ["2d", "3d"],
        default: "3d",
        description:
          "Which addon virtual camera class to create: '3d' (PhantomCamera3D, default) or '2d' " +
          "(PhantomCamera2D). Affects which position format applies ('x,y,z' vs 'x,y').",
      },
      position: {
        type: "string",
        description:
          "Optional position as 'x,y,z' (3D, default) or 'x,y' (2D). Applied because the addon " +
          "cameras derive from Node3D / Node2D; defaults to the origin.",
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
