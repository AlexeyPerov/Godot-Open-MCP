// CLI command wrapper for `configure`.
//
// Thin adapter over the settings R/W helpers (src/utils/settings.ts): resolves
// argv into one of three modes — `--list` (show current settings), `--get
// <key>` (show one value), or one or more `--set key=value` (write a patch) —
// and maps the outcome into a `CliCommandResult`.
//
// The CLI only writes keys the bridge reads (`authMode`, `bindAddress`) and
// validates them against the bridge's valid-value sets, so it never writes
// garbage the bridge would fail-closed on. Tool enable/disable is NOT here —
// that is Phase 8 `manage_tools`; this command's help points operators there.
//
// Exit codes: 0 on success (including a no-op write with `changed: false`); 1
// on a validation failure (unknown key, invalid value, cross-field invariant)
// or when `--get` names an unknown key.

import { EXIT } from "../exit-codes.js";
import type { CliCommandResult } from "../commands.js";
import {
  readSettings,
  writeSettings,
  settingsPath,
  parseSetAssignments,
  isKnownSettingKey,
  SettingsValidationError,
  type BridgeSettings,
  type SettingKey,
} from "../utils/settings.js";

export interface ConfigureCliInput {
  /** Resolved project root (positional `path` / `--project` / cwd). */
  projectPath: string;
  /** `--list` — print current settings and exit. */
  list: boolean;
  /** `--get <key>` — print one value and exit. */
  getKey?: string;
  /** One or more `--set key=value` assignments. */
  setAssignments: string[];
}

/**
 * Run the `configure` command. Returns a `CliCommandResult`; the dispatcher
 * prints the JSON or human summary and exits with `result.exitCode`.
 *
 * Mode precedence: `--list` wins, then `--get <key>`, then `--set`. When none
 * are given, the command behaves as `--list` (a bare `configure` shows current
 * settings) so the common case is informative rather than an error.
 */
export async function configureCommand(
  input: ConfigureCliInput,
): Promise<CliCommandResult> {
  const file = settingsPath(input.projectPath);

  // Mode 1: --list (or no mode → list).
  if (input.list || (!input.list && !input.getKey && input.setAssignments.length === 0)) {
    const settings = readSettings(input.projectPath);
    return {
      exitCode: EXIT.SUCCESS,
      json: {
        command: "configure",
        mode: "list",
        path: file,
        settings,
      },
      human: formatListHuman(file, settings),
    };
  }

  // Mode 2: --get <key>.
  if (input.getKey) {
    if (!isKnownSettingKey(input.getKey)) {
      return unknownKeyResult(file, input.getKey);
    }
    const settings = readSettings(input.projectPath);
    const key = input.getKey as SettingKey;
    const value = settings[key];
    return {
      exitCode: EXIT.SUCCESS,
      json: {
        command: "configure",
        mode: "get",
        path: file,
        key,
        value,
      },
      human: `${key}: ${value}`,
    };
  }

  // Mode 3: --set key=value [key=value ...].
  let patch: Partial<BridgeSettings>;
  try {
    patch = parseSetAssignments(input.setAssignments);
  } catch (err) {
    return validationFailure(file, err);
  }

  let result;
  try {
    result = writeSettings(input.projectPath, patch);
  } catch (err) {
    return validationFailure(file, err);
  }

  return {
    exitCode: EXIT.SUCCESS,
    json: {
      command: "configure",
      mode: "set",
      changed: result.changed,
      path: result.path,
      previous: result.previous,
      settings: result.next,
    },
    human: formatSetHuman(result),
  };
}

// ---------------------------------------------------------------------------
// Failure shapes
// ---------------------------------------------------------------------------

/** Build the unknown-key failure result for a `--get <unknown>` invocation. */
function unknownKeyResult(file: string, key: string): CliCommandResult {
  const json = {
    command: "configure",
    mode: "get",
    path: file,
    key,
    error: {
      code: "unknown_key",
      message: `Unknown setting key '${key}'. Known keys: authMode, bindAddress.`,
    },
  };
  return {
    exitCode: EXIT.ERRORS,
    json,
    human: `Unknown setting key '${key}'. Known keys: authMode, bindAddress.`,
    errorLabel: "unknown_key",
  };
}

/**
 * Map a `SettingsValidationError` (from parseSetAssignments or writeSettings)
 * to a `CliCommandResult`. The errorLabel + message carry the actionable reason.
 */
function validationFailure(file: string, err: unknown): CliCommandResult {
  const label = err instanceof SettingsValidationError ? err.errorLabel : "settings_write_failed";
  const message = err instanceof Error ? err.message : String(err);
  const json = {
    command: "configure",
    mode: "set",
    path: file,
    error: { code: label, message },
  };
  return {
    exitCode: EXIT.ERRORS,
    json,
    human: message,
    errorLabel: label,
  };
}

// ---------------------------------------------------------------------------
// Human-readable formatting
// ---------------------------------------------------------------------------

function formatListHuman(file: string, settings: BridgeSettings): string {
  return [
    `Settings: ${file}`,
    `authMode:    ${settings.authMode}`,
    `bindAddress: ${settings.bindAddress}`,
  ].join("\n");
}

function formatSetHuman(result: {
  changed: boolean;
  path: string;
  previous: BridgeSettings;
  next: BridgeSettings;
}): string {
  const headline = result.changed
    ? "Settings updated."
    : "Settings unchanged (values already matched).";
  return [
    headline,
    `Path:        ${result.path}`,
    `authMode:    ${result.previous.authMode} → ${result.next.authMode}`,
    `bindAddress: ${result.previous.bindAddress} → ${result.next.bindAddress}`,
  ].join("\n");
}
