// Idempotent stdio MCP-client config writer — writes / merges a `godot-open-mcp`
// server entry into an AI client's config so the client can spawn the local
// stdio MCP server.
//
// Adapted from the Godot-MCP behavior reference (`cli/src/lib/setup-mcp.ts`)
// with the HTTP URL + cloud-host + token surfaces stripped entirely (ADR-001:
// stdio-only transport). The library resolves the agent registry, builds the
// stdio spawn descriptor (npx by default, or `node` against the monorepo
// checkout when `useLocal`), and merges the entry into the client config
// atomically, preserving sibling servers and stripping any foreign HTTP keys a
// prior Godot-MCP HTTP entry could have left behind.
//
// Library-safe: no stdout noise, no `process.exit`, no throws past the public
// boundary; returns a `{ kind: "success" | "failure" }` union. Idempotent: a
// re-run whose entry already matches the computed stdio descriptor reports
// `changed: false` and writes nothing.

import * as fs from "fs";
import * as path from "path";
import { fileURLToPath } from "node:url";

import {
  MCP_SERVER_NAME,
  getAgentById,
  getAgentIds,
  mergeStdioServerEntry,
  type AgentDefinition,
  type StdioPropsInput,
} from "../utils/agents.js";
import { isGodotProjectRoot } from "../utils/project-godot.js";
import type {
  SetupMcpFailure,
  SetupMcpOptions,
  SetupMcpResult,
  SetupMcpStdio,
  SetupMcpSuccess,
} from "./types.js";

/**
 * Install the `godot-open-mcp` MCP server (stdio) into a Godot project, writing
 * a config that an AI client (Cursor, Claude, …) can load. Idempotent.
 *
 *  1. Resolve the agent by id (fail with `unknown_agent` + available ids).
 *  2. Resolve + validate the absolute project path. Project-scoped agents
 *     require a valid `project.godot`; global agents only need an absolute path.
 *  3. Build the stdio spawn descriptor (npx default / `node` with `--use-local`).
 *  4. Read the existing config (or `{}`); parse-failures surface as
 *     `invalid_existing_config`.
 *  5. Merge the entry via `mergeStdioServerEntry` (strips foreign HTTP keys,
 *     preserves sibling servers, computes `changed`).
 *  6. Write atomically (temp + rename) only when `changed`.
 */
export async function setupMcp(opts: SetupMcpOptions): Promise<SetupMcpResult> {
  const warnings: string[] = [];

  // 1. Validate the agent id.
  if (typeof opts?.agentId !== "string" || opts.agentId.length === 0) {
    return fail(
      "unknown_agent",
      `agentId is required. Available agent IDs: ${getAgentIds().join(", ")}.`,
      warnings,
    );
  }
  const agent = getAgentById(opts.agentId);
  if (!agent) {
    return fail(
      "unknown_agent",
      `Unknown agent '${opts.agentId}'. Available agent IDs: ${getAgentIds().join(", ")}.`,
      warnings,
    );
  }

  // 2. Resolve + validate the project path.
  if (typeof opts?.godotProjectPath !== "string" || opts.godotProjectPath.length === 0) {
    return fail(
      "not_godot_project",
      "godotProjectPath is required and must be a non-empty string.",
      warnings,
    );
  }
  const projectPath = path.resolve(opts.godotProjectPath);
  // The env var must be absolute — a relative input can only produce a relative
  // env value, which would silently break server discovery. Reject up-front.
  if (!path.isAbsolute(projectPath)) {
    return fail(
      "relative_project_path",
      `godotProjectPath must resolve to an absolute path (got '${opts.godotProjectPath}').`,
      warnings,
    );
  }
  if (agent.scope === "project" && !isGodotProjectRoot(projectPath)) {
    return fail(
      "not_godot_project",
      `Not a valid Godot project (missing project.godot): ${projectPath}`,
      warnings,
    );
  }

  // 3. Build the stdio spawn descriptor.
  const stdio = buildStdioProps(projectPath, opts);

  // 4. Resolve the config path (explicit override wins, else the agent default).
  const configPath = opts.configPath
    ? path.resolve(opts.configPath)
    : agent.getConfigPath(projectPath);

  // 5. Read + parse the existing config.
  let root: Record<string, unknown>;
  try {
    root = readExistingConfig(configPath);
  } catch (err) {
    return fail(
      "invalid_existing_config",
      `Could not parse existing config at ${configPath}: ${errMsg(err)}`,
      warnings,
    );
  }

  // 6. Merge the server entry (pure — returns new root + changed flag).
  const props = agent.getStdioProps(stdio);
  const { root: nextRoot, changed } = mergeStdioServerEntry(
    root,
    agent.bodyPath,
    MCP_SERVER_NAME,
    props,
    agent.removeKeys,
  );

  // 7. Write atomically only when something changed.
  if (changed) {
    try {
      writeJsonAtomic(configPath, nextRoot);
    } catch (err) {
      return fail(
        "config_write_failed",
        `Could not write config to ${configPath}: ${errMsg(err)}`,
        warnings,
      );
    }
  }

  const result: SetupMcpSuccess = {
    kind: "success",
    success: true,
    changed,
    agentId: agent.id,
    configPath,
    serverName: MCP_SERVER_NAME,
    transport: "stdio",
    stdio,
    warnings,
  };
  return result;
}

// ---------------------------------------------------------------------------
// stdio descriptor builder
// ---------------------------------------------------------------------------

/**
 * Build the uniform stdio spawn descriptor.
 *
 * Default (`npx`): `{ command: "npx", args: ["-y", "godot-open-mcp@<version>"], env }`.
 * The version is pinned from `packageVersion` so the server the client spawns
 * stays in lockstep with the CLI. Falls back to `godot-open-mcp` (no pin) when
 * the version is unknown.
 *
 * `useLocal` (`node`): `{ command: "node", args: ["<monorepo>/mcp-server/dist/index.js"], env }`.
 * Resolved relative to this module's location so it works whether the CLI runs
 * from `src/`, built `dist/`, or the installed `dist/` (the relative layout is
 * identical). Used by contributors / CI running from a checkout.
 *
 * `env` always carries `GODOT_PROJECT_PATH` as an absolute path.
 */
function buildStdioProps(projectPath: string, opts: SetupMcpOptions): SetupMcpStdio {
  const env: Record<string, string> = {
    GODOT_PROJECT_PATH: projectPath,
  };

  if (opts.useLocal) {
    const serverEntry = resolveLocalMcpServerEntry();
    return {
      command: "node",
      args: [serverEntry],
      env,
    };
  }

  const version = opts.packageVersion && opts.packageVersion.length > 0
    ? opts.packageVersion
    : null;
  const pkgSpec = version ? `godot-open-mcp@${version}` : "godot-open-mcp";
  return {
    command: "npx",
    args: ["-y", pkgSpec],
    env,
  };
}

/**
 * Resolve the absolute path to the monorepo's built MCP server entry
 * (`mcp-server/dist/index.js`) relative to this module. The CLI package sits at
 * `cli/src/lib/setup-mcp.ts` (or `cli/dist/lib/setup-mcp.js`); the MCP server
 * lives at the sibling `../mcp-server/`. Returns the resolved path verbatim —
 * callers see a clear error if it isn't built (we don't silently fall back to
 * `npx`, since `--use-local` is an explicit opt-in).
 *
 * ESM-safe: `__dirname` is not defined under `"type": "module"`, so the path is
 * derived from `import.meta.url` via `fileURLToPath` + `dirname`.
 */
function resolveLocalMcpServerEntry(moduleUrl: string = import.meta.url): string {
  // cli/src/lib/setup-mcp.ts  →  ../../mcp-server/dist/index.js
  // (works from dist/lib/setup-mcp.js too — same relative depth).
  const here = path.dirname(fileURLToPath(moduleUrl));
  return path.resolve(here, "..", "..", "..", "mcp-server", "dist", "index.js");
}

// ---------------------------------------------------------------------------
// config read / write
// ---------------------------------------------------------------------------

/**
 * Read + parse an existing JSON config file into a root object. Returns `{}` when
 * the file is absent. Throws a structured error when the file is present but
 * cannot be parsed or is not a JSON object — the caller maps it to
 * `invalid_existing_config`.
 */
function readExistingConfig(configPath: string): Record<string, unknown> {
  if (!fs.existsSync(configPath)) return {};
  const text = fs.readFileSync(configPath, "utf-8");
  const trimmed = text.trim();
  if (trimmed.length === 0) return {};
  let parsed: unknown;
  try {
    parsed = JSON.parse(trimmed);
  } catch (err) {
    throw new Error(`invalid JSON — ${errMsg(err)}`);
  }
  if (!isObject(parsed)) {
    throw new Error("root is not a JSON object");
  }
  return parsed;
}

/**
 * Write a config root to disk atomically: serialize, write to a temp sibling,
 * then rename over the target. Creates parent directories as needed. The
 * rename is atomic on POSIX; on Windows it overwrites (best-effort). Emits a
 * trailing newline to match editor conventions.
 */
function writeJsonAtomic(configPath: string, root: Record<string, unknown>): void {
  const dir = path.dirname(configPath);
  fs.mkdirSync(dir, { recursive: true });
  const body = JSON.stringify(root, null, 2) + "\n";
  const tmp = `${configPath}.${process.pid}.${Date.now()}.tmp`;
  fs.writeFileSync(tmp, body, "utf-8");
  fs.renameSync(tmp, configPath);
}

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

// ---------------------------------------------------------------------------
// failure helper
// ---------------------------------------------------------------------------

function fail(
  errorLabel: SetupMcpFailure["errorLabel"],
  message: string,
  warnings: string[],
): SetupMcpFailure {
  return {
    kind: "failure",
    success: false,
    errorLabel,
    warnings,
    error: new Error(message),
  };
}

function errMsg(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

// Re-export so the CLI command can reference the registry + helpers without a
// second import path.
export {
  MCP_SERVER_NAME,
  getAgentIds,
  type AgentDefinition,
  type StdioPropsInput,
};
