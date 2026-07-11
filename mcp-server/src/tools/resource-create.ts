// `godot_open_mcp_resource_create` tool definition (P4.2).
//
// Mutating (default gate "enforce"). Instantiates a Godot Resource subclass via ClassDB, applies
// validated initial properties, and persists it at a new res:// destination through ResourceSaver.
// The handler lives in the bridge (POST /tools/godot_open_mcp_resource_create); this file is the
// catalog metadata only — name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/scriptableobject-create.ts (adapt fidelity):
// Unity's ScriptableObject.CreateInstance + AssetDatabase.CreateAsset pattern is mapped to Godot's
// ClassDB.Instantiate + ResourceSaver.Save. The "fields" array becomes "properties" (Godot
// property-path patches rather than reflection field assignments). The disallowed ReflectorNet
// patch model from the Godot-MCP reference is replaced by a first-party ResourcePropertyPatcher
// (greenfield).
//
// Atomicity: type validation + patch validation run BEFORE any mutation. A single failure leaves
// the in-memory object untouched and no file is written. The destination must not already exist
// (P4.2: no overwrite flag). paths_hint is mandatory and must contain the destination path — this
// is both the gate scope and a handler-level guard that fires even when an agent overrides with
// gate:"off".
//
// The property-path grammar is slash-separated: `property`, `nested/property`, `array/[0]`,
// `dictionary/[key]`. The first segment must be a property name. Values are raw JSON tokens
// converted to the target Godot Variant type (primitives, vectors, colors, resource refs by
// res:// path, nulls).
//
// The result envelope is { resource, changed, unchanged, saved } plus the standard gate block.
// `resource` is a ResourceIdentity (resourcePath, uid, type). `changed` / `unchanged` list the
// property paths that actually changed vs matched the existing value. `saved` is always true on
// success. Use resource_get_data (P4.1) to read the created resource back.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const resourceCreate: Tool = {
  name: "godot_open_mcp_resource_create",
  description:
    "Create a new Godot resource (.tres/.res) by instantiating a Resource subclass via ClassDB and " +
    "persisting it through ResourceSaver. Mutating — runs the full gate cycle (checkpoint → save → " +
    "validate → delta) by default. The destination must not already exist (no overwrite). Optional " +
    "initial properties are validated and applied before the first save (all-or-nothing — a single " +
    "bad patch aborts the create with no file written). paths_hint is mandatory and must contain the " +
    "destination path. The property-path grammar is slash-separated: 'property', 'nested/property', " +
    "'array/[0]', 'dictionary/[key]'. Values are raw JSON converted to the target Variant type " +
    "(bool, int, float, string, vectors as [x,y,...], colors as [r,g,b,a], resource refs as " +
    "{\"resource_path\":\"res://...\"}, null to clear).",
  inputSchema: {
    type: "object",
    required: ["resource_path", "paths_hint"],
    properties: {
      resource_path: {
        type: "string",
        description:
          "Required: new res:// destination for the resource file. Must end with .tres or .res. " +
          "Must not already exist — use resource_modify to change an existing resource.",
      },
      type_class_name: {
        type: "string",
        default: "Resource",
        description:
          "Godot class name to instantiate (default 'Resource'). Must exist in ClassDB, be " +
          "instantiable, and inherit from Resource. Examples: 'Resource', 'StandardMaterial3D', " +
          "'FastNoiseLite', 'Gradient', 'Curve'.",
      },
      properties: {
        type: "array",
        items: {
          type: "object",
          required: ["path", "value"],
          properties: {
            path: {
              type: "string",
              description:
                "Property path (slash-separated): 'property', 'nested/property', 'array/[0]', " +
                "'dictionary/[key]'. The first segment must be a property name.",
            },
            value: {
              description:
                "Raw JSON value converted to the property's Variant type. Primitives (bool/int/" +
                "float/string), vectors ([x,y,...]), colors ([r,g,b,a]), resource refs " +
                "({\"resource_path\":\"res://...\"}), or null to clear. A type mismatch fails " +
                "validation with 'value_type_mismatch'.",
            },
          },
          additionalProperties: false,
        },
        description:
          "Optional initial property assignments applied before the first save. All patches are " +
          "validated atomically — a single bad path or value aborts the create. Each path must be " +
          "unique within the array.",
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
