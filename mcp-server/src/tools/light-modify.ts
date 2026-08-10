// `godot_open_mcp_light_modify` tool definition (P16.3).
//
// Bulk-patches multiple allow-listed light scalars on an existing light node in
// one call — an alias of `light_set` for the multi-field case. Resolves the
// node, type-checks it against Light3D / Light2D, then walks the `fields` map
// applying each entry through the same allow-listed + clamped path `light_set`
// uses, accumulating per-field results (applied + errors) so a single bad entry
// does not abort the batch. The handler lives in the bridge
// (POST /tools/godot_open_mcp_light_modify); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over
// stdio ListTools.
//
// Adapted from Unity Open MCP's `light_modify`
// (TypedTools/Extensions/Lighting/LightingTools.cs — adapt fidelity): same
// reflective multi-field patch shape, but the field vocabulary + clamping are
// Godot's (the same allow-list `light_set` enforces). Unity's field-name
// reflection over arbitrary serialized fields is NOT ported — `light_modify`
// only accepts the typed allow-list (color / energy / range / spot_angle /
// attenuation / shadow_enabled); use node_modify for arbitrary properties.
//
// This is a `lighting` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "lighting" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const lightModify: Tool = {
  name: "godot_open_mcp_light_modify",
  description:
    "Bulk-patch multiple allow-listed light scalars on an existing light node (Light3D / Light2D) in " +
    "one call. Walks the `fields` map applying each entry through the same allow-listed + clamped " +
    "path light_set uses, accumulating per-field results (applied + errors) so a single bad entry " +
    "does not abort the batch. Field keys: 'color' / 'energy' / 'range' / 'spot_angle' / " +
    "'attenuation' / 'shadow_enabled' (same vocabulary + clamping as light_set; some fields apply " +
    "only to 3D or omni/spot lights — those entries surface in `errors`).\n\n" +
    "Mutating — paths_hint is the edited scene path. This is a `lighting` group tool — activate the " +
    "group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["node_path", "fields", "paths_hint"],
    properties: {
      node_path: {
        type: "string",
        description:
          "The light node to mutate — a scene-tree path (same resolver as node_find). Must resolve to " +
          "a Light3D / Light2D node in the edited scene (use light_create first if it does not exist). " +
          "A non-light node surfaces wrong_node_type.",
      },
      fields: {
        type: "object",
        description:
          "A map of {field → value} entries to patch. Keys must be one of the allow-listed light " +
          "scalars (color / energy / range / spot_angle / attenuation / shadow_enabled). Values use " +
          "the same shape light_set accepts (color → {r,g,b[,a]} object or 'r,g,b[,a]' string; " +
          "energy / range / spot_angle / attenuation → number; shadow_enabled → bool). An entry with " +
          "an unrecognized field or a field that does not apply to the node's type surfaces in " +
          "`errors` rather than aborting the batch.",
        additionalProperties: true,
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
