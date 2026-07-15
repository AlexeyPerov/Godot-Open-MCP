// Integration tests for the `setup-mcp` library (src/lib/setup-mcp.ts).
//
// Each test builds a temp Godot project + a temp config-file location on disk,
// runs the writer, and asserts on the resulting file contents + the returned
// result union. The temp dirs are cleaned up in a finally block. No real MCP
// client or Godot editor is involved — the writer is pure Node fs.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";

import { setupMcp } from "./setup-mcp.js";
import { MCP_SERVER_NAME } from "../utils/agents.js";

// ---------------------------------------------------------------------------
// temp-fixture helpers
// ---------------------------------------------------------------------------

function scratchDir(): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), "godot-open-mcp-setup-"));
}

function cleanup(dir: string): void {
  fs.rmSync(dir, { recursive: true, force: true });
}

function buildGodotProject(root: string): string {
  const project = path.join(root, "project");
  fs.mkdirSync(project, { recursive: true });
  fs.writeFileSync(
    path.join(project, "project.godot"),
    '[application]\n\nname="T"\nconfig_version=5\n',
  );
  return project;
}

const PKG_VERSION = "0.0.1";

// ---------------------------------------------------------------------------
// happy path — project-scoped (cursor)
// ---------------------------------------------------------------------------

test("setupMcp: writes a cursor stdio config into a project", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir);
    const result = await setupMcp({
      agentId: "cursor",
      godotProjectPath: project,
      packageVersion: PKG_VERSION,
    });
    assert.equal(result.kind, "success");
    if (result.kind !== "success") return;
    assert.equal(result.changed, true);
    assert.equal(result.agentId, "cursor");
    assert.equal(result.serverName, MCP_SERVER_NAME);
    assert.equal(result.transport, "stdio");
    assert.equal(result.configPath, path.join(project, ".cursor", "mcp.json"));
    assert.equal(result.stdio.command, "npx");
    assert.deepEqual(result.stdio.args, ["-y", `godot-open-mcp@${PKG_VERSION}`]);
    assert.equal(result.stdio.env.GODOT_PROJECT_PATH, project);

    // File landed with the canonical bare-stdio shape under mcpServers.
    const written = JSON.parse(
      fs.readFileSync(result.configPath, "utf-8"),
    ) as Record<string, unknown>;
    const servers = written.mcpServers as Record<string, unknown>;
    assert.deepEqual(servers[MCP_SERVER_NAME], {
      command: "npx",
      args: ["-y", `godot-open-mcp@${PKG_VERSION}`],
      env: { GODOT_PROJECT_PATH: project },
    });
    // No foreign HTTP keys leaked.
    assert.equal("url" in (servers[MCP_SERVER_NAME] as Record<string, unknown>), false);
  } finally {
    cleanup(dir);
  }
});

test("setupMcp: GODOT_PROJECT_PATH in env is always absolute", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir);
    const result = await setupMcp({
      agentId: "cursor",
      godotProjectPath: project,
      packageVersion: PKG_VERSION,
    });
    assert.equal(result.kind, "success");
    if (result.kind !== "success") return;
    assert.ok(path.isAbsolute(result.stdio.env.GODOT_PROJECT_PATH));
  } finally {
    cleanup(dir);
  }
});

// ---------------------------------------------------------------------------
// global agent — claude-desktop
// ---------------------------------------------------------------------------

test("setupMcp: claude-desktop writes a global config (no project.godot required)", async () => {
  const dir = scratchDir();
  try {
    // A project path without project.godot is fine for a global agent.
    const project = path.join(dir, "project");
    fs.mkdirSync(project, { recursive: true });
    // Redirect the config file into the temp dir via --config-path so the test
    // doesn't clobber the user's real Claude Desktop config.
    const configPath = path.join(dir, "claude_desktop_config.json");

    const result = await setupMcp({
      agentId: "claude-desktop",
      godotProjectPath: project,
      configPath,
      packageVersion: PKG_VERSION,
    });
    assert.equal(result.kind, "success");
    if (result.kind !== "success") return;
    assert.equal(result.changed, true);
    assert.equal(result.configPath, configPath);

    const written = JSON.parse(
      fs.readFileSync(configPath, "utf-8"),
    ) as Record<string, unknown>;
    const servers = written.mcpServers as Record<string, unknown>;
    assert.deepEqual(servers[MCP_SERVER_NAME], {
      command: "npx",
      args: ["-y", `godot-open-mcp@${PKG_VERSION}`],
      env: { GODOT_PROJECT_PATH: project },
    });
  } finally {
    cleanup(dir);
  }
});

// ---------------------------------------------------------------------------
// idempotency
// ---------------------------------------------------------------------------

test("setupMcp: re-run reports changed:false and does not rewrite the file", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir);
    const first = await setupMcp({
      agentId: "cursor",
      godotProjectPath: project,
      packageVersion: PKG_VERSION,
    });
    assert.equal(first.kind, "success");
    if (first.kind !== "success") return;
    assert.equal(first.changed, true);

    const textAfterFirst = fs.readFileSync(first.configPath, "utf-8");

    const second = await setupMcp({
      agentId: "cursor",
      godotProjectPath: project,
      packageVersion: PKG_VERSION,
    });
    assert.equal(second.kind, "success");
    if (second.kind !== "success") return;
    assert.equal(second.changed, false);

    // Byte-identical (no churn).
    const textAfterSecond = fs.readFileSync(second.configPath, "utf-8");
    assert.equal(textAfterFirst, textAfterSecond);
  } finally {
    cleanup(dir);
  }
});

test("setupMcp: version bump reports changed:true and updates args", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir);
    await setupMcp({
      agentId: "cursor",
      godotProjectPath: project,
      packageVersion: "0.0.1",
    });
    const second = await setupMcp({
      agentId: "cursor",
      godotProjectPath: project,
      packageVersion: "0.0.2",
    });
    assert.equal(second.kind, "success");
    if (second.kind !== "success") return;
    assert.equal(second.changed, true);
    assert.deepEqual(second.stdio.args, ["-y", "godot-open-mcp@0.0.2"]);
  } finally {
    cleanup(dir);
  }
});

// ---------------------------------------------------------------------------
// preserve sibling servers + strip foreign HTTP keys
// ---------------------------------------------------------------------------

test("setupMcp: preserves sibling servers in the same mcpServers map", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir);
    const configPath = path.join(project, ".cursor", "mcp.json");
    fs.mkdirSync(path.dirname(configPath), { recursive: true });
    fs.writeFileSync(
      configPath,
      JSON.stringify({
        mcpServers: {
          "other-tool": { command: "node", args: ["other.js"] },
        },
      }),
    );

    const result = await setupMcp({
      agentId: "cursor",
      godotProjectPath: project,
      packageVersion: PKG_VERSION,
    });
    assert.equal(result.kind, "success");

    const written = JSON.parse(
      fs.readFileSync(result.configPath, "utf-8"),
    ) as Record<string, unknown>;
    const servers = written.mcpServers as Record<string, unknown>;
    // Sibling preserved.
    assert.deepEqual(servers["other-tool"], { command: "node", args: ["other.js"] });
    // Our entry present.
    assert.ok(servers[MCP_SERVER_NAME]);
  } finally {
    cleanup(dir);
  }
});

test("setupMcp: strips stale url/headers from a prior HTTP entry on our key", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir);
    const configPath = path.join(project, ".cursor", "mcp.json");
    fs.mkdirSync(path.dirname(configPath), { recursive: true });
    fs.writeFileSync(
      configPath,
      JSON.stringify({
        mcpServers: {
          [MCP_SERVER_NAME]: {
            type: "http",
            url: "https://ai-game.dev/mcp",
            headers: { Authorization: "Bearer xxx" },
          },
        },
      }),
    );

    const result = await setupMcp({
      agentId: "cursor",
      godotProjectPath: project,
      packageVersion: PKG_VERSION,
    });
    assert.equal(result.kind, "success");
    if (result.kind !== "success") return;
    assert.equal(result.changed, true);

    const written = JSON.parse(
      fs.readFileSync(result.configPath, "utf-8"),
    ) as Record<string, unknown>;
    const ourEntry = (written.mcpServers as Record<string, unknown>)[
      MCP_SERVER_NAME
    ] as Record<string, unknown>;
    assert.equal("url" in ourEntry, false);
    assert.equal("headers" in ourEntry, false);
    assert.equal("type" in ourEntry, false);
    assert.equal(ourEntry.command, "npx");
  } finally {
    cleanup(dir);
  }
});

// ---------------------------------------------------------------------------
// VS Code variant (servers key + type:stdio)
// ---------------------------------------------------------------------------

test("setupMcp: vscode-copilot writes under `servers` with type:stdio", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir);
    const result = await setupMcp({
      agentId: "vscode-copilot",
      godotProjectPath: project,
      packageVersion: PKG_VERSION,
    });
    assert.equal(result.kind, "success");
    if (result.kind !== "success") return;
    assert.equal(result.configPath, path.join(project, ".vscode", "mcp.json"));

    const written = JSON.parse(
      fs.readFileSync(result.configPath, "utf-8"),
    ) as Record<string, unknown>;
    // Under `servers`, not `mcpServers`.
    assert.ok(written.servers);
    assert.equal(written.mcpServers, undefined);
    const entry = (written.servers as Record<string, unknown>)[
      MCP_SERVER_NAME
    ] as Record<string, unknown>;
    assert.equal(entry.type, "stdio");
  } finally {
    cleanup(dir);
  }
});

// ---------------------------------------------------------------------------
// --use-local variant
// ---------------------------------------------------------------------------

test("setupMcp: --use-local writes node + monorepo dist path", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir);
    const result = await setupMcp({
      agentId: "cursor",
      godotProjectPath: project,
      useLocal: true,
    });
    assert.equal(result.kind, "success");
    if (result.kind !== "success") return;
    assert.equal(result.stdio.command, "node");
    assert.equal(result.stdio.args.length, 1);
    assert.ok(result.stdio.args[0].includes("mcp-server"), `${result.stdio.args[0]}`);
    assert.ok(path.isAbsolute(result.stdio.args[0]));
    assert.equal(result.stdio.args[0].endsWith(path.join("mcp-server", "dist", "index.js")), true);
  } finally {
    cleanup(dir);
  }
});

test("setupMcp: --use-local re-run is idempotent", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir);
    const first = await setupMcp({
      agentId: "cursor",
      godotProjectPath: project,
      useLocal: true,
    });
    assert.equal(first.kind, "success");
    if (first.kind !== "success") return;
    assert.equal(first.changed, true);

    const second = await setupMcp({
      agentId: "cursor",
      godotProjectPath: project,
      useLocal: true,
    });
    assert.equal(second.kind, "success");
    if (second.kind !== "success") return;
    assert.equal(second.changed, false);
  } finally {
    cleanup(dir);
  }
});

// ---------------------------------------------------------------------------
// failure modes
// ---------------------------------------------------------------------------

test("setupMcp: unknown_agent when id is not in the registry", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir);
    const result = await setupMcp({
      agentId: "bogus",
      godotProjectPath: project,
    });
    assert.equal(result.kind, "failure");
    if (result.kind !== "failure") return;
    assert.equal(result.errorLabel, "unknown_agent");
    assert.match(result.error.message, /bogus/);
  } finally {
    cleanup(dir);
  }
});

test("setupMcp: unknown_agent when agentId is empty", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir);
    const result = await setupMcp({ agentId: "", godotProjectPath: project });
    assert.equal(result.kind, "failure");
    if (result.kind !== "failure") return;
    assert.equal(result.errorLabel, "unknown_agent");
  } finally {
    cleanup(dir);
  }
});

test("setupMcp: not_godot_project when project-scoped agent + missing project.godot", async () => {
  const dir = scratchDir();
  try {
    const notAProject = path.join(dir, "no-project");
    fs.mkdirSync(notAProject, { recursive: true });
    const result = await setupMcp({
      agentId: "cursor",
      godotProjectPath: notAProject,
    });
    assert.equal(result.kind, "failure");
    if (result.kind !== "failure") return;
    assert.equal(result.errorLabel, "not_godot_project");
    assert.match(result.error.message, /project\.godot/);
  } finally {
    cleanup(dir);
  }
});

test("setupMcp: rejects empty godotProjectPath", async () => {
  const result = await setupMcp({ agentId: "cursor", godotProjectPath: "" });
  assert.equal(result.kind, "failure");
  if (result.kind !== "failure") return;
  assert.equal(result.errorLabel, "not_godot_project");
});

test("setupMcp: invalid_existing_config when the config file is not JSON", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir);
    const configPath = path.join(project, ".cursor", "mcp.json");
    fs.mkdirSync(path.dirname(configPath), { recursive: true });
    fs.writeFileSync(configPath, "not json {{{");

    const result = await setupMcp({
      agentId: "cursor",
      godotProjectPath: project,
      configPath,
      packageVersion: PKG_VERSION,
    });
    assert.equal(result.kind, "failure");
    if (result.kind !== "failure") return;
    assert.equal(result.errorLabel, "invalid_existing_config");
  } finally {
    cleanup(dir);
  }
});

test("setupMcp: invalid_existing_config when the root is not an object", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir);
    const configPath = path.join(project, ".cursor", "mcp.json");
    fs.mkdirSync(path.dirname(configPath), { recursive: true });
    fs.writeFileSync(configPath, JSON.stringify(["not", "an", "object"]));

    const result = await setupMcp({
      agentId: "cursor",
      godotProjectPath: project,
      configPath,
      packageVersion: PKG_VERSION,
    });
    assert.equal(result.kind, "failure");
    if (result.kind !== "failure") return;
    assert.equal(result.errorLabel, "invalid_existing_config");
    assert.match(result.error.message, /object/);
  } finally {
    cleanup(dir);
  }
});

// ---------------------------------------------------------------------------
// missing version fallback
// ---------------------------------------------------------------------------

test("setupMcp: missing packageVersion writes an unpinned npx spec", async () => {
  const dir = scratchDir();
  try {
    const project = buildGodotProject(dir);
    const result = await setupMcp({
      agentId: "cursor",
      godotProjectPath: project,
      // no packageVersion
    });
    assert.equal(result.kind, "success");
    if (result.kind !== "success") return;
    assert.deepEqual(result.stdio.args, ["-y", "godot-open-mcp"]);
  } finally {
    cleanup(dir);
  }
});
