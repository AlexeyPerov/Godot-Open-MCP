// `godot_open_mcp_light_set` tool definition (P16.3).
//
// Patches ONE allow-listed light scalar on an existing light node, with central
// clamping + validation. Resolves the node, type-checks it against Light3D /
// Light2D, validates the field is one of the light allow-list, clamps it, and
// writes it. The handler lives in the bridge
// (POST /tools/godot_open_mcp_light_set); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over
// stdio ListTools.
//
// Adapted from Unity Open MCP's `light_set`
// (TypedTools/Extensions/Lighting/LightingTools.cs — adapt fidelity): same
// set-one-typed-field shape, but the field vocabulary is Godot's (color / energy
// / range / spot_angle / attenuation / shadow_enabled) and clamping uses Godot's
// documented bounds. Unity render_mode / cullingMask are NOT ported.
//
// This is a `lighting` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "lighting" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const lightSet: Tool = {
  name: "godot_open_mcp_light_set",
  description:
    "Set ONE allow-listed light scalar on an existing light node (Light3D / Light2D) and mark the " +
    "scene unsaved. Resolves the node, type-checks it, validates the field, clamps it via Godot's " +
    "documented bounds, writes it, and reports the applied field. Fields: 'color' ({r,g,b[,a]} 0-1 " +
    "object or 'r,g,b[,a]' string), 'energy' (float, clamped ≥ 0), 'range' (float, clamped strictly " +
    "positive — Omni/Spot3D only), 'spot_angle' (float degrees, clamped [0.01,180] — SpotLight3D " +
    "only), 'attenuation' (float, clamped ≥ 0 — Omni/Spot3D only), 'shadow_enabled' (bool). A field " +
    "that does not apply to the node's type surfaces unsupported_field.\n\n" +
    "Mutating — paths_hint is the edited scene path. This is a `lighting` group tool — activate the " +
    "group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["node_path", "field", "value", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "The light node to mutate — a scene-tree path (same resolver as node_find). Must resolve to " +
          "a Light3D / Light2D node in the edited scene (use light_create first if it does not exist). " +
          "A non-light node surfaces wrong_node_type.",
      },
      field: {
        type: "string",
        enum: ["color", "energy", "range", "spot_angle", "attenuation", "shadow_enabled"],
        description:
          "The light scalar to set. 'color' (every light), 'energy' (every light, clamped ≥ 0), " +
          "'range' (Omni/Spot3D only, clamped strictly positive), 'spot_angle' (SpotLight3D only, " +
          "clamped [0.01,180] degrees), 'attenuation' (Omni/Spot3D only, clamped ≥ 0), " +
          "'shadow_enabled' (every light).",
      },
      value: {
        description:
          "The value to write. color → {r,g,b[,a]} object or 'r,g,b[,a]' string (0–1 floats); " +
          "energy / range / spot_angle / attenuation → number; shadow_enabled → bool. Re-parsed into " +
          "the field's Godot type.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the edited scene res:// path. Mandatory even when gate is 'off' " +
          "(handler-level guard).",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → save → validate → delta; new errors " +
          "fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the cycle " +
          "(paths_hint is still required).",
      },
    },
    additionalProperties: false,
  },
};
