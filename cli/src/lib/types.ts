// Shared public types for the CLI library API.
//
// Library-safe result unions: every public function returns a discriminated
// `{ kind: "success" | "failure" }` union and never throws past the public
// boundary, so callers (the CLI command wrapper, the future Hub, tests) can
// pattern-match without try/catch. No top-level side effects, no runtime deps
// beyond TypeScript types.
//
// Adapted from the Godot-MCP behavior reference (`cli/src/lib/types.ts`) with
// the NuGet/csproj + cloud/download surfaces stripped (ADR-004 + P6.2 out of
// scope) and the plugin path renamed to `godot_open_mcp`.

// ---------------------------------------------------------------------------
// install-plugin
// ---------------------------------------------------------------------------

export interface InstallPluginOptions {
  /** Absolute or relative path to the Godot project root. */
  godotProjectPath: string;
  /**
   * Local directory to copy `addons/godot_open_mcp/` from (offline / dev / CI
   * path). When omitted, the installer falls back to the monorepo default
   * (`packages/bridge`) when running from a checkout. There is no download
   * path in v1 — release-zip packaging is deferred (P6.2 out of scope).
   */
  source?: string;
  /**
   * Skip materializing the addon files and only enable the plugin in
   * `project.godot`. For callers that manage the addon tree themselves.
   */
  skipMaterialize?: boolean;
}

/** What `install-plugin` did to materialize the `addons/godot_open_mcp/` files. */
export interface AddonMaterializeOutcome {
  /** Where the addon files came from. `skipped` when `skipMaterialize` was set. */
  source: "local" | "skipped";
  /** Absolute path to the materialized `addons/godot_open_mcp/` directory. */
  addonDir: string;
  /** The local source directory used (`--source` or monorepo default). */
  sourceDir?: string;
  /**
   * True when the addon dir was actually written (newly created or content
   * changed). False on a no-op re-run whose source matches the installed tree
   * byte-for-byte — the swap is skipped so the install is idempotent.
   */
  changed: boolean;
}

export interface InstallPluginSuccess {
  kind: "success";
  success: true;
  /** True when the project was newly changed (files copied and/or plugin enabled); false when already correct. */
  changed: boolean;
  /** Absolute path to the Godot project root. */
  projectPath: string;
  /** Absolute path to `project.godot`. */
  projectGodotPath: string;
  /** Absolute path to the installed `addons/godot_open_mcp/` directory. */
  addonDir: string;
  /** Canonical plugin.cfg resource path. */
  pluginPath: string;
  /** Ordered list of enabled plugin.cfg paths after the toggle. */
  enabledPlugins: string[];
  /** Addon-files materialization outcome (local copy / skipped). */
  materialize: AddonMaterializeOutcome;
  warnings: string[];
}

export interface InstallPluginFailure {
  kind: "failure";
  success: false;
  /** Structured error label (see the table in P6.2.md). */
  errorLabel: InstallPluginErrorLabel;
  /** Absolute path to `project.godot` when it was located, else undefined. */
  projectGodotPath?: string;
  warnings: string[];
  error: Error;
}

/**
 * Error labels surfaced in JSON failure payloads. Stable strings so CI and
 * scripts can branch without parsing prose.
 */
export type InstallPluginErrorLabel =
  | "not_godot_project"
  | "source_missing"
  | "materialize_failed"
  | "project_godot_write_failed";

export type InstallPluginResult = InstallPluginSuccess | InstallPluginFailure;

// ---------------------------------------------------------------------------
// setup-mcp
// ---------------------------------------------------------------------------

export interface SetupMcpOptions {
  /** Agent id to configure (e.g. `cursor`, `claude-desktop`). */
  agentId: string;
  /** Absolute or relative path to the Godot project root. */
  godotProjectPath: string;
  /**
   * Write a `node <monorepo>/mcp-server/dist/index.js` entry instead of the
   * `npx`-based default. Used by contributors / CI running from a checkout so
   * they don't depend on a published npm package.
   */
  useLocal?: boolean;
  /** Optional explicit config-file path (overrides the agent's default). */
  configPath?: string;
  /** Package version, used to pin the npx args (`godot-open-mcp@<version>`). */
  packageVersion?: string;
}

/**
 * The stdio spawn descriptor written under the server entry. Mirrors the
 * `stdio` field of the success JSON so the wire shape is consistent.
 */
export interface SetupMcpStdio {
  command: string;
  args: string[];
  env: Record<string, string>;
}

export interface SetupMcpSuccess {
  kind: "success";
  success: true;
  /** True when the config file was newly written or changed. */
  changed: boolean;
  /** The agent id that was configured. */
  agentId: string;
  /** Absolute config-file path written. */
  configPath: string;
  /** MCP server key (always `godot-open-mcp`). */
  serverName: string;
  /** Always `"stdio"` — the transport we write. */
  transport: "stdio";
  /** The stdio spawn descriptor. */
  stdio: SetupMcpStdio;
  warnings: string[];
}

export interface SetupMcpFailure {
  kind: "failure";
  success: false;
  /** Structured error label (see the table in P6.3.md). */
  errorLabel: SetupMcpErrorLabel;
  warnings: string[];
  error: Error;
}

/**
 * Error labels surfaced in JSON failure payloads. Stable strings so CI and
 * scripts can branch without parsing prose.
 */
export type SetupMcpErrorLabel =
  | "unknown_agent"
  | "not_godot_project"
  | "relative_project_path"
  | "config_write_failed"
  | "invalid_existing_config";

export type SetupMcpResult = SetupMcpSuccess | SetupMcpFailure;

// ---------------------------------------------------------------------------
// open
// ---------------------------------------------------------------------------

export interface OpenProjectOptions {
  /** Absolute or relative path to the Godot project root. */
  projectPath: string;
  /**
   * Explicit path to the Godot editor binary (skips discovery). When omitted,
   * the opener resolves via env / PATH / common install roots.
   */
  editorPath?: string;
  /**
   * When true (default), run `dotnet build` before launching IF a `.csproj`
   * exists at the project root. `--no-build` sets this false. GDScript-only
   * projects skip the build automatically (no .csproj).
   */
  build?: boolean;
  /** MSBuild configuration for the pre-open build (default: Debug). */
  buildConfiguration?: string;
  /**
   * Optional path to the `dotnet` executable. Defaults to `dotnet` on PATH.
   * Injected so tests can stub the build step.
   */
  dotnetPath?: string;
  /**
   * Spawn implementation injected for tests. Defaults to the real
   * `child_process.spawn` via the editor-discovery launch helper. When set,
   * `dotnetPath` is also routed through it.
   */
  buildSpawnImpl?: (cmd: string, args: string[], opts: {
    cwd: string;
    stdio: "ignore" | "pipe";
  }) => SpawnLike;
}

/** Minimal spawn-like handle the build step needs (a ChildProcess subset). */
export interface SpawnLike {
  on(event: "error", listener: (err: Error) => void): unknown;
  on(event: "close", listener: (code: number | null) => void): unknown;
}

export interface OpenProjectSuccess {
  kind: "success";
  success: true;
  /** True when the editor was newly launched; false when one was already running. */
  launched: boolean;
  /** Absolute path to the resolved Godot editor binary. */
  editorPath: string;
  /** PID of the launched (or already-running) editor, when known. */
  editorPid?: number;
  /** Absolute path to the Godot project root. */
  projectPath: string;
  /** True when a `dotnet build` ran and succeeded; false when skipped. */
  built: boolean;
  warnings: string[];
}

export interface OpenProjectFailure {
  kind: "failure";
  success: false;
  /** Structured error label (see the table in P6.4.md). */
  errorLabel: OpenProjectErrorLabel;
  /** Absolute project path when it was resolved, else undefined. */
  projectPath?: string;
  /** Absolute editor path when it was resolved, else undefined. */
  editorPath?: string;
  warnings: string[];
  error: Error;
}

/**
 * Error labels surfaced in JSON failure payloads. Stable strings so CI and
 * scripts can branch without parsing prose.
 */
export type OpenProjectErrorLabel =
  | "project_not_found"
  | "not_godot_project"
  | "editor_not_found"
  | "build_failed"
  | "launch_failed";

export type OpenProjectResult = OpenProjectSuccess | OpenProjectFailure;

// ---------------------------------------------------------------------------
// wait-for-ready / ping (shared outcome shape)
// ---------------------------------------------------------------------------

/** Status token returned by the poller; see src/ping-poller.ts PollOutcome. */
export type ReadinessStatus =
  | "ready"
  | "compiling"
  | "offline"
  | "dead_bridge"
  | "timeout";
