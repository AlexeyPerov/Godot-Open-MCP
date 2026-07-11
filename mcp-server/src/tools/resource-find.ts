// `godot_open_mcp_resource_find` tool definition (P4.1).
//
// Read-only (gate-free). Discovers Godot resources (.tres/.res) in the project's res:// filesystem
// by exact path/UID lookup or indexed type search. The handler lives in the bridge (POST
// /tools/godot_open_mcp_resource_find); this file is the catalog metadata only — name / description /
// input schema — advertised to AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/search-assets.ts (adapt fidelity): Unity's
// GUID/asset-path identity becomes canonical Godot res:// path plus optional uid://; Unity's
// AssetDatabase type search becomes Godot's EditorFileSystem importer-metadata scan (which does NOT
// eagerly load every candidate — it reads the import index). The bounded, paged search discipline is
// preserved; only the identity + type-matching primitives change.
//
// Selector precedence: uid > resource_path > type_filter.
//   - uid (priority 1): resolves a single resource via ResourceUid.
//   - resource_path (priority 2): resolves a single resource by exact res:// path (also accepts a
//     uid:// value and maps it first). Returns resource_not_found when the path does not exist.
//   - type_filter (no direct selector): recursive EditorFileSystem scan for files whose
//     importer-assigned type equals or derives from the filter (ClassDB.IsParentClass). Scoped by an
//     optional directory; results sorted by canonical path before paging.
// At least one of the three is required (invalid_request otherwise). Direct lookup returns at most
// one item and ignores search-only options (type_filter, directory, page_size, cursor).
//
// The result envelope is { count, resources: ResourceIdentity[], pagination }. Each ResourceIdentity
// carries resourcePath, uid (nullable), type (nullable). Use resource_get_data to inspect a found
// resource's properties.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const resourceFind: Tool = {
  name: "godot_open_mcp_resource_find",
  description:
    "Find Godot resources (.tres/.res) in the project's res:// filesystem. Read-only (gate-free). " +
    "Two modes: (a) direct lookup by uid or resource_path — returns a single resource's identity " +
    "(path, uid, type) or resource_not_found; (b) indexed type search by type_filter — recursively " +
    "scans the editor filesystem for files whose importer-assigned type equals or derives from the " +
    "filter (no eager load of every candidate), scoped by an optional directory, sorted by path, and " +
    "paged. Selector precedence: uid > resource_path > type_filter — a direct selector resolves one " +
    "resource and ignores search-only options. At least one of the three is required. " +
    "resource_path also accepts a uid:// value (mapped to its res:// path first). Each result carries " +
    "resourcePath, uid, and type; chain into resource_get_data to inspect properties.",
  inputSchema: {
    type: "object",
    properties: {
      uid: {
        type: "string",
        description:
          "Priority 1 direct selector: a 'uid://' identifier. Resolves to a single resource via " +
          "ResourceUid. Takes precedence over resource_path and type_filter.",
      },
      resource_path: {
        type: "string",
        description:
          "Priority 2 direct selector: an exact res:// path to a .tres/.res resource. Also accepts " +
          "a 'uid://' value (mapped to its res:// path first). Ignored when uid is set.",
      },
      type_filter: {
        type: "string",
        description:
          "Search selector: a Godot class/type name (e.g. 'StandardMaterial3D', 'Texture2D', " +
          "'Resource'). Only files whose importer-assigned type equals or derives from it " +
          "(ClassDB.IsParentClass) are returned. Ignored when uid or resource_path is set.",
      },
      directory: {
        type: "string",
        description:
          "Optional res:// directory scope for the type-filtered scan (e.g. 'res://materials/'). " +
          "Defaults to the whole project (res://). Ignored for direct lookups.",
      },
      page_size: {
        type: "integer",
        default: 50,
        minimum: 1,
        maximum: 200,
        description:
          "Search mode: max results per page. Default 50, hard cap 200. The remainder is reported " +
          "via pagination.nextCursor.",
      },
      cursor: {
        type: "string",
        description:
          "Search mode: opaque continuation cursor from a previous response's pagination.nextCursor. " +
          "Omit for the first page.",
      },
    },
    additionalProperties: false,
  },
};
