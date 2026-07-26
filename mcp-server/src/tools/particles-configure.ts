// `godot_open_mcp_particles_configure` tool definition (P12.3).
//
// Patches clamped scalar properties on a GpuParticles2D or GpuParticles3D. Each
// scalar is applied independently (non-aborting). The handler lives in the bridge
// (POST /tools/godot_open_mcp_particles_configure); this file is the catalog
// metadata only.
//
// Adapted from Unity Open MCP's particle_system_modify (adapt fidelity): same
// mutating-tool shape (paths_hint required + gate default enforce) and the same
// flat-known-fields style used by node_modify. The Godot delta: the property
// surface is an explicit allow-list with centralized clamping (the P12.3 design
// decision §1/§2 — particles tuning has many interdependent properties and
// easy-to-set invalid ranges, so the pack restricts to a curated scalar surface
// rather than Unity's broader module-field patch). The same property names apply
// to BOTH the 2D and 3D emitter classes; the handler clamps each scalar to its
// valid range and echoes the clamped values. emitting is intentionally NOT in the
// allow-list — use the dedicated particles_set_emitting tool instead.
//
// This is a `particles` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const particlesConfigure: Tool = {
  name: "godot_open_mcp_particles_configure",
  description:
    "Patch clamped scalar properties on a GpuParticles2D or GpuParticles3D emitter. Only the fields " +
    "you send are applied — omitted scalars are left unchanged (the result echoes every applied value, " +
    "clamped to its valid range). amount is clamped to [1, 100000]; lifetime to strictly positive; " +
    "preprocess and speed_scale to non-negative; explosiveness and randomness to [0, 1]; fixed_fps to " +
    "non-negative; the booleans pass through. A non-numeric value is silently skipped (non-aborting — " +
    "a bad value on one key does not skip the rest). Use particles_defaults for starter values, then " +
    "particles_get to read back the full config. To start/stop emission, use particles_set_emitting " +
    "(not this tool). The scene is marked unsaved. This is a `particles` group tool — activate the " +
    "group with manage_tools first. Mutating: runs the gate cycle by default; paths_hint is the edited " +
    "scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target GpuParticles2D or GpuParticles3D, relative to the edited " +
          "scene root. A node of any other type returns wrong_node_type.",
      },
      amount: {
        type: "integer",
        minimum: 1,
        description:
          "Number of particles to emit. Clamped to [1, 100000]. Engine default is 8 (both 2D and 3D).",
      },
      lifetime: {
        type: "number",
        exclusiveMinimum: 0,
        description:
          "Particle lifetime in seconds. Clamped to strictly positive (must be > 0).",
      },
      one_shot: {
        type: "boolean",
        description:
          "If true, emit once and stop (one-shot burst) rather than continuously. Use " +
          "particles_set_emitting to re-fire a one-shot.",
      },
      preprocess: {
        type: "number",
        minimum: 0,
        description:
          "Preprocess duration in seconds — simulate the emitter for this long before the first frame " +
          "so it appears already populated. Clamped to non-negative. 0 = start fresh.",
      },
      speed_scale: {
        type: "number",
        minimum: 0,
        description:
          "Speed scaling factor for particle simulation. Clamped to non-negative. 1.0 = real-time.",
      },
      explosiveness: {
        type: "number",
        minimum: 0,
        maximum: 1,
        description:
          "Emission explosiveness ratio in [0, 1]. 0 = even emission over the lifetime; 1 = all " +
          "particles emitted at once.",
      },
      randomness: {
        type: "number",
        minimum: 0,
        maximum: 1,
        description:
          "Emission randomness ratio in [0, 1]. 0 = no jitter; 1 = maximum lifetime/position jitter.",
      },
      fixed_fps: {
        type: "integer",
        minimum: 0,
        description:
          "Fixed simulation framerate. Clamped to non-negative. 0 = use the render frame rate.",
      },
      interpolate: {
        type: "boolean",
        description:
          "If true, interpolate particle positions between fixed-fps steps for smoother motion.",
      },
      fract_delta: {
        type: "boolean",
        description:
          "If true, use fractional delta time for simulation timing (smoother at variable framerates).",
      },
      local_coords: {
        type: "boolean",
        description:
          "If true, particles use the emitter's local coordinate space; false = global space.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the edited scene res:// path. Mandatory even when gate is 'off'.",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description: "Gate mode. 'enforce' (default), 'warn', or 'off' (paths_hint still required).",
      },
    },
    additionalProperties: false,
  },
};
