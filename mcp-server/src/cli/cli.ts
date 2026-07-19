// CLI dispatcher.
//
// Adapted from Unity Open MCP's `mcp-server/src/cli/cli.ts`
// (copy/adapt fidelity). Entry point for `godot-open-mcp <command>`:
//   1. parse argv (./args.ts)
//   2. handle --help / --version fast paths
//   3. build the router stack (./commands.ts) and dispatch the command
//   4. print JSON or human output and return an exit code
//
// When argv[0] is NOT a known subcommand, `runCli` returns `handled: false`
// so the caller (index.ts) falls through to the stdio MCP server. That keeps a
// single `bin` working for both `godot-open-mcp run-tool …` and an MCP client
// spawning `node dist/index.js`.

import { parseCliArgs } from "./args.js";
import { helpText, versionText } from "./help-text.js";
import { readPackageVersion } from "../package-version.js";
import {
  resolveEnv,
  buildRouterStack,
  runRunToolCommand,
  ResolveEnvError,
  type RouterStack,
  type CliCommandResult,
} from "./commands.js";

export interface CliRunOptions {
  /** Package version, used by --version. */
  version: string;
  /** Invocation name for help text. */
  binName?: string;
  /** argv after node + script path. Defaults to process.argv.slice(2). */
  argv?: string[];
}

export interface CliRunOutcome {
  /** True when argv recognized a CLI subcommand (or --help/--version). */
  handled: boolean;
  /** Process exit code; only meaningful when handled === true. */
  exitCode: number;
}

/**
 * Run the CLI. Returns whether the invocation was a CLI subcommand so index.ts
 * can decide whether to fall through to the stdio server. Writes to
 * stdout/stderr directly but does NOT call process.exit — the caller does, so
 * tests can drive it without tearing down the test runner.
 */
export async function runCli(opts: CliRunOptions): Promise<CliRunOutcome> {
  const argv = opts.argv ?? process.argv.slice(2);
  const binName = opts.binName ?? "godot-open-mcp";
  const parsed = parseCliArgs(argv);

  if (parsed.command === "help") {
    process.stdout.write(helpText(binName) + "\n");
    return { handled: true, exitCode: 0 };
  }
  if (parsed.command === "version") {
    const version = opts.version || readPackageVersion();
    process.stdout.write(versionText(version) + "\n");
    return { handled: true, exitCode: 0 };
  }

  // No recognized command → caller falls through to the stdio server.
  if (!parsed.command) {
    return { handled: false, exitCode: 0 };
  }

  if (parsed.error) {
    process.stderr.write(
      `${binName}: ${parsed.error}\n\n${helpText(binName)}\n`,
    );
    return { handled: true, exitCode: 2 };
  }

  let stack: RouterStack;
  try {
    const env = resolveEnv(parsed.projectPath, parsed.port);
    process.stderr.write(
      `[${binName}] Bridge port resolved to ${env.port} for project ${env.projectPath}\n`,
    );
    stack = buildRouterStack(env);
  } catch (err) {
    if (err instanceof ResolveEnvError) {
      process.stderr.write(`${binName}: ${err.message}\n`);
      return { handled: true, exitCode: 2 };
    }
    throw err;
  }

  let result: CliCommandResult;
  try {
    switch (parsed.command) {
      case "run-tool":
        result = await runRunToolCommand(stack, {
          toolName: parsed.toolName!,
          toolArgs: parsed.toolArgs,
        });
        break;
      default:
        process.stderr.write(
          `${binName}: internal error — unhandled command '${parsed.command}'.\n`,
        );
        return { handled: true, exitCode: 2 };
    }
  } finally {
    // Tear down the SSE reader (run-tool may have started one via pull_events)
    // so the process can exit cleanly.
    try {
      stack.eventStream.stop();
    } catch {
      // best-effort
    }
  }

  if (parsed.json) {
    process.stdout.write(JSON.stringify(result.json, null, 2) + "\n");
  } else {
    const stream = result.exitCode === 0 ? process.stdout : process.stderr;
    stream.write(result.human + "\n");
  }
  return { handled: true, exitCode: result.exitCode };
}
