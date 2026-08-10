// `godot_open_mcp_material_set_shader` tool definition (P16.2).
//
// Loads a `.tres` `ShaderMaterial`, loads a `.gdshader` resource, assigns the
// shader via `material.Shader = shader`, and persists through `ResourceSaver`.
// After the assignment the shader's uniforms become settable properties,
// reflected by `material_get_properties`. The handler lives in the bridge
// (POST /tools/godot_open_mcp_material_set_shader); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Adapted from Unity Open MCP's `material_set_shader`
// (TypedTools/MaterialTools.cs — adapt fidelity): same assign-shader shape, but
// Godot addresses the shader by a res:// .gdshader path rather than Unity's
// Shader.Find(name), and the target must be a ShaderMaterial (Unity assigns a
// shader to any Material).
//
// This is a `materials` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "materials" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const materialSetShader: Tool = {
  name: "godot_open_mcp_material_set_shader",
  description:
    "Assign a .gdshader to a ShaderMaterial and persist it. Loads the material .tres, type-checks " +
    "it is a ShaderMaterial, loads the shader at shader_path, assigns it via material.Shader = " +
    "shader, and saves. After the assignment the shader's uniforms become settable properties — " +
    "use shader_get_data to enumerate them, then material_set_property to set each uniform.\n\n" +
    "Mutating — paths_hint is the material's .tres path (the .gdshader path may also be included). " +
    "This is a `materials` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["resource_path", "shader_path", "paths_hint"],
    properties: {
      resource_path: {
        type: "string",
        description:
          "The ShaderMaterial to mutate — a res:// path or uid:// identifier. Must point at a saved " +
          ".tres/.res ShaderMaterial (use material_create with kind:'shader' first if it does not " +
          "exist). A non-ShaderMaterial surfaces wrong_resource_type.",
      },
      shader_path: {
        type: "string",
        description:
          "The res:// .gdshader to assign. Must be an existing shader resource. After assignment " +
          "the shader's uniforms appear as settable properties on the material.",
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
