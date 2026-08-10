// `godot_open_mcp_material_create` tool definition (P16.2).
//
// Instantiates a Godot material (`StandardMaterial3D` / `ORMMaterial3D` /
// `ShaderMaterial`) via `ClassDB.Instantiate` and persists it at a new `res://`
// destination through `ResourceSaver.Save`. The handler lives in the bridge
// (POST /tools/godot_open_mcp_material_create); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Adapted from Unity Open MCP's `material_create` (TypedTools/MaterialTools.cs —
// adapt fidelity): same create + save shape, but Godot has three first-class
// material families (standard / orm / shader) vs Unity's single Material +
// Shader.Find, and the shader kind takes a res:// .gdshader path instead of a
// Unity shader name. Unity render-queue / SRP-batcher keyword APIs are
// intentionally NOT ported.
//
// This is a `materials` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "materials" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const materialCreate: Tool = {
  name: "godot_open_mcp_material_create",
  description:
    "Create a new Godot material (.tres/.res) by instantiating StandardMaterial3D, ORMMaterial3D, " +
    "or ShaderMaterial via ClassDB and persisting it through ResourceSaver. Mutating — runs the full " +
    "gate cycle (checkpoint → save → validate → delta) by default. The destination must not already " +
    "exist unless overwrite:true is passed (a material at the path surfaces material_exists rather " +
    "than silently re-GUIDing it, which would break references).\n\n" +
    "Kinds: 'standard' (StandardMaterial3D — the PBR default), 'orm' (ORMMaterial3D), 'shader' " +
    "(ShaderMaterial — requires shader_path pointing at a res:// .gdshader, assigned at create time " +
    "so the shader's uniforms become immediately settable via material_set_property).\n\n" +
    "This is a `materials` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["resource_path", "kind", "paths_hint"],
    properties: {
      resource_path: {
        type: "string",
        description:
          "Required: new res:// destination for the material file. Must end with .tres or .res. " +
          "Must not already exist unless overwrite:true is passed.",
      },
      kind: {
        type: "string",
        enum: ["standard", "orm", "shader"],
        description:
          "Which material family to instantiate. 'standard' → StandardMaterial3D (PBR material with " +
          "albedo/metallic/roughness). 'orm' → ORMMaterial3D. 'shader' → ShaderMaterial (requires " +
          "shader_path pointing at a res:// .gdshader).",
      },
      shader_path: {
        type: "string",
        description:
          "Required when kind is 'shader': a res:// .gdshader to assign to the new ShaderMaterial at " +
          "create time (so the shader's uniforms become settable). Ignored for standard/orm kinds.",
      },
      overwrite: {
        type: "boolean",
        default: false,
        description:
          "When true, replace a material that already exists at resource_path (the .tres is re-saved " +
          "with a fresh instance of the requested kind). Default false — a taken path surfaces " +
          "material_exists. Re-saving re-GUIDs the resource, so use resource_modify instead when you " +
          "only need to change properties.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — must contain the destination resource_path. The gate validates only " +
          "these paths after the save. Mandatory even when gate is 'off' (handler-level guard).",
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
