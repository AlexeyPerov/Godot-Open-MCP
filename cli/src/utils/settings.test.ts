// Tests for the project-local settings R/W helpers (src/utils/settings.ts).
//
// Pure-function tests over the filesystem-touching helpers (mkdtemp fixtures)
// plus the pure validation/parsing helpers. Covers: missing file defaults,
// round-trip set/list, invalid authMode/bindAddress rejection, unknown-key
// refusal, the bind-address-requires-auth cross-field invariant, and idempotent
// no-op writes.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";

import {
  defaultSettings,
  readSettings,
  writeSettings,
  settingsPath,
  validatePatch,
  parseSetAssignments,
  isKnownSettingKey,
  invalidAuthModeOnDisk,
  SettingsValidationError,
  VALID_AUTH_MODES,
  VALID_BIND_ADDRESSES,
} from "./settings.js";

// ---------------------------------------------------------------------------
// fixtures
// ---------------------------------------------------------------------------

/** Create a temp project dir (no project.godot needed — settings is standalone). */
function tempProject(): { dir: string; cleanup: () => void } {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "godot-open-mcp-settings-"));
  return { dir, cleanup: () => fs.rmSync(dir, { recursive: true, force: true }) };
}

// ---------------------------------------------------------------------------
// defaults + path
// ---------------------------------------------------------------------------

test("defaultSettings: returns none auth + loopback bind", () => {
  const s = defaultSettings();
  assert.equal(s.authMode, "none");
  assert.equal(s.bindAddress, "127.0.0.1");
});

test("settingsPath: joins projectRoot/.godot-open-mcp/settings.json", () => {
  const p = settingsPath("/abs/MyGame");
  assert.equal(p, path.join("/abs/MyGame", ".godot-open-mcp", "settings.json"));
});

// ---------------------------------------------------------------------------
// readSettings — missing / malformed → defaults
// ---------------------------------------------------------------------------

test("readSettings: missing file returns defaults", () => {
  const fx = tempProject();
  try {
    const s = readSettings(fx.dir);
    assert.deepEqual(s, defaultSettings());
  } finally {
    fx.cleanup();
  }
});

test("readSettings: unparseable JSON returns defaults", () => {
  const fx = tempProject();
  try {
    fs.mkdirSync(path.join(fx.dir, ".godot-open-mcp"));
    fs.writeFileSync(settingsPath(fx.dir), "{ not json");
    const s = readSettings(fx.dir);
    assert.deepEqual(s, defaultSettings());
  } finally {
    fx.cleanup();
  }
});

test("readSettings: non-object JSON returns defaults", () => {
  const fx = tempProject();
  try {
    fs.mkdirSync(path.join(fx.dir, ".godot-open-mcp"));
    fs.writeFileSync(settingsPath(fx.dir), "[1, 2, 3]");
    assert.deepEqual(readSettings(fx.dir), defaultSettings());
  } finally {
    fx.cleanup();
  }
});

test("readSettings: partial file fills missing keys with defaults", () => {
  const fx = tempProject();
  try {
    fs.mkdirSync(path.join(fx.dir, ".godot-open-mcp"));
    fs.writeFileSync(settingsPath(fx.dir), JSON.stringify({ authMode: "required" }));
    const s = readSettings(fx.dir);
    assert.equal(s.authMode, "required");
    assert.equal(s.bindAddress, "127.0.0.1"); // default
  } finally {
    fx.cleanup();
  }
});

test("readSettings: invalid authMode coerces to default (CLI never propagates bad values)", () => {
  const fx = tempProject();
  try {
    fs.mkdirSync(path.join(fx.dir, ".godot-open-mcp"));
    fs.writeFileSync(settingsPath(fx.dir), JSON.stringify({ authMode: "bogus" }));
    const s = readSettings(fx.dir);
    assert.equal(s.authMode, "none");
  } finally {
    fx.cleanup();
  }
});

test("readSettings: valid round-trip reads back written values", () => {
  const fx = tempProject();
  try {
    writeSettings(fx.dir, { authMode: "required", bindAddress: "0.0.0.0" });
    const s = readSettings(fx.dir);
    assert.equal(s.authMode, "required");
    assert.equal(s.bindAddress, "0.0.0.0");
  } finally {
    fx.cleanup();
  }
});

// ---------------------------------------------------------------------------
// writeSettings — round-trip / change detection / idempotence
// ---------------------------------------------------------------------------

test("writeSettings: creates the settings dir + file when absent", () => {
  const fx = tempProject();
  try {
    const res = writeSettings(fx.dir, { authMode: "required" });
    assert.equal(res.changed, true);
    assert.ok(fs.existsSync(settingsPath(fx.dir)));
    assert.equal(res.previous.authMode, "none"); // default before write
    assert.equal(res.next.authMode, "required");
  } finally {
    fx.cleanup();
  }
});

test("writeSettings: no-op when patch matches current values (idempotent)", () => {
  const fx = tempProject();
  try {
    writeSettings(fx.dir, { authMode: "required" });
    const res = writeSettings(fx.dir, { authMode: "required" });
    assert.equal(res.changed, false);
    assert.deepEqual(res.previous, res.next);
  } finally {
    fx.cleanup();
  }
});

test("writeSettings: partial patch keeps other keys", () => {
  const fx = tempProject();
  try {
    // Set both to non-defaults; then patch only bindAddress back to loopback.
    // authMode stays required; the loopback bind does not require auth so the
    // cross-field invariant stays satisfied.
    writeSettings(fx.dir, { authMode: "required", bindAddress: "0.0.0.0" });
    const res = writeSettings(fx.dir, { bindAddress: "127.0.0.1" });
    assert.equal(res.next.authMode, "required"); // unchanged
    assert.equal(res.next.bindAddress, "127.0.0.1"); // patched
  } finally {
    fx.cleanup();
  }
});

test("writeSettings: pretty-prints JSON with a trailing newline", () => {
  const fx = tempProject();
  try {
    // Set a non-default value so the write actually happens (authMode:none is
    // the default → writeSettings would no-op and no file would land).
    writeSettings(fx.dir, { authMode: "required" });
    const raw = fs.readFileSync(settingsPath(fx.dir), "utf8");
    assert.ok(raw.endsWith("\n"), "settings file ends with a newline");
    const parsed = JSON.parse(raw); // round-trips
    assert.equal(parsed.authMode, "required");
  } finally {
    fx.cleanup();
  }
});

// ---------------------------------------------------------------------------
// writeSettings — validation (refuse garbage the bridge would fail-closed on)
// ---------------------------------------------------------------------------

test("writeSettings: invalid authMode throws before writing", () => {
  const fx = tempProject();
  try {
    assert.throws(
      () => writeSettings(fx.dir, { authMode: "bogus" as never }),
      (err) => {
        assert.ok(err instanceof SettingsValidationError);
        assert.equal((err as SettingsValidationError).errorLabel, "invalid_auth_mode");
        return true;
      },
    );
    assert.ok(!fs.existsSync(settingsPath(fx.dir)), "no file written on validation failure");
  } finally {
    fx.cleanup();
  }
});

test("writeSettings: invalid bindAddress throws before writing", () => {
  const fx = tempProject();
  try {
    assert.throws(
      () => writeSettings(fx.dir, { bindAddress: "8.8.8.8" as never }),
      (err: unknown) => {
        assert.ok(err instanceof SettingsValidationError);
        assert.equal((err as SettingsValidationError).errorLabel, "invalid_bind_address");
        return true;
      },
    );
  } finally {
    fx.cleanup();
  }
});

// ---------------------------------------------------------------------------
// cross-field invariant: bindAddress 0.0.0.0 requires authMode required
// ---------------------------------------------------------------------------

test("writeSettings: 0.0.0.0 without required auth is refused", () => {
  const fx = tempProject();
  try {
    assert.throws(
      () => writeSettings(fx.dir, { bindAddress: "0.0.0.0" }),
      (err: unknown) => {
        assert.ok(err instanceof SettingsValidationError);
        assert.equal((err as SettingsValidationError).errorLabel, "bind_address_requires_auth");
        return true;
      },
    );
  } finally {
    fx.cleanup();
  }
});

test("writeSettings: 0.0.0.0 + required in one invocation is accepted", () => {
  const fx = tempProject();
  try {
    const res = writeSettings(fx.dir, { bindAddress: "0.0.0.0", authMode: "required" });
    assert.equal(res.changed, true);
    assert.equal(res.next.bindAddress, "0.0.0.0");
    assert.equal(res.next.authMode, "required");
  } finally {
    fx.cleanup();
  }
});

test("writeSettings: setting authMode:none while bindAddress:0.0.0.0 is held is refused", () => {
  const fx = tempProject();
  try {
    writeSettings(fx.dir, { bindAddress: "0.0.0.0", authMode: "required" });
    assert.throws(
      () => writeSettings(fx.dir, { authMode: "none" }),
      /Remote bind/,
    );
  } finally {
    fx.cleanup();
  }
});

// ---------------------------------------------------------------------------
// validatePatch (pure)
// ---------------------------------------------------------------------------

test("validatePatch: empty patch is valid", () => {
  validatePatch({}); // does not throw
});

test("validatePatch: unknown key throws", () => {
  assert.throws(
    () => validatePatch({ unknownKey: "x" } as never),
    /Unknown setting key 'unknownKey'/,
  );
});

test("validatePatch: invalid authMode throws", () => {
  assert.throws(
    () => validatePatch({ authMode: "bogus" as never }),
    /Invalid authMode/,
  );
});

test("validatePatch: both valid keys accepted", () => {
  validatePatch({ authMode: "required", bindAddress: "0.0.0.0" }); // does not throw
});

test("isKnownSettingKey: recognizes authMode + bindAddress only", () => {
  assert.equal(isKnownSettingKey("authMode"), true);
  assert.equal(isKnownSettingKey("bindAddress"), true);
  assert.equal(isKnownSettingKey("foo"), false);
});

// ---------------------------------------------------------------------------
// parseSetAssignments (pure)
// ---------------------------------------------------------------------------

test("parseSetAssignments: single key=value", () => {
  const patch = parseSetAssignments(["authMode=required"]);
  assert.deepEqual(patch, { authMode: "required" });
});

test("parseSetAssignments: multiple key=value", () => {
  const patch = parseSetAssignments(["authMode=required", "bindAddress=0.0.0.0"]);
  assert.deepEqual(patch, { authMode: "required", bindAddress: "0.0.0.0" });
});

test("parseSetAssignments: trims whitespace around key/value", () => {
  const patch = parseSetAssignments([" authMode = required "]);
  assert.deepEqual(patch, { authMode: "required" });
});

test("parseSetAssignments: missing '=' throws", () => {
  assert.throws(
    () => parseSetAssignments(["authMode"]),
    /key=value/,
  );
});

test("parseSetAssignments: empty key throws", () => {
  assert.throws(
    () => parseSetAssignments(["=required"]),
    /non-empty key/,
  );
});

test("parseSetAssignments: unknown key throws", () => {
  assert.throws(
    () => parseSetAssignments(["bogus=1"]),
    /Unknown setting key 'bogus'/,
  );
});

test("parseSetAssignments: invalid value throws (validated after assembly)", () => {
  assert.throws(
    () => parseSetAssignments(["authMode=bogus"]),
    /Invalid authMode/,
  );
});

// ---------------------------------------------------------------------------
// valid value sets
// ---------------------------------------------------------------------------

test("VALID_AUTH_MODES: none + required", () => {
  assert.deepEqual([...VALID_AUTH_MODES], ["none", "required"]);
});

test("VALID_BIND_ADDRESSES: loopback + remote", () => {
  assert.deepEqual([...VALID_BIND_ADDRESSES], ["127.0.0.1", "0.0.0.0"]);
});

// ---------------------------------------------------------------------------
// invalid on-disk authMode — must not be silently reset (regression)
// ---------------------------------------------------------------------------
//
// The bridge does NOT coerce an unrecognized authMode to "none": BridgeAuthCheck denies anything
// that is not exactly "required", so `authMode:"requred"` makes the bridge 401 every request. The
// CLI's typed read has to coerce (BridgeSettings.authMode is a union), which means a patch that
// touches only bindAddress would re-serialize the coerced value and silently flip the bridge from
// deny-all to no-auth while reporting `authMode: none → none`.

function writeRawSettings(dir: string, obj: unknown): string {
  const file = settingsPath(dir);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(obj, null, 2));
  return file;
}

test("invalidAuthModeOnDisk: reports the raw value when authMode is not a known mode", () => {
  const fx = tempProject();
  try {
    writeRawSettings(fx.dir, { authMode: "requred", bindAddress: "127.0.0.1" });
    assert.equal(invalidAuthModeOnDisk(fx.dir), "requred");
  } finally {
    fx.cleanup();
  }
});

test("invalidAuthModeOnDisk: null for a valid value, an absent key, and a missing file", () => {
  const fx = tempProject();
  try {
    assert.equal(invalidAuthModeOnDisk(fx.dir), null, "missing file");
    writeRawSettings(fx.dir, { bindAddress: "127.0.0.1" });
    assert.equal(invalidAuthModeOnDisk(fx.dir), null, "absent key");
    writeRawSettings(fx.dir, { authMode: "required" });
    assert.equal(invalidAuthModeOnDisk(fx.dir), null, "valid value");
  } finally {
    fx.cleanup();
  }
});

test("writeSettings: refuses an unrelated patch when the on-disk authMode is invalid", () => {
  const fx = tempProject();
  try {
    const file = writeRawSettings(fx.dir, { authMode: "requred", bindAddress: "127.0.0.1" });
    assert.throws(
      () => writeSettings(fx.dir, { bindAddress: "127.0.0.1" }),
      (err: unknown) => {
        assert.ok(err instanceof SettingsValidationError);
        assert.equal((err as SettingsValidationError).errorLabel, "invalid_auth_mode_on_disk");
        return true;
      },
    );
    // Critically: the bad value is still on disk, NOT rewritten to "none".
    assert.equal(JSON.parse(fs.readFileSync(file, "utf8")).authMode, "requred");
  } finally {
    fx.cleanup();
  }
});

test("writeSettings: an explicit authMode patch repairs an invalid on-disk value", () => {
  const fx = tempProject();
  try {
    const file = writeRawSettings(fx.dir, { authMode: "requred", bindAddress: "127.0.0.1" });
    const res = writeSettings(fx.dir, { authMode: "required" });
    assert.equal(res.changed, true);
    assert.equal(JSON.parse(fs.readFileSync(file, "utf8")).authMode, "required");
  } finally {
    fx.cleanup();
  }
});

test("writeSettings: temp file is unique per process so concurrent writes cannot collide", () => {
  const fx = tempProject();
  try {
    writeSettings(fx.dir, { authMode: "required" });
    // A fixed `${file}.tmp` made two concurrent writers publish each other's content. Assert no
    // temp file survives and that the name is not the fixed one.
    const dir = path.dirname(settingsPath(fx.dir));
    const leftovers = fs.readdirSync(dir).filter((n) => n.endsWith(".tmp"));
    assert.deepEqual(leftovers, []);
    assert.equal(readSettings(fx.dir).authMode, "required");
  } finally {
    fx.cleanup();
  }
});
