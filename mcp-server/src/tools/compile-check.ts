// `godot_open_mcp_compile_check` tool definition (P17.4).
//
// The ACTIVE build trigger, complementing the PASSIVE `read_compile_errors`
// (P7.4). `read_compile_errors` reads whatever the live editor already wrote to
// the Godot log; `compile_check` spawns a FRESH build (no live bridge, no live
// editor required) so an agent can verify a fix compiled without asking the
// operator to re-open the editor. Use it after editing source to confirm the
// project compiles, or to reproduce a build failure on demand.
//
// Route: `local` (NOT `batch`). Godot has no headless editor — pinned by
// route-policy.ts, there is intentionally no `batch` route. This tool shells
// out to the platform's actual Godot build model directly from the MCP process:
//   - C# project (a `.csproj`/`.sln` is present)  → `dotnet build`
//   - GDScript / tool-script project (otherwise)  → `godot --headless …`
// This mirrors how `restart_editor` acts on the OS process and
// `resource_pressure` samples it server-side: the tool does its own bounded OS
// work without a bridge round-trip.
//
// Adapted from Unity Open MCP's `mcp-server/src/tools/compile-check.ts`
// (copy the tool SHAPE: `timeout_ms` cap + structured-error contract + the
// pre-spawn `project_locked` guard). Intentional deltas:
//   - No headless-editor spawn. Unity launches a batch Unity; Godot has none.
//     The build model is `dotnet`/`godot` CLI — a greenfield invocation path.
//   - Project-type detection (C# vs GDScript) — greenfield; Unity has only C#.
//   - The `project_locked` guard keys off the Godot instance lock
//     (instance-discovery.ts), the same file `bridge_status`/`restart_editor`
//     trust — not Unity's one-Editor-per-project OS lock.
//
// Always-visible meta-tool (no group assignment). It refuses to build under a
// live editor (instance lock alive): a concurrent build can conflict with the
// editor's own .NET/GDScript pipeline. The workaround is to close the editor
// first (or kill a wedged one via `restart_editor`).

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const compileCheck: Tool = {
  name: "godot_open_mcp_compile_check",
  description:
    "Trigger a FRESH build of the Godot project (no live bridge, no live " +
      "editor required) and report whether it compiles right now, with " +
      "structured errors. The ACTIVE counterpart to read_compile_errors " +
      "(which only reads what the live editor already wrote to the log): " +
      "use this after editing source to verify a fix compiles, or to " +
      "reproduce a build failure on demand. Local-routed — it shells out to " +
      "the project's actual build model directly from the MCP process: " +
      "`dotnet build` for C# projects (when a `.csproj`/`.sln` is present) " +
      "or `godot --headless` for GDScript/tool-script projects. Godot has no " +
      "headless editor, so there is no batch spawn. Detects the project type " +
      "automatically. For a GDScript project, omit script_path to load + " +
      "parse the whole project at startup (surfaces autoload + main-scene " +
      "parse errors — scripts never loaded at startup are NOT checked), or " +
      "pass script_path to parse exactly one script (`godot --headless " +
      "--check-only --script <path>`). Returns structured diagnostics " +
      "(file/line/code/message) reusing the read_compile_errors parser. " +
      "Refuses with `project_locked` when a live editor holds the project " +
      "(instance lock alive) — a concurrent build can conflict with the " +
      "editor; close the editor first (or kill a wedged one via " +
      "restart_editor). Returns `builder_not_found` when `dotnet`/`godot` " +
      "is not on PATH (set GODOT_OPEN_MCP_GODOT_PATH to override the Godot " +
      "binary). `timeout_ms` caps the build (default 300s, bounds 30s–600s). " +
      "Check `success` first; when false, scan `errors` (errors are listed " +
      "before warnings). This is a read-only build (it writes build outputs " +
      "to obj/bin like any compile, but does not mutate project assets).",
  inputSchema: {
    type: "object",
    properties: {
      timeout_ms: {
        type: "integer",
        default: 300000,
        minimum: 30000,
        maximum: 600000,
        description:
          "Maximum time to wait for the build to finish (ms), clamped to " +
          "[30000, 600000]. On timeout the build process is killed " +
          "(SIGTERM → SIGKILL) and the result carries timedOut: true with " +
          "whatever partial output + diagnostics were captured. Large C# " +
          "projects may need the upper bound; a single GDScript script " +
          "check usually finishes in a few seconds.",
      },
      script_path: {
        type: "string",
        description:
          "GDScript only — target a single script for an exact parse check " +
          "(`godot --headless --check-only --script <path>`). A `res://` " +
          "path or a native path. Ignored for C# projects (C# always builds " +
          "the whole solution/project). When omitted on a GDScript project, " +
          "the whole project is loaded + parsed at startup (autoload + " +
          "main-scene scripts); scripts never loaded at startup are NOT " +
          "checked, so pass script_path to verify a specific file.",
      },
    },
    additionalProperties: false,
  },
};
