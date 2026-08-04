// `godot_open_mcp_resource_pressure` tool definition (P15.4).
//
// Proactive resource-exhaustion prediction: samples the live Godot process's
// file-descriptor / handle count and reports headroom + trend so an agent can
// warn the operator to save and restart BEFORE the editor wedges. Companion to
// `restart_editor` (the reactive kill half) and `read_compile_errors` (the
// diagnosis half). Where `restart_editor` acts AFTER a hang and
// `read_compile_errors` surfaces compile failures, this tool samples the OS
// process directly to catch a slow fd/handle leak across recompiles / editor
// reloads — before the bridge (the thing that dies on exhaustion) is gone.
//
// Adapted from Unity Open MCP's `mcp-server/src/tools/resource-pressure.ts`
// (copy for the pid-override schema shape + the description structure); the
// intentional deltas mirror `process-diagnostics.ts`:
//   - The ceiling is PROBED per-OS (Linux `/proc/<pid>/limits`, macOS
//     `launchctl limit maxfiles`, Windows none) rather than Unity's fixed
//     Mono ~1024 ceiling. The response surfaces the probed ceiling + its
//     method, and the actionable signal is the TREND, not the absolute count.
//   - PID resolution comes from the instance lock (instance-discovery.ts),
//     not an OS process scan (the router handler reads the lock).
//
// Operator surface: no group assignment in `capabilities/tool-groups.ts`, so
// it sits in the always-visible meta-tool bucket alongside `bridge_status` /
// `read_compile_errors` / `restart_editor`. Read-only, gate-free, local-routed.
//
// The probe runs server-side against the OS (macOS: `lsof -p <pid>`; Linux:
// `/proc/<pid>/fd`; Windows: `Get-Process -Id <pid>.HandleCount` — approximate)
// and does NOT require the bridge to be reachable — the bridge is what dies on
// resource exhaustion. The session-scoped sample ring (no disk cache) lives in
// `ToolSessionState`; a server restart clears the history.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const resourcePressure: Tool = {
  name: "godot_open_mcp_resource_pressure",
  description:
    "Sample the live Godot process's file-descriptor / handle usage and report " +
      "headroom + trend — a proactive warning before the editor wedges from " +
      "resource exhaustion (the reactive recovery is godot_open_mcp_restart_editor; " +
      "the diagnosis channel is godot_open_mcp_read_compile_errors). Use this after " +
      "heavy automation (many recompiles / editor reloads / long play sessions) to " +
      "catch a slow fd/handle leak across samples. The probe runs server-side " +
      "against the OS (macOS: `lsof -p <pid>`; Linux: `/proc/<pid>/fd`; Windows: " +
      "`Get-Process -Id <pid>.HandleCount` — approximate) and does NOT require the " +
      "bridge to be reachable — the bridge is the thing that dies on resource " +
      "exhaustion. Resolves the live Godot PID from the project's instance lock " +
      "(same source as bridge_status), or accepts an explicit `pid`. " +
      "Response fields: `fdCount` (null when the probe failed), `fdMethod`, " +
      "`approximate` (Windows handle count), `ceiling` (probed per-OS — null on " +
      "Windows), `ceilingMethod`, `headroom`, `pressureRatio`, `state` (`ok` | " +
      "`warn` at ≥80% | `critical` at ≥90% | `unknown` when the count or ceiling " +
      "could not be read), `trend` (`stable` | `rising` | `leaking` — a monotonic " +
      "climb across ≥3 samples is a leak in progress; absolute count alone is not " +
      "enough), and `samples[]` (the session-scoped in-memory ring — no disk cache). " +
      "The ceiling is PROBED per-OS (Linux `/proc/<pid>/limits` 'Max open files' " +
      "soft limit; macOS `launchctl limit maxfiles` system-wide soft limit — a " +
      "GUI-launched Godot inherits it; Windows has no Unix fd ceiling), so the " +
      "actionable signal is the TREND (rising/leaking), not the absolute count. " +
      "When `state` is warn/critical or `trend.state` is leaking, surface the risk " +
      "to the operator and recommend saving scene work + restarting via the Hub/CLI " +
      "before the next reload trips the ceiling. No disk cache — samples live in the " +
      "session store and are cleared on MCP-server restart.",
  inputSchema: {
    type: "object",
    properties: {
      pid: {
        type: "integer",
        minimum: 1,
        description:
          "Optional explicit Godot PID to probe. When omitted, the tool " +
            "resolves the live Godot process for this project from the " +
            "instance lock (same source as godot_open_mcp_bridge_status). " +
            "Pass an explicit PID only when you have one from a prior call " +
            "and want to skip the lock read.",
      },
    },
    additionalProperties: false,
  },
};
