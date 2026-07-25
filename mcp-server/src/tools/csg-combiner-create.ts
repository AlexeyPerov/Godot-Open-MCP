// `godot_open_mcp_csg_combiner_create` tool definition (P12.5).
//
// Creates a CsgCombiner3D node in the currently edited scene and returns its
// NodeData (same shape as node_create). The handler lives in the bridge
// (POST /tools/godot_open_mcp_csg_combiner_create); this file is the catalog
// metadata only.
//
// Greenfield from the Godot catalog (greenfield fidelity): there is no Unity CSG
// twin. The CsgCombiner3D is the Godot equivalent of a "boolean group" — its
// child CsgShape3D nodes combine according to each child's Operation property.
// Unity ProBuilder has no direct equivalent (face extrude / delete are the
// ProBuilder verbs; intentionally NOT ported). The combiner has no primitive
// scalars of its own — its job is to be a parent for boolean combinations.
//
// This is a `csg` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const csgCombinerCreate: Tool = {
  name: "godot_open_mcp_csg_combiner_create",
  description:
    "Create a CsgCombiner3D node in the currently edited scene and return its NodeData (same shape " +
    "as node_create). The combiner is Godot's boolean-group container: every child CsgShape3D " +
    "(box / sphere / cylinder / nested combiner) combines according to its own Operation property. " +
    "Create primitives under the combiner by passing its path as parent_node_path to the primitive " +
    "create tools, then call csg_set_operation on each child to define how it combines (e.g. " +
    "subtraction on a cutter carves out the cutter's shape). The combiner has no primitive scalars " +
    "of its own; the only knob is the optional `operation` (rarely needed — a combiner's own " +
    "operation only matters when it is itself a child of another combiner). The new node's owner is " +
    "set to the edited scene root so it persists in the .tscn on save; the scene is marked unsaved. " +
    "This is a `csg` group tool — activate the group with manage_tools first. Mutating: runs the " +
    "gate cycle by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      name: {
        type: "string",
        description:
          "Optional name for the new combiner. When omitted, Godot assigns a default name (e.g. 'CSGCombiner3D').",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root. " +
          "Accepts 'Main', 'Main/Geometry', '/root/Main/Geometry', or '.' for the root itself " +
          "(same resolver as node_find / node_create). Defaults to the edited scene root. Nested " +
          "combiners are supported: pass an existing CsgCombiner3D's path to nest this combiner as " +
          "a boolean child.",
      },
      position: {
        type: "string",
        description:
          "Optional position as 'x,y,z' (the combiner derives from Node3D). Applied best-effort; " +
          "a malformed vector is ignored. Defaults to the origin.",
      },
      operation: {
        type: "string",
        enum: ["union", "intersection", "subtraction"],
        description:
          "Optional boolean operation this combiner applies when it is itself a child of another " +
          "CsgCombiner3D. 'union' (Godot's default) merges the geometry; 'intersection' keeps only " +
          "the overlap; 'subtraction' removes the cutter's shape from the sibling-before-it. Omit to " +
          "leave the engine default (union).",
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
