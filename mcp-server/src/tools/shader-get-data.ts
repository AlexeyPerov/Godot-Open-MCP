// `godot_open_mcp_shader_get_data` tool definition (P16.2).
//
// Loads a `.gdshader` resource, creates a temporary `ShaderMaterial`, assigns the
// shader (so the uniform set materializes as properties on the material), and
// enumerates the uniforms ({name, type}). The temporary material is freed before
// return. The handler lives in the bridge
// (POST /tools/godot_open_mcp_shader_get_data); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Adapted from Unity Open MCP's `shader_get_data`
// (TypedTools/ShaderTools.cs — adapt fidelity): same read-uniforms shape, but
// Godot's uniform system is enumerated via a temporary ShaderMaterial's
// GetPropertyList (the stable, version-portable surface) rather than Unity's
// Shader.GetPropertyCount / GetPropertyName / GetPropertyType reflection. The
// ShaderMaterial parameter list is the same API an agent uses to set uniform
// values via material_set_property, so the names round-trip exactly.
//
// This is a `materials` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "materials" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const shaderGetData: Tool = {
  name: "godot_open_mcp_shader_get_data",
  description:
    "Read a .gdshader's uniforms (names + types). Loads the shader, creates a temporary " +
    "ShaderMaterial to materialize the uniform set as properties, enumerates them via " +
    "GetPropertyList, and frees the temporary material. The uniform names match what " +
    "material_set_property accepts on a ShaderMaterial that has this shader assigned.\n\n" +
    "Accepts a res:// path to a .gdshader. This is a `materials` group tool — activate the group " +
    "with manage_tools first. Read-only.",
  inputSchema: {
    type: "object",
    required: ["shader_path"],
    properties: {
      shader_path: {
        type: "string",
        description:
          "The shader to inspect — a res:// .gdshader path. Returns the uniform names + Variant " +
          "types so an agent can plan material_set_property calls after material_set_shader. A " +
          "shader with compile errors surfaces shader_parse_error (never throws).",
      },
    },
    additionalProperties: false,
  },
};
