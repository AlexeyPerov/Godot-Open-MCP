// Tests for the run-tool command shaping.
//
// Pins the stdout JSON contract the Validation Suite's Rust runner
// (`src-tauri/src/mcp_runner.rs`) parses: `{ command, tool, isError, result }`,
// plus the unknown-tool usage error. A fake router stack stands in for the live
// bridge so these run offline (no editor / bridge required).

import test from "node:test";
import assert from "node:assert/strict";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

import {
  runRunToolCommand,
  resolveEnv,
  ResolveEnvError,
  type RouterStack,
} from "./commands.js";

function fakeStack(route: () => Promise<CallToolResult>): RouterStack {
  return {
    router: { route } as unknown as RouterStack["router"],
    eventStream: { stop() {} } as unknown as RouterStack["eventStream"],
    projectPath: "/proj/demo",
    port: 22028,
  };
}

test("unknown tool returns a structured error with exit code 2", async () => {
  const stack = fakeStack(async () => {
    throw new Error("router should not be called for an unknown tool");
  });
  const res = await runRunToolCommand(stack, {
    toolName: "godot_open_mcp_not_a_real_tool",
    toolArgs: {},
  });
  assert.equal(res.exitCode, 2);
  const json = res.json as {
    command: string;
    tool: string;
    isError?: boolean;
    error?: { code: string };
  };
  assert.equal(json.command, "run-tool");
  // Uniform envelope: unknown tool is flagged isError so the Rust runner (and
  // the suite's action log) never treat it as a successful action.
  assert.equal(json.isError, true);
  assert.equal(json.error?.code, "unknown_tool");
});

test("known tool success emits { command, tool, isError:false, result } with exit 0", async () => {
  const stack = fakeStack(async () => ({
    content: [{ type: "text", text: JSON.stringify({ ok: true }) }],
    isError: false,
  }));
  const res = await runRunToolCommand(stack, {
    toolName: "godot_open_mcp_ping",
    toolArgs: {},
  });
  assert.equal(res.exitCode, 0);
  assert.deepEqual(res.json, {
    command: "run-tool",
    tool: "godot_open_mcp_ping",
    isError: false,
    result: { ok: true },
  });
});

test("tool error surfaces isError:true and exit code 1", async () => {
  const stack = fakeStack(async () => ({
    content: [{ type: "text", text: JSON.stringify({ error: { code: "bridge_offline" } }) }],
    isError: true,
  }));
  const res = await runRunToolCommand(stack, {
    toolName: "godot_open_mcp_ping",
    toolArgs: {},
  });
  assert.equal(res.exitCode, 1);
  const json = res.json as { isError: boolean };
  assert.equal(json.isError, true);
});

test("result body falls back to the whole result for non-text content", async () => {
  const raw: CallToolResult = {
    content: [{ type: "image", data: "abc", mimeType: "image/png" }],
    isError: false,
  };
  const stack = fakeStack(async () => raw);
  const res = await runRunToolCommand(stack, {
    toolName: "godot_open_mcp_ping",
    toolArgs: {},
  });
  const json = res.json as { result: unknown };
  // Non-text first content → extractResultBody returns the entire result.
  assert.deepEqual(json.result, raw);
});

test("result body is the raw string when text content is not JSON", async () => {
  const stack = fakeStack(async () => ({
    content: [{ type: "text", text: "not json at all" }],
    isError: false,
  }));
  const res = await runRunToolCommand(stack, {
    toolName: "godot_open_mcp_ping",
    toolArgs: {},
  });
  const json = res.json as { result: unknown };
  assert.equal(json.result, "not json at all");
});

test("resolveEnv throws ResolveEnvError when no project path is set", () => {
  const saved = process.env.GODOT_PROJECT_PATH;
  delete process.env.GODOT_PROJECT_PATH;
  try {
    assert.throws(() => resolveEnv(undefined, undefined), ResolveEnvError);
  } finally {
    if (saved !== undefined) process.env.GODOT_PROJECT_PATH = saved;
  }
});

test("resolveEnv honours an explicit project path override", () => {
  const env = resolveEnv("/explicit/project", 22028);
  assert.equal(env.projectPath, "/explicit/project");
  assert.equal(env.port, 22028);
});
