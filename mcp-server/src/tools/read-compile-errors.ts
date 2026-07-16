// `godot_open_mcp_read_compile_errors` tool definition (P7.4).
//
// Offline, filesystem-only diagnostic: reads the tail of the project's
// configured Godot log file and extracts structured C#/GDScript/plugin-load
// diagnostics. The one recovery channel that works when the bridge addon
// itself has failed to compile or load — in that state every in-bridge channel
// (`console_get_logs`, `bridge_status`'s `/ping`) is dead with it. The live
// Godot editor still writes parse/compile/plugin-load errors to the project
// log regardless of bridge health, so this tool retrieves them without touching
// Godot or the bridge.
//
// Adapted from Unity Open MCP's `mcp-server/src/tools/read-compile-errors.ts`
// (copy fidelity for the offline bounded-diagnostic surface + the schema
// shape). Intentional deltas:
//   - Resolves Godot `user://` logging settings (`debug/file_logging/*`) instead
//     of Unity's global `Editor.log`.
//   - Parses GDScript and addon/script load failures in addition to C#.
//   - File logging is OFF by default in Godot; the resolver surfaces that as a
//     `logging_disabled` status (a successful, explanatory result — the tool
//     itself succeeded, the file just does not exist yet).
//   - No Unity package/assembly/Safe-Mode/fd-exhaustion issue kinds; only
//     evidenced Godot patterns ship here.
//   - No per-call `log_path` argument (no arbitrary file-read surface); the
//     operator env override `GODOT_OPEN_MCP_LOG_FILE` is honored instead.
//
// Route: always `offline`. Never calls the bridge. Never spawns Godot.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const readCompileErrors: Tool = {
  name: "godot_open_mcp_read_compile_errors",
  description:
    "Read C# compiler errors AND GDScript parse errors AND script/addon " +
    "load failures directly from the project's configured Godot log file " +
    "(offline, no bridge, no Godot spawn). Returns structured diagnostics " +
    "with per-entry `kind` (csharp | gdscript | script_load | addon_load | " +
    "other), `severity` (error | warning), and — when the log carries them — " +
    "`file`, `line`, `column`, `code`, `message`, and the raw line. Use this " +
    "when: (a) the bridge is unreachable after a reload — a `dead_bridge` " +
    "bridge_status points here; (b) a C# or GDScript script fails to compile " +
    "or load and the addon's own `console_get_logs` collector is empty " +
    "because the addon never started; (c) you suspect a plugin failed to " +
    "initialize. Works even when the bridge addon itself failed to load, " +
    "because it reads the log file Godot writes independently of the bridge. " +
    "Check `status` + `unhealthy` first; when `unhealthy` is true, scan " +
    "`headline` for a one-line triage then drill into `diagnostics`. When " +
    "`status` is `logging_disabled`, file logging is off in project.godot — " +
    "enable `debug/file_logging/enable_file_logging` and reproduce the " +
    "failure. When `staleLogSuspected` is true, the cited source files were " +
    "edited more recently than the log — the error block may reference on-disk " +
    "code you have already fixed. Force a fresh Godot editor reload / recompile " +
    "before trusting the errors.",
  inputSchema: {
    type: "object",
    properties: {
      tail_bytes: {
        type: "integer",
        default: 262144,
        minimum: 4096,
        maximum: 1048576,
        description:
          "Maximum number of bytes to read from the END of the Godot log " +
          "(default 256 KiB). Compiler/parse errors are written in contiguous " +
          "blocks near the end, so a modest tail is ample. Increase only if " +
          "errors are reported missing.",
      },
      include_rotated: {
        type: "boolean",
        default: true,
        description:
          "When true (default), fall back to the newest rotated log " +
          "(`godot.log.N`) if the current `godot.log` is missing or empty. " +
          "Only the configured log directory is scanned, with a strict " +
          "`godot.log.<digits>` filename policy bounded by the project's " +
          "`debug/file_logging/max_log_files` setting.",
      },
      max_diagnostics: {
        type: "integer",
        default: 50,
        minimum: 1,
        maximum: 200,
        description:
          "Maximum number of distinct diagnostics to surface (default 50). " +
          "Duplicates are removed first; the cap then bounds the unique set. " +
          "Raise to see more of a large error burst.",
      },
    },
    additionalProperties: false,
  },
};
