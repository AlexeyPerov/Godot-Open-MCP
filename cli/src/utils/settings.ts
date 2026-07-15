// Read/write helpers for the project-local bridge settings file.
//
// The bridge reads `<project>/.godot-open-mcp/settings.json` via
// `BridgeProjectSettings.cs` (P5.2) for two keys: `authMode` and `bindAddress`.
// This module mirrors that schema byte-for-byte so the CLI `configure` command
// only ever writes keys the bridge honors — it never writes garbage the bridge
// would fail-closed on, and never silently drops a key the bridge reads.
//
// Fidelity: copy/adapt. The schema is copied from the bridge (single source of
// truth: `packages/bridge/Editor/Bridge/BridgeProjectSettings.cs`); the R/W
// helpers are adapted from the Godot-MCP behavior reference
// (`cli/src/utils/config.ts`) with the `features.json` tool-toggle surface
// stripped (Phase 8 `manage_tools`) and the strict authMode/bindAddress
// validation added to honor the bridge's fail-closed policy.
//
// Pure over the filesystem: `readSettings` / `writeSettings` are the only fs
// touches; `defaultSettings`, `settingsPath`, `validateKey`, and
// `parseSetAssignments` are pure and exported for unit tests. `writeSettings`
// creates the `.godot-open-mcp/` directory on first write and pretty-prints
// JSON with a trailing newline (matches the bridge's `BridgeProjectSettings.Save`).

import * as fs from "fs";
import * as path from "path";

// ---------------------------------------------------------------------------
// Schema — MUST agree with `packages/bridge/Editor/Bridge/BridgeProjectSettings.cs`
// ---------------------------------------------------------------------------

/** Settings directory under the project root. */
export const SETTINGS_DIR_NAME = ".godot-open-mcp";
/** Settings file name inside the directory. */
export const SETTINGS_FILE_NAME = "settings.json";

/** Valid `authMode` values. Mirrors `BridgeAuthPolicy.ValidModes` (C#). */
export const VALID_AUTH_MODES = ["none", "required"] as const;
/** Valid `bindAddress` values. Mirrors `BridgeBindAddress.ValidAddresses` (C#). */
export const VALID_BIND_ADDRESSES = ["127.0.0.1", "0.0.0.0"] as const;

/** Canonical `authMode` type — the only two values the bridge treats as valid. */
export type AuthMode = (typeof VALID_AUTH_MODES)[number];
/** Canonical `bindAddress` type — loopback or remote wildcard. */
export type BindAddress = (typeof VALID_BIND_ADDRESSES)[number];

/** The bridge settings shape the CLI reads and writes. */
export interface BridgeSettings {
  authMode: AuthMode;
  bindAddress: BindAddress;
}

/** The default settings when the file is missing or a key is absent. */
export function defaultSettings(): BridgeSettings {
  // Mirrors `BridgeAuthPolicy.Default` ("none") and `BridgeBindAddress.Default`
  // (loopback "127.0.0.1"). The bridge returns these when the file is absent
  // or the key is missing; the CLI agrees byte-for-byte.
  return { authMode: "none", bindAddress: "127.0.0.1" };
}

/** Known setting keys the bridge reads. Used to refuse unknown keys. */
export const KNOWN_SETTING_KEYS = ["authMode", "bindAddress"] as const;
export type SettingKey = (typeof KNOWN_SETTING_KEYS)[number];

// ---------------------------------------------------------------------------
// Path resolution
// ---------------------------------------------------------------------------

/** Absolute path to the settings file for a project root. */
export function settingsPath(projectRoot: string): string {
  return path.join(projectRoot, SETTINGS_DIR_NAME, SETTINGS_FILE_NAME);
}

// ---------------------------------------------------------------------------
// Read
// ---------------------------------------------------------------------------

/**
 * Read the settings file for a project. Returns the defaults when the file is
 * missing or unparseable (the bridge does the same — a corrupt file never
 * blocks start). Never throws: a read/parse failure degrades to defaults so a
 * `configure --list` against a broken file still shows something useful.
 *
 * Unknown keys in the file are ignored (forward-compat); only the two keys the
 * bridge reads are surfaced. An invalid `authMode`/`bindAddress` value is
 * coerced to the default — the CLI never propagates an out-of-set value, so the
 * status/configure output never claims a mode the bridge would fail-closed on.
 */
export function readSettings(projectRoot: string): BridgeSettings {
  const defaults = defaultSettings();
  const file = settingsPath(projectRoot);
  if (!fs.existsSync(file)) return defaults;

  let raw: string;
  try {
    raw = fs.readFileSync(file, "utf8");
  } catch {
    return defaults;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return defaults;
  }

  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    return defaults;
  }

  const obj = parsed as Record<string, unknown>;
  return {
    authMode: readAuthMode(obj.authMode),
    bindAddress: readBindAddress(obj.bindAddress),
  };
}

function readAuthMode(value: unknown): AuthMode {
  return typeof value === "string" && (VALID_AUTH_MODES as readonly string[]).includes(value)
    ? (value as AuthMode)
    : defaultSettings().authMode;
}

function readBindAddress(value: unknown): BindAddress {
  return typeof value === "string" && (VALID_BIND_ADDRESSES as readonly string[]).includes(value)
    ? (value as BindAddress)
    : defaultSettings().bindAddress;
}

// ---------------------------------------------------------------------------
// Write
// ---------------------------------------------------------------------------

/** Outcome of a `writeSettings` call — surfaces before/after + whether the file changed. */
export interface WriteSettingsResult {
  /** Absolute path written. */
  path: string;
  /** Settings as they were BEFORE this write (the prior on-disk values, or defaults). */
  previous: BridgeSettings;
  /** Settings as they are AFTER this write (the new on-disk values). */
  next: BridgeSettings;
  /** True when the file was newly created or its contents changed. */
  changed: boolean;
}

/**
 * Apply a patch to the settings file. Reads the current file (defaults when
 * absent), validates every key in the patch, merges, and writes — atomic temp +
 * rename so a partial write never corrupts the file. Creates the settings
 * directory on first write.
 *
 * Validation:
 *   - Unknown keys → throws `SettingsValidationError` (fail closed; the bridge
 *     would ignore them but the CLI refuses to write a typo silently).
 *   - Invalid `authMode` (not `none`/`required`) → throws.
 *   - Invalid `bindAddress` (not `127.0.0.1`/`0.0.0.0`) → throws.
 *   - `bindAddress:"0.0.0.0"` without `authMode:"required"` → throws
 *     (the bridge refuses to start on a non-loopback interface without token
 *     auth; refusing here gives an actionable error at configure time instead
 *     of a silent refusal at listen time). The check uses the MERGED settings
 *     so `--set bindAddress=0.0.0.0 --set authMode=required` in one invocation
 *     is accepted.
 *
 * Idempotent: a patch that produces settings identical to the current file
 * reports `changed: false` and writes nothing.
 */
export function writeSettings(
  projectRoot: string,
  patch: Partial<BridgeSettings>,
): WriteSettingsResult {
  // Validate the patch keys before touching the filesystem.
  validatePatch(patch);

  const file = settingsPath(projectRoot);
  const previous = readSettings(projectRoot);
  const next: BridgeSettings = { ...previous, ...patch };

  // Cross-field invariant: remote bind requires required auth. Checked against
  // the MERGED settings so a single invocation can set both together.
  if (next.bindAddress === "0.0.0.0" && next.authMode !== "required") {
    throw new SettingsValidationError(
      "bind_address_requires_auth",
      `Remote bind (0.0.0.0) requires authMode "required" — the bridge refuses to start on a non-loopback interface without token auth. Set authMode to "required" first (or in the same invocation): --set authMode=required --set bindAddress=0.0.0.0`,
    );
  }

  const changed = !settingsEqual(previous, next);
  if (changed) {
    const dir = path.dirname(file);
    if (!fs.existsSync(dir)) {
      fs.mkdirSync(dir, { recursive: true });
    }
    const json = serializeSettings(next);
    // Atomic write: .tmp + rename. Matches the bridge's BridgeProjectSettings.Save.
    const tmp = `${file}.tmp`;
    fs.writeFileSync(tmp, json);
    if (fs.existsSync(file)) {
      fs.renameSync(tmp, file);
    } else {
      fs.renameSync(tmp, file);
    }
  }

  return { path: file, previous, next, changed };
}

/** Pretty-print settings as the bridge expects: 2-space indent + trailing newline. */
function serializeSettings(s: BridgeSettings): string {
  // Key order is fixed (authMode, bindAddress) to match the bridge's BuildJson.
  const obj: Record<string, unknown> = { authMode: s.authMode, bindAddress: s.bindAddress };
  return `${JSON.stringify(obj, null, 2)}\n`;
}

function settingsEqual(a: BridgeSettings, b: BridgeSettings): boolean {
  return a.authMode === b.authMode && a.bindAddress === b.bindAddress;
}

// ---------------------------------------------------------------------------
// Validation helpers
// ---------------------------------------------------------------------------

/** True when `key` is one of the bridge settings keys. */
export function isKnownSettingKey(key: string): key is SettingKey {
  return (KNOWN_SETTING_KEYS as readonly string[]).includes(key);
}

/**
 * Validate that a patch only carries known keys with valid values. Throws
 * `SettingsValidationError` on any violation. Pure — no I/O.
 */
export function validatePatch(patch: Partial<BridgeSettings>): void {
  for (const key of Object.keys(patch)) {
    if (!isKnownSettingKey(key)) {
      throw new SettingsValidationError(
        "unknown_key",
        `Unknown setting key '${key}'. Known keys: ${KNOWN_SETTING_KEYS.join(", ")}.`,
      );
    }
  }
  if (patch.authMode !== undefined && !(VALID_AUTH_MODES as readonly string[]).includes(patch.authMode)) {
    throw new SettingsValidationError(
      "invalid_auth_mode",
      `Invalid authMode '${String(patch.authMode)}'. Valid values: ${VALID_AUTH_MODES.join(", ")}.`,
    );
  }
  if (
    patch.bindAddress !== undefined &&
    !(VALID_BIND_ADDRESSES as readonly string[]).includes(patch.bindAddress)
  ) {
    throw new SettingsValidationError(
      "invalid_bind_address",
      `Invalid bindAddress '${String(patch.bindAddress)}'. Valid values: ${VALID_BIND_ADDRESSES.join(", ")}.`,
    );
  }
}

// ---------------------------------------------------------------------------
// --set parsing (key=value)
// ---------------------------------------------------------------------------

/** Parse one or more `--set key=value` tokens into a settings patch. */
export function parseSetAssignments(
  assignments: string[],
): Partial<BridgeSettings> {
  const patch: Partial<BridgeSettings> = {};
  for (const raw of assignments) {
    const eq = raw.indexOf("=");
    if (eq < 0) {
      throw new SettingsValidationError(
        "invalid_set_assignment",
        `--set expects key=value (got '${raw}'). Example: --set authMode=required`,
      );
    }
    const key = raw.slice(0, eq).trim();
    const value = raw.slice(eq + 1).trim();
    if (key.length === 0) {
      throw new SettingsValidationError(
        "invalid_set_assignment",
        `--set expects a non-empty key (got '${raw}'). Example: --set authMode=required`,
      );
    }
    if (!isKnownSettingKey(key)) {
      throw new SettingsValidationError(
        "unknown_key",
        `Unknown setting key '${key}'. Known keys: ${KNOWN_SETTING_KEYS.join(", ")}.`,
      );
    }
    // Assign into the patch; value-type coercion happens in validatePatch by
    // membership check (the valid value sets are string-typed).
    (patch as Record<string, unknown>)[key] = value;
  }
  // Validate the assembled patch (catches invalid values + the cross-field
  // invariant is checked in writeSettings against merged settings).
  validatePatch(patch);
  return patch;
}

// ---------------------------------------------------------------------------
// Error type
// ---------------------------------------------------------------------------

/** Stable error labels surfaced in JSON failure payloads. */
export type SettingsErrorLabel =
  | "unknown_key"
  | "invalid_auth_mode"
  | "invalid_bind_address"
  | "bind_address_requires_auth"
  | "invalid_set_assignment"
  | "settings_write_failed";

/** A typed validation error so command code can map errorLabel → JSON cleanly. */
export class SettingsValidationError extends Error {
  readonly errorLabel: SettingsErrorLabel;
  constructor(errorLabel: SettingsErrorLabel, message: string) {
    super(message);
    this.name = "SettingsValidationError";
    this.errorLabel = errorLabel;
  }
}
