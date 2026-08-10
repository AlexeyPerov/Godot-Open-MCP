// `godot_open_mcp_environment_set` tool definition (P16.3).
//
// Creates or replaces the `WorldEnvironment` node's `Environment` resource in
// the currently edited scene. Resolves the WorldEnvironment node (explicit
// `node_path`, the first one in the edited scene, or a fresh one under the root
// when `create_if_missing:true`), loads the Environment `.tres` at
// `environment_path`, assigns it to the node's `Environment` property, and marks
// the scene unsaved. The handler lives in the bridge
// (POST /tools/godot_open_mcp_environment_set); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Greenfield (Godot-specific) — WorldEnvironment + Environment is Godot's scene-
// environment resource model (sky / fog / tonemap / ambient / glow / SSAO /
// etc.). Unity's RenderSettings.skybox / ambientLight covers a subset, but the
// resource model is Godot's own. Use `resource_create` (kind Environment) to
// build the Environment .tres first, then this tool to wire it into the scene.
//
// This is a `lighting` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "lighting" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const environmentSet: Tool = {
  name: "godot_open_mcp_environment_set",
  description:
    "Set or replace the WorldEnvironment node's Environment resource in the currently edited scene. " +
    "Resolves the WorldEnvironment node one of three ways: (1) explicit node_path → that node " +
    "(must be a WorldEnvironment); (2) no node_path → the first WorldEnvironment in the edited " +
    "scene; (3) no node_path + create_if_missing:true → a fresh WorldEnvironment under the scene " +
    "root. Loads the Environment resource at environment_path (a res:// .tres built via " +
    "resource_create, or an existing one), assigns it to the node's Environment property, and " +
    "marks the scene unsaved.\n\n" +
    "The Environment resource carries the scene's sky (ProceduralSkyMaterial / PanoramaSkyMaterial " +
    "/ custom), fog, tonemap, ambient light, glow, SSAO, and other post-processing settings — " +
    "configure those via resource_modify / material_set_property on the Environment .tres. This " +
    "tool only wires the resource into the scene.\n\n" +
    "Mutating — paths_hint is the edited scene path. This is a `lighting` group tool — activate " +
    "the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["environment_path", "paths_hint"],
    properties: {
      environment_path: {
        type: "string",
        description:
          "Required: a res:// Environment .tres to assign. Must point at a saved Environment " +
          "resource (build one with resource_create first if it does not exist). A non-Environment " +
          "resource surfaces wrong_resource_type.",
      },
      node_path: {
        type: "string",
        description:
          "Optional: an explicit WorldEnvironment node to target (scene-tree path, same resolver " +
          "as node_find). When omitted, the handler finds the first WorldEnvironment in the edited " +
          "scene, or (with create_if_missing:true) creates one under the root. A non-WorldEnvironment " +
          "node surfaces wrong_node_type.",
      },
      create_if_missing: {
        type: "boolean",
        default: false,
        description:
          "When true and no WorldEnvironment node exists in the edited scene (and no node_path was " +
          "given), the handler creates a fresh WorldEnvironment under the scene root before " +
          "assigning the Environment resource. Default false — a missing WorldEnvironment surfaces " +
          "environment_node_not_found so the agent decides whether to create one.",
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
