// `godot_open_mcp_csg_get` tool definition (P12.5).
//
// Reads the scalar configuration of any CSG shape — the operation (shared) plus
// the kind-specific scalars (size for box; radius / radial_segments / rings /
// smooth_faces for sphere; radius / height / sides / cone / smooth_faces for
// cylinder; none for combiner). The handler lives in the bridge
// (POST /tools/godot_open_mcp_csg_get); this file is the catalog metadata only.
//
// Greenfield from the Godot catalog (greenfield fidelity): there is no Unity CSG
// twin. The catalog's `csg-get` tool reads the scalar config only — no full mesh
// vertex dump (a CSG mesh's vertex array is large and not what an agent tunes).
// The shape kind is inferred from the resolved node's class, so a single get tool
// covers all four kinds.
//
// This is a `csg` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const csgGet: Tool = {
  name: "godot_open_mcp_csg_get",
  description:
    "Read the scalar configuration of any CSG shape (CsgBox3D / CsgSphere3D / CsgCylinder3D / " +
    "CsgCombiner3D). Returns the resolved `type` (Godot class name) + `kind` (box / sphere / " +
    "cylinder / combiner) + `operation` (union / intersection / subtraction) plus the kind-specific " +
    "scalars (box: size x/y/z; sphere: radius / radial_segments / rings / smooth_faces; cylinder: " +
    "radius / height / sides / cone / smooth_faces; combiner: none). No mesh vertex dump — a CSG " +
    "mesh's vertex array is large and not what an agent tunes. Use this to verify a boolean setup or " +
    "to read back what a create tool landed. This is a `csg` group tool — activate the group with " +
    "manage_tools first. Read-only.",
  inputSchema: {
    type: "object",
    required: ["node_path"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the CSG shape to read, relative to the edited scene root " +
          "(same resolver as node_find). The node must be a CsgShape3D (any primitive or combiner); " +
          "a non-CSG node returns wrong_node_type.",
      },
    },
    additionalProperties: false,
  },
};
