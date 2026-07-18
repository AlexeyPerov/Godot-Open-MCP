// `godot_open_mcp_manage_tools` tool definition (P8.3).
//
// Server-only meta-tool for per-session tool-group visibility. The MCP server
// holds the session state (ToolSessionState); the bridge does not track it.
// Activating a group makes its tools appear in subsequent ListTools responses;
// deactivating removes them. `reset` restores the catalog's default-on groups
// (`core` only — the gate/verify safety surface is folded into `core`).
//
// The tool definition ships with every MCP-server build (always visible — it
// is in the `ALWAYS_VISIBLE_TOOLS` allow-list in `tool-session-state.ts`) so an
// agent can always reach it before any other group is active. The CallTool
// handler special-cases the name and calls `ToolRouter.routeManageTools`
// directly (no `POST /tools/manage_tools` endpoint on the bridge) — it is the
// only mutator of `ToolSessionState`.
//
// Four actions only:
//   - `list_groups` — read-only. Enumerate every catalog group with `active`,
//     `defaultEnabled`, `activationSource`, and the tool roster.
//   - `activate` / `deactivate` — toggle one group (requires `group`). Idempotent:
//     a no-op reports `changed: false`. Activating an already-active group does
//     nothing; activating a default-on group that was deactivated flips its
//     source to `manual`.
//   - `reset` — restore the default-on groups (always `core` only).
//
// `notifications/tools/list_changed` emission is stubbed in P8.3 — the
// `notifyToolListChanged` callback may be a no-op or a real emitter; P8.4 owns
// the correct emit-on-change semantics and tests.
//
// Adapted from Unity Open MCP's `mcp-server/src/tools/manage-tools.ts` (copy
// for the four-action schema + result contract). Intentional deltas:
//   - No `suggest` / `activate_for` / `intent` / `tags` — the Unity intent-
//     driven actions are out of scope for P8 (roadmap P8.3 done-when: an agent
//     can enable `typed-editor`).
//   - Default message text says `core` only — Unity describes two default-on
//     groups (`core` + `gate-and-verify`); Godot folds the gate surface into
//     `core`.
//   - No `available` / `availableReason` / `unityPackage` / `packageDependency`
//     fields in the list_groups response — Godot has no bridge compile inventory
//     for domain packs yet. The `toolCount: 0` of a stub pack conveys the same
//     "empty until the pack ships" signal without a separate field.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const manageTools: Tool = {
  name: "godot_open_mcp_manage_tools",
  description:
    "Manage which tool groups are visible in this session. Sessions start " +
    "with `core` enabled (ping + gate/verify: validate_edit, " +
    "checkpoint_create, delta, apply_fix). Activate `typed-editor` to add " +
    "node/scene/resource/filesystem/editor-state/selection/console/" +
    "screenshot/reflection tools to your ListTools surface; deactivate to " +
    "hide them again. State is ephemeral and per-session — it resets to " +
    "`core` only when the MCP server restarts. Actions: `list_groups` (show " +
    "every group with active flag, description, activation source, and tool " +
    "roster), `activate` (enable a group — its tools become visible on the " +
    "next ListTools), `deactivate` (hide a group's tools), `reset` (restore " +
    "`core` only). Always call `list_groups` first to discover group ids. " +
    "Domain pack groups (tilemap, navigation, particles, animation, csg) " +
    "appear in the catalog even when their roster is empty — they are " +
    "reserved ids that future packs reuse without a rename.",
  inputSchema: {
    type: "object",
    required: ["action"],
    properties: {
      action: {
        enum: ["list_groups", "activate", "deactivate", "reset"],
        description:
          "list_groups: enumerate every group with active flag + tool roster " +
          "(read-only — does not mutate state). " +
          "activate / deactivate: toggle one group (requires `group`). " +
          "reset: restore the default-on group set (`core` only).",
      },
      group: {
        type: "string",
        description:
          "Group id (required for activate / deactivate). Valid ids are " +
          "returned by list_groups. Unknown ids return a structured " +
          "`unknown_group` error.",
      },
    },
    additionalProperties: false,
  },
};
