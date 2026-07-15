// Tests for the agent registry + stdio merge helpers
// (src/utils/agents.ts). The registry tests are pure assertions on the agent
// definitions; the merge tests are pure functions over inline JSON roots, so
// no filesystem is touched.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'
//
// The stdio entry shapes asserted here mirror Unity Open MCP's
// `docs/setup/manual-setup.md` (the canonical per-client envelopes), adapted
// with the `GODOT_PROJECT_PATH` env var and the `godot-open-mcp` server key.

import { test } from "node:test";
import assert from "node:assert/strict";
import * as path from "path";

import {
  MCP_SERVER_NAME,
  agentRegistry,
  getAgentById,
  getAgentIds,
  mergeStdioServerEntry,
  type StdioPropsInput,
} from "./agents.js";

const STDIO: StdioPropsInput = {
  command: "npx",
  args: ["-y", "godot-open-mcp@0.0.1"],
  env: { GODOT_PROJECT_PATH: "/abs/project" },
};

// ---------------------------------------------------------------------------
// registry shape
// ---------------------------------------------------------------------------

test("agentRegistry: every entry has a stable id + name + scope + bodyPath", () => {
  for (const a of agentRegistry) {
    assert.ok(a.id.length > 0, `${a.name} missing id`);
    assert.ok(a.name.length > 0, `${a.id} missing name`);
    assert.ok(a.scope === "project" || a.scope === "global", `${a.id} bad scope`);
    assert.ok(a.bodyPath.length > 0, `${a.id} missing bodyPath`);
    assert.ok(a.removeKeys.length > 0, `${a.id} has no removeKeys`);
    assert.equal(typeof a.getConfigPath, "function");
    assert.equal(typeof a.getStdioProps, "function");
  }
});

test("agentRegistry: ids are unique", () => {
  const ids = agentRegistry.map((a) => a.id);
  assert.equal(new Set(ids).size, ids.length);
});

test("agentRegistry: includes the Phase 6.3 Done-when set (cursor + claude-desktop + claude-code)", () => {
  const ids = getAgentIds();
  assert.ok(ids.includes("cursor"));
  assert.ok(ids.includes("claude-desktop"));
  assert.ok(ids.includes("claude-code"));
});

test("getAgentById: returns the agent by id, undefined when unknown", () => {
  assert.equal(getAgentById("cursor")?.id, "cursor");
  assert.equal(getAgentById("nope"), undefined);
});

// ---------------------------------------------------------------------------
// project-scoped config paths
// ---------------------------------------------------------------------------

test("cursor getConfigPath: project-local .cursor/mcp.json", () => {
  const a = getAgentById("cursor")!;
  assert.equal(a.getConfigPath("/p"), path.join("/p", ".cursor", "mcp.json"));
});

test("claude-code getConfigPath: project-local .mcp.json", () => {
  const a = getAgentById("claude-code")!;
  assert.equal(a.getConfigPath("/p"), path.join("/p", ".mcp.json"));
});

test("vscode-copilot getConfigPath: project-local .vscode/mcp.json", () => {
  const a = getAgentById("vscode-copilot")!;
  assert.equal(a.getConfigPath("/p"), path.join("/p", ".vscode", "mcp.json"));
});

test("opencode getConfigPath: project-local opencode.json", () => {
  const a = getAgentById("opencode")!;
  assert.equal(a.getConfigPath("/p"), path.join("/p", "opencode.json"));
});

// ---------------------------------------------------------------------------
// stdio entry shapes (per Unity manual-setup)
// ---------------------------------------------------------------------------

test("cursor getStdioProps: bare {command, args, env} shape", () => {
  const a = getAgentById("cursor")!;
  const props = a.getStdioProps(STDIO);
  assert.deepEqual(props, {
    command: "npx",
    args: ["-y", "godot-open-mcp@0.0.1"],
    env: { GODOT_PROJECT_PATH: "/abs/project" },
  });
  // No HTTP/transport keys.
  assert.equal("url" in props, false);
  assert.equal("type" in props, false);
  assert.equal("headers" in props, false);
});

test("claude-desktop getStdioProps: same bare shape as cursor", () => {
  const a = getAgentById("claude-desktop")!;
  const props = a.getStdioProps(STDIO);
  assert.deepEqual(props, {
    command: "npx",
    args: ["-y", "godot-open-mcp@0.0.1"],
    env: { GODOT_PROJECT_PATH: "/abs/project" },
  });
});

test("vscode-copilot getStdioProps: adds type:stdio (servers key)", () => {
  const a = getAgentById("vscode-copilot")!;
  const props = a.getStdioProps(STDIO);
  assert.deepEqual(props, {
    type: "stdio",
    command: "npx",
    args: ["-y", "godot-open-mcp@0.0.1"],
    env: { GODOT_PROJECT_PATH: "/abs/project" },
  });
});

test("opencode getStdioProps: command as array, environment key, type:local", () => {
  const a = getAgentById("opencode")!;
  const props = a.getStdioProps(STDIO);
  assert.deepEqual(props, {
    type: "local",
    command: ["npx", "-y", "godot-open-mcp@0.0.1"],
    enabled: true,
    environment: { GODOT_PROJECT_PATH: "/abs/project" },
  });
  // OpenCode uses `environment`, NOT `env`.
  assert.equal("env" in props, false);
});

// ---------------------------------------------------------------------------
// mergeStdioServerEntry — happy paths
// ---------------------------------------------------------------------------

test("mergeStdioServerEntry: writes the entry into an empty root", () => {
  const root = {};
  const props = getAgentById("cursor")!.getStdioProps(STDIO);
  const { root: next, changed } = mergeStdioServerEntry(
    root,
    ["mcpServers"],
    MCP_SERVER_NAME,
    props,
    ["url", "type", "headers"],
  );
  assert.equal(changed, true);
  const servers = (next as Record<string, unknown>).mcpServers as Record<string, unknown>;
  assert.deepEqual(servers[MCP_SERVER_NAME], props);
});

test("mergeStdioServerEntry: preserves sibling servers", () => {
  const root = {
    mcpServers: {
      "other-server": { command: "node", args: ["other.js"] },
    },
  };
  const props = getAgentById("cursor")!.getStdioProps(STDIO);
  const { root: next, changed } = mergeStdioServerEntry(
    root,
    ["mcpServers"],
    MCP_SERVER_NAME,
    props,
    ["url", "type"],
  );
  assert.equal(changed, true);
  const servers = (next as Record<string, unknown>).mcpServers as Record<string, unknown>;
  // Sibling untouched.
  assert.deepEqual(servers["other-server"], { command: "node", args: ["other.js"] });
  // Our entry added.
  assert.deepEqual(servers[MCP_SERVER_NAME], props);
});

test("mergeStdioServerEntry: walks nested bodyPath (mcp.servers for ZCode shape)", () => {
  const root = {};
  const props = getAgentById("cursor")!.getStdioProps(STDIO);
  const { root: next, changed } = mergeStdioServerEntry(
    root,
    ["mcp", "servers"],
    MCP_SERVER_NAME,
    props,
    ["url"],
  );
  assert.equal(changed, true);
  const top = (next as Record<string, unknown>).mcp as Record<string, unknown>;
  const servers = top.servers as Record<string, unknown>;
  assert.deepEqual(servers[MCP_SERVER_NAME], props);
});

test("mergeStdioServerEntry: preserves unrelated top-level keys", () => {
  const root = {
    "$schema": "https://opencode.ai/config.json",
    "mcp": {},
    "theme": "dark",
  };
  const props = getAgentById("opencode")!.getStdioProps(STDIO);
  const { root: next } = mergeStdioServerEntry(
    root,
    ["mcp"],
    MCP_SERVER_NAME,
    props,
    ["url"],
  );
  assert.deepEqual(next["$schema"], "https://opencode.ai/config.json");
  assert.deepEqual(next["theme"], "dark");
});

// ---------------------------------------------------------------------------
// mergeStdioServerEntry — strip foreign HTTP keys
// ---------------------------------------------------------------------------

test("mergeStdioServerEntry: strips stale url/headers from a prior HTTP entry", () => {
  // Simulate a prior Godot-MCP HTTP entry on our server key.
  const root = {
    mcpServers: {
      [MCP_SERVER_NAME]: {
        type: "http",
        url: "https://ai-game.dev/mcp",
        headers: { Authorization: "Bearer xxx" },
      },
    },
  };
  const props = getAgentById("cursor")!.getStdioProps(STDIO);
  const { root: next, changed } = mergeStdioServerEntry(
    root,
    ["mcpServers"],
    MCP_SERVER_NAME,
    props,
    ["url", "type", "headers"],
  );
  assert.equal(changed, true);
  const entry = (next as Record<string, unknown>).mcpServers as Record<string, unknown>;
  const ourEntry = entry[MCP_SERVER_NAME] as Record<string, unknown>;
  // Foreign HTTP keys gone.
  assert.equal("url" in ourEntry, false);
  assert.equal("headers" in ourEntry, false);
  assert.equal("type" in ourEntry, false);
  // stdio keys present.
  assert.equal(ourEntry.command, "npx");
  assert.deepEqual(ourEntry.args, ["-y", "godot-open-mcp@0.0.1"]);
});

test("mergeStdioServerEntry: preserves user-authored custom keys on our entry", () => {
  // A user-added `notes` field should survive a re-run.
  const root = {
    mcpServers: {
      [MCP_SERVER_NAME]: {
        command: "npx",
        args: ["-y", "godot-open-mcp@0.0.1"],
        env: { GODOT_PROJECT_PATH: "/abs/project" },
        notes: "my local server",
      },
    },
  };
  const props = getAgentById("cursor")!.getStdioProps(STDIO);
  const { root: next, changed } = mergeStdioServerEntry(
    root,
    ["mcpServers"],
    MCP_SERVER_NAME,
    props,
    ["url"],
  );
  // Unchanged — entry already matches + custom key preserved.
  assert.equal(changed, false);
  const entry = ((next as Record<string, unknown>).mcpServers as Record<string, unknown>)[
    MCP_SERVER_NAME
  ] as Record<string, unknown>;
  assert.equal(entry.notes, "my local server");
});

// ---------------------------------------------------------------------------
// mergeStdioServerEntry — idempotency
// ---------------------------------------------------------------------------

test("mergeStdioServerEntry: re-merge reports changed:false when entry already matches", () => {
  const props = getAgentById("cursor")!.getStdioProps(STDIO);
  const root = {
    mcpServers: {
      [MCP_SERVER_NAME]: { ...props },
    },
  };
  const { changed } = mergeStdioServerEntry(
    root,
    ["mcpServers"],
    MCP_SERVER_NAME,
    props,
    ["url", "type"],
  );
  assert.equal(changed, false);
});

test("mergeStdioServerEntry: changed:true when args differ (version bump)", () => {
  const props = getAgentById("cursor")!.getStdioProps(STDIO);
  const root = {
    mcpServers: {
      [MCP_SERVER_NAME]: {
        command: "npx",
        args: ["-y", "godot-open-mcp@0.0.0"], // older version
        env: { GODOT_PROJECT_PATH: "/abs/project" },
      },
    },
  };
  const { changed } = mergeStdioServerEntry(
    root,
    ["mcpServers"],
    MCP_SERVER_NAME,
    props,
    ["url", "type"],
  );
  assert.equal(changed, true);
});

test("mergeStdioServerEntry: pure — does not mutate the input root", () => {
  const root = {
    mcpServers: {
      [MCP_SERVER_NAME]: { command: "old" },
    },
  };
  const props = getAgentById("cursor")!.getStdioProps(STDIO);
  mergeStdioServerEntry(root, ["mcpServers"], MCP_SERVER_NAME, props, ["url"]);
  // The original entry is untouched (we shallow-cloned the root).
  const entry = (root.mcpServers as Record<string, unknown>)[MCP_SERVER_NAME] as Record<string, unknown>;
  assert.deepEqual(entry, { command: "old" });
});
