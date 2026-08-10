// `godot_open_mcp_material_set_property` tool definition (P16.2).
//
// Loads a `.tres` material, validates a property exists on it, sets it to a
// re-parsed Variant value, and persists the result through `ResourceSaver.Save`.
// The handler lives in the bridge
// (POST /tools/godot_open_mcp_material_set_property); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Adapted from Unity Open MCP's `material_set_property`
// (TypedTools/MaterialTools.cs — adapt fidelity): same set-one-property shape,
// but the value is re-parsed verbatim into a Godot Variant via `Json.ParseString`
// so any property type (Color / Vector / scalar / texture-ref / enum) round-trips.
// Unity shader-property index validation becomes a `GetPropertyList` membership
// check on the Godot material.
//
// This is a `materials` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "materials" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const materialSetProperty: Tool = {
  name: "godot_open_mcp_material_set_property",
  description:
    "Set a single property on a saved material (.tres) and persist it through ResourceSaver. The " +
    "value is re-parsed into a Godot Variant via Json.ParseString so any property type round-trips: " +
    "a Color is an {r,g,b[,a]} object, a Vector3 an {x,y,z} object, a scalar a number, an enum an " +
    "int, a texture a {\"resource_path\":\"res://...\"} object. Pass null to clear the property.\n\n" +
    "The handler validates the property exists on the material (property_not_found for a typo). For " +
    "a ShaderMaterial, the shader's uniforms become settable after material_set_shader assigns a " +
    ".gdshader. Mutating — paths_hint is the material's .tres path. This is a `materials` group " +
    "tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["resource_path", "property", "value", "paths_hint"],
    properties: {
      resource_path: {
        type: "string",
        description:
          "The material to mutate — a res:// path or uid:// identifier. Must point at a saved " +
          ".tres/.res material.",
      },
      property: {
        type: "string",
        description:
          "The property name to set (Godot property name, e.g. 'albedo_color', 'metallic', " +
          "'roughness', 'emission_enabled', or a shader uniform name on a ShaderMaterial). The " +
          "handler validates it exists via GetPropertyList.",
      },
      value: {
        description:
          "The value to write — any JSON type re-parsed into the property's Variant type. Color → " +
          "{r,g,b[,a]} object; Vector2/3/4 → {x,y[,z[,w]]} object or [x,y,...] array; scalar → " +
          "number; enum → int; bool → true/false; texture → {\"resource_path\":\"res://...\"} " +
          "object; null clears the property. A type that does not match the property fails on Set.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — must contain the material resource_path. Mandatory even when gate is " +
          "'off' (handler-level guard).",
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
