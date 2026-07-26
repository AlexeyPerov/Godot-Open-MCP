// `godot_open_mcp_particles_defaults` tool definition (P12.3).
//
// First tool of the particles domain pack. A pure helper — returns recommended
// starter scalars for a 2D or 3D GpuParticles emitter as a JSON object an agent
// can spread into particles_create / particles_configure. No scene required; no
// gate surface. The handler lives in the bridge
// (POST /tools/godot_open_mcp_particles_defaults); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Greenfield from the Godot catalog (greenfield/adapt fidelity): Unity Open MCP
// ships no standalone defaults tool — its particle_system_modify carries inline
// defaults an agent has to infer from prose. The Godot catalog surfaces defaults
// as its own tool so an agent gets a single-call probe of the allow-listed
// scalar surface it can then spread into create/configure.
//
// This is a `particles` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "particles" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const particlesDefaults: Tool = {
  name: "godot_open_mcp_particles_defaults",
  description:
    "Return the recommended starter scalars (amount, lifetime, one_shot, preprocess, speed_scale, " +
    "explosiveness, randomness, fixed_fps, interpolate, fract_delta, local_coords) for a 2D or 3D " +
    "GpuParticles emitter. Pure helper — no scene required, no mutation. The 3D defaults lean higher " +
    "on amount (30 vs 16 in 2D) since billboarded 3D particles need more samples to read as a " +
    "continuous plume; both are mid-range values inside the clamp ranges so a spread-into-configure " +
    "round-trips losslessly (the result keys already match the snake_case configure schema). Spread " +
    "the result's properties into particles_create's initial properties object, or use the values as " +
    "guidance when calling particles_configure. This is a `particles` group tool — activate the group " +
    "with manage_tools first. Read-only.",
  inputSchema: {
    type: "object",
    required: ["dimension"],
    properties: {
      dimension: {
        type: "string",
        enum: ["2d", "3d"],
        description:
          "Which emitter family to return defaults for. '2d' selects GpuParticles2D; '3d' selects " +
          "GpuParticles3D.",
      },
    },
    additionalProperties: false,
  },
};
