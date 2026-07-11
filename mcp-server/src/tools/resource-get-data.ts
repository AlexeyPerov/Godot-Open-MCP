// `godot_open_mcp_resource_get_data` tool definition (P4.1).
//
// Read-only (gate-free). Loads a Godot resource (.tres/.res) and returns a bounded, cycle-safe
// property tree. The handler lives in the bridge (POST
// /tools/godot_open_mcp_resource_get_data); this file is the catalog metadata only — name /
// description / input schema — advertised to AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/read-asset.ts (adapt fidelity): Unity's
// compact/balanced/full profile drill-down concept is preserved, but the serialization is Godot
// Variant-based and cycle-safe via instance-ID visited sets. The disallowed ReflectorNet serializer
// from the Godot-MCP reference is replaced by a first-party GodotPropertySerializer (greenfield).
//
// The profile axis maps to a recursion depth:
//   - compact (default): top-level properties only (depth 0).
//   - balanced: one level of nesting (depth 2 — a resource property's direct children expand).
//   - full: walk the whole bounded tree (hard cap depth 6).
// max_depth overrides the profile default (clamped to [0, 6]). collection_page_size bounds how many
// items an Array/Dictionary emits before clipping (the remainder is reported in truncationReasons).
// property_path drills into a specific subtree (e.g. 'albedo_color').
//
// Object references that would create a cycle or an unbounded graph are represented by a descriptive
// reference leaf (res:// path + uid when available) and never blindly traversed — process-local
// instance IDs are NOT exposed as durable resource identity.
//
// The result envelope is { identity, profile, maxDepth, properties, truncation }. `identity` is a
// ResourceIdentity (resourcePath, uid, type). Each property in `properties` is a ResourcePropertyData
// (name, variantType, value, children, referenceDescription, truncationReason). `truncation` reports
// whether any node/collection/string was clipped and why. P7 adds offline (no-editor) behavior later;
// full profile can be expensive — prefer compact first, then drill in with property_path.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const resourceGetData: Tool = {
  name: "godot_open_mcp_resource_get_data",
  description:
    "Load a Godot resource (.tres/.res) and return a bounded, cycle-safe property tree (read-only, " +
    "gate-free). Identify the resource with resource_path (a res:// path, or a uid:// which is " +
    "mapped to its path first). Use resource_find to locate it first. The profile axis controls " +
    "recursion depth: compact (default, top-level only), balanced (one level of nesting), or full " +
    "(whole bounded tree — can be expensive). Object references that would cycle or blow the graph " +
    "are emitted as descriptive reference leaves (res:// path + uid), never blindly traversed. The " +
    "result carries identity (path/uid/type), the property tree, and truncation metadata. Prefer " +
    "compact first, then drill in with property_path or a higher max_depth.",
  inputSchema: {
    type: "object",
    properties: {
      resource_path: {
        type: "string",
        description:
          "Required: canonical res:// path of the resource, or a uid:// identifier (mapped to its " +
          "res:// path first). Must end with .tres or .res.",
      },
      profile: {
        type: "string",
        enum: ["compact", "balanced", "full"],
        default: "compact",
        description:
          "Serialization profile controlling recursion depth: compact (top-level properties only), " +
          "balanced (one level of nesting), full (whole bounded tree, hard cap depth 6). Default " +
          "compact. Overridden by max_depth when set.",
      },
      property_path: {
        type: "string",
        description:
          "Optional drill-down: navigate the property tree to this path (slash-separated, e.g. " +
          "'albedo_color' or 'metadata/player') and serialize just that subtree instead of the whole " +
          "resource.",
      },
      max_depth: {
        type: "integer",
        minimum: 0,
        maximum: 6,
        description:
          "Optional recursion-depth override (clamped to [0, 6]). When set, takes precedence over " +
          "the profile default.",
      },
      collection_page_size: {
        type: "integer",
        default: 50,
        minimum: 1,
        maximum: 200,
        description:
          "Max items emitted per Array/Dictionary collection before clipping (remainder reported in " +
          "truncationReasons). Default 50, hard cap 200.",
      },
      cursor: {
        type: "string",
        description:
          "Opaque continuation cursor for paging large child collections (future use). Omit for the " +
          "first page.",
      },
    },
    additionalProperties: false,
  },
};
