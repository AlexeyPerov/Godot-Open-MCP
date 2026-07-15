// MCP-client agent registry — where each AI client's stdio MCP config lives and
// what shape the server entry takes.
//
// Adapted from the Godot-MCP behavior reference (`cli/src/utils/agents.ts`) with
// the HTTP transport surface stripped entirely and replaced by **stdio** spawn
// props. Godot Open MCP is stdio-only (ADR-001): an AI client launches the MCP
// server (`godot-open-mcp`) as a child process, and the server reaches the
// Godot bridge over loopback HTTP. So every agent here writes a stdio entry —
// `command` / `args` / `env` — never a `url` / `type:"http"` entry.
//
// The stdio entry shapes mirror Unity Open MCP's `docs/setup/manual-setup.md`
// (the canonical source of truth for per-client JSON envelopes): bare
// `{command, args, env}` under `mcpServers` for Cursor/Claude/Cline/…, a
// `type:"stdio"` variant under `servers` for VS Code, a `command: [array]` +
// `environment` variant under `mcp` for OpenCode.
//
// Godot-MCP supplied the OS config-path inventory (Claude Desktop, Cline,
// GitHub Copilot CLI global paths); Unity supplied the entry shapes. The two
// references are combined here with the env var renamed to `GODOT_PROJECT_PATH`
// and the server key renamed to `godot-open-mcp` (ADR-003).
//
// Pure — no I/O, no stdout, no throws. The JSON merge helper
// (`mergeStdioServerEntry`) operates on a parsed root object and returns the new
// root + a `changed` flag; the library owns the read/write.

import * as os from "os";
import * as path from "path";

// ---------------------------------------------------------------------------
// Server-name + env contract
// ---------------------------------------------------------------------------

/**
 * The MCP server key written into every client config. Matches the npm package
 * name (`godot-open-mcp`) and its bin — the stdio server the AI client spawns.
 */
export const MCP_SERVER_NAME = "godot-open-mcp";

// ---------------------------------------------------------------------------
// Agent definition
// ---------------------------------------------------------------------------

/**
 * Uniform stdio spawn descriptor handed to each agent's `getStdioProps`. The
 * library resolves `command` / `args` / `env` (npx vs. `--use-local`, absolute
 * `GODOT_PROJECT_PATH`), then each agent shapes this into its client-specific
 * envelope.
 */
export interface StdioPropsInput {
  command: string;
  args: string[];
  env: Record<string, string>;
}

export interface AgentDefinition {
  /** Stable id used on the CLI (`setup-mcp <id>`). */
  id: string;
  /** Human-readable client name (for `--list` + success messages). */
  name: string;
  /**
   * Whether the config file lives inside the Godot project (`project`) or in a
   * user-global location (`global`). Project-scoped agents require a valid
   * `project.godot`; global agents only need an absolute project path for the
   * env var.
   */
  scope: "project" | "global";
  /** Human-readable label for the config path shown in `--list`. */
  configPathDisplay: string;
  /**
   * Dotted path segments locating the servers map inside the config root
   * (e.g. `["mcpServers"]`, `["mcp","servers"]`, `["servers"]`). The merge
   * helper walks/creates each segment.
   */
  bodyPath: readonly string[];
  /** Resolve the absolute config-file path for a given project root. */
  getConfigPath(projectPath: string): string;
  /**
   * Build the server entry written under `bodyPath[MCP_SERVER_NAME]` from the
   * uniform stdio descriptor. Each client wraps `command`/`args`/`env` into its
   * own envelope shape.
   */
  getStdioProps(input: StdioPropsInput): Record<string, unknown>;
  /**
   * Keys to delete from a pre-existing entry before merging new props. These are
   * the foreign HTTP/transport keys a prior Godot-MCP HTTP entry (or a different
   * client's shape) could have left behind — stripping them guarantees a re-run
   * can never leave a stale `url`/`headers`/`type:"http"` lingering on our entry.
   */
  removeKeys: readonly string[];
}

// ---------------------------------------------------------------------------
// Stdio entry shapers
// ---------------------------------------------------------------------------

/**
 * Bare stdio entry shared by Cursor, Claude Desktop/Code, Cline, Gemini, Kilo
 * Code, GitHub Copilot CLI, and the generic custom target: the Unity
 * manual-setup canonical shape `{ command, args, env }` under `mcpServers`.
 */
function bareStdio(input: StdioPropsInput): Record<string, unknown> {
  return { command: input.command, args: input.args, env: input.env };
}

/**
 * VS Code Copilot / Visual Studio Copilot variant: nests under `servers` with a
 * `type: "stdio"` marker (per Unity manual-setup).
 */
function vscodeStdio(input: StdioPropsInput): Record<string, unknown> {
  return {
    type: "stdio",
    command: input.command,
    args: input.args,
    env: input.env,
  };
}

/**
 * OpenCode variant: nests under `mcp` with `command` as a single array (command
 * + args concatenated), `environment` instead of `env`, `type: "local"`, and an
 * `enabled: true` flag (per Unity manual-setup).
 */
function opencodeStdio(input: StdioPropsInput): Record<string, unknown> {
  return {
    type: "local",
    command: [input.command, ...input.args],
    enabled: true,
    environment: input.env,
  };
}

// ---------------------------------------------------------------------------
// Remove-key sets (foreign keys stripped before merge)
// ---------------------------------------------------------------------------

/**
 * Foreign keys stripped from a bare-stdio entry. Covers every HTTP/transport
 * shape Godot-MCP or another client could have written: `url`/`type`/`headers`
 * (HTTP), `serverUrl`/`disabled` (Antigravity), `tools` (Copilot CLI pin),
 * `environment` (OpenCode — bare stdio uses `env`), and the Codex timeout
 * scalars. Stripping a key that isn't present is a no-op, so listing the union
 * is safe.
 */
const BARE_STDIO_REMOVE_KEYS = [
  "url",
  "type",
  "headers",
  "disabled",
  "serverUrl",
  "tools",
  "tool_timeout_sec",
  "startup_timeout_sec",
  "environment",
] as const;

/**
 * Foreign keys stripped from a VS Code `servers` entry. `type` is NOT stripped —
 * VS Code's canonical stdio entry carries `type: "stdio"`, and the merge
 * overwrites any stale `type:"http"` with `"stdio"`.
 */
const VSCODE_STDIO_REMOVE_KEYS = [
  "url",
  "headers",
  "disabled",
  "serverUrl",
  "tools",
  "environment",
] as const;

/**
 * Foreign keys stripped from an OpenCode `mcp` entry. OpenCode uses
 * `command` (array) + `environment`, so the bare-stdio `args` and `env` forms
 * are removed to avoid a stale parallel pair lingering after a re-run.
 */
const OPENCODE_STDIO_REMOVE_KEYS = [
  "url",
  "headers",
  "args",
  "env",
  "disabled",
  "serverUrl",
  "tools",
] as const;

// ---------------------------------------------------------------------------
// Platform helpers (global config paths)
// ---------------------------------------------------------------------------

function appData(): string {
  return process.env["APPDATA"] ?? path.join(os.homedir(), "AppData", "Roaming");
}

function home(): string {
  return os.homedir();
}

function isWindows(): boolean {
  return process.platform === "win32";
}

function isMac(): boolean {
  return process.platform === "darwin";
}

// ---------------------------------------------------------------------------
// Agent registry
// ---------------------------------------------------------------------------

/**
 * The shipped agent roster. The Phase 6.3 Done-when set is `cursor` +
 * `claude-desktop` + `claude-code`; the remaining JSON agents are ported from
 * Unity `docs/setup/manual-setup.md` + the Godot-MCP path table because their
 * stdio shapes are unambiguous and the registry is designed to be extended.
 *
 * Codex (TOML config) is intentionally deferred — the Phase 6.3 plan marks TOML
 * as stretch ("ship JSON agents first"). It will register here with
 * `configFormat: "toml"` once a minimal TOML emitter lands.
 */
export const agentRegistry: readonly AgentDefinition[] = [
  // ── Cursor (project-local) ────────────────────────────────────
  {
    id: "cursor",
    name: "Cursor",
    scope: "project",
    configPathDisplay: ".cursor/mcp.json",
    bodyPath: ["mcpServers"],
    getConfigPath: (p) => path.join(p, ".cursor", "mcp.json"),
    getStdioProps: bareStdio,
    removeKeys: BARE_STDIO_REMOVE_KEYS,
  },

  // ── Claude Code (project-local .mcp.json) ────────────────────
  {
    id: "claude-code",
    name: "Claude Code",
    scope: "project",
    configPathDisplay: ".mcp.json",
    bodyPath: ["mcpServers"],
    getConfigPath: (p) => path.join(p, ".mcp.json"),
    getStdioProps: bareStdio,
    removeKeys: BARE_STDIO_REMOVE_KEYS,
  },

  // ── Claude Desktop (user-global) ─────────────────────────────
  {
    id: "claude-desktop",
    name: "Claude Desktop",
    scope: "global",
    configPathDisplay: "~/Library/Application Support/Claude/claude_desktop_config.json",
    bodyPath: ["mcpServers"],
    getConfigPath: () => {
      if (isWindows()) {
        return path.join(appData(), "Claude", "claude_desktop_config.json");
      }
      if (isMac()) {
        return path.join(
          home(),
          "Library",
          "Application Support",
          "Claude",
          "claude_desktop_config.json",
        );
      }
      return path.join(home(), ".config", "Claude", "claude_desktop_config.json");
    },
    getStdioProps: bareStdio,
    removeKeys: BARE_STDIO_REMOVE_KEYS,
  },

  // ── VS Code Copilot (project-local .vscode/mcp.json, `servers`) ──
  {
    id: "vscode-copilot",
    name: "Visual Studio Code (Copilot)",
    scope: "project",
    configPathDisplay: ".vscode/mcp.json",
    bodyPath: ["servers"],
    getConfigPath: (p) => path.join(p, ".vscode", "mcp.json"),
    getStdioProps: vscodeStdio,
    removeKeys: VSCODE_STDIO_REMOVE_KEYS,
  },

  // ── Visual Studio Copilot (project-local .vs/mcp.json, `servers`) ──
  {
    id: "vs-copilot",
    name: "Visual Studio (Copilot)",
    scope: "project",
    configPathDisplay: ".vs/mcp.json",
    bodyPath: ["servers"],
    getConfigPath: (p) => path.join(p, ".vs", "mcp.json"),
    getStdioProps: vscodeStdio,
    removeKeys: VSCODE_STDIO_REMOVE_KEYS,
  },

  // ── OpenCode (project-local opencode.json, `mcp`) ────────────
  {
    id: "opencode",
    name: "OpenCode",
    scope: "project",
    configPathDisplay: "opencode.json",
    bodyPath: ["mcp"],
    getConfigPath: (p) => path.join(p, "opencode.json"),
    getStdioProps: opencodeStdio,
    removeKeys: OPENCODE_STDIO_REMOVE_KEYS,
  },

  // ── Gemini (project-local .gemini/settings.json) ────────────
  {
    id: "gemini",
    name: "Gemini",
    scope: "project",
    configPathDisplay: ".gemini/settings.json",
    bodyPath: ["mcpServers"],
    getConfigPath: (p) => path.join(p, ".gemini", "settings.json"),
    getStdioProps: bareStdio,
    removeKeys: BARE_STDIO_REMOVE_KEYS,
  },

  // ── Cline (user-global) ──────────────────────────────────────
  {
    id: "cline",
    name: "Cline",
    scope: "global",
    configPathDisplay: "~/.../saoudrizwan.claude-dev/settings/cline_mcp_settings.json",
    bodyPath: ["mcpServers"],
    getConfigPath: () => {
      if (isWindows()) {
        return path.join(
          appData(),
          "Code",
          "User",
          "globalStorage",
          "saoudrizwan.claude-dev",
          "settings",
          "cline_mcp_settings.json",
        );
      }
      const base = isMac()
        ? path.join(home(), "Library", "Application Support", "Code", "User", "globalStorage")
        : path.join(home(), ".config", "Code", "User", "globalStorage");
      return path.join(base, "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json");
    },
    getStdioProps: bareStdio,
    removeKeys: BARE_STDIO_REMOVE_KEYS,
  },

  // ── Kilo Code (project-local .kilocode/mcp.json) ─────────────
  {
    id: "kilo-code",
    name: "Kilo Code",
    scope: "project",
    configPathDisplay: ".kilocode/mcp.json",
    bodyPath: ["mcpServers"],
    getConfigPath: (p) => path.join(p, ".kilocode", "mcp.json"),
    getStdioProps: bareStdio,
    removeKeys: BARE_STDIO_REMOVE_KEYS,
  },

  // ── GitHub Copilot CLI (user-global) ─────────────────────────
  {
    id: "github-copilot-cli",
    name: "GitHub Copilot CLI",
    scope: "global",
    configPathDisplay: "~/.copilot/mcp-config.json",
    bodyPath: ["mcpServers"],
    getConfigPath: () => path.join(home(), ".copilot", "mcp-config.json"),
    getStdioProps: bareStdio,
    removeKeys: BARE_STDIO_REMOVE_KEYS,
  },

  // ── Custom (generic mcpServers entry at a caller path) ───────
  {
    id: "custom",
    name: "Custom (generic MCP client)",
    scope: "project",
    configPathDisplay: "mcp.json",
    bodyPath: ["mcpServers"],
    getConfigPath: (p) => path.join(p, "mcp.json"),
    getStdioProps: bareStdio,
    removeKeys: BARE_STDIO_REMOVE_KEYS,
  },
];

// ---------------------------------------------------------------------------
// Lookup helpers
// ---------------------------------------------------------------------------

export function getAgentById(id: string): AgentDefinition | undefined {
  return agentRegistry.find((a) => a.id === id);
}

export function getAgentIds(): string[] {
  return agentRegistry.map((a) => a.id);
}

// ---------------------------------------------------------------------------
// JSON merge — pure
// ---------------------------------------------------------------------------

/**
 * Merge a stdio server entry into a parsed config root, preserving sibling
 * servers and unrelated keys. Pure — returns a new root object and a `changed`
 * flag; never touches the filesystem.
 *
 * Steps:
 *  1. Walk/create the `bodyPath` segments (e.g. `mcpServers`, or `mcp.servers`).
 *  2. Get (or create) the `bodyPath[serverName]` entry object.
 *  3. Strip `removeKeys` from the existing entry (foreign HTTP/transport keys).
 *  4. Merge the new stdio props onto the stripped entry (overwriting
 *     `command`/`args`/`env`/`type`/etc.).
 *  5. `changed` is true unless the entry was already byte-identical (deep, order-
 *     independent) to the merged result — so an idempotent re-run reports
 *     `changed: false` and the library skips the write.
 *
 * User-authored keys on the entry that are neither in `removeKeys` nor in the
 * new props are preserved (e.g. a custom `notes` field).
 */
export function mergeStdioServerEntry(
  root: Record<string, unknown>,
  bodyPath: readonly string[],
  serverName: string,
  props: Record<string, unknown>,
  removeKeys: readonly string[],
): { root: Record<string, unknown>; changed: boolean } {
  // Clone the root so the caller's object is never mutated (lets the library
  // decide whether to persist based on `changed`).
  const next: Record<string, unknown> = { ...root };

  // Walk/create the bodyPath segments. Each existing segment is shallow-cloned
  // before descending so the original root's nested objects are never mutated
  // — this keeps the function pure AND lets the `changed` comparison walk the
  // unmodified original root for a true before/after diff. Sibling entries
  // inside a cloned segment are still shared references (so they're preserved
  // by reference), only the container objects on the path get fresh copies.
  let cursor: Record<string, unknown> = next;
  for (const seg of bodyPath) {
    const child = cursor[seg];
    if (isObject(child)) {
      const cloned: Record<string, unknown> = { ...child };
      cursor[seg] = cloned;
      cursor = cloned;
    } else {
      const fresh: Record<string, unknown> = {};
      cursor[seg] = fresh;
      cursor = fresh;
    }
  }

  // Get or create the server entry.
  const oldEntry = isObject(cursor[serverName]) ? { ...(cursor[serverName] as Record<string, unknown>) } : {};

  // Strip foreign keys.
  for (const key of removeKeys) {
    delete oldEntry[key];
  }

  // Merge new props (overwrites command/args/env/type/etc.).
  const newEntry: Record<string, unknown> = { ...oldEntry, ...props };
  cursor[serverName] = newEntry;

  const before = walkToExistingEntry(root, bodyPath, serverName);
  const changed = !jsonDeepEqual(newEntry, before);
  return { root: next, changed };
}

/**
 * Walk `bodyPath` on the ORIGINAL root to fetch the pre-merge entry (or
 * undefined), for `changed` comparison. Kept separate so `mergeStdioServerEntry`
 * stays linear and the clone doesn't confuse the comparator.
 */
function walkToExistingEntry(
  root: Record<string, unknown>,
  bodyPath: readonly string[],
  serverName: string,
): unknown {
  let cursor: unknown = root;
  for (const seg of bodyPath) {
    if (!isObject(cursor) || !(seg in cursor)) return undefined;
    cursor = cursor[seg];
  }
  if (!isObject(cursor)) return undefined;
  return cursor[serverName];
}

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

/**
 * Order-independent deep equality for plain JSON values (primitives, arrays,
 * plain objects). Used by the merge so `changed` reflects content equality,
 * not key-order or whitespace drift — an idempotent re-run whose entry already
 * matches reports `changed: false`.
 */
function jsonDeepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (typeof a !== typeof b) return false;
  if (Array.isArray(a)) {
    if (!Array.isArray(b) || a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) {
      if (!jsonDeepEqual(a[i], b[i])) return false;
    }
    return true;
  }
  if (isObject(a)) {
    if (!isObject(b)) return false;
    const keysA = Object.keys(a);
    const keysB = Object.keys(b);
    if (keysA.length !== keysB.length) return false;
    for (const k of keysA) {
      if (!(k in b)) return false;
      if (!jsonDeepEqual(a[k], b[k])) return false;
    }
    return true;
  }
  return false;
}
