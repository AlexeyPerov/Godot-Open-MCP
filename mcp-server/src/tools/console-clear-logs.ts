// `godot_open_mcp_console_clear_logs` tool definition (P4.7).
//
// Gate-free direct (mutates only ephemeral addon-owned collector state — not project files, not the
// native editor Output panel). Empties the Godot Open MCP log collector and returns the number
// removed plus nativeOutputCleared:false. The handler lives in the bridge
// (POST /tools/godot_open_mcp_console_clear_logs); this file is the catalog metadata only — name /
// description / input schema — advertised to AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/console-clear.ts (adapt fidelity): Unity's
// console-clear clears the native editor console window via LogEntries.Clear(). Godot's C# API
// exposes no managed hook to clear the editor's own Output panel, so (unlike Unity) this clears only
// the addon-side collector, not the Godot editor console window — nativeOutputCleared is always
// false. The collector sequence is NOT reset on clear (a future P5.4 event-stream cursor stays
// monotonic across a clear).
//
// Clear is classified gate-free because checkpoint/delta cannot meaningfully cover ephemeral memory.
// There is no paths_hint and no gate surface — never invent a fake paths_hint for it.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const consoleClearLogs: Tool = {
  name: "godot_open_mcp_console_clear_logs",
  description:
    "Clear the Godot Open MCP log cache (read by console_get_logs). Gate-free — mutates only " +
    "ephemeral addon-owned collector state, not project files or Godot editor state. Useful for " +
    "isolating logs to a specific action by clearing the slate first. NOTE: Godot's C# API exposes no " +
    "managed hook to clear the editor's own Output panel, so (unlike Unity) this clears ONLY the " +
    "addon-side collector — the response always reports nativeOutputCleared:false. The collector " +
    "sequence is not reset on clear (a future event-stream cursor stays monotonic across a clear). " +
    "Returns the number of entries cleared and the post-clear retained count.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
