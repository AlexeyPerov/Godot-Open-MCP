// `godot_open_mcp_csg_box_create` tool definition (P12.5).
//
// Creates a CsgBox3D node in the currently edited scene and returns its NodeData
// (same shape as node_create). The handler lives in the bridge
// (POST /tools/godot_open_mcp_csg_box_create); this file is the catalog metadata
// only.
//
// Greenfield from the Godot catalog (greenfield fidelity): there is no Unity CSG
// twin. Unity ProBuilder is a face-editing mesh primitive addon (pattern reference
// only); the Godot catalog surfaces CSG primitives as separate create tools per
// kind rather than a single create_shape with a shape enum — clearer for agents
// picking a primitive. The create makes a node (CsgBox3D is a Node3D subclass),
// not a Unity component add. An optional `operation` applies Godot's
// CsgShape3D.OperationEnum at create time.
//
// This is a `csg` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const csgBoxCreate: Tool = {
  name: "godot_open_mcp_csg_box_create",
  description:
    "Create a CsgBox3D node in the currently edited scene and return its NodeData (same shape as " +
    "node_create). An optional `size` ('x,y,z') sets the box extents — each component is clamped to " +
    "strictly positive (the engine default is 1,1,1). An optional `operation` applies the boolean " +
    "operation (union / intersection / subtraction) at create time — most useful when this box is a " +
    "child of a CsgCombiner3D. The new node's owner is set to the edited scene root so it persists in " +
    "the .tscn on save; the scene is marked unsaved. For a typical boolean cut: create a CsgCombiner3D " +
    "parent, create a CsgBox3D (union, the body) under it, then create another CsgBox3D (or sphere / " +
    "cylinder) under the combiner and call csg_set_operation subtraction on the cutter. This is a `csg` " +
    "group tool — activate the group with manage_tools first. Mutating: runs the gate cycle by " +
    "default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      name: {
        type: "string",
        description:
          "Optional name for the new box. When omitted, Godot assigns a default name (e.g. 'CSGBox3D').",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root. " +
          "Accepts 'Main', 'Main/Geometry', '/root/Main/Geometry', or '.' for the root itself " +
          "(same resolver as node_find / node_create). Defaults to the edited scene root. Pass a " +
          "CsgCombiner3D's path to group this box into a boolean combination.",
      },
      position: {
        type: "string",
        description:
          "Optional position as 'x,y,z' (the box derives from Node3D). Applied best-effort; " +
          "a malformed vector is ignored. Defaults to the origin.",
      },
      size: {
        type: "string",
        description:
          "Optional box extents as 'x,y,z'. Each component is clamped to strictly positive " +
          "(≥ 0.0001) — a zero-size face is degenerate. Engine default is 1,1,1.",
      },
      operation: {
        type: "string",
        enum: ["union", "intersection", "subtraction"],
        description:
          "Optional boolean operation this shape applies when it is a child of a CsgCombiner3D " +
          "(or itself a child of another CSG shape). 'union' (Godot's default) merges the geometry; " +
          "'intersection' keeps only the overlap; 'subtraction' removes the cutter's shape from the " +
          "sibling-before-it. Omit to leave the engine default (union).",
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
