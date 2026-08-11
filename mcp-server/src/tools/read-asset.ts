// `godot_open_mcp_read_asset` tool definition (P17.1).
//
// Offline, token-budgeted structured summary of any `.tres`/`.tscn`/
// `.gdshader`/`.import` (or any small text asset). Default `profile: "compact"`
// returns the headline only (kind / type / counts / integrity signals);
// balanced expands the per-kind roster (ext_resources / nodes / uniforms /
// properties); full raises the inline cap. Page large rosters with
// `page_size`/`cursor`. Integrity signals surface `missing_reference`
// (`[ext_resource]` path not on disk), `orphaned_import` (`.import` source
// gone), and `parse_failure` (unreadable / malformed).
//
// Adapted from Unity Open MCP's mcp-server/src/tools/read-asset.ts (adapt
// fidelity for the profile + paging schema; adapt for Godot asset kinds).
// Route: always `offline` — never probes the bridge (P17 phase decision).

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const readAsset: Tool = {
  name: "godot_open_mcp_read_asset",
  description:
    "Read a Godot asset as a compact, token-budgeted structured summary " +
    "(kind + type + counts + integrity signals). Default " +
    "(`profile: 'compact'`) returns the headline only; raise to " +
    "balanced/full to expand the per-kind roster (ext_resources for " +
    "scenes/resources, nodes for scenes, uniforms for shaders, properties " +
    "for resources/imports). Page large rosters with page_size/cursor. " +
    "Integrity signals flag missing `[ext_resource]` references, orphaned " +
    "`.import` sources, and parse failures. Works offline by parsing " +
    "`.tres`/`.tscn`/`.gdshader`/`.import` text on disk — no Godot editor " +
    "required. Use to understand an asset before mutating it, or to triage " +
    "why a scene/resource will not load cleanly.",
  inputSchema: {
    type: "object",
    properties: {
      asset_path: {
        type: "string",
        description:
          "Canonical `res://` path of the asset to read (e.g. " +
          "`res://Resources/DemoData.tres`, `res://Scenes/Main.tscn`, " +
          "`res://Shaders/Water.gdshader`). Required.",
      },
      profile: {
        enum: ["compact", "balanced", "full"],
        default: "compact",
        description:
          "Token-budget output profile. 'compact' (default) = headline " +
          "(kind/type/counts/integrity) only. 'balanced' = expand the " +
          "per-kind roster (ext_resources / nodes / uniforms / properties) " +
          "with an inline cap. 'full' = raise the inline cap (page with " +
          "page_size for large rosters). An explicit profile wins over the " +
          "legacy `detail` param.",
      },
      page_size: {
        type: "integer",
        minimum: 1,
        description:
          "Page the per-kind roster (balanced/full — nodes for scenes, " +
          "uniforms for shaders, properties for resources). When set, the " +
          "response carries a `pagination` block with a `next_cursor` to " +
          "resume. Omit to receive the inline-capped roster in one response.",
      },
      cursor: {
        type: "string",
        description:
          "Opaque continuation token from a previous response's " +
          "`pagination.next_cursor`. Pages the per-kind roster.",
      },
      detail: {
        enum: ["summary", "normal", "verbose"],
        default: "summary",
        description:
          "Legacy compression level (alias for `profile`: summary=compact, " +
          "normal=balanced, verbose=full). Prefer `profile`; ignored when " +
          "`profile` is set.",
      },
      max_per_section: {
        type: "integer",
        description:
          "Cap on the per-kind roster before paging takes over (balanced/" +
          "full). Omit to use the profile default (balanced: 40, full: 200).",
      },
    },
    required: ["asset_path"],
    additionalProperties: false,
  },
};
