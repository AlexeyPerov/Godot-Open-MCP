// `godot_open_mcp_restart_editor` tool definition (P15.3).
//
// Auto-recover from a wedged Godot editor by terminating the hung process.
// Companion to `bridge_status` (which reports `unreachable` / `dead_bridge`
// when the listener stops responding) and `read_compile_errors` (which
// surfaces the failure from the on-disk Godot log). Where those two only
// DIAGNOSE, this tool ACTS: with explicit confirmation it terminates the hung
// Godot process so the operator/Hub can relaunch a fresh one. The relaunch
// itself is deferred — the interactive Godot editor's launch recipe (the flags
// the Hub/operator used) is not knowable from the server, so the response
// carries clear "relaunch via the Hub/CLI" guidance instead.
//
// Adapted from Unity Open MCP's `mcp-server/src/tools/restart-editor.ts`
// (copy for the confirm-gate + dry-run + kill-grace schema shape; adapt the
// signature description and recovery wording). Intentional deltas:
//   - The hang signature is Godot-specific (crash marker in the Godot log OR
//     frozen main thread: live PID + unreachable /ping + stale log). Unity
//     keys on an fd-exhaustion string (`Could not register to wait for file
//     descriptor N`); that signature does not apply to Godot.
//   - PID resolution comes from the instance lock (instance-discovery.ts),
//     not an OS process scan.
//   - Confirm-only gate — no separate `gate: "off"` coupling (Unity couples
//     both; Godot simplifies to confirm-only).
//
// Operator-only surface (sibling to bridge_status): no group assignment, so
// it sits in the always-visible meta-tool bucket. It is NOT a mutating project-
// asset tool — the gate validates asset fallout, which does not apply to an OS
// process kill — so it routes `local` with no gate hop.
//
// Safety contract (the plan's "explicit confirmation" requirement):
//   1. Refuses unless `confirm: true` is passed. A dry-run call (confirm false
//      or absent) returns the PID + diagnosis it would act on, no side effect.
//   2. Refuses when the Godot hang signature is ABSENT. Never restart on a
//      fixable compile failure (read_compile_errors surfaces those) or a
//      merely offline bridge.
//   3. Surfaces unsaved-scene risk in the response when the bridge is still
//      reachable enough to report dirty scenes. Killing the editor can destroy
//      unsaved scene work.
//   4. SIGTERM → grace period → SIGKILL fallback on macOS/Linux; `taskkill /T
//      /F` on Windows.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const restartEditor: Tool = {
  name: "godot_open_mcp_restart_editor",
  description:
    "Auto-recover from a wedged Godot editor by terminating the hung Godot " +
      "process. Use ONLY when the editor is truly hung: bridge_status reports " +
      "`unreachable` (live PID, /ping not responding) or `dead_bridge`, AND " +
      "the Godot log shows a crash marker (backtrace / segfault / abort / " +
      "fatal) or a frozen signature (live PID + unreachable /ping + no recent " +
      "log writes). The editor is frozen/crashed in that state and will not " +
      "recover on its own. Requires EXPLICIT confirmation (`confirm: true`) — " +
      "killing the editor can destroy unsaved scene work and in-flight asset " +
      "imports. The tool refuses when the hang signature is absent (no restart " +
      "on a fixable compile failure — use read_compile_errors + fix the source " +
      "instead), when no live Godot PID matches the project's instance lock, " +
      "or when the PID is invalid. Local-routed: it acts on the OS process via " +
      "process.kill (SIGTERM → SIGKILL on macOS/Linux) or taskkill /T /F on " +
      "Windows — no bridge round-trip, no Godot spawn. The response carries " +
      "the killed PID + kill method + clear 'editor terminated, relaunch " +
      "required via the Hub/CLI' guidance; relaunch is NOT automatic. A dry-run " +
      "call (confirm false/absent) returns what WOULD happen without any side " +
      "effect. After relaunch, poll godot_open_mcp_bridge_status until it " +
      "returns `running` to confirm the bridge reconnected. The active-scene-" +
      "dirty signal is checked when the bridge is still reachable and surfaced " +
      "as a `dirtyScenesWarning` in the response (it does NOT block the kill — " +
      "the editor is hung).",
  inputSchema: {
    type: "object",
    properties: {
      confirm: {
        type: "boolean",
        default: false,
        description:
          "Must be `true` to actually terminate the editor. When false or " +
            "absent the call is a dry-run: it returns the PID + diagnosis it " +
            "would act on without any side effect. Killing the editor is " +
            "destructive (unsaved scene work, in-flight imports) — never pass " +
            "this without first confirming the hang signature via " +
            "godot_open_mcp_bridge_status and godot_open_mcp_read_compile_errors.",
      },
      kill_grace_ms: {
        type: "integer",
        default: 5000,
        minimum: 0,
        maximum: 15000,
        description:
          "SIGTERM→SIGKILL grace window in milliseconds on macOS/Linux. " +
            "Default 5000ms. Ignored on Windows (taskkill /F is forced). Raise " +
            "to give Godot more time to flush its log + release the instance " +
            "lock on a cooperative shutdown; lower to fail faster when the " +
            "editor is fully wedged.",
      },
    },
    additionalProperties: false,
  },
};
