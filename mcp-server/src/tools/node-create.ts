// `godot_open_mcp_node_create` tool definition (P2.3).
//
// The first mutating node tool. Creates a Godot Node in the currently edited
// scene and returns its NodeData (same shape as node_find) so an agent can
// chain immediately into node_find / (later) node_modify. The handler lives in
// the bridge (POST /tools/godot_open_mcp_node_create); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/gameobject-create.ts
// (adapt fidelity): same mutating-tool shape (paths_hint + gate forward-compat
// fields, parent + transform args), but the creation surface is swapped for
// Godot:
//   - `type_class_name` + `instance_scene_path` replace Unity's
//     `primitive_type` (Godot has no Cube/Sphere primitives — it uses
//     ClassDB instantiation or PackedScene instancing).
//   - `instance_scene_path` takes precedence when set (Godot scene instancing
//     analog of Unity prefab instantiation).
//   - `parent_node_path` replaces Unity's `parent_path` (renamed to match
//     node_find's resolver vocabulary).
//   - `position` / `rotation` / `scale` stay as "x,y,z" / "x,y" strings
//     (2-component for Node2D), applied only when the new node is a Node3D /
//     Node2D. Unity's `local_space` is dropped — Godot's Node3D/Node2D
//     transforms are local to their parent already.
//   - Unity's `name` is required; Godot makes it optional (defaults to the
//     type's auto-name), so it is optional here.
//   - `paths_hint` and `gate` are schema no-ops until the gate flow lands
//     (P3.5). They are present for forward-compat so an agent's mutating-tool
//     call shape does not change across phases.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const nodeCreate: Tool = {
  name: "godot_open_mcp_node_create",
  description:
    "Create a new Node in the currently edited Godot scene and return its NodeData " +
    "(instanceId, name, path, type, scriptResourcePath, childCount, optional children). Two " +
    "creation modes: (1) typed — pass `type_class_name` (a Godot class like 'Node3D', " +
    "'Sprite2D', 'CharacterBody3D'; defaults to 'Node'); (2) instanced scene — pass " +
    "`instance_scene_path` (a res:// path to a .tscn/.scn PackedScene), which takes precedence " +
    "when both are set. The new Node's owner is set to the edited scene root so it persists in " +
    "the .tscn on save; the scene is marked unsaved. Optionally pass `parent_node_path` (defaults " +
    "to the edited scene root) and `name` (defaults to the type's auto-name). Transform fields " +
    "(position/rotation/scale) apply only when the new Node is a Node3D or Node2D; rotation is in " +
    "degrees. NOTE: this is a mutating tool, but the gate safety layer is not wired yet — there " +
    "is no checkpoint/validate/delta cycle and no editor Undo until the gate lands. Verify the " +
    "result with node_find and save the scene explicitly when you need persistence.",
  inputSchema: {
    type: "object",
    properties: {
      name: {
        type: "string",
        description:
          "Name for the new Node. When omitted, Godot assigns a default name for the type/scene " +
          "(e.g. 'Node3D', 'Sprite2D'). Unlike Unity's gameobject_create, name is optional.",
      },
      type_class_name: {
        type: "string",
        description:
          "Godot class name to instantiate via ClassDB (e.g. 'Node3D', 'Sprite2D', " +
          "'CharacterBody3D', 'Node'). Used when instance_scene_path is not provided. Defaults " +
          "to 'Node'. Replaces Unity's primitive_type — Godot has no Cube/Sphere primitives.",
      },
      instance_scene_path: {
        type: "string",
        description:
          "res:// path to a PackedScene (.tscn/.scn) to instance as the new Node. Takes " +
          "precedence over type_class_name when both are supplied (Godot analog of Unity's " +
          "prefab instantiation).",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root. " +
          "Accepts 'Main', 'Main/Player', '/root/Main/Player', or '.' for the root itself " +
          "(same resolver as node_find). Defaults to the edited scene root. Renamed from " +
          "Unity's parent_path to match the node tool vocabulary.",
      },
      position: {
        type: "string",
        description:
          "Optional position as 'x,y,z' (Node3D) or 'x,y' (Node2D). Applied only when the new " +
          "Node is a Node3D / Node2D; otherwise ignored. Defaults to the type's origin.",
      },
      rotation: {
        type: "string",
        description:
          "Optional rotation in degrees as 'x,y,z' (Node3D) or 'x,y' (Node2D). Applied only " +
          "when the new Node is a Node3D / Node2D; otherwise ignored. Defaults to identity.",
      },
      scale: {
        type: "string",
        description:
          "Optional scale as 'x,y,z' (Node3D) or 'x,y' (Node2D). Applied only when the new " +
          "Node is a Node3D / Node2D; otherwise ignored. Defaults to (1,1,1) / (1,1).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the edited scene res:// path. Forward-compat no-op until the gate " +
          "flow (P3.5) lands; the mutating handler runs without checkpoint/validate/delta until " +
          "then.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "off",
        description:
          "Gate mode. Forward-compat no-op until P3.5; default 'off' because the gate is not " +
          "wired yet.",
      },
    },
    additionalProperties: false,
  },
};
