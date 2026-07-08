// `godot_open_mcp_node_modify` tool definition (P2.4).
//
// Applies property updates to one or more Nodes in the currently edited Godot
// scene and returns each updated Node's NodeData (same shape as node_find) plus
// a warnings array. The handler lives in the bridge (POST
// /tools/godot_open_mcp_node_modify); this file is the catalog metadata only —
// name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/gameobject-modify.ts
// (adapt fidelity): same mutating-tool shape (paths_hint + gate forward-compat
// fields, single + batch targets), but the property surface is swapped for
// Godot:
//   - `node_path` (single) + `node_paths` (array) replace Unity's
//     instance_id/path resolvers. Godot scenes are single-rooted under the
//     edited scene root; the Unity cross-scene instance_id resolver is not
//     ported.
//   - `properties` (a string→string map) replaces Unity's component property
//     paths + RFC 7396 three-surface form. Godot has no Unity component
//     concept — a node IS its type — so P2.4 mutates node-level properties
//     directly. P2.4 supports: visible, name, position/rotation/scale (Node3D/
//     Node2D), modulate (CanvasItem). Anything else → unsupported_property
//     warning (non-aborting).
//   - `position` / `rotation` / `scale` top-level convenience fields are kept
//     (same shape as node_create) and override the same key inside `properties`
//     on a collision.
//   - `local_space` is dropped — Godot Node3D/Node2D transforms are local to
//     their parent already (matches node_create).
//   - Unity's gameObjectDiffs / pathPatchesPerGameObject / jsonPatchesPerGameObject
//     three-surface RFC 7396 form is not ported (Godot nodes are not GameObjects
//     with a component graph). The flat fields + properties map cover P2.4 scope.
//   - `paths_hint` and `gate` are schema no-ops until the gate flow lands
//     (P3.5). They are present for forward-compat so an agent's mutating-tool
//     call shape does not change across phases.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const nodeModify: Tool = {
  name: "godot_open_mcp_node_modify",
  description:
    "Modify properties of one or more Nodes in the currently edited Godot scene. Mutating: applies " +
    "the supplied property updates to each target and returns the updated NodeData array (instanceId, " +
    "name, path, type, scriptResourcePath, childCount) plus a warnings list. Two target shapes: " +
    "(1) single — pass `node_path`; (2) batch — pass `node_paths` (array). At least one is required.\n\n" +
    "Property surface: (a) a free-form `properties` map (string→string entries coerced to the right " +
    "Godot type per key); (b) top-level transform convenience fields `position` / `rotation` / `scale` " +
    "(applied only when the target is a Node3D / Node2D; rotation in degrees), which override the same " +
    "key inside `properties` on a collision; (c) `name` to rename (per target).\n\n" +
    "P2.4 supported keys: `visible` (bool, CanvasItem/Node3D), `name` (string), `position` / `rotation` " +
    "/ `scale` (vector strings, Node3D/Node2D), `modulate` (Color 'r,g,b[,a]', CanvasItem only). Unknown " +
    "keys → `unsupported_property` warnings (the batch does NOT abort). Invalid values for known keys → " +
    "`invalid_property_value` warnings (per-target, non-aborting so good entries still land). Per-target " +
    "resolution misses → `node_not_found` warnings (non-aborting). The scene is marked unsaved when any " +
    "mutation landed. NOTE: this is a mutating tool, but the gate safety layer is not wired yet — no " +
    "checkpoint/validate/delta cycle and no editor Undo until the gate lands. Verify the result with " +
    "node_find.",
  inputSchema: {
    type: "object",
    properties: {
      node_path: {
        type: "string",
        description:
          "Single-target scene-tree path (same resolver as node_find: 'Main/Player', " +
          "'/root/Main/Player', or '.' for the edited scene root). When both node_path and node_paths " +
          "are set, the union of targets is applied (de-duplicated).",
      },
      node_paths: {
        type: "array",
        items: { type: "string" },
        description:
          "Batch-target scene-tree paths. The same property/transform fields are applied to each " +
          "resolved target. Empty entries are dropped.",
      },
      properties: {
        type: "object",
        description:
          "Free-form string→string property map applied to each target. Keys are property names " +
          "(visible, name, modulate, position, rotation, scale); values are the raw string the handler " +
          "coerces (e.g. 'false', '1,0,0,1'). Unknown keys produce unsupported_property warnings. " +
          "Top-level position/rotation/scale/name override the same key here on a collision.",
        additionalProperties: { type: "string" },
      },
      position: {
        type: "string",
        description:
          "Optional position as 'x,y,z' (Node3D) or 'x,y' (Node2D). Applied only when the target is a " +
          "Node3D / Node2D; otherwise produces an unsupported_property warning. Overrides " +
          "properties.position on a collision.",
      },
      rotation: {
        type: "string",
        description:
          "Optional rotation in degrees as 'x,y,z' (Node3D) or 'x,y' (Node2D). Applied only when the " +
          "target is a Node3D / Node2D. Overrides properties.rotation on a collision.",
      },
      scale: {
        type: "string",
        description:
          "Optional scale as 'x,y,z' (Node3D) or 'x,y' (Node2D). Applied only when the target is a " +
          "Node3D / Node2D. Overrides properties.scale on a collision.",
      },
      name: {
        type: "string",
        description:
          "Optional new name. Applied per target — in a batch this can collide (last writer owns the " +
          "name); use unique node_paths when renaming multiple nodes. Overrides properties.name on a " +
          "collision.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the edited scene res:// path. Forward-compat no-op until the gate flow " +
          "(P3.5) lands; the mutating handler runs without checkpoint/validate/delta until then.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "off",
        description:
          "Gate mode. Forward-compat no-op until P3.5; default 'off' because the gate is not wired yet.",
      },
    },
    additionalProperties: false,
  },
};
