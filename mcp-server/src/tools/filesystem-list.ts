// `godot_open_mcp_filesystem_list` tool definition (P4.4).
//
// Read-only (gate-free). Lists the immediate children of one res:// directory from the editor's
// indexed filesystem — directories first, then files, each group sorted by name. The handler lives
// in the bridge (POST /tools/godot_open_mcp_filesystem_list); this file is the catalog metadata
// only — name / description / input schema — advertised to AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/list-assets.ts (adapt fidelity): Unity's
// AssetDatabase folder→kind→count listing becomes Godot's EditorFileSystemDirectory one-level walk.
// Unity's GUID/asset-type identity becomes Godot res:// path + optional uid:// + importer-assigned
// resource type. The bounded, paged discipline is preserved; only the identity + indexing
// primitives change. Listing is one level — recursive full-tree listing is deferred to the offline
// project indexer (Phase 7).
//
// No resource is loaded: the type comes from EditorFileSystemDirectory.GetFileType and the uid from
// ResourceLoader.GetResourceUid (both read the import index). The result carries the directory path,
// full-count totals (directoryCount / fileCount across the whole directory), the current page of
// entries, and a paging cursor. A path that is not a res:// directory, contains a parent-traversal
// segment, or names a file is rejected with invalid_path / directory_not_found.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const filesystemList: Tool = {
  name: "godot_open_mcp_filesystem_list",
  description:
    "List the immediate children of one res:// directory from the editor's indexed filesystem. " +
    "Read-only (gate-free). Returns directories first, then files, each group sorted by name. " +
    "Each file entry includes the importer-assigned resource type (e.g. 'StandardMaterial3D', " +
    "'PackedScene') and uid:// when assigned — read straight from the editor filesystem index, so " +
    "no resource is loaded. The result carries full-count totals (directoryCount / fileCount across " +
    "the whole directory) plus a paged entries array. Omit 'path' (or pass 'res://') to list the " +
    "project root. A path that is not a res:// directory, contains a '..' segment, or names a file " +
    "yields invalid_path; an indexed-but-missing directory yields directory_not_found. Listing is " +
    "one level — use this iteratively to descend the tree.",
  inputSchema: {
    type: "object",
    properties: {
      path: {
        type: "string",
        description:
          "Optional res:// directory to list (a trailing slash is optional). Omit or pass 'res://' " +
          "for the project root.",
      },
      page_size: {
        type: "integer",
        default: 100,
        minimum: 1,
        maximum: 500,
        description:
          "Max entries per page (directories + files combined, directories first). Default 100, " +
          "hard cap 500. The remainder is reported via pagination.nextCursor.",
      },
      cursor: {
        type: "string",
        description:
          "Opaque continuation cursor from a previous response's pagination.nextCursor. Omit for " +
          "the first page.",
      },
      include_hidden: {
        type: "boolean",
        default: false,
        description:
          "When true, include hidden entries the editor index exposes. Default false.",
      },
    },
    additionalProperties: false,
  },
};
