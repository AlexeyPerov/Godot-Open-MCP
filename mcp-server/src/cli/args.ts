// CLI argument parser.
//
// Adapted from Unity Open MCP's `mcp-server/src/cli/args.ts`
// (copy/adapt fidelity), reduced to the Godot CLI surface: `run-tool`
// plus the shared `--project` / `--port` / `--json` / `--args` / `--arg`
// options. Pure + total: never throws, never touches process state, so it
// is fully unit-testable. A malformed invocation is reported via
// `parsed.error` (usage exit code 2) rather than an exception.

/** Subcommands the CLI recognizes (drives the index.ts dispatch fork). */
export const KNOWN_COMMANDS = ["run-tool"] as const;

export type CliCommand = (typeof KNOWN_COMMANDS)[number];

export interface ParsedCliArgs {
  /** `run-tool`, `help`, `version`, or undefined (fall through to stdio). */
  command: CliCommand | "help" | "version" | undefined;
  /** Populated when parsing found a usage error (exit code 2). */
  error?: string;
  /** Emit JSON instead of human-readable output. */
  json: boolean;
  /** Project path override (`--project` / `-P`); flag wins over env. */
  projectPath?: string;
  /** Bridge port override (`--port` / `-p`); flag wins over env. */
  port?: number;
  /** Tool name positional for `run-tool`. */
  toolName?: string;
  /** Merged tool args from `--args '<json>'` + repeated `--arg k=v`. */
  toolArgs: Record<string, unknown>;
}

function isFlag(token: string): boolean {
  return token.startsWith("-");
}

/** Parse a `key=value` pair; the value is JSON-parsed when it is valid JSON. */
function parseKeyValue(pair: string): { key: string; value: unknown } | undefined {
  const eq = pair.indexOf("=");
  if (eq <= 0) return undefined;
  const key = pair.slice(0, eq);
  const raw = pair.slice(eq + 1);
  try {
    return { key, value: JSON.parse(raw) };
  } catch {
    return { key, value: raw };
  }
}

export function parseCliArgs(argv: string[]): ParsedCliArgs {
  const parsed: ParsedCliArgs = {
    command: undefined,
    json: false,
    toolArgs: {},
  };

  const first = argv[0];
  if (first === "--help" || first === "-h") {
    parsed.command = "help";
    return parsed;
  }
  if (first === "--version" || first === "-V") {
    parsed.command = "version";
    return parsed;
  }
  if (first === undefined || isFlag(first)) {
    // No recognized subcommand → caller falls through to the stdio server.
    return parsed;
  }
  if (!(KNOWN_COMMANDS as readonly string[]).includes(first)) {
    // Unknown positional command → fall through (parity with Unity: index.ts
    // only forks on a KNOWN command; anything else runs the stdio server).
    return parsed;
  }

  parsed.command = first as CliCommand;

  // run-tool takes the tool name as the next positional.
  let i = 1;
  if (parsed.command === "run-tool") {
    const maybeTool = argv[i];
    if (maybeTool !== undefined && !isFlag(maybeTool)) {
      parsed.toolName = maybeTool;
      i += 1;
    }
  }

  for (; i < argv.length; i++) {
    const token = argv[i];
    switch (token) {
      case "--json":
        parsed.json = true;
        break;
      case "--project":
      case "-P": {
        const val = argv[++i];
        if (val === undefined) {
          parsed.error = `${token} requires a path argument.`;
          return parsed;
        }
        parsed.projectPath = val;
        break;
      }
      case "--port":
      case "-p": {
        const val = argv[++i];
        const n = Number(val);
        if (val === undefined || !Number.isInteger(n) || n < 1 || n > 65535) {
          parsed.error = `${token} requires an integer in [1, 65535].`;
          return parsed;
        }
        parsed.port = n;
        break;
      }
      case "--args": {
        const val = argv[++i];
        if (val === undefined) {
          parsed.error = "--args requires a JSON object argument.";
          return parsed;
        }
        let obj: unknown;
        try {
          obj = JSON.parse(val);
        } catch (e) {
          parsed.error = `--args is not valid JSON: ${(e as Error).message}`;
          return parsed;
        }
        if (obj === null || typeof obj !== "object" || Array.isArray(obj)) {
          parsed.error = "--args must be a JSON object.";
          return parsed;
        }
        Object.assign(parsed.toolArgs, obj as Record<string, unknown>);
        break;
      }
      case "--arg": {
        const val = argv[++i];
        const kv = val !== undefined ? parseKeyValue(val) : undefined;
        if (!kv) {
          parsed.error = "--arg requires a key=value argument.";
          return parsed;
        }
        parsed.toolArgs[kv.key] = kv.value;
        break;
      }
      default:
        parsed.error = `Unknown option '${token}'.`;
        return parsed;
    }
  }

  if (parsed.command === "run-tool" && !parsed.toolName) {
    parsed.error = "run-tool requires a tool name (e.g. godot_open_mcp_ping).";
  }

  return parsed;
}
