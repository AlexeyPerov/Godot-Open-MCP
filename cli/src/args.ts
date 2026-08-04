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
//   godot-open-mcp-cli setup-mcp [<agent-id> [path]] [--list] [--use-local] [--config-path <file>] [--json]
//   godot-open-mcp-cli open [path] [--editor-path <bin>] [--json]
//   godot-open-mcp-cli wait-for-ready [path] [--timeout-ms N] [--interval-ms N] [--json]
//   godot-open-mcp-cli status [path] [--port N] [--json]
//   godot-open-mcp-cli configure [path] [--list] [--get <key>] [--set <key=value> ...] [--json]
//   godot-open-mcp-cli verify [path] [--fail-on error|warn|none] [--json]
//   godot-open-mcp-cli baseline create|update [path] [--baseline-path <file>] [--platform-profile <p>] [--json]
//   godot-open-mcp-cli regression check [path] [--baseline-path <file>] [--threshold N] [--per-category-threshold <ruleId=N> ...] [--platform-profile <p>] [--json]
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
  | "install-plugin"
  | "setup-mcp"
  | "open"
  | "wait-for-ready"
  | "ping"
  | "status"
  | "configure"
  | "verify"
  | "baseline"
  | "regression";

/** Commands recognized by the dispatcher (excludes help/version). */
export const KNOWN_COMMANDS: readonly string[] = [
  // Commands register as their plans land:
  "install-plugin", // P6.2
  "setup-mcp", // P6.3
  "open", // P6.4
  "wait-for-ready", // P6.4
  "ping", // P6.4
  "status", // P6.5
  "configure", // P6.5
  "verify", // P15.2
  "baseline", // P15.2
  "regression", // P15.2
];

/**
 * Commands that take a subcommand as their second positional
 * (`baseline create`, `regression check`). Maps command → the set of valid
 * subcommand tokens. A command not in this map has no subcommand level.
 */
export const SUBCOMMANDS: ReadonlyMap<string, readonly string[]> = new Map([
  ["baseline", ["create", "update"]],
  ["regression", ["check"]],
]);

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
  /** setup-mcp `--use-local` — spawn the monorepo build instead of `npx`. */
  useLocal: boolean;
  /** setup-mcp `--list` — print the agent registry and exit. */
  list: boolean;
  /** setup-mcp `--config-path <path>` — override the agent's default config path. */
  configPath: string | undefined;
  /** open `--editor-path <bin>` — explicit Godot editor binary (skips discovery). */
  editorPath: string | undefined;
  /** open `--no-build` — skip the pre-open `dotnet build` for C# projects. */
  noBuild: boolean;
  /** open `--build-configuration <cfg>` — MSBuild configuration (default Debug). */
  buildConfiguration: string | undefined;
  /** open `--wait` — chain into wait-for-ready after a successful launch. */
  wait: boolean;
  /** configure `--get <key>` — read one setting value. */
  getKey: string | undefined;
  /** configure `--set key=value` — write one or more setting assignments. */
  setAssignments: string[];
  /** verify `--fail-on error|warn|none` — gate severity (default `error`). */
  failOn: string | undefined;
  /** baseline/regression `--baseline-path <file>` — baseline JSON path. */
  baselinePath: string | undefined;
  /** regression `--threshold N` — global error-count-delta threshold (default 0). */
  threshold: number | undefined;
  /** regression `--per-category-threshold ruleId=N` — per-rule thresholds (repeatable). */
  perCategoryThresholds: string[];
  /** baseline/regression `--platform-profile mobile|console|desktop` (default desktop). */
  platformProfile: string | undefined;
  /** Positional path argument ([path] in the usage strings). */
  positionalPath: string | undefined;
  /**
   * setup-mcp second positional (the agent id when invoked as
   * `setup-mcp <agent-id> [path]`). The first positional is always the command
   * name; for setup-mcp the second positional is the agent id and the third is
   * the path. We keep the original positionalPath slot for the path so the
   * dispatcher's `resolveProjectPath` keeps working uniformly.
   */
  agentId: string | undefined;
  /**
   * baseline/regression subcommand (`create` | `update` for baseline; `check`
   * for regression). The second positional when the command is in
   * {@link SUBCOMMANDS}; the path positional shifts to the third slot, exactly
   * like setup-mcp's agent-id-then-path shape.
   */
  subcommand: string | undefined;
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
    useLocal: false,
    list: false,
    configPath: undefined,
    editorPath: undefined,
    noBuild: false,
    buildConfiguration: undefined,
    wait: false,
    getKey: undefined,
    setAssignments: [],
    failOn: undefined,
    baselinePath: undefined,
    threshold: undefined,
    perCategoryThresholds: [],
    platformProfile: undefined,
    positionalPath: undefined,
    agentId: undefined,
    subcommand: undefined,
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
      // Enforce the upper bound the error message already promises. Without it, an out-of-range port
      // passed parsing and was then silently discarded downstream: resolvePort re-validates the
      // range and, on failure, falls through to the lock/hash port — so `--port 99999` probed a
      // completely different port with no indication that the override had been ignored.
      if (n === undefined || n > 65535) {
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
    if (tok === "--use-local") {
      parsed.useLocal = true;
      i++;
      continue;
    }
    if (tok === "--list") {
      parsed.list = true;
      i++;
      continue;
    }
    if (tok === "--config-path") {
      const v = args[i + 1];
      if (!v || v.startsWith("-")) {
        parsed.error = `${tok} requires a file path.`;
        return parsed;
      }
      parsed.configPath = v;
      i += 2;
      continue;
    }
    if (tok === "--editor-path") {
      const v = args[i + 1];
      if (!v || v.startsWith("-")) {
        parsed.error = `${tok} requires a path to the Godot editor binary.`;
        return parsed;
      }
      parsed.editorPath = v;
      i += 2;
      continue;
    }
    if (tok === "--no-build") {
      parsed.noBuild = true;
      i++;
      continue;
    }
    if (tok === "--build-configuration") {
      const v = args[i + 1];
      if (!v || v.startsWith("-")) {
        parsed.error = `${tok} requires a configuration name (e.g. Debug, Release).`;
        return parsed;
      }
      parsed.buildConfiguration = v;
      i += 2;
      continue;
    }
    if (tok === "--wait") {
      parsed.wait = true;
      i++;
      continue;
    }
    if (tok === "--get") {
      const v = args[i + 1];
      if (!v || v.startsWith("-")) {
        parsed.error = `${tok} requires a setting key (authMode or bindAddress).`;
        return parsed;
      }
      // A second --get overwrites the first; the command surfaces the last one
      // (consistent with the single-value flag semantics of --port/--source).
      parsed.getKey = v;
      i += 2;
      continue;
    }
    if (tok === "--set") {
      const v = args[i + 1];
      // `key=value` always contains `=`; reject a missing value or a bare flag.
      if (!v || v.startsWith("-") || !v.includes("=")) {
        parsed.error = `${tok} requires a key=value assignment (e.g. authMode=required).`;
        return parsed;
      }
      parsed.setAssignments.push(v);
      i += 2;
      continue;
    }
    if (tok === "--fail-on") {
      const v = args[i + 1];
      if (!v || v.startsWith("-")) {
        parsed.error = `${tok} requires a severity (error, warn, or none).`;
        return parsed;
      }
      if (v !== "error" && v !== "warn" && v !== "none") {
        parsed.error = `${tok} must be one of: error, warn, none.`;
        return parsed;
      }
      parsed.failOn = v;
      i += 2;
      continue;
    }
    if (tok === "--baseline-path") {
      const v = args[i + 1];
      if (!v || v.startsWith("-")) {
        parsed.error = `${tok} requires a file path.`;
        return parsed;
      }
      parsed.baselinePath = v;
      i += 2;
      continue;
    }
    if (tok === "--threshold") {
      const v = args[i + 1];
      const n = parseNonNegativeInt(v);
      if (n === undefined) {
        parsed.error = `${tok} requires a non-negative integer.`;
        return parsed;
      }
      parsed.threshold = n;
      i += 2;
      continue;
    }
    if (tok === "--per-category-threshold") {
      const v = args[i + 1];
      // `ruleId=N` always contains `=`; reject a missing value or a bare flag.
      if (!v || v.startsWith("-") || !v.includes("=")) {
        parsed.error = `${tok} requires a ruleId=N assignment (e.g. broken_references=2).`;
        return parsed;
      }
      parsed.perCategoryThresholds.push(v);
      i += 2;
      continue;
    }
    if (tok === "--platform-profile") {
      const v = args[i + 1];
      if (!v || v.startsWith("-")) {
        parsed.error = `${tok} requires a profile (mobile, console, or desktop).`;
        return parsed;
      }
      if (v !== "mobile" && v !== "console" && v !== "desktop") {
        parsed.error = `${tok} must be one of: mobile, console, desktop.`;
        return parsed;
      }
      parsed.platformProfile = v;
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
        // Second positional. Three command-specific shapes:
        //   - setup-mcp:     `<agent-id> [path]` — 2nd = agent id, 3rd = path.
        //   - baseline/regression: `<subcommand> [path]` — 2nd = subcommand
        //     (create|update / check), 3rd = path.
        //   - everything else: `[path]`.
        if (parsed.command === "setup-mcp") {
          parsed.agentId = tok;
        } else if (SUBCOMMANDS.has(parsed.command ?? "")) {
          const allowed = SUBCOMMANDS.get(parsed.command!)!;
          if (!allowed.includes(tok)) {
            parsed.error = `Unknown ${parsed.command} subcommand '${tok}'. Known: ${allowed.join(", ")}.`;
            return parsed;
          }
          parsed.subcommand = tok;
        } else {
          parsed.positionalPath = tok;
        }
        positionalCount++;
        i++;
        continue;
      } else if (positionalCount === 2 && (parsed.command === "setup-mcp" || SUBCOMMANDS.has(parsed.command ?? ""))) {
        // setup-mcp's third positional is the project path; likewise the third
        // positional of a subcommand-bearing command (baseline/regression) is
        // the project path.
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

  // baseline/regression require a subcommand (`create`|`update` / `check`). A
  // bare `baseline` or `regression` with no subcommand is a usage error.
  if (parsed.command && SUBCOMMANDS.has(parsed.command) && !parsed.subcommand) {
    const allowed = SUBCOMMANDS.get(parsed.command)!;
    parsed.error = `${parsed.command} requires a subcommand. Known: ${allowed.join(", ")}.`;
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

/** Parse a non-negative integer (allows 0, unlike {@link parsePositiveInt}).
 *  Used by `regression check --threshold` (default 0 = any increase fails). */
function parseNonNegativeInt(v: string | undefined): number | undefined {
  if (v === undefined || v.startsWith("-")) return undefined;
  const n = Number(v);
  if (!Number.isInteger(n) || n < 0) return undefined;
  return n;
}
