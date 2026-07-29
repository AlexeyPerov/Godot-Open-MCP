// `godot_open_mcp_dependencies` tool definition (P13.2).
//
// Offline forward + reverse dependency lookup with optional transitive impact
// analysis. Forward edges come from the target file's `[ext_resource]`
// headers; reverse edges reuse P13.1 find_references. Always offline.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/dependencies.ts (adapt
// for Godot uid:// + res:// identity and always-offline routing).

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const dependencies: Tool = {
  name: "godot_open_mcp_dependencies",
  description:
    "Forward + reverse dependency lookup for Godot assets. Returns what the " +
    "target depends on (its `[ext_resource]` header declarations), what " +
    "depends on it (reverse edges), broken forward references, optional " +
    "dependency cycles, and — when `include_impact: true` — a bounded " +
    "transitive reverse closure ('what breaks if I delete/move this?'). " +
    "Works offline by scanning `.tscn`/`.tres` text on disk — no Godot " +
    "editor required. Use before move/delete/rename to understand both " +
    "upstream and downstream coupling.",
  inputSchema: {
    type: "object",
    properties: {
      asset_path: {
        type: "string",
        description:
          "Target asset as a canonical `res://` path. Provide asset_path OR " +
          "uid (exactly one).",
      },
      uid: {
        type: "string",
        description:
          "Target asset as a `uid://…` handle. Provide asset_path OR uid " +
          "(exactly one).",
      },
      detail: {
        enum: ["summary", "normal"],
        default: "normal",
        description:
          "Output compression. `summary` = counts only (no edge rosters). " +
          "`normal` = full forward + reverse edge lists.",
      },
      max_results: {
        type: "integer",
        default: 100,
        description:
          "Cap the reverse-dependencies roster (forward edges are never capped).",
      },
      include_impact: {
        type: "boolean",
        default: false,
        description:
          "When true, include the transitive reverse closure with per-node hop " +
          "depth. Bounded by `max_impact_depth`. Offline-only (expensive BFS).",
      },
      max_impact_depth: {
        type: "integer",
        minimum: 1,
        maximum: 20,
        default: 5,
        description:
          "Max hop depth for the impact BFS when `include_impact` is true. " +
          "Sets `impact.truncated` when the frontier is non-empty at this bound.",
      },
    },
    oneOf: [{ required: ["asset_path"] }, { required: ["uid"] }],
    additionalProperties: false,
  },
};
