// `godot_open_mcp_particles_get` tool definition (P12.3).
//
// Reads the scalar configuration of a GpuParticles2D or GpuParticles3D emitter.
// Read-only (gate-free). The handler lives in the bridge
// (POST /tools/godot_open_mcp_particles_get); this file is the catalog metadata
// only.
//
// The read-only analog in Unity Open MCP is particle_system_get (adapt fidelity):
// same bounded-inspect contract, but the Godot result includes the resolved type
// and dimension so an agent does not need a second probe to know which property
// set applies, plus the process_material_path (or null when unassigned). The
// scalar surface mirrors the configure allow-list exactly so a get → configure
// round-trip is lossless on the allow-listed properties. No particle-instance
// arrays in v1.
//
// This is a `particles` group tool — activate the group with manage_tools first.
// Read-only — no paths_hint, no gate.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const particlesGet: Tool = {
  name: "godot_open_mcp_particles_get",
  description:
    "Read the scalar configuration of a GpuParticles2D or GpuParticles3D emitter. Returns the resolved " +
    "type and dimension so an agent does not need a second probe to know which property set applies, " +
    "plus the full allow-listed scalar set (amount, lifetime, one_shot, preprocess, speed_scale, " +
    "explosiveness, randomness, fixed_fps, interpolate, fract_delta, local_coords, emitting) and the " +
    "process_material_path (or null when unassigned). The scalar surface mirrors particles_configure's " +
    "allow-list exactly so a get → configure round-trip is lossless on those properties. Read-only " +
    "(gate-free). This is a `particles` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["node_path"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target GpuParticles2D or GpuParticles3D, relative to the edited " +
          "scene root. Must resolve to a GpuParticles2D or GpuParticles3D; a different node type " +
          "returns wrong_node_type.",
      },
    },
    additionalProperties: false,
  },
};
