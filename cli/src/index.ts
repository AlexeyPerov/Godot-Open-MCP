#!/usr/bin/env node

// CLI process entry — the bin target for `godot-open-mcp-cli`.
//
// Adapted from Unity Open MCP's `mcp-server/src/index.ts` CLI dispatch. Unlike
// Unity (where the same bin falls through to a stdio MCP server when argv has
// no recognized command), this is a dedicated CLI package: every invocation is
// a CLI command (or help/version), and the process always exits with the
// dispatcher's exit code. The MCP server lives in `mcp-server/` and has its
// own bin (`godot-open-mcp`).

import { runCli } from "./cli.js";
import { readPackageVersion } from "./package-version.js";
import { DEFAULT_BIN_NAME } from "./env.js";

async function main(): Promise<void> {
  const outcome = await runCli({
    version: readPackageVersion(),
    binName: DEFAULT_BIN_NAME,
  });
  // The Godot CLI always handles the invocation (no MCP fallthrough) — exit
  // with the dispatcher's code.
  process.exit(outcome.exitCode);
}

main().catch((err) => {
  console.error(`${DEFAULT_BIN_NAME} fatal:`, err);
  process.exit(1);
});
