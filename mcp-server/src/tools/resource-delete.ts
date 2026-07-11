// `godot_open_mcp_resource_delete` tool definition (P4.3).
//
// Mutating (default gate "enforce"). Removes a .tres/.res file (and its .import sidecar when
// present) via DirAccess.RemoveAbsolute. The handler lives in the bridge
// (POST /tools/godot_open_mcp_resource_delete); this file is the catalog metadata only — name /
// description / input schema — advertised to AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/assets-delete.ts (adapt fidelity): Unity's
// AssetDatabase.DeleteAsset (which removes the asset + .meta together) becomes explicit file +
// .import sidecar handling via DirAccess, because Godot has no single engine API that removes both
// in one call. The Godot-MCP reference (Tool_Resource.Delete) provides the DirAccess.RemoveAbsolute
// + sidecar try/finally-scan behavior pattern.
//
// A pre-delete identity snapshot (path/uid/type) is captured BEFORE the file is removed so the
// agent has a durable record of what was deleted. The primary file is removed first, the sidecar
// second. If the sidecar removal fails, the orphan sidecar path is reported in the result — the
// contract never claims atomicity across two OS-level operations. Delete is explicit and immediate;
// there is no soft-delete or recycle bin.
//
// paths_hint is mandatory and must contain the resource path — this is both the gate scope and a
// handler-level guard that fires even when an agent overrides with gate:"off". The known .import
// sidecar path may also be included.
//
// The result envelope is { resource, sidecarDeleted, filesystemScan, deleted } plus the standard
// gate block on success. `resource` is the pre-delete ResourceIdentity snapshot. On partial
// failure (sidecar remains), the error carries { resource, sidecarDeleted, filesystemScan,
// orphanSidecarPath, deleted:false }.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const resourceDelete: Tool = {
  name: "godot_open_mcp_resource_delete",
  description:
    "Delete a Godot resource file (.tres/.res) and its .import sidecar via DirAccess.RemoveAbsolute. " +
    "Mutating — runs the full gate cycle (checkpoint → delete → validate → delta) by default. " +
    "Delete is explicit and immediate (no soft-delete or recycle bin). A pre-delete identity " +
    "snapshot (path/uid/type) is returned so the agent has a record of what was removed. The .import " +
    "sidecar is removed alongside the primary file when present; if the sidecar removal fails, the " +
    "orphan sidecar path is reported (the contract never claims atomicity across two OS-level " +
    "operations). Consider calling resource_find first to check for dependents — references to the " +
    "deleted resource will break. paths_hint is mandatory and must contain the resource path.",
  inputSchema: {
    type: "object",
    required: ["resource_path", "paths_hint"],
    properties: {
      resource_path: {
        type: "string",
        description:
          "Required: canonical res:// path of the resource to delete, or a uid:// identifier " +
          "(mapped to its res:// path first). Must end with .tres or .res and exist on disk.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — must contain the resource_path. The gate validates only these paths " +
          "after the delete (the resource path is now-missing). Mandatory even when gate is 'off' " +
          "(handler-level guard). The known .import sidecar path may also be included.",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → delete → validate → delta; new errors " +
          "fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the cycle " +
          "(paths_hint is still required).",
      },
    },
    additionalProperties: false,
  },
};
