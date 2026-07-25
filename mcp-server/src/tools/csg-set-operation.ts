// `godot_open_mcp_csg_set_operation` tool definition (P12.5).
//
// Sets the boolean Operation property (union / intersection / subtraction) on
// any CSG shape — primitives (CsgBox3D / CsgSphere3D / CsgCylinder3D) and
// CsgCombiner3D alike (the Operation property lives on the shared CsgShape3D
// base). The handler lives in the bridge
// (POST /tools/godot_open_mcp_csg_set_operation); this file is the catalog
// metadata only.
//
// Greenfield from the Godot catalog (greenfield fidelity): Unity ProBuilder has
// no direct equivalent — ProBuilder's verbs are face extrude / delete (intentionally
// NOT ported). Godot's CSG exposes the boolean operation as a per-shape property,
// so this tool is the second half of the boolean workflow: create a combiner
// parent, add primitive children, then call set_operation on each child to
// define how it combines.
//
// This is a `csg` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const csgSetOperation: Tool = {
  name: "godot_open_mcp_csg_set_operation",
  description:
    "Set the boolean Operation property on any CSG shape (CsgBox3D / CsgSphere3D / CsgCylinder3D / " +
    "CsgCombiner3D — the property lives on the shared CsgShape3D base). The operation only takes " +
    "effect when the shape is a child of a CsgCombiner3D (or another CSG shape): 'union' merges the " +
    "child's geometry with the sibling-before-it, 'intersection' keeps only the overlap, 'subtraction' " +
    "removes the child's shape from the sibling-before-it (a cutter carves out its silhouette). " +
    "Godot rebuilds the CSG mesh when the scene updates; the pack marks the scene dirty and does not " +
    "force a manual rebuild. This is a `csg` group tool — activate the group with manage_tools first. " +
    "Mutating: runs the gate cycle by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "operation", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the CSG shape to mutate, relative to the edited scene root " +
          "(same resolver as node_find). The node must be a CsgShape3D (any primitive or combiner); " +
          "a non-CSG node returns wrong_node_type.",
      },
      operation: {
        type: "string",
        enum: ["union", "intersection", "subtraction"],
        description:
          "The boolean operation to set. 'union' merges geometry; 'intersection' keeps only the " +
          "overlap; 'subtraction' removes this shape's silhouette from the sibling-before-it. " +
          "Mirrors Godot's CsgShape3D.OperationEnum.",
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
