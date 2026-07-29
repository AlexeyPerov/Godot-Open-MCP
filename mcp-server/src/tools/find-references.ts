// `godot_open_mcp_find_references` tool definition (P13.1).
//
// Offline reverse dependency lookup: returns every asset that references a
// given `res://` path or `uid://` handle. Scans `.tscn`/`.tres` for
// `[ext_resource]` / bare `uid://` tokens (optional `.gd`/`preload`/`load`
// literals behind `include_scripts`). Default `profile: "compact"` returns
// counts + byKind/byFolder only; raise to balanced/full for per-asset paths
// (and field locations). Page large result sets with `page_size`/`cursor`.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/find-references.ts (copy
// for the profile + paging schema; adapt for Godot uid:// + res:// identity).
// Route: always `offline` — never probes the bridge (P13 phase decision).

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const findReferences: Tool = {
  name: "godot_open_mcp_find_references",
  description:
    "Reverse dependency lookup for Godot assets. Returns all assets that " +
    "reference the given `res://` path or `uid://` handle. Default " +
    "(`profile: 'compact'`) returns counts grouped by kind/folder; raise to " +
    "balanced/full for the per-asset path list (and verbose field locations). " +
    "Page large result sets with page_size/cursor. Works offline by scanning " +
    "`.tscn`/`.tres` text on disk — no Godot editor required. Use before " +
    "move/delete/rename to see who depends on an asset. Optional " +
    "`include_scripts` also scans `.gd`/`preload`/`load` string literals " +
    "(off by default — noisier and slower).",
  inputSchema: {
    type: "object",
    properties: {
      asset_path: {
        type: "string",
        description:
          "Target asset as a canonical `res://` path (e.g. " +
          "`res://Resources/DemoData.tres`). Provide asset_path OR uid " +
          "(exactly one).",
      },
      uid: {
        type: "string",
        description:
          "Target asset as a `uid://…` handle. Resolved to a path through " +
          "the offline uid index when available; when the uid has no current " +
          "path the result sets `unresolvedUid: true` and still scans for " +
          "that uid token. Provide asset_path OR uid (exactly one).",
      },
      profile: {
        enum: ["compact", "balanced", "full"],
        default: "compact",
        description:
          "Token-budget output profile. 'compact' (default) = counts + " +
          "byKind/byFolder groupings only (no per-asset list). 'balanced' = " +
          "referencing asset paths. 'full' = also includes which fields/" +
          "headers reference the target. An explicit profile wins over the " +
          "legacy `detail` param.",
      },
      page_size: {
        type: "integer",
        minimum: 1,
        description:
          "Page the referencing-assets list (balanced/full). When set, the " +
          "response carries a `pagination` block with a `next_cursor` to " +
          "resume. Omit to receive up to `max_results` entries in one response.",
      },
      cursor: {
        type: "string",
        description:
          "Opaque continuation token from a previous response's " +
          "`pagination.next_cursor`. Pages the referencing-assets list.",
      },
      detail: {
        enum: ["summary", "normal", "verbose"],
        default: "summary",
        description:
          "Legacy compression level (alias for `profile`: summary=compact, " +
          "normal=balanced, verbose=full). Prefer `profile`; ignored when " +
          "`profile` is set.",
      },
      max_results: {
        type: "integer",
        default: 100,
        description:
          "Max referencing assets returned when page_size is omitted. " +
          "Legacy alias of the single-page cap. Callers should pass >= 1; " +
          "the value 0 is a server-internal sentinel meaning 'unlimited " +
          "(for paging)' and is emitted by the server itself when page_size " +
          "is set — never a value a caller needs to pass.",
      },
      max_per_file: {
        type: "integer",
        default: 5,
        description:
          "Full/verbose mode: max field/header locations per referencing file.",
      },
      include_scripts: {
        type: "boolean",
        default: false,
        description:
          "When true, also scan `.gd`/`.cs` files for `preload`/`load`/" +
          "`ResourceLoader.load` string literals pointing at the target. " +
          "Off by default (noisier and slower than the `.tscn`/`.tres` scan).",
      },
    },
    oneOf: [{ required: ["asset_path"] }, { required: ["uid"] }],
    additionalProperties: false,
  },
};
