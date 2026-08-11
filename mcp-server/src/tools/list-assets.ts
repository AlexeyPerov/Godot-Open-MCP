// `godot_open_mcp_list_assets` tool definition (P17.1).
//
// Offline compressed `res://` directory listing. Walks the project tree from
// disk (no editor) and returns folder → kind → count with sample file names.
// `.import`/`.uid` sidecars are folded into their parent file (never listed on
// their own). `profile` (compact/balanced/full) controls the sample-per-folder
// cap; page the folder list with `page_size`/`cursor`.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/list-assets.ts (copy
// fidelity for the folder → kind → count shape; adapt for Godot kinds +
// sidecar folding). Route: always `offline` — never probes the bridge.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const listAssets: Tool = {
  name: "godot_open_mcp_list_assets",
  description:
    "List assets under a `res://` folder as a compressed directory listing " +
    "(offline, no editor required). Returns folder → kind → count with " +
    "sample file names. `.import`/`.uid` sidecars are folded into their " +
    "parent file and never listed on their own. Filter by folder and/or " +
    "asset kind. `profile` (compact/balanced/full) controls the per-kind " +
    "sample cap; page large folder lists with page_size/cursor. Useful for " +
    "understanding project structure before drilling into specific assets " +
    "with `read_asset` or `search_assets`.",
  inputSchema: {
    type: "object",
    properties: {
      folder: {
        type: "string",
        default: "res://",
        description:
          "`res://` folder to list under (default `res://` — the whole " +
          "project). Example: `res://Scenes/`.",
      },
      type: {
        type: "string",
        description:
          "Comma-separated kind filter (scene / resource / script / shader " +
          "/ texture / audio / font / other). Example: `scene,resource`. " +
          "Empty = list all kinds.",
      },
      max_per_folder: {
        type: "integer",
        description:
          "Max sample file names accumulated per kind per folder before the " +
          "folder is marked truncated. Controls listing verbosity. Omit to " +
          "use the profile default (compact: 3, balanced: 6, full: 12).",
      },
      profile: {
        enum: ["compact", "balanced", "full"],
        default: "compact",
        description:
          "Token-budget output profile. 'compact' (default) = per-kind " +
          "counts only (samples dropped). 'balanced' = up to 6 samples per " +
          "kind per folder. 'full' = up to 12 samples. An explicit profile " +
          "wins over the legacy `detail` param.",
      },
      page_size: {
        type: "integer",
        minimum: 1,
        description:
          "Page the folder list. When set, the response carries a " +
          "`pagination` block with a `next_cursor` to resume. Omit to " +
          "receive the whole folder list in one response.",
      },
      cursor: {
        type: "string",
        description:
          "Opaque continuation token from a previous response's " +
          "`pagination.next_cursor`. Pages the folder list.",
      },
      detail: {
        enum: ["summary", "normal", "verbose"],
        default: "summary",
        description:
          "Legacy compression level (alias for `profile`: summary=compact, " +
          "normal=balanced, verbose=full). Prefer `profile`; ignored when " +
          "`profile` is set.",
      },
    },
    additionalProperties: false,
  },
};
