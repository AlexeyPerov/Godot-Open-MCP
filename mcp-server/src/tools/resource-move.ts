// `godot_open_mcp_resource_move` tool definition (P4.3).
//
// Mutating (default gate "enforce"). Moves a .tres/.res file (and its .import sidecar when present)
// to a new res:// destination via DirAccess.RenameAbsolute. The handler lives in the bridge
// (POST /tools/godot_open_mcp_resource_move); this file is the catalog metadata only — name /
// description / input schema — advertised to AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/assets-move.ts (adapt fidelity): Unity's
// AssetDatabase.MoveAsset (which moves the asset + .meta atomically and preserves the GUID) becomes
// explicit file + .import sidecar handling via DirAccess, because Godot has no single engine API
// that moves both in one call. The Godot-MCP reference (Tool_Resource.Move) provides the DirAccess
// + sidecar try/finally-scan behavior pattern.
//
// No reference rewriting: hard-coded res:// references in other text assets are NOT rewritten.
// UID-based references are expected to remain stable where Godot supports them. Use resource_find /
// find_references to check dependents before moving.
//
// Atomicity: preflight (same-path, existence, collision, extension) runs before any mutation. The
// primary file moves first, the sidecar second. If the sidecar move fails, a rollback of the
// primary file is attempted and the result exposes the observed final state. The contract never
// claims atomicity across two OS-level operations. paths_hint is mandatory and must contain BOTH
// the source and destination paths — this is both the gate scope and a handler-level guard that
// fires even when an agent overrides with gate:"off".
//
// The result envelope is { before, after, sidecarMoved, filesystemScan, moved } plus the standard
// gate block on success. On partial failure, the error carries { before, sidecarMoved,
// filesystemScan, sourceExistsAfter, destinationExistsAfter, rolledBack, moved:false } so the agent
// can recover. `before`/`after` are ResourceIdentity objects (resourcePath, uid, type).
// `filesystemScan` reports whether the editor filesystem scan settled. `moved` is true on success.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const resourceMove: Tool = {
  name: "godot_open_mcp_resource_move",
  description:
    "Move a Godot resource file (.tres/.res) and its .import sidecar to a new res:// destination " +
    "via DirAccess.RenameAbsolute. Mutating — runs the full gate cycle (checkpoint → move → " +
    "validate → delta) by default. The destination must not already exist (no overwrite). The " +
    "destination parent directory is created when missing. The .import sidecar is moved alongside " +
    "the primary file when present; if the sidecar move fails, a rollback of the primary is " +
    "attempted and the observed final state is reported (the contract never claims atomicity across " +
    "two OS-level operations). Hard-coded res:// references in other resources are NOT rewritten — " +
    "UID-based references remain stable where Godot supports them. paths_hint is mandatory and must " +
    "contain BOTH the source and destination paths.",
  inputSchema: {
    type: "object",
    required: ["source_path", "destination_path", "paths_hint"],
    properties: {
      source_path: {
        type: "string",
        description:
          "Required: canonical res:// path of the resource to move, or a uid:// identifier " +
          "(mapped to its res:// path first). Must end with .tres or .res and exist on disk.",
      },
      destination_path: {
        type: "string",
        description:
          "Required: new res:// destination for the resource file. Must end with .tres or .res " +
          "(extension changes are rejected). Must not already exist — resource_move does not " +
          "overwrite. The destination parent directory is created when missing.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — must contain BOTH the normalized source_path and destination_path. " +
          "The gate validates only these paths after the move (the source path is now-missing, the " +
          "destination is now-present). Mandatory even when gate is 'off' (handler-level guard).",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → move → validate → delta; new errors " +
          "fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the cycle " +
          "(paths_hint is still required).",
      },
    },
    additionalProperties: false,
  },
};
