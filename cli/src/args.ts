// Thin CLI argument parsing — hand-rolled, zero runtime deps.
//
// Adapted from Unity Open MCP's `mcp-server/src/cli/args.ts`. The parser
// covers the shared flags every P6 command reuses plus `--help` / `--version`
// short-circuits. Concrete commands (install-plugin, setup-mcp, open,
// wait-for-ready, status, configure) register in KNOWN_COMMANDS as their plans
// land; P6.1 ships an empty list so the only recognized invocations are
// `--help` and `--version` — everything else is a clean "unknown command"
// error.
//
// Command shapes (declared in help once implemented):
//   godot-open-mcp-cli install-plugin [path] [--source <dir>] [--json]
//   godot-open-mcp-cli setup-mcp <agent-id> [path] [--list] [--json]
//   godot-open-mcp-cli open [path] [--editor-path <bin>] [--json]
//   godot-open-mcp-cli wait-for-ready [path] [--timeout-ms N] [--interval-ms N] [--json]
//   godot-open-mcp-cli status [path] [--json]
//   godot-open-mcp-cli configure [path] [--list] [--set authMode=none|required] [--json]
//   godot-open-mcp-cli --help | -h
//   godot-open-mcp-cli --version | -V
//
// Shared options (where relevant):
//   --project <path> | -P <path>   override GODOT_PROJECT_PATH
//   --port <n>      | -p <n>       override GODOT_OPEN_MCP_BRIDGE_PORT
//   --timeout-ms <n>               wait-for-ready overall timeout in milliseconds
//   --interval-ms <n>              wait-for-ready poll interval
//   --json                         emit JSON instead of human-readable output

export type CliCommand =
  | "help"
  | "version"
  | "install-plugin";

/** Commands recognized by the dispatcher (excludes help/version). */
export const KNOWN_COMMANDS: readonly string[] = [
  // Commands register as their plans land:
  "install-plugin", // P6.2
  // setup-mcp (P6.3), open / wait-for-ready (P6.4), status / configure (P6.5)
  // append here as they land.
];

export interface ParsedCli {
  command: CliCommand | null;
  /** Bare `--json` flag — switches human-readable output to JSON. */
  json: boolean;
  /** Resolved project path (flag wins, else GODOT_PROJECT_PATH env). */
  projectPath: string | undefined;
  /** Resolved bridge port override (flag wins, else GODOT_OPEN_MCP_BRIDGE_PORT env). */
  port: number | undefined;
  /** wait-for-ready overall timeout (ms). */
  timeoutMs: number | undefined;
  /** wait-for-ready poll interval (ms). */
  intervalMs: number | undefined;
  /** install-plugin `--source <dir>` (local addon root). */
  source: string | undefined;
  /** Positional path argument ([path] in the usage strings). */
  positionalPath: string | undefined;
  /** Parse error message; when set, the dispatcher prints it and exits non-zero. */
  error: string | undefined;
  /** Unknown / unparsed leftovers (currently an error condition). */
  unknown: string[];
}

export function emptyParsed(): ParsedCli {
  return {
    command: null,
    json: false,
    projectPath: undefined,
    port: undefined,
    timeoutMs: undefined,
    intervalMs: undefined,
    source: undefined,
    positionalPath: undefined,
    error: undefined,
    unknown: [],
  };
}

/**
 * Parse the CLI argv (everything after `node dist/index.js`). Returns a
 * structured result; the dispatcher interprets `command`/`error`. Never throws
 * — parse problems are reported through `error`.
 */
export function parseCliArgs(argv: string[]): ParsedCli {
  const parsed = emptyParsed();
  // Make a mutable copy; we walk it with an index so value-taking flags can
  // consume their following token.
  const args = argv.slice();

  let i = 0;
  // First non-flag token is the command (positional). A second non-flag token
  // is the command's optional [path] positional (for commands that take one).
  let positionalCount = 0;

  while (i < args.length) {
    const tok = args[i];

    // --- flags that take no value ---
    if (tok === "--json") {
      parsed.json = true;
      i++;
      continue;
    }
    if (tok === "-h" || tok === "--help") {
      parsed.command = "help";
      return parsed;
    }
    if (tok === "-V" || tok === "--version") {
      parsed.command = "version";
      return parsed;
    }

    // --- flags that take a value ---
    if (tok === "--project" || tok === "-P") {
      const v = args[i + 1];
      if (!v || v.startsWith("-")) {
        parsed.error = `${tok} requires a project path.`;
        return parsed;
      }
      parsed.projectPath = v;
      i += 2;
      continue;
    }
    if (tok === "--port" || tok === "-p") {
      const v = args[i + 1];
      const n = parsePositiveInt(v);
      if (n === undefined) {
        parsed.error = `${tok} requires a valid port number (1-65535).`;
        return parsed;
      }
      parsed.port = n;
      i += 2;
      continue;
    }
    if (tok === "--timeout-ms") {
      const v = args[i + 1];
      const n = parsePositiveInt(v);
      if (n === undefined) {
        parsed.error = "--timeout-ms requires a positive integer (ms).";
        return parsed;
      }
      parsed.timeoutMs = n;
      i += 2;
      continue;
    }
    if (tok === "--interval-ms") {
      const v = args[i + 1];
      const n = parsePositiveInt(v);
      if (n === undefined) {
        parsed.error = "--interval-ms requires a positive integer (ms).";
        return parsed;
      }
      parsed.intervalMs = n;
      i += 2;
      continue;
    }
    if (tok === "--source") {
      const v = args[i + 1];
      if (!v || v.startsWith("-")) {
        parsed.error = `${tok} requires a directory path.`;
        return parsed;
      }
      parsed.source = v;
      i += 2;
      continue;
    }

    // --- positionals ---
    if (!tok.startsWith("-")) {
      if (positionalCount === 0) {
        // First positional is the command.
        if (!KNOWN_COMMANDS.includes(tok)) {
          parsed.error = `Unknown command '${tok}'. Known: ${
            KNOWN_COMMANDS.length > 0
              ? KNOWN_COMMANDS.join(", ")
              : "(none yet — see --help)"
          }.`;
          return parsed;
        }
        parsed.command = tok as CliCommand;
        positionalCount++;
        i++;
        continue;
      } else if (positionalCount === 1) {
        // Second positional is the optional [path] argument for commands that
        // take one.
        parsed.positionalPath = tok;
        positionalCount++;
        i++;
        continue;
      } else {
        parsed.error = `Unexpected positional '${tok}'.`;
        return parsed;
      }
    }

    // Anything else is an unknown flag. Collect for a helpful error.
    parsed.unknown.push(tok);
    i++;
  }

  if (parsed.unknown.length > 0) {
    parsed.error = `Unknown option(s): ${parsed.unknown.join(", ")}.`;
    return parsed;
  }

  // No command at all. Unlike Unity (which falls through to the stdio MCP
  // server when argv has no recognized command), the Godot CLI never starts an
  // MCP server — the dispatcher treats a bare invocation (no --help/--version,
  // no command) as a request for help so `godot-open-mcp-cli` with no args
  // prints usage and exits 0.
  if (!parsed.command) {
    parsed.command = "help";
  }

  return parsed;
}

function parsePositiveInt(v: string | undefined): number | undefined {
  if (v === undefined || v.startsWith("-")) return undefined;
  const n = Number(v);
  if (!Number.isInteger(n) || n <= 0) return undefined;
  return n;
}
