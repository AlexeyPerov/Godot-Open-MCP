// `godot_open_mcp_reflection_method_find` tool definition (P5.1).
//
// Read-only member discovery across loaded Godot/.NET assemblies (gate-free, group reflection).
// The handler lives in the bridge (POST /tools/godot_open_mcp_reflection_method_find); this file is
// the catalog metadata only — name / description / input schema — advertised to AI clients over stdio
// ListTools. CallTool routes through LiveClient → POST, same as the other live tools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/find-members.ts (adapt fidelity): the same
// query / kind / assembly_filter / include_signatures / max_results surface and the same structured
// member fields (returnType, parameters[], isStatic, isGeneric, genericParameters[] for methods;
// propertyType, canRead, canWrite for properties). The Godot-specific deltas:
//   - `include_godot_editor` replaces Unity's `include_unity_editor` (filters GodotSharpEditor /
//     *Editor assemblies instead of UnityEditor.* / Unity.*Editor).
//   - `type_name` is added as an optional single-type drill-down (faster than a broad query when the
//     declaring type is already known; enumerates only that type's declared members).
//   - The Unity `kind` enum is unchanged (type | method | property | all); overloads are listed
//     separately so an agent can pick one for `reflection_method_call`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const reflectionMethodFind: Tool = {
  name: "godot_open_mcp_reflection_method_find",
  description:
    "Discover C# types, methods, and properties across loaded Godot/.NET assemblies so an agent " +
    "can plan reflection_method_call invocations against the actually-installed assemblies instead " +
    "of hallucinating signatures. Read-only (gate-free). Token-bounded: max_results caps the " +
    "returned list and truncated reports how many additional matches were dropped. Each member " +
    "carries a flat signature string AND structured fields (returnType, parameters[], isStatic, " +
    "isGeneric, genericParameters[] for methods; propertyType, canRead, canWrite for properties) " +
    "so an agent can pick a specific overload and call it. When query matches a method with " +
    "overloads, every overload is listed separately. Pass include_signatures:false for a lighter " +
    "names-only payload. Set type_name to drill into a single declaring type (faster than a broad " +
    "query when the type is known). Defaults match Unity Open MCP's find_members so an agent " +
    "migrating between the projects sees the same discovery bound.",
  inputSchema: {
    type: "object",
    properties: {
      query: {
        type: "string",
        description:
          "Substring filter (case-insensitive) matched against type names, full names, and member " +
          "names. Empty/unset still scans (bounded by max_results) and reports truncated.",
      },
      kind: {
        type: "string",
        enum: ["type", "method", "property", "all"],
        default: "all",
        description: "Member-kind filter. An unrecognized value normalizes to 'all'.",
      },
      assembly_filter: {
        type: "string",
        description:
          "Assembly simple-name contains filter (case-insensitive). When set, it wins over " +
          "include_godot_editor / include_project (both are ignored).",
      },
      include_godot_editor: {
        type: "boolean",
        default: true,
        description:
          "Include Godot editor assemblies (names starting with GodotSharpEditor or Godot.*Editor). " +
          "Default true. Replaces Unity's include_unity_editor.",
      },
      include_project: {
        type: "boolean",
        default: true,
        description:
          "Include the game/scripts assembly (the one assembly not prefixed by a framework/engine " +
          "root). Default true.",
      },
      include_signatures: {
        type: "boolean",
        default: true,
        description:
          "Include the flat signature string and structured parameter/generic metadata on each " +
          "member (default). Set false for a lighter names-only payload.",
      },
      type_name: {
        type: "string",
        description:
          "Optional: limit the member enumeration to a single declaring type (full or simple name). " +
          "When set, query is still applied to the type's members. Faster drill-down after a type hit.",
      },
      max_results: {
        type: "integer",
        default: 50,
        minimum: 1,
        maximum: 200,
        description:
          "Max members returned. Additional matches are counted in 'truncated' so an agent knows " +
          "whether to refine the query.",
      },
    },
    additionalProperties: false,
  },
};
