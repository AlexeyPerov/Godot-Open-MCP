// `godot_open_mcp_console_get_logs` tool definition (P4.7).
//
// Read-only (gate-free). Queries the Godot Open MCP bounded log collector and returns matching
// entries newest-first, with capture-capability metadata so callers do not assume complete native
// logs. The handler lives in the bridge (POST /tools/godot_open_mcp_console_get_logs); this file is
// the catalog metadata only — name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/read-console.ts (adapt fidelity): Unity's
// read-console reads the native editor console via Application.logMessageReceivedThreaded. Godot's
// C# API exposes NO such global managed log hook at the 4.3 baseline, so the collector is fed by the
// plugin's own BridgeLog path and tool-handler error capture — NOT every line in the editor Output
// panel. The response carries explicit capture-capability metadata (nativeOutputComplete,
// engineErrorSinkActive) so callers do not over-trust the contents. Godot 4.5+ may add a passive
// engine/script error sink; that enhancement is version-gated and reported in the capability
// metadata, never assumed.
//
// Godot-MCP reference (Tool_Console.GetLogs / GodotLogCollector): the ring-buffer + filter behavior
// (max_entries, severity filter, last_minutes, include_stack_trace) and the FIFO eviction are lifted
// from there as read-only behavior guidance. The monotonic sequence number, the source tag, the
// capture-capability metadata, and the structured JSON envelope are greenfield for this port.
//
// An empty result is a successful response with capture metadata, NOT an error.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const consoleGetLogs: Tool = {
  name: "godot_open_mcp_console_get_logs",
  description:
    "Retrieve captured Godot Open MCP log lines, newest-first. Read-only (gate-free). NOTE: Godot's " +
    "C# API exposes no global managed log hook at the 4.3 baseline, so this returns the addon's own " +
    "captured activity (bridge lifecycle, tool-handler errors, routed game/script output when a " +
    "supported hook is active) — NOT the entire Godot editor Output panel. The response carries " +
    "explicit capture-capability metadata (capture.nativeOutputComplete, capture.engineErrorSinkActive) " +
    "so callers do not over-trust the contents. Each entry has a monotonic sequence (for future " +
    "event-stream cursor use), a logType (log/warning/error), the message, a UTC timestamp, an " +
    "optional stack trace, and a source (bridge/script/engine/tool). Supports severity filter, age " +
    "filter, max-entries cap, and stack-trace control. An empty result is a success, not an error.",
  inputSchema: {
    type: "object",
    properties: {
      max_entries: {
        type: "integer",
        default: 100,
        minimum: 1,
        maximum: 1000,
        description:
          "Maximum number of log entries to return. Default 100, clamped to [1, 1000]. Entries are " +
          "returned newest-first, so the cap keeps the most recent lines.",
      },
      log_type_filter: {
        type: "array",
        items: { type: "string", enum: ["log", "warning", "error"] },
        description:
          "Restrict to the listed severities. Omitted/null means all severities. Multiple values are " +
          "unioned (e.g. ['warning','error'] returns warnings and errors).",
      },
      include_stack_trace: {
        type: "boolean",
        default: false,
        description:
          "Include stack-trace strings in each entry. Default false — stack traces are stored only " +
          "when available and omitted from output unless requested.",
      },
      last_minutes: {
        type: "integer",
        default: 0,
        minimum: 0,
        description:
          "Return only lines captured in the last N minutes. 0 (default) returns all retained lines. " +
          "The age filter is applied against the capture timestamp.",
      },
    },
    additionalProperties: false,
  },
};
