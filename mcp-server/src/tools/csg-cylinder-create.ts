// `godot_open_mcp_csg_cylinder_create` tool definition (P12.5).
//
// Creates a CsgCylinder3D node in the currently edited scene and returns its
// NodeData (same shape as node_create). The handler lives in the bridge
// (POST /tools/godot_open_mcp_csg_cylinder_create); this file is the catalog
// metadata only.
//
// Greenfield from the Godot catalog (greenfield fidelity): same create-a-node
// pattern as csg_box_create, with cylinder-specific scalars (radius / height /
// sides / cone / smooth_faces). See csg-box-create.ts header for the fidelity
// rationale. An optional `operation` applies Godot's CsgShape3D.OperationEnum
// at create time.
//
// This is a `csg` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const csgCylinderCreate: Tool = {
  name: "godot_open_mcp_csg_cylinder_create",
  description:
    "Create a CsgCylinder3D node in the currently edited scene and return its NodeData (same shape " +
    "as node_create). Optional scalars tune the cylinder: `radius` (strictly positive, default 0.5), " +
    "`height` (strictly positive, default 2.0), `sides` (clamped to [3, 1000], default 8 — higher is " +
    "smoother), `cone` (bool, default false — set true to make a cone), `smooth_faces` (bool, default " +
    "true). An optional `operation` applies the boolean operation (union / intersection / " +
    "subtraction) at create time — most useful when this cylinder is a child of a CsgCombiner3D. The " +
    "new node's owner is set to the edited scene root so it persists in the .tscn on save; the scene " +
    "is marked unsaved. This is a `csg` group tool — activate the group with manage_tools first. " +
    "Mutating: runs the gate cycle by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      name: {
        type: "string",
        description:
          "Optional name for the new cylinder. When omitted, Godot assigns a default name (e.g. 'CSGCylinder3D').",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root. " +
          "Accepts 'Main', 'Main/Geometry', '/root/Main/Geometry', or '.' for the root itself " +
          "(same resolver as node_find / node_create). Defaults to the edited scene root. Pass a " +
          "CsgCombiner3D's path to group this cylinder into a boolean combination.",
      },
      position: {
        type: "string",
        description:
          "Optional position as 'x,y,z' (the cylinder derives from Node3D). Applied best-effort; " +
          "a malformed vector is ignored. Defaults to the origin.",
      },
      radius: {
        type: "number",
        exclusiveMinimum: 0,
        description:
          "Optional cylinder radius. Clamped to strictly positive (≥ 0.0001). Engine default is 0.5.",
      },
      height: {
        type: "number",
        exclusiveMinimum: 0,
        description:
          "Optional cylinder height. Clamped to strictly positive (≥ 0.0001). Engine default is 2.0.",
      },
      sides: {
        type: "integer",
        minimum: 3,
        description:
          "Optional number of sides (a higher value is smoother; 3 makes a triangular prism). " +
          "Clamped to [3, 1000]. Engine default is 8.",
      },
      cone: {
        type: "boolean",
        description:
          "If true, create a cone instead of a cylinder (the top collapses to a point). " +
          "Engine default is false.",
      },
      smooth_faces: {
        type: "boolean",
        description:
          "If true (default), normals are set to give a smooth appearance; false gives a faceted look.",
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
