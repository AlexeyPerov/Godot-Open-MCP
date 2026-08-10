// `godot_open_mcp_material_get_properties` tool definition (P16.2).
//
// Loads a `.tres` material and enumerates its property list (names + types +
// current values, serialized to JSON by Godot's own `Json.Stringify` so every
// Variant type — Color, Vector2/3, texture refs — round-trips). The handler
// lives in the bridge (POST /tools/godot_open_mcp_material_get_properties); this
// file is the catalog metadata only — name / description / input schema —
// advertised to AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's `material_get_properties`
// (TypedTools/MaterialTools.cs — adapt fidelity): same list-properties shape,
// but the property vocabulary is Godot's (albedo_color / metallic / roughness /
// emission / etc. on StandardMaterial3D; shader uniforms on ShaderMaterial) and
// inspector-only entries (category/group annotations) are filtered by their
// usage flags.
//
// This is a `materials` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "materials" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const materialGetProperties: Tool = {
  name: "godot_open_mcp_material_get_properties",
  description:
    "Read a saved material's properties (names + types + current values). Loads the .tres, walks " +
    "GetPropertyList, and serializes each value to JSON via Godot's own Json.Stringify so every " +
    "Variant type (Color, Vector2/3, texture refs) round-trips. For a ShaderMaterial, the shader's " +
    "uniforms appear as settable properties once a shader is assigned (material_set_shader).\n\n" +
    "Accepts a res:// path or uid:// identifier. This is a `materials` group tool — activate the " +
    "group with manage_tools first. Read-only.",
  inputSchema: {
    type: "object",
    required: ["resource_path"],
    properties: {
      resource_path: {
        type: "string",
        description:
          "The material to inspect — a res:// path or uid:// identifier. Must point at a saved " +
          ".tres/.res material (StandardMaterial3D / ORMMaterial3D / ShaderMaterial). Use " +
          "material_create first if the material does not yet exist.",
      },
    },
    additionalProperties: false,
  },
};
