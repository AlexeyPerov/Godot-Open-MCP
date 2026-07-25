// `godot_open_mcp_pull_events` tool definition (P5.4).
//
// Drains the per-process BridgeEventStream — a single SSE subscription the MCP server keeps open to
// the bridge `GET /events` endpoint. The first call opens the subscription; later calls return only
// incremental events (console logs + editor-state transitions) buffered since the previous drain.
//
// Why poll instead of push? The MCP server runs over a stdio transport; it has no native way to
// forward bridge SSE → MCP notifications. Polling per call keeps the model in the loop and lets an
// agent decide when to drain (e.g. right after a mutation that may produce logs).
//
// This file carries only the catalog metadata (name / description / input schema), like every other
// tool in this folder. The CallTool dispatcher in `index.ts` special-cases the name and calls
// `BridgeEventStream.pull` directly (a **local-drains-live-stream** route — no
// `POST /tools/pull_events` endpoint on the bridge). The stream is constructed once in `main()` and
// shared across calls so every pull amortizes one connection.
//
// Read-only, gate-free, live (requires a connected bridge). When the bridge is offline, `pull`
// returns `connected:false` + `lastError` rather than throwing — the caller branches on those
// fields. Result envelope: `{ subscriberId, events[], dropped, connected, started, lastError }`.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/pull_events.ts (adapt fidelity): the schema
// is `max_events` only — Unity still declares a `subscriber` param, but no handler reads it on
// either side (BridgeEventStream fixes its id at construction), so the Godot port drops the
// misleading knob. Result envelope is unchanged. Only the tool-name prefix (`godot_open_mcp_*`,
// no `unity_senses_*` alias — ADR-003) differs. See the NOTE on the schema below for re-adding it.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const pullEvents: Tool = {
  name: "godot_open_mcp_pull_events",
  description:
    "Drain incremental bridge events (console logs + editor-state transitions) since the previous " +
    "call. The first call opens a server-side SSE subscription to the bridge's /events endpoint; " +
    "later calls return only new events. Use this after mutations to stream console output " +
    "without polling /ping or re-reading the full console (console_get_logs). Each event carries " +
    "`seq`, `ts`, `type` ('log' | 'editor_state'), and type-specific fields (logType/message/stack " +
    "for logs, state/isCompiling/isPlaying for state). `dropped` reports events evicted from the " +
    "client-side queue before this pull; `connected` reports the SSE reader state and `lastError` " +
    "the last reconnect failure (non-null only when disconnected). When the bridge is offline, the " +
    "tool returns connected:false + lastError rather than throwing. Requires a live Godot Editor " +
    "connection; non-mutating (gate-free).",
  inputSchema: {
    type: "object",
    properties: {
      max_events: {
        type: "integer",
        default: 50,
        minimum: 1,
        maximum: 1000,
        description:
          "Maximum events to return per call (default 50, clamped to [1, 1000]). Additional " +
          "buffered events remain queued and are counted in `dropped` only if the client-side " +
          "queue overflows (capacity 500).",
      },
      // NOTE: no `subscriber` property.
      //
      // The schema previously advertised one ("Optional subscriber id to resume a prior
      // subscription"), but no handler ever read it: routePullEvents reads only `max_events`, and
      // BridgeEventStream fixes its subscriberId at construction. An agent passing `subscriber` got
      // its value silently discarded and a *different* subscriberId echoed back in the result — so
      // the advertised resume-across-restarts behavior never happened, while the schema kept
      // inviting agents to try. Removed rather than half-implemented; the id is server-scoped and
      // reported in the result envelope. Re-add this only together with an
      // `ensureSubscription(subscriberId)` re-subscribe path on the stream.
    },
    additionalProperties: false,
  },
};
