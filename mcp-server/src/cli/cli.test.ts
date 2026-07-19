// Tests for the CLI dispatcher glue (`runCli`).
//
// Covers the paths args/commands tests can't: --help / --version output,
// no-command fall-through, usage-error + env-error exit codes, and --json vs
// human stream routing. The unknown-tool cases also exercise the
// `finally { stack.eventStream.stop() }` teardown (buildRouterStack runs but no
// bridge is contacted, since an unknown tool short-circuits before routing).

import test from "node:test";
import assert from "node:assert/strict";

import { runCli } from "./cli.js";

/** Capture process stdout/stderr writes for the duration of `fn`. */
async function capture(
  fn: () => Promise<unknown>,
): Promise<{ out: string; err: string; result: unknown }> {
  const origOut = process.stdout.write.bind(process.stdout);
  const origErr = process.stderr.write.bind(process.stderr);
  let out = "";
  let err = "";
  process.stdout.write = ((chunk: string) => {
    out += chunk;
    return true;
  }) as typeof process.stdout.write;
  process.stderr.write = ((chunk: string) => {
    err += chunk;
    return true;
  }) as typeof process.stderr.write;
  try {
    const result = await fn();
    return { out, err, result };
  } finally {
    process.stdout.write = origOut;
    process.stderr.write = origErr;
  }
}

test("--version prints the supplied version and exits 0", async () => {
  const { out, result } = await capture(() =>
    runCli({ version: "9.9.9", argv: ["--version"] }),
  );
  assert.deepEqual(result, { handled: true, exitCode: 0 });
  assert.match(out, /godot-open-mcp 9\.9\.9/);
});

test("--help prints usage and exits 0", async () => {
  const { out, result } = await capture(() =>
    runCli({ version: "0.0.0", argv: ["--help"] }),
  );
  assert.deepEqual(result, { handled: true, exitCode: 0 });
  assert.match(out, /Usage: godot-open-mcp/);
});

test("no recognized command falls through (handled: false)", async () => {
  const { result } = await capture(() =>
    runCli({ version: "0.0.0", argv: [] }),
  );
  assert.deepEqual(result, { handled: false, exitCode: 0 });
});

test("usage error (run-tool with no tool name) exits 2 to stderr", async () => {
  const { err, result } = await capture(() =>
    runCli({ version: "0.0.0", argv: ["run-tool", "--json"] }),
  );
  assert.deepEqual(result, { handled: true, exitCode: 2 });
  assert.match(err, /tool name/);
});

test("missing project path is an env error, exit 2 to stderr", async () => {
  const saved = process.env.GODOT_PROJECT_PATH;
  delete process.env.GODOT_PROJECT_PATH;
  try {
    const { err, result } = await capture(() =>
      runCli({
        version: "0.0.0",
        argv: ["run-tool", "godot_open_mcp_ping", "--json"],
      }),
    );
    assert.deepEqual(result, { handled: true, exitCode: 2 });
    assert.match(err, /GODOT_PROJECT_PATH/);
  } finally {
    if (saved !== undefined) process.env.GODOT_PROJECT_PATH = saved;
  }
});

test("unknown tool with --project --json emits JSON to stdout, exit 2, no hang", async () => {
  // buildRouterStack runs (LiveClient + event stream constructed) but the
  // unknown tool short-circuits before any bridge hop; the finally block stops
  // the event stream so the process exits cleanly.
  const { out, result } = await capture(() =>
    runCli({
      version: "0.0.0",
      argv: ["run-tool", "godot_open_mcp_not_real", "--project", "/tmp", "--json"],
    }),
  );
  assert.deepEqual(result, { handled: true, exitCode: 2 });
  const parsed = JSON.parse(out);
  assert.equal(parsed.command, "run-tool");
  assert.equal(parsed.isError, true);
  assert.equal(parsed.error.code, "unknown_tool");
});

test("unknown tool without --json writes human output to stderr", async () => {
  const { err, result } = await capture(() =>
    runCli({
      version: "0.0.0",
      argv: ["run-tool", "godot_open_mcp_not_real", "--project", "/tmp"],
    }),
  );
  assert.equal((result as { exitCode: number }).exitCode, 2);
  assert.match(err, /Unknown tool/);
});
