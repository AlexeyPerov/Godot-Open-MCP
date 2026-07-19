// CLI-parity fixture test for the Hub's MCP stdio-entry builder.
//
// Hard rule (P11.2): at least one automated assertion that the Hub's MCP entry
// matches the CLI `setup-mcp` output for the same inputs — server key, env
// keys, no `url`. These fixtures are the exact shapes the CLI's
// `buildStdioProps` (cli/src/lib/setup-mcp.ts) + `bareStdio`
// (cli/src/utils/agents.ts) produce. If the Rust writer or the CLI changes,
// this test and the Rust `mcp_config` tests should catch the drift together.
//
// Run: node --test --experimental-strip-types --no-warnings src/lib/**/*.test.ts

import { test } from "node:test";
import assert from "node:assert/strict";

import { buildBareEntry, buildMcpEnv } from "./mcp_entry.ts";
import { MCP_SERVER_NAME } from "../version.ts";

test("server key matches the CLI + package name", () => {
  assert.equal(MCP_SERVER_NAME, "godot-open-mcp");
});

test("npx entry matches the canonical CLI shape (pinned version)", () => {
  const entry = buildBareEntry({
    projectPath: "/abs/proj",
    source: "npx-published",
    packageVersion: "0.0.1",
  });
  assert.deepEqual(entry, {
    command: "npx",
    args: ["-y", "godot-open-mcp@0.0.1"],
    env: { GODOT_PROJECT_PATH: "/abs/proj" },
  });
});

test("npx entry without a version falls back to the bare package name", () => {
  const entry = buildBareEntry({ projectPath: "/abs/proj", source: "npx-published" });
  assert.deepEqual(entry, {
    command: "npx",
    args: ["-y", "godot-open-mcp"],
    env: { GODOT_PROJECT_PATH: "/abs/proj" },
  });
});

test("local-checkout entry uses node + <repo>/mcp-server/dist/index.js", () => {
  const entry = buildBareEntry({
    projectPath: "/abs/proj",
    source: "local-checkout",
    monorepoPath: "/repos/Godot-Open-MCP",
  });
  assert.deepEqual(entry, {
    command: "node",
    args: ["/repos/Godot-Open-MCP/mcp-server/dist/index.js"],
    env: { GODOT_PROJECT_PATH: "/abs/proj" },
  });
});

test("override port is written as GODOT_OPEN_MCP_BRIDGE_PORT (string)", () => {
  const env = buildMcpEnv("/abs/proj", 24567);
  assert.deepEqual(env, {
    GODOT_PROJECT_PATH: "/abs/proj",
    GODOT_OPEN_MCP_BRIDGE_PORT: "24567",
  });
});

test("entry never contains url or http type keys (ADR-001 stdio only)", () => {
  const entry = buildBareEntry({
    projectPath: "/abs/proj",
    source: "npx-published",
    packageVersion: "0.0.1",
  });
  assert.equal("url" in entry, false);
  assert.equal("type" in entry, false);
  assert.equal("headers" in entry, false);
});

test("GODOT_PROJECT_PATH is the absolute project path", () => {
  const env = buildMcpEnv("/Users/me/MyGame");
  assert.equal(env.GODOT_PROJECT_PATH, "/Users/me/MyGame");
  assert.equal("GODOT_OPEN_MCP_BRIDGE_PORT" in env, false);
});
