// `godot_open_mcp_search_assets` tool definition (P17.1).
//
// Offline project-wide asset search. Returns reason-tagged matches so the
// agent knows WHY each asset was returned and which drill-down to run next.
// Default `profile: "compact"` returns counts grouped by kind; balanced/full
// return the per-asset match list with node-type hits and attached scripts.
// Page large result sets with `page_size`/`cursor`.
//
// Criteria (all optional; case-insensitive substrings where applicable):
//   - `name`       — file basename substring.
//   - `kind`       — comma-separated kinds (scene/resource/script/shader/import).
//   - `node_type`  — `.tscn` node `type=` substring (e.g. `Camera3D`).
//   - `script`     — attached script path substring.
//   - `uid`        — `uid://…` token; matches assets that reference it.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/search-assets.ts (adapt
// fidelity for the reason-tagged shape + profile + paging contract; adapt for
// Godot criteria). Route: always `offline` — never probes the bridge.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const searchAssets: Tool = {
  name: "godot_open_mcp_search_assets",
  description:
    "Search the project for Godot assets by file name / kind / node type / " +
    "attached script / referenced uid. Returns reason-tagged matches so an " +
    "agent knows WHY each asset was returned (by_name / by_kind / " +
    "by_node_type / by_script / references_uid) and which drill-down to run " +
    "next. Default (`profile: 'compact'`) returns counts grouped by kind; " +
    "raise to balanced/full for the per-asset match list (with node-type " +
    "hits and attached scripts). Page large result sets with page_size/cursor. " +
    "Works offline by scanning `.tscn`/`.tres`/`.gd`/`.gdshader`/`.import` " +
    "text on disk — no Godot editor required. Use `uid` for a lightweight " +
    "reverse-lookup; use `find_references` for the authoritative per-target " +
    "view with field locations.",
  inputSchema: {
    type: "object",
    properties: {
      name: {
        type: "string",
        description:
          "Case-insensitive substring on file basename (e.g. `Player` " +
          "matches `Player.tscn`, `PlayerController.gd`).",
      },
      kind: {
        type: "string",
        description:
          "Comma-separated kind filter (scene / resource / script / shader " +
          "/ import). Example: `scene,resource`. Empty = search all kinds.",
      },
      node_type: {
        type: "string",
        description:
          "Case-insensitive substring on `.tscn` node `type=` attribute " +
          "(e.g. `Camera3D`, `RigidBody3D`, `CharacterBody`). Scenes only.",
      },
      script: {
        type: "string",
        description:
          "Case-insensitive substring on an attached script path " +
          "(`script = ExtResource(\"…\")` resolved, or an `[ext_resource]` " +
          "`path=` ending in `.gd`/`.cs`).",
      },
      uid: {
        type: "string",
        description:
          "`uid://…` token — matches assets that reference this uid " +
          "(lightweight reverse lookup). The asset's OWN uid is not a match.",
      },
      profile: {
        enum: ["compact", "balanced", "full"],
        default: "compact",
        description:
          "Token-budget output profile. 'compact' (default) = counts + " +
          "byKind only (no per-asset list). 'balanced'/'full' = per-asset " +
          "match list with reasons (+ node-type hits and scripts under full).",
      },
      page_size: {
        type: "integer",
        minimum: 1,
        description:
          "Page the per-asset match list (balanced/full). When set, the " +
          "response carries a `pagination` block with a `next_cursor` to " +
          "resume. Omit to receive up to `max_results` matches in one response.",
      },
      cursor: {
        type: "string",
        description:
          "Opaque continuation token from a previous response's " +
          "`pagination.next_cursor`. Pages the per-asset match list.",
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
          "Max matches accumulated when page_size is omitted. Callers " +
          "should pass >= 1; the value 0 is a server-internal sentinel " +
          "meaning 'unlimited (for paging)' and is emitted by the server " +
          "itself when page_size is set — never a value a caller needs to pass.",
      },
    },
    additionalProperties: false,
  },
};
