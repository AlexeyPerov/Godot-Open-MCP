// `godot_open_mcp_baseline_create` tool definition (P15.1).
//
// Runs the offline whole-project scan and writes a baseline JSON file (schema
// v1) for regression tracking. The baseline records the severity summary, the
// per-rule issue keys, and the `ciExcludedRules` that the offline scanner
// cannot detect (so a consumer never mistakes "absent offline" for "clean").
//
// Adapted from Unity Open MCP's `mcp-server/src/tools/baseline-create.ts`
// (copy for the schema shape + default `CI/…-baseline.json` path; adapt the
// route to `offline` because Godot has no headless editor). The tool definition
// is catalog metadata only; the handler lives in `tool-router.ts` and calls
// `scanProjectOffline` + `buildBaseline` + `saveBaseline`.
//
// Route: always `offline` — never probes the bridge, never spawns Godot.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const baselineCreate: Tool = {
  name: "godot_open_mcp_baseline_create",
  description:
    "Run a full offline scan and save a baseline JSON file (schema v1) for " +
    "regression tracking. The baseline records the severity summary, the " +
    "per-rule issue keys, and the ciExcludedRules the offline scanner cannot " +
    "detect. Works offline by scanning `.tscn`/`.tres` text on disk — no " +
    "Godot editor required. Commit the resulting file (convention: " +
    "`CI/godot-open-mcp-baseline.json`) and compare future scans against it " +
    "with godot_open_mcp_regression_check.",
  inputSchema: {
    type: "object",
    properties: {
      baseline_path: {
        type: "string",
        default: "CI/godot-open-mcp-baseline.json",
        description:
          "Path for the baseline file, relative to the project root or " +
          "absolute. Parent directories are created when missing.",
      },
      platform_profile: {
        enum: ["mobile", "console", "desktop"],
        default: "desktop",
        description:
          "Platform profile recorded in the baseline metadata (informational; " +
          "does not change which rules run).",
      },
    },
    additionalProperties: false,
  },
};
