// `godot_open_mcp_resource_modify` tool definition (P4.2).
//
// Mutating (default gate "enforce"). Loads a .tres/.res resource, applies validated property patches
// through the first-party ResourcePropertyPatcher, and persists the result via ResourceSaver. The
// handler lives in the bridge (POST /tools/godot_open_mcp_resource_modify); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/object-modify.ts (adapt fidelity): Unity's
// reflection-based field patches become Godot property-path assignments (Set/index/dict-key). The
// disallowed ReflectorNet patch model from the Godot-MCP reference is replaced by a first-party
// ResourcePropertyPatcher (greenfield), with atomic all-or-nothing validation replacing the
// Godot-MCP best-effort per-patch log model.
//
// Atomicity: every patch is resolved and its value converted BEFORE any mutation. A single failure
// leaves the resource untouched. No-op detection reports 'no_changes' when all converted values
// already equal the originals (the save is skipped entirely). Imported/generated resources (those
// with a .import sidecar) are rejected with 'resource_not_writable' — only hand-authored .tres/.res
// files are writable.
//
// The property-path grammar is slash-separated: `property`, `nested/property`, `array/[0]`,
// `dictionary/[key]`. The first segment must be a property name. Values are raw JSON tokens
// converted to the target Godot Variant type.
//
// The result envelope is { resource, changed, unchanged, saved } plus the standard gate block.
// `changed` lists patches whose value actually changed; `unchanged` lists no-op patches. `saved`
// is always true on success. Use resource_get_data (P4.1) to read the modified resource back.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const resourceModify: Tool = {
  name: "godot_open_mcp_resource_modify",
  description:
    "Modify writable properties of an existing Godot resource (.tres/.res) through explicit " +
    "property-path assignments, then persist via ResourceSaver. Mutating — runs the full gate " +
    "cycle (checkpoint → modify → validate → delta) by default. All patches are validated " +
    "atomically before any mutation — a single bad path or value aborts with the resource " +
    "untouched. No-op detection reports 'no_changes' (save skipped) when all patches already match. " +
    "Imported/generated resources (with a .import sidecar) are rejected. paths_hint is mandatory " +
    "and must contain the resource path. The property-path grammar is slash-separated: 'property', " +
    "'nested/property', 'array/[0]', 'dictionary/[key]'. Values are raw JSON converted to the " +
    "target Variant type (bool, int, float, string, vectors as [x,y,...], colors as [r,g,b,a], " +
    "resource refs as {\"resource_path\":\"res://...\"}, null to clear).",
  inputSchema: {
    type: "object",
    required: ["resource_path", "patches", "paths_hint"],
    properties: {
      resource_path: {
        type: "string",
        description:
          "Required: canonical res:// path of the resource to modify, or a uid:// identifier " +
          "(mapped to its res:// path first). Must end with .tres or .res.",
      },
      patches: {
        type: "array",
        minItems: 1,
        items: {
          type: "object",
          required: ["path", "value"],
          properties: {
            path: {
              type: "string",
              description:
                "Property path (slash-separated): 'property', 'nested/property', 'array/[0]', " +
                "'dictionary/[key]'. The first segment must be a property name. Each path in the " +
                "array must be unique.",
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
          "Non-empty array of property patches applied in request order. All patches are validated " +
          "atomically — a single bad path/value/duplicate aborts the modify with 'patch_invalid'. " +
          "When every patch normalizes to a value that already matches, the handler reports " +
          "'no_changes' and skips the save.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — must contain the resource_path. The gate validates only these paths " +
          "after the save. Mandatory even when gate is 'off' (handler-level guard).",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → modify → validate → delta; new errors " +
          "fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the cycle " +
          "(paths_hint is still required).",
      },
    },
    additionalProperties: false,
  },
};
