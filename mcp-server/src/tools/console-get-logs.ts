// `godot_open_mcp_console_get_logs` tool definition (P4.7; capture scope extended in P18.3).
//
// Read-only (gate-free). Queries the Godot Open MCP bounded log collector and returns matching
// entries newest-first, with capture-capability metadata so callers know which capture mode is
// active. The handler lives in the bridge (POST /tools/godot_open_mcp_console_get_logs); this file is
// the catalog metadata only — name / description / input schema — advertised to AI clients over stdio
// ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/read-console.ts (adapt fidelity): Unity's
// read-console reads the native editor console via Application.logMessageReceivedThreaded. Godot's C#
// API exposes NO such global managed log hook at the 4.3 baseline, so the collector is fed by the
// plugin's own BridgeLog path and tool-handler error capture (capture.mode "addon_only"). On Godot
// 4.5+ the bridge ALSO arms the global managed Logger hook (OS.add_logger, PR #91006) via a
// GDScript Logger subclass + reflection (the addon compiles against GodotSharp 4.3.0 so the 4.5+
// type cannot be referenced at compile time) — so the collector ingests native prints / push_warning
// / push_error too (capture.mode "native_output"). The hook arms from plugin enable; pre-enable boot
// lines are missed. Any arm failure degrades gracefully back to addon_only. The response carries
// explicit capture-capability metadata (nativeOutputComplete, engineErrorSinkActive, mode) so callers
// know which mode is active and do not over-trust the contents.
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
    "Retrieve captured Godot Open MCP log lines, newest-first. Read-only (gate-free). Capture scope " +
    "is version-gated: on Godot 4.3 (the addon floor) the collector holds only the addon's own " +
    "activity (bridge lifecycle, tool-handler errors) — capture.mode is \"addon_only\"; on Godot 4.5+ " +
    "the bridge arms the global Logger hook (OS.add_logger) so the collector ALSO ingests native " +
    "editor Output (prints, push_warning, push_error) from plugin enable onward — capture.mode is " +
    "\"native_output\". The response carries explicit capture-capability metadata " +
    "(capture.nativeOutputComplete, capture.engineErrorSinkActive, capture.mode) so callers do not " +
    "over-trust the contents; pre-enable boot lines are never captured. Each entry has a monotonic " +
    "sequence (for event-stream cursor use), a logType (log/warning/error), the message, a UTC " +
    "timestamp, an optional stack trace, and a source (bridge/script/engine/tool). Supports severity " +
    "filter, age filter, max-entries cap, and stack-trace control. An empty result is a success, " +
    "not an error.",
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
