// `godot_open_mcp_reserialize` tool definition (P17.2).
//
// Mutating (default gate "enforce"). Round-trips writable Godot assets
// (.tres/.tscn/.res) through ResourceLoader.Load + ResourceSaver.Save so on-disk drift
// (hand-edits, stale format, missing fields) is normalized through the first-party serializer.
// The handler lives in the bridge (POST /tools/godot_open_mcp_reserialize); this file is the
// catalog metadata only — name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/reserialize.ts (adapt fidelity): Unity's
// path-list input + gate surface carry over; Unity's AssetDatabase.ForceReserializeAssets becomes
// Godot's load+save round-trip. Unity's all-or-nothing pre-flight becomes a per-path status report
// (the P17.2 spec asks for per-path success/failure + normalization changes). Folder expansion is
// greenfield for Godot (Unity is explicit-paths-only).
//
// Imported/generated resources (with a .import sidecar) are rejected per-path with not_writable —
// only hand-authored .tres/.tscn/.res files are reserializable. Each result reports whether the
// round-trip actually changed the on-disk bytes (`normalized: true/false`) so the agent knows
// whether the asset was already canonical.
//
// The result envelope is { results, summary } plus the standard gate block. `results` carries one
// entry per requested/rejected path with { path, status, normalized?, error? }; `summary` rolls up
// totals. Use read_asset (P17.1) to inspect a reserialized asset afterward.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const reserialize: Tool = {
  name: "godot_open_mcp_reserialize",
  description:
    "Round-trip writable Godot assets (.tres/.tscn/.res) through ResourceLoader + ResourceSaver " +
    "to normalize on-disk format — collapses hand-edits, stale format versions, and missing " +
    "fields into the canonical serialized form. Mutating — runs the full gate cycle " +
    "(checkpoint → reserialize → validate → delta) by default. Accepts a list of res:// file " +
    "paths and/or folders (a folder is walked recursively for writable resource files). " +
    "Imported/generated resources (with a .import sidecar) are skipped per-path as not_writable. " +
    "paths_hint is mandatory and must contain every target file being reserialized (folders must " +
    "be expanded into their files in paths_hint — list_assets gives the contents); the gate " +
    "validates exactly those paths afterward. Each result reports a per-path status and whether " +
    "the round-trip actually changed the on-disk bytes (normalized flag).",
  inputSchema: {
    type: "object",
    required: ["paths", "paths_hint"],
    properties: {
      paths: {
        type: "array",
        minItems: 1,
        items: { type: "string" },
        description:
          "Non-empty array of res:// paths to reserialize. Each entry is either a writable " +
          "resource file (.tres/.tscn/.res) or a folder (walked recursively for those types). " +
          "uid:// identifiers are resolved to their res:// path first. Imported resources and " +
          "unsupported extensions are reported per-path (not_writable / unsupported) rather than " +
          "aborting the call. Whole-project reserialize is not supported — enumerate explicitly.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — must contain every expanded target file. When a folder is passed in " +
          "paths, list its files here too (use list_assets to enumerate). The gate validates only " +
          "these paths after the round-trip. Mandatory even when gate is 'off' (handler-level " +
          "guard); a missing target fails with paths_hint_required listing what to add.",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → reserialize → validate → delta; new " +
          "errors fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the " +
          "cycle (paths_hint is still required).",
      },
    },
    additionalProperties: false,
  },
};
