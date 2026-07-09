// `godot_open_mcp_checkpoint_create` tool definition (P3.6).
//
// Capture a project-health baseline over res:// paths and stash it in the
// session-scoped CheckpointStore so a later `godot_open_mcp_delta` call can
// compare the post-mutation state against it. The handler lives in the bridge
// (POST /tools/godot_open_mcp_checkpoint_create); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools. CallTool routes through LiveClient → POST.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/checkpoint-create.ts
// (copy for the shape; adapt for the Godot scope note). Intentional deltas:
//   - `paths` is OPTIONAL but strongly recommended. Unlike Unity's whole-project
//     summary fallback, the Godot bridge gate has no whole-project fallback for
//     its own dispatch path. This explicit meta-tool accepts an empty/absent
//     scope (yielding a baseline of nothing), but agents should pass the paths
//     they intend to mutate so the delta is meaningful.
//   - The response carries `checkpointId` (the resume key for `delta`),
//     `timestamp`, and a per-rule `fingerprint` map of errors / warnings /
//     issueKeys[].

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const checkpointCreate: Tool = {
  name: "godot_open_mcp_checkpoint_create",
  description:
    "Capture a project-health baseline over res:// paths and stash it in a session-scoped " +
    "(in-memory) store so a later `godot_open_mcp_delta` call can compute the before/after issue " +
    "delta. This is the explicit form of the gate's checkpoint step. Returns `checkpointId` (the " +
    "opaque resume key to pass to `delta`), `timestamp`, and a per-rule `fingerprint` map of " +
    "`errors` / `warnings` / `issueKeys[]` (canonical `{ruleId}|{severity}|{assetPath}|{issueCode}` " +
    "keys). Pass the res:// paths you intend to mutate so the baseline covers them; an absent/empty " +
    "paths array yields a baseline of nothing (every post-mutation issue counts as new). Checkpoints " +
    "are session-scoped: they are cleared on script recompile, assembly reload, or editor restart, " +
    "and a `delta` against a cleared checkpoint returns a structured `unavailable` payload (not a " +
    "hard error) so the agent can fall back to `godot_open_mcp_validate_edit`. Optional `label` " +
    "tags the checkpoint for logging.",
  inputSchema: {
    type: "object",
    properties: {
      paths: {
        type: "array",
        items: { type: "string" },
        description:
          "res:// paths to capture in the baseline (e.g. [\"res://Scenes/Main.tscn\"]). " +
          "Recommended — pass the paths you intend to mutate so the delta is meaningful. When " +
          "omitted/empty, the baseline covers nothing and every post-mutation issue counts as new.",
      },
      label: {
        type: "string",
        description:
          "Optional human-readable label for the checkpoint (logging/UI only; not part of identity).",
      },
    },
    additionalProperties: false,
  },
};
