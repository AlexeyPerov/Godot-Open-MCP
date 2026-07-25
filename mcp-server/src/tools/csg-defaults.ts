// `godot_open_mcp_csg_defaults` tool definition (P12.5).
//
// First tool of the CSG domain pack. A pure helper — returns recommended
// starter scalars for a CSG primitive kind (box / sphere / cylinder / combiner)
// as a JSON object an agent can spread into the matching csg_*_create tool. No
// scene required; no gate surface. The handler lives in the bridge
// (POST /tools/godot_open_mcp_csg_defaults); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over
// stdio ListTools.
//
// Greenfield from the Godot catalog (greenfield fidelity): there is no Unity
// CSG twin. Unity ships ProBuilder (a face-editing mesh primitive addon), which
// the plan consulted as a pattern reference only; the Godot catalog surfaces
// CSG primitives (box / sphere / cylinder / combiner) as a 7-tool family with
// its own defaults helper. The Godot defaults mirror the engine's own inspector
// defaults so an agent gets back exactly what a fresh Csg*3D node would carry.
//
// This is a `csg` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "csg" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const csgDefaults: Tool = {
  name: "godot_open_mcp_csg_defaults",
  description:
    "Return the recommended starter scalars for a CSG primitive kind (box / sphere / cylinder / " +
    "combiner) plus the shared 'operation' (union / intersection / subtraction). Pure helper — no " +
    "scene required, no mutation. The values mirror Godot's engine defaults for a fresh Csg*3D node " +
    "(box size 1×1×1; sphere radius 0.5 / radial_segments 12 / rings 6 / smooth_faces true; cylinder " +
    "radius 0.5 / height 2.0 / sides 8 / cone false / smooth_faces true; combiner carries operation " +
    "only). Spread the relevant fields into csg_box_create / csg_sphere_create / csg_cylinder_create / " +
    "csg_combiner_create. This is a `csg` group tool — activate the group with manage_tools first. " +
    "Read-only.",
  inputSchema: {
    type: "object",
    required: ["kind"],
    properties: {
      kind: {
        type: "string",
        enum: ["box", "sphere", "cylinder", "combiner"],
        description:
          "Which primitive family to return defaults for. 'box' → CsgBox3D; 'sphere' → CsgSphere3D; " +
          "'cylinder' → CsgCylinder3D; 'combiner' → CsgCombiner3D (no primitive scalars — operation only).",
      },
    },
    additionalProperties: false,
  },
};
