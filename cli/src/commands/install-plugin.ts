// CLI command wrapper for `install-plugin`.
//
// Thin adapter over the library-safe `installPlugin()` (src/lib/install-plugin.ts):
// resolves argv into options, runs the installer, and maps the result union into
// a `CliCommandResult` the dispatcher prints + exits on. No stdout noise lives
// in the library; this module owns the human + JSON presentation.
//
// Exit codes: 0 on success (including `changed: false`), 1 on failure — matches
// the P6 shared invariants (0 success, 1 errors).

import * as path from "path";

import { EXIT } from "../exit-codes.js";
import { installPlugin } from "../lib/install-plugin.js";
import type {
  InstallPluginFailure,
  InstallPluginResult,
  InstallPluginSuccess,
} from "../lib/types.js";
import { resolveAddonSource } from "../utils/addon-source.js";
import { GODOT_OPEN_MCP_PLUGIN_PATH } from "../utils/project-godot.js";
import type { CliCommandResult } from "../commands.js";

export interface InstallPluginCliInput {
  /** Resolved project root (positional `path` / `--project` / cwd). */
  projectPath: string;
  /** `--source <dir>` override (local addon root). */
  source?: string;
}

/**
 * Run the `install-plugin` command. Returns a `CliCommandResult`; the dispatcher
 * prints the JSON or human summary and exits with `result.exitCode`.
 */
export async function installPluginCommand(
  input: InstallPluginCliInput,
): Promise<CliCommandResult> {
  const projectPath = path.resolve(input.projectPath);

  const result = await installPlugin({
    godotProjectPath: projectPath,
    source: input.source,
  });

  return result.kind === "success"
    ? successResult(result, input.source)
    : failureResult(result);
}

/** Build the success `CliCommandResult` (human + JSON). */
function successResult(
  result: InstallPluginSuccess,
  sourceArg: string | undefined,
): CliCommandResult {
  const resolvedSource = resolveAddonSource(sourceArg);
  const sourceJson =
    resolvedSource.kind === "local"
      ? { kind: "local", path: resolvedSource.path }
      : { kind: "default-or-skipped" };

  const json = {
    command: "install-plugin",
    changed: result.changed,
    projectPath: result.projectPath,
    addonDir: result.addonDir,
    pluginPath: result.pluginPath,
    enabledPlugins: result.enabledPlugins,
    source: sourceJson,
    warnings: result.warnings,
  };

  const headline = result.changed
    ? "Godot Open MCP addon installed and enabled."
    : "Godot Open MCP addon was already installed and enabled.";

  const lines: string[] = [headline];
  // Addon-files outcome.
  if (result.materialize.source === "local") {
    lines.push(
      `Addon files: copied from ${result.materialize.sourceDir} → ${result.materialize.addonDir}`,
    );
  } else {
    lines.push(`Addon files: skipped (managed elsewhere) → ${result.materialize.addonDir}`);
  }
  lines.push(`project.godot: ${result.projectGodotPath}`);
  lines.push(
    `Enabled plugins: ${result.enabledPlugins.join(", ") || "(none)"}`,
  );
  for (const warning of result.warnings) {
    lines.push("", `Warning: ${warning}`);
  }

  return {
    exitCode: EXIT.SUCCESS,
    json,
    human: lines.join("\n"),
  };
}

/** Build the failure `CliCommandResult` (human + JSON + errorLabel). */
function failureResult(result: InstallPluginFailure): CliCommandResult {
  const json = {
    command: "install-plugin",
    changed: false,
    projectGodotPath: result.projectGodotPath,
    error: {
      code: result.errorLabel,
      message: result.error.message,
    },
    warnings: result.warnings,
  };

  const lines: string[] = [
    `Failed to install plugin: ${result.error.message}`,
  ];
  if (result.projectGodotPath) {
    lines.push(`project.godot: ${result.projectGodotPath}`);
  }
  for (const warning of result.warnings) {
    lines.push("", `Warning: ${warning}`);
  }

  return {
    exitCode: EXIT.ERRORS,
    json,
    human: lines.join("\n"),
    errorLabel: result.errorLabel,
  };
}

// Re-export the canonical plugin path so the help text / tests can reference it
// without reaching into utils.
export { GODOT_OPEN_MCP_PLUGIN_PATH };
