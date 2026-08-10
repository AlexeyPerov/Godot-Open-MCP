// `godot_open_mcp_light_create` tool definition (P16.3).
//
// Creates a Godot light node — DirectionalLight3D / OmniLight3D / SpotLight3D
// (3D) or DirectionalLight2D / PointLight2D (2D) — in the currently edited
// scene, with optional starter scalars (color / energy / range / spot_angle /
// attenuation / shadow_enabled) applied through the same allow-listed + clamped
// path `light_set` uses. The handler lives in the bridge
// (POST /tools/godot_open_mcp_light_create); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Adapted from Unity Open MCP's `light_add`
// (TypedTools/Extensions/Lighting/LightingTools.cs — adapt fidelity): same
// create-with-starter-scalars shape, but Godot lights are Node3D / Node2D
// subclasses (not Unity Light components attached to a GameObject), and the kind
// enum selects the concrete Godot light class. Unity render_mode / cullingMask
// are intentionally NOT ported in the typed surface (Godot has no per-light
// render-mode; light_cull_mask is settable via node_modify).
//
// This is a `lighting` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "lighting" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const lightCreate: Tool = {
  name: "godot_open_mcp_light_create",
  description:
    "Create a Godot light node in the currently edited scene by `kind` and return its NodeData. " +
    "Kinds: 'directional3d' (DirectionalLight3D — sun/moon, no range), 'omni3d' (OmniLight3D — point " +
    "light with range), 'spot3d' (SpotLight3D — cone with range + spot_angle), 'directional2d' " +
    "(DirectionalLight2D), 'point2d' (PointLight2D). Optional starter scalars (color / energy / range " +
    "/ spot_angle / attenuation / shadow_enabled) apply at create time through the same allow-listed + " +
    "clamped path light_set uses; omit a scalar to leave the engine default. The new node's owner is " +
    "the edited scene root; the scene is marked unsaved.\n\n" +
    "Mutating — runs the full gate cycle (checkpoint → save → validate → delta) by default. This is a " +
    "`lighting` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["kind", "paths_hint"],
    properties: {
      kind: {
        type: "string",
        enum: ["directional3d", "omni3d", "spot3d", "directional2d", "point2d"],
        description:
          "Which light node family to instantiate. 'directional3d' → DirectionalLight3D (parallel " +
          "rays, no attenuation/range — models sun/moon). 'omni3d' → OmniLight3D (point light, range + " +
          "attenuation apply). 'spot3d' → SpotLight3D (cone, range + spot_angle + attenuation apply). " +
          "'directional2d' → DirectionalLight2D. 'point2d' → PointLight2D.",
      },
      name: {
        type: "string",
        description:
          "Optional name for the new light node. When omitted, Godot assigns a default name for the " +
          "type (e.g. 'DirectionalLight3D').",
      },
      parent_node_path: {
        type: "string",
        description:
          "Optional scene-tree path of the parent Node, relative to the edited scene root (same " +
          "resolver as node_find / node_create). Defaults to the edited scene root.",
      },
      position: {
        type: "string",
        description:
          "Optional position as 'x,y,z' (3D lights) or 'x,y' (2D lights). Applied to the Node3D / " +
          "Node2D transform. Defaults to the origin.",
      },
      color: {
        type: "string",
        description:
          "Optional light color as 'r,g,b[,a]' (0–1 floats). Applied via Light3D.LightColor / " +
          "Light2D.Color. Defaults to white (1,1,1).",
      },
      energy: {
        type: "number",
        description:
          "Optional light energy/intensity (float, clamped to ≥ 0). Applied via Light3D.LightEnergy / " +
          "Light2D.Energy. A negative value is clamped to 0. Defaults to 1.",
      },
      range: {
        type: "number",
        description:
          "Optional range (float, clamped strictly positive). Omni/Spot3D only — applied via " +
          "OmniLight3D.OmniRange / SpotLight3D.SpotRange. Ignored for directional lights. Defaults " +
          "to the engine default (5.0 for 3D).",
      },
      spot_angle: {
        type: "number",
        description:
          "Optional spot angle in degrees (float, clamped to [0.01, 180]). SpotLight3D only — applied " +
          "via SpotLight3D.SpotAngle. Ignored for other lights. Defaults to 45.",
      },
      attenuation: {
        type: "number",
        description:
          "Optional attenuation (float, clamped to ≥ 0). Omni/Spot3D only — applied via " +
          "OmniLight3D.OmniAttenuation / SpotLight3D.SpotAttenuation. Ignored for directional lights.",
      },
      shadow_enabled: {
        type: "boolean",
        description:
          "Optional shadow toggle (bool). Applied via Light3D.ShadowEnabled / Light2D.ShadowEnabled. " +
          "Defaults to the engine default (true for 3D, false for 2D).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the edited scene res:// path. The gate validates only these paths after " +
          "the mutation. Mandatory even when gate is 'off' (handler-level guard).",
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
