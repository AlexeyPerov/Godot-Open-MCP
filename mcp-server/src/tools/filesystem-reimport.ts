// `godot_open_mcp_filesystem_reimport` tool definition (P4.4).
//
// Mutating (default gate "enforce"). Reimports specific res:// files via
// EditorFileSystem.ReimportFiles, or triggers a full EditorFileSystem.Scan when no files are given.
// The handler lives in the bridge (POST /tools/godot_open_mcp_filesystem_reimport); this file is the
// catalog metadata only — name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/assets-refresh.ts (adapt fidelity): Unity's
// AssetDatabase.Refresh becomes Godot's EditorFileSystem.ReimportFiles (exact-file) or Scan
// (full-project). The Godot-MCP reference (Tool_FileSystem.Reimport) provides the prime-then-drain
// settle behavior pattern for the asynchronous Scan().
//
// Two modes:
//   - files (exact reimport): pass a non-empty 'files' array of res:// paths. The entire list is
//     validated and normalized BEFORE any file is touched — a single bad/missing entry is a clean
//     error (invalid_path / file_not_found), never a partial effect. Duplicates are removed while
//     preserving first occurrence.
//   - full scan: omit 'files' (or pass an empty array). Triggers EditorFileSystem.Scan to pick up
//     added/removed/changed files. Requires explicit paths_hint: ["res://"] — there is no implicit
//     whole-project gate fallback.
//
// The call blocks until the import pipeline settles (bounded by timeout_ms). A timeout is a
// SUCCESSFUL request with settle.settled:false — the tool never falsely claims completion. The
// settle status reports whether the scan started, whether it settled, the scanning progress (when
// busy), the elapsed settle time, and a reason on timeout. Call filesystem_list / resource_find
// afterwards to observe the post-scan state.
//
// paths_hint is mandatory: the exact files (must contain every requested file), or ["res://"] for a
// full scan. This is both the gate scope and a handler-level guard that fires even when an agent
// overrides with gate:"off".

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const filesystemReimport: Tool = {
  name: "godot_open_mcp_filesystem_reimport",
  description:
    "Reimport specific res:// files via EditorFileSystem.ReimportFiles, or trigger a full " +
    "EditorFileSystem.Scan when no files are given. Mutating — runs the full gate cycle " +
    "(checkpoint → reimport → validate → delta) by default. Two modes: (a) pass a non-empty " +
    "'files' array to reimport exactly those files — the entire list is validated first, so a " +
    "single bad/missing entry is a clean error, never a partial effect; (b) omit 'files' (or pass " +
    "empty) to run a full scan that picks up added/removed/changed files — this requires explicit " +
    "paths_hint: [\"res://\"]. The call blocks until the import pipeline settles (bounded by " +
    "timeout_ms). A timeout is a SUCCESSFUL request with settle.settled:false — the tool never " +
    "falsely claims completion; call filesystem_list / resource_find afterwards to observe the " +
    "post-scan state. paths_hint is mandatory: the exact files (must contain every requested file) " +
    "or [\"res://\"] for a full scan.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      files: {
        type: "array",
        items: { type: "string" },
        description:
          "Optional list of res:// file paths to reimport exactly. When omitted or empty, a full " +
          "filesystem scan is run instead. Every file must exist and be a valid res:// path (no " +
          "traversal, no directory). Duplicates are removed while preserving first occurrence.",
      },
      timeout_ms: {
        type: "integer",
        default: 5000,
        minimum: 1000,
        maximum: 60000,
        description:
          "Bounded settle timeout in milliseconds. The handler waits at most this long for the " +
          "import pipeline to start and drain. Default 5000, clamped to [1000, 60000]. A timeout " +
          "yields settle.settled:false (not an error).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the exact files (must contain every requested file), or [\"res://\"] " +
          "for an explicit full scan. Mandatory even when gate is 'off' (handler-level guard). " +
          "There is no implicit whole-project gate fallback.",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → reimport → validate → delta; new " +
          "errors fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the " +
          "cycle (paths_hint is still required).",
      },
    },
    additionalProperties: false,
  },
};
