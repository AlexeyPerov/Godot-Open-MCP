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
