// `godot_open_mcp_particles_set_emitting` tool definition (P12.3).
//
// Starts or stops emission on a GpuParticles2D / GpuParticles3D by flipping the
// Emitting property, with an optional restart that clears existing particles.
// The handler lives in the bridge
// (POST /tools/godot_open_mcp_particles_set_emitting); this file is the catalog
// metadata only.
//
// Greenfield from the Godot catalog (greenfield/adapt fidelity): Unity Open MCP
// exposes emission toggling only through the broader particle_system_modify. The
// Godot catalog surfaces it as a first-class verb because it pairs the toggle
// with a restart (Godot's Restart() clears existing particles and restarts the
// emission cycle — useful for one-shot re-fire or resetting a continuous
// emitter's accumulator). emitting is intentionally excluded from
// particles_configure's allow-list so this tool stays the single toggle path.
//
// This is a `particles` group tool — activate the group with manage_tools first.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const particlesSetEmitting: Tool = {
  name: "godot_open_mcp_particles_set_emitting",
  description:
    "Start or stop emission on a GpuParticles2D or GpuParticles3D by setting its emitting flag. Pass " +
    "emitting true to start, false to stop. When restart is true, Godot's Restart() is called first — " +
    "it clears existing particles and restarts the emission cycle (useful for one-shot re-fire or " +
    "resetting a continuous emitter's accumulator). This is the single toggle path — emitting is " +
    "intentionally not in particles_configure's allow-list. The scene is marked unsaved. This is a " +
    "`particles` group tool — activate the group with manage_tools first. Mutating: runs the gate cycle " +
    "by default; paths_hint is the edited scene res:// path.",
  inputSchema: {
    type: "object",
    required: ["node_path", "emitting", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "Scene-tree path of the target GpuParticles2D or GpuParticles3D, relative to the edited " +
          "scene root. A node of any other type returns wrong_node_type.",
      },
      emitting: {
        type: "boolean",
        description:
          "true to start emission; false to stop. A non-boolean (e.g. 1) is rejected with " +
          "missing_parameter.",
      },
      restart: {
        type: "boolean",
        default: false,
        description:
          "If true, call Godot's Restart() before flipping emitting — clears existing particles and " +
          "restarts the emission cycle. Defaults to false (a plain toggle).",
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
