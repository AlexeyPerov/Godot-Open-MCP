// `godot_open_mcp_particles_create` tool definition (P12.3).
//
// Creates a GpuParticles2D or GpuParticles3D node in the currently edited scene
// and returns its NodeData (same shape as node_create). The dimension arg selects
// the class. The handler lives in the bridge
// (POST /tools/godot_open_mcp_particles_create); this file is the catalog
// metadata only.
//
// Adapted from Unity Open MCP's particle-system surface (adapt fidelity): same
// mutating-tool shape (paths_hint required + gate default enforce), but the Godot
// surface is a node, not a ParticleSystem component — create makes a node under a
// parent (default: edited scene root). An optional process_material_path points
// at a pre-existing ParticleProcessMaterial (Godot emitters render nothing
// without one), and an optional initial properties object applies the same
// allow-listed + clamped scalars as particles_configure post-creation.
//
// This is a `particles` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const particlesCreate: Tool = {
  name: "godot_open_mcp_particles_create",
  description:
    "Create a GpuParticles2D (dimension '2d') or GpuParticles3D (dimension '3d') node in the " +
    "currently edited scene and return its NodeData. A GPU particle emitter needs a process material " +
    "(typically a ParticleProcessMaterial) to render anything — pass an optional process_material_path " +
    "(res:// to an existing material) to assign one at create time, or add it later via resource " +
    "tooling. An optional initial properties object applies the same allow-listed + clamped scalars as " +
    "particles_configure (amount, lifetime, speed_scale, ...) post-creation. The new node's owner is " +
    "set to the edited scene root so it persists in the .tscn on save; the scene is marked unsaved. " +
    "This is a `particles` group tool — activate the group with manage_tools first. Mutating: runs the " +
    "gate cycle by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["dimension", "paths_hint"],
    properties: {
      dimension: {
        type: "string",
        enum: ["2d", "3d"],
        description: "Which emitter class to create. '2d' → GpuParticles2D; '3d' → GpuParticles3D.",
      },
      name: {
        type: "string",
        description:
          "Optional name for the new emitter. When omitted, Godot assigns a default name " +
          "(e.g. 'GPUParticles3D').",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root. " +
          "Accepts 'Main', 'Main/Effects', '/root/Main/Effects', or '.' for the root itself " +
          "(same resolver as node_find / node_create). Defaults to the edited scene root.",
      },
      position: {
        type: "string",
        description:
          "Optional position as 'x,y' (2D) or 'x,y,z' (3D). Applied because the emitter derives " +
          "from Node2D / Node3D; defaults to the origin.",
      },
      process_material_path: {
        type: "string",
        description:
          "Optional res:// (or uid://) path to an existing ProcessMaterial (ParticleProcessMaterial is " +
          "the typical choice for GPU particles). Assigned at create time so the emitter renders " +
          "immediately. A type mismatch returns resource_load_failed. Omit to assign one later.",
      },
      properties: {
        type: "object",
        additionalProperties: false,
        description:
          "Optional initial scalar properties applied post-creation (same allow-list + clamping as " +
          "particles_configure). Only the fields you send are applied; omitted scalars keep the engine " +
          "defaults. Use particles_defaults for starter values.",
        properties: {
          amount: {
            type: "integer",
            minimum: 1,
            description:
              "Number of particles to emit. Clamped to [1, 100000]. Engine default is 8 (2D) / 16 (3D).",
          },
          lifetime: {
            type: "number",
            exclusiveMinimum: 0,
            description:
              "Particle lifetime in seconds. Clamped to strictly positive. Must be > 0.",
          },
          one_shot: {
            type: "boolean",
            description:
              "If true, emit once and stop (one-shot burst) rather than continuously.",
          },
          preprocess: {
            type: "number",
            minimum: 0,
            description:
              "Preprocess duration in seconds — simulate the emitter for this long before the first " +
              "frame so it appears already populated. Clamped to non-negative. 0 = start fresh.",
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
        },
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
