// `godot_open_mcp_bridge_status` tool definition (P5.3).
//
// Operator / meta health snapshot. A thin wrapper over the existing
// instance-lock classifier (`instance-discovery.ts#classifyInstance`) plus a
// single `/ping` probe. It returns a coarse `status` token
// (`running | compiling | stopped | unreachable | dead_bridge`) alongside the
// underlying signals so an operator (and the future Validation Suite) can
// branch recovery in one call: retry vs read logs vs reopen the editor.
//
// The composition (read lock → classify → ping → derive) lives in
// `LiveClient.routeBridgeStatus`; the pure mapping is in
// `./bridge-status-derive.ts`. This file carries only the catalog metadata
// (name / description / input schema), like every other tool in this folder.
// The CallTool dispatcher in `index.ts` special-cases the name and calls
// `LiveClient.routeBridgeStatus` directly (a **local/live hybrid** route — no
// `POST /tools/bridge_status` endpoint on the bridge).
//
// Read-only, gate-free, never spawns Godot. The `/ping` fetch uses the
// bridge's standard 5 s timeout; this tool takes no arguments.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/bridge-status.ts (copy
// fidelity): identical schema (empty input object, no properties) and the
// same status vocabulary. Only the tool-name prefix (`godot_open_mcp_*`) and
// the Godot-specific recovery wording differ. The two follow-on admin tools
// (`bridge_stop` / `bridge_start`) are intentionally deferred (no HTTP
// start/stop route on the bridge; `stop` has a self-disconnect hazard).

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const bridgeStatus: Tool = {
  name: "godot_open_mcp_bridge_status",
  description:
    "Operator-oriented bridge health snapshot. Wraps the instance-lock " +
    "classifier (instance-discovery.ts#classifyInstance) plus a single " +
    "/ping probe and returns a coarse `status` token: " +
    "`running` (bridge connected, idle), `compiling` (bridge connected, " +
    "Godot compiling/reloading), `stopped` (Godot not running OR the bridge " +
    "addon is disabled — no live listener), `unreachable` (Godot process " +
    "alive but the listener did not respond — usually a transient " +
    "editor-reload window; retry shortly), or `dead_bridge` (Godot process " +
    "alive but the bridge heartbeat is stale — the addon is not running its " +
    "HTTP listener, so /ping will not recover on its own; the recovery hint " +
    "points at godot_open_mcp_read_compile_errors to read the failure from " +
    "the Godot log on disk). Also surfaces a top-level `classification` field " +
    "(healthy | reloading | dead_bridge | gone) mirroring the instance " +
    "lock, and a structured `recoveryHint` ({ tool, reason }) that is " +
    "non-null only when the status has a specific recovery tool " +
    "(dead_bridge → godot_open_mcp_read_compile_errors, the always-offline " +
    "log reader). When classification is dead_bridge the result explicitly " +
    "reads as 'Godot alive, bridge heartbeat stale / plugin failed to load' " +
    "rather than a generic stopped, so an agent can branch on the " +
    "machine-readable signal. Designed for operators and the future " +
    "Validation Suite's manual bridge-offline scenario pattern — not a " +
    "general agent health check (use godot_open_mcp_ping for a lightweight " +
    "probe). Read-only, gate-free, never spawns Godot. The /ping fetch uses " +
    "the bridge's standard 5s timeout; this tool takes no arguments.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
