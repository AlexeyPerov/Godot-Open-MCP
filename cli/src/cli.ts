// CLI dispatcher — the entry point for `godot-open-mcp-cli <command>`.
//
// Adapted from Unity Open MCP's `mcp-server/src/cli/cli.ts`. It:
//   1. parses argv (src/args.ts)
//   2. short-circuits --help / --version
//   3. routes a parse error or unknown command to a structured result
//   4. (later) dispatches to the right command module
//   5. prints JSON or human-readable output and returns the exit code
//
// Intentional delta from Unity: there is NO stdio MCP fallthrough. Unity's
// `runCli` returns `handled: false` when argv has no recognized command so
// `index.ts` can start the MCP server; the Godot CLI is a separate package
// from the MCP server and never starts one. A bare invocation (no args) prints
// help and exits 0; an unknown command exits non-zero.

import { parseCliArgs, KNOWN_COMMANDS } from "./args.js";
import {
  helpText,
  versionText,
  unknownCommandResult,
  type CliCommandResult,
} from "./commands.js";
import { installPluginCommand } from "./commands/install-plugin.js";
import { setupMcpCommand } from "./commands/setup-mcp.js";
import { EXIT } from "./exit-codes.js";
import { DEFAULT_BIN_NAME, PROJECT_PATH_ENV_VAR } from "./env.js";
import { readPackageVersion } from "./package-version.js";

export interface CliRunOptions {
  /** Package version, used by --version. */
  version: string;
  /** Invocation name for help text (e.g. "godot-open-mcp-cli"). */
  binName?: string;
  /** argv after the node binary + script path. Defaults to process.argv.slice(2). */
  argv?: string[];
}

export interface CliRunOutcome {
  /**
   * Always `true` for the Godot CLI — every invocation is handled (help,
   * version, or an error). Unlike Unity, there is no MCP-server fallthrough.
   */
  handled: boolean;
  /** Process exit code. */
  exitCode: number;
}

/**
 * Run the CLI. Returns the exit code; the caller (index.ts) calls
 * `process.exit`.
 *
 * This function writes to stdout/stderr itself and is meant to be the top of
 * the process. It does NOT call process.exit — the caller does, so tests can
 * drive it without tearing down the test runner.
 */
export async function runCli(opts: CliRunOptions): Promise<CliRunOutcome> {
  const argv = opts.argv ?? process.argv.slice(2);
  const parsed = parseCliArgs(argv);
  const binName = opts.binName ?? DEFAULT_BIN_NAME;

  // --help short-circuits before anything else. (parseCliArgs already maps a
  // bare invocation to "help" so `godot-open-mcp-cli` with no args lands here.)
  if (parsed.command === "help") {
    await writeAndDrain(process.stdout, helpText(binName) + "\n");
    return { handled: true, exitCode: EXIT.SUCCESS };
  }

  // --version.
  if (parsed.command === "version") {
    await writeAndDrain(process.stdout, versionText(opts.version) + "\n");
    return { handled: true, exitCode: EXIT.SUCCESS };
  }

  // Parse error (unknown command, bad flag value, unexpected positional).
  // The error message already carries the why; append help so the user sees
  // usage without a second invocation. Checked before command dispatch so a bad
  // flag value on a recognized command still surfaces the parse error.
  if (parsed.error) {
    const result: CliCommandResult = {
      exitCode: EXIT.ERRORS,
      json: {
        command: parsed.command,
        error: { code: "parse_error", message: parsed.error },
      },
      human: `${binName}: ${parsed.error}\n\n${helpText(binName)}`,
      errorLabel: "parse_error",
    };
    await emitResult(result, parsed.json);
    return { handled: true, exitCode: result.exitCode };
  }

  // Command dispatch. Each implemented command maps parsed argv → a library
  // call → a CliCommandResult, which the dispatcher prints + exits on.
  if (parsed.command === "install-plugin") {
    const projectPath = resolveProjectPath(parsed);
    const result = await installPluginCommand({
      projectPath,
      source: parsed.source,
    });
    await emitResult(result, parsed.json);
    return { handled: true, exitCode: result.exitCode };
  }

  if (parsed.command === "setup-mcp") {
    const projectPath = resolveProjectPath(parsed);
    const result = await setupMcpCommand({
      agentId: parsed.agentId,
      projectPath,
      useLocal: parsed.useLocal,
      list: parsed.list,
      configPath: parsed.configPath,
      packageVersion: readPackageVersion(),
    });
    await emitResult(result, parsed.json);
    return { handled: true, exitCode: result.exitCode };
  }

  // No command and no error — shouldn't happen (parseCliArgs defaults to
  // "help"), but guard so we never exit silently.
  await writeAndDrain(process.stdout, helpText(binName) + "\n");
  return { handled: true, exitCode: EXIT.SUCCESS };
}

/**
 * Resolve the Godot project path for a command: explicit `--project` flag wins,
 * else the positional `[path]`, else the `GODOT_PROJECT_PATH` env var, else cwd.
 * Centralized so every command applies the same precedence.
 */
function resolveProjectPath(parsed: { projectPath?: string; positionalPath?: string }): string {
  if (parsed.projectPath) return parsed.projectPath;
  if (parsed.positionalPath) return parsed.positionalPath;
  const env = process.env[PROJECT_PATH_ENV_VAR];
  if (env && env.length > 0) return env;
  return process.cwd();
}

/**
 * Minimal writable interface {@link writeAndDrain} needs. Narrowed from
 * NodeJS.WriteStream so the function is unit-testable with a small fake.
 */
export interface DrainableWritable {
  write(chunk: string): boolean;
  write(chunk: string, callback: (err?: Error | null) => void): boolean;
  once(event: "drain", listener: () => void): unknown;
}

/**
 * Write a string to a Writable stream and resolve once the OS has consumed it.
 *
 * `stream.write` is asynchronous when the destination is a pipe. There are two
 * layers of "pending": (1) Node's internal buffer fills past the high-water
 * mark → `.write()` returns `false` and the stream emits `'drain'` once the
 * buffer drops back below the mark; (2) libuv still has the write queued at the
 * kernel level after `'drain'` fires. Waiting only for `'drain'` was not
 * enough: `process.exit()` immediately afterwards truncated large payloads
 * (~64 KB on macOS) because the OS write was still in flight.
 *
 * The `write(chunk, callback)` overload is the correct primitive — its
 * callback fires only after libuv completes (or errors) the actual write to
 * the kernel, regardless of backpressure. Resolves immediately for TTY/file
 * destinations where the write is effectively synchronous.
 */
export function writeAndDrain(
  stream: DrainableWritable,
  chunk: string,
): Promise<void> {
  return new Promise((resolve, reject) => {
    stream.write(chunk, (err) => {
      if (err) reject(err);
      else resolve();
    });
  });
}

async function emitResult(
  result: CliCommandResult,
  json: boolean,
): Promise<void> {
  const stream = result.exitCode === 0 ? process.stdout : process.stderr;
  if (json) {
    await writeAndDrain(
      process.stdout,
      JSON.stringify(result.json, null, 2) + "\n",
    );
    return;
  }
  // Human-readable: keep success on stdout, failures on stderr so a CI
  // pipeline can capture the message without mixing it into stdout JSON.
  await writeAndDrain(stream, result.human + "\n");
}

// Re-export so command modules and tests can import from a single entry.
export { KNOWN_COMMANDS, unknownCommandResult };
export type { CliCommandResult };
