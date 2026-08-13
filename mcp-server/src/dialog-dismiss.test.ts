// dialog-dismiss.ts unit tests (P18.4).
//
// Pins the per-OS dispatch contract without launching osascript/xdotool/
// PowerShell: the single-token stdout parser, the macOS AppleScript shape (it
// embeds the right Godot fragments + tokens + blocked kinds), the xdotool regex
// escape, the config resolution + per-session override holder, and the
// probeGodotDialog tool-facing entry driven by a stub probe. The pure policy
// tables themselves are covered by dialog-policy.test.ts.
//
// Adapted from Unity Open MCP's dialog-dismiss.test.ts (copy the
// parse-output + script-shape assertion shape; the assertions are
// Godot-specific).

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  parseDismissOutput,
  macosDismissAppleScript,
  regexEscapeForXdotool,
  readDialogDismissConfig,
  resolveEffectivePolicy,
  getActiveDialogPolicyOverride,
  setActiveDialogPolicyOverride,
  probeGodotDialog,
  effectivePolicyActs,
  WINDOWS_DISMISS_PS_SCRIPT,
  LINUX_XDOTOOL_MISSING_PREFIX,
  type DismissOutcome,
  type DismissPlatform,
  type DismissProbeOptions,
  _resetDialogDismissStateForTests,
} from "./dialog-dismiss.js";

// ---------------------------------------------------------------------------
// parseDismissOutput
// ---------------------------------------------------------------------------

test("parseDismissOutput: not-found", () => {
  assert.deepEqual(parseDismissOutput("not-found"), { kind: "not-found" });
  // Trailing whitespace / extra blank lines must not change the result.
  assert.deepEqual(parseDismissOutput("not-found\n\n  "), { kind: "not-found" });
  assert.deepEqual(parseDismissOutput(""), { kind: "not-found" });
});

test("parseDismissOutput: dismissed:<button>:<kind>", () => {
  assert.deepEqual(parseDismissOutput("dismissed:Reimport:reimport"), {
    kind: "dismissed",
    button: "Reimport",
    dialog: "reimport",
  });
  assert.deepEqual(parseDismissOutput("dismissed:Save:unsaved_changes"), {
    kind: "dismissed",
    button: "Save",
    dialog: "unsaved_changes",
  });
  assert.deepEqual(parseDismissOutput("dismissed:Focus:script_reload"), {
    kind: "dismissed",
    button: "Focus",
    dialog: "script_reload",
  });
});

test("parseDismissOutput: dismissed inspects the LAST non-empty line", () => {
  // A stray osascript notice before the contract token must not misclassify.
  assert.deepEqual(
    parseDismissOutput("some osascript notice\ndismissed:Reload:script_reload"),
    { kind: "dismissed", button: "Reload", dialog: "script_reload" },
  );
});

test("parseDismissOutput: detected:<kind>:<pipe-buttons>", () => {
  assert.deepEqual(parseDismissOutput("detected:reimport:Reimport|Cancel"), {
    kind: "detected",
    dialog: "reimport",
    detectedButtons: ["Reimport", "Cancel"],
  });
  // Empty button list (Linux path cannot enumerate buttons).
  assert.deepEqual(parseDismissOutput("detected:script_reload:"), {
    kind: "detected",
    dialog: "script_reload",
    detectedButtons: [],
  });
  // No second colon → kind only, empty buttons.
  assert.deepEqual(parseDismissOutput("detected:reimport"), {
    kind: "detected",
    dialog: "reimport",
    detectedButtons: [],
  });
});

test("parseDismissOutput: blocked:<kind>", () => {
  assert.deepEqual(parseDismissOutput("blocked:unsaved_changes"), {
    kind: "blocked",
    dialog: "unsaved_changes",
    message: "Policy declined to dismiss the unsaved_changes dialog",
  });
});

test("parseDismissOutput: unsupported + error", () => {
  assert.deepEqual(parseDismissOutput("unsupported:Wayland is not supported"), {
    kind: "unsupported",
    message: "Wayland is not supported",
  });
  assert.deepEqual(parseDismissOutput("error:not permitted"), {
    kind: "error",
    message: "not permitted",
  });
});

test("parseDismissOutput: unknown / malformed → not-found (defensive)", () => {
  assert.deepEqual(parseDismissOutput("garbage"), { kind: "not-found" });
  assert.deepEqual(parseDismissOutput("dismissed:onlyonefield"), {
    kind: "dismissed",
    button: "onlyonefield",
    dialog: "script_reload",
  });
});

test("parseDismissOutput: type-narrowing round-trips every kind", () => {
  // Smoke: every parse result is a valid DismissOutcome (no extra fields).
  const samples: DismissOutcome[] = [
    { kind: "dismissed", button: "Save", dialog: "unsaved_changes" },
    { kind: "detected", dialog: "reimport", detectedButtons: ["Reimport"] },
    { kind: "not-found" },
    { kind: "blocked", dialog: "unsaved_changes", message: "x" },
    { kind: "unsupported", message: "x" },
    { kind: "error", message: "x" },
  ];
  for (const s of samples) {
    assert.ok(typeof s.kind === "string");
  }
});

// ---------------------------------------------------------------------------
// macOS AppleScript shape
// ---------------------------------------------------------------------------

test("macosDismissAppleScript: ignore script carries detect-only path (no click)", () => {
  const opts: DismissProbeOptions = {
    platform: "darwin",
    policy: "ignore",
    allowUnsavedDismiss: false,
  };
  const script = macosDismissAppleScript(opts);
  // Always checks for the Godot process.
  assert.match(script, /exists process "Godot"/);
  // Human-readable fragments are embedded for each kind.
  assert.match(script, /Save Changes/);
  assert.match(script, /Reimport/);
  assert.match(script, /Reload/);
  // Under ignore there is no dismissed return (detect-only). The detect path
  // returns a detected: line.
  assert.match(script, /return "detected:/);
  assert.doesNotMatch(script, /return "dismissed:/, "ignore must not click");
});

test("macosDismissAppleScript: auto script carries click (dismissed) + blocked guard", () => {
  const opts: DismissProbeOptions = {
    platform: "darwin",
    policy: "auto",
    allowUnsavedDismiss: false,
  };
  const script = macosDismissAppleScript(opts);
  // auto clicks → a dismissed return is present.
  assert.match(script, /return "dismissed:/);
  // unsaved_changes is blocked (no opt-in) → blocked return present.
  assert.match(script, /return "blocked:unsaved_changes"/);
  // reimport is dismissable under auto → a Reimport click block is present.
  assert.match(script, /bt contains "Reimport"/);
});

test("macosDismissAppleScript: auto + opt-in dismisses unsaved_changes", () => {
  const opts: DismissProbeOptions = {
    platform: "darwin",
    policy: "auto",
    allowUnsavedDismiss: true,
  };
  const script = macosDismissAppleScript(opts);
  // No blocked guard for unsaved_changes (opt-in lifts it) → the Save click
  // path is embedded instead.
  assert.doesNotMatch(script, /return "blocked:unsaved_changes"/);
  assert.match(script, /bt contains "Save"/);
});

test("macosDismissAppleScript: detectOnly suppresses the click path under auto", () => {
  const opts: DismissProbeOptions = {
    platform: "darwin",
    policy: "auto",
    allowUnsavedDismiss: false,
    detectOnly: true,
  };
  const script = macosDismissAppleScript(opts);
  assert.doesNotMatch(script, /return "dismissed:/, "detect-only must not click");
  assert.match(script, /return "detected:/);
});

test("macosDismissAppleScript: manual never clicks", () => {
  const opts: DismissProbeOptions = {
    platform: "darwin",
    policy: "manual",
    allowUnsavedDismiss: true,
  };
  const script = macosDismissAppleScript(opts);
  assert.doesNotMatch(script, /return "dismissed:/);
});

test("macosDismissAppleScript: cancel clicks the cancel token", () => {
  const opts: DismissProbeOptions = {
    platform: "darwin",
    policy: "cancel",
    allowUnsavedDismiss: false,
  };
  const script = macosDismissAppleScript(opts);
  assert.match(script, /return "dismissed:/);
  assert.match(script, /bt contains "Cancel"/);
});

// ---------------------------------------------------------------------------
// Windows PowerShell script shape
// ---------------------------------------------------------------------------

test("WINDOWS_DISMISS_PS_SCRIPT embeds the Godot process name + BM_CLICK", () => {
  assert.match(WINDOWS_DISMISS_PS_SCRIPT, /Get-Process -Name 'Godot'/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT, /BM_CLICK = 0x00F5/);
  // The C# normalize + classify path is present.
  assert.match(WINDOWS_DISMISS_PS_SCRIPT, /EnumWindows/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT, /static string Norm\(/);
});

// ---------------------------------------------------------------------------
// xdotool helpers
// ---------------------------------------------------------------------------

test("regexEscapeForXdotool escapes regex metacharacters", () => {
  assert.equal(regexEscapeForXdotool("reimport"), "reimport");
  assert.equal(regexEscapeForXdotool("a.b*c?"), String.raw`a\.b\*c\?`);
  assert.equal(regexEscapeForXdotool("(group)"), String.raw`\(group\)`);
});

// ---------------------------------------------------------------------------
// Config + per-session override
// ---------------------------------------------------------------------------

test("readDialogDismissConfig: defaults to ignore + no opt-in", () => {
  _resetDialogDismissStateForTests();
  const cfg = readDialogDismissConfig({});
  assert.equal(cfg.policy, "ignore");
  assert.equal(cfg.allowUnsavedDismiss, false);
});

test("readDialogDismissConfig: env policy + opt-in are honored", () => {
  _resetDialogDismissStateForTests();
  const cfg = readDialogDismissConfig({
    GODOT_OPEN_MCP_DIALOG_POLICY: "auto",
    GODOT_OPEN_MCP_ALLOW_UNSAVED_DISMISS: "1",
  });
  assert.equal(cfg.policy, "auto");
  assert.equal(cfg.allowUnsavedDismiss, true);
});

test("per-session override wins over env", () => {
  _resetDialogDismissStateForTests();
  setActiveDialogPolicyOverride("cancel");
  assert.equal(getActiveDialogPolicyOverride(), "cancel");
  assert.equal(
    resolveEffectivePolicy({ GODOT_OPEN_MCP_DIALOG_POLICY: "auto" }),
    "cancel",
  );
  // Clearing the override falls back to env.
  setActiveDialogPolicyOverride(undefined);
  assert.equal(
    resolveEffectivePolicy({ GODOT_OPEN_MCP_DIALOG_POLICY: "auto" }),
    "auto",
  );
  _resetDialogDismissStateForTests();
});

test("effectivePolicyActs is false for ignore/manual", () => {
  _resetDialogDismissStateForTests();
  assert.equal(effectivePolicyActs({ GODOT_OPEN_MCP_DIALOG_POLICY: "ignore" }), false);
  assert.equal(effectivePolicyActs({ GODOT_OPEN_MCP_DIALOG_POLICY: "manual" }), false);
  assert.equal(effectivePolicyActs({ GODOT_OPEN_MCP_DIALOG_POLICY: "auto" }), true);
});

// ---------------------------------------------------------------------------
// probeGodotDialog (stubbed probe — no OS call)
// ---------------------------------------------------------------------------

/** Build a stub probe that returns a fixed outcome. */
function stubProbe(
  outcome: DismissOutcome,
): (opts: DismissProbeOptions) => Promise<DismissOutcome> {
  return async () => outcome;
}

test("probeGodotDialog: not-found result carries policy + platform note", async () => {
  _resetDialogDismissStateForTests();
  const result = await probeGodotDialog({
    env: { GODOT_OPEN_MCP_DIALOG_POLICY: "auto" },
    platform: "darwin",
    probe: stubProbe({ kind: "not-found" }),
  });
  assert.equal(result.policy, "auto");
  assert.equal(result.platform, "darwin");
  assert.equal(result.clickPolicy, true);
  assert.equal(result.outcome, "not-found");
  assert.match(result.platformNote ?? "", /Accessibility/);
});

test("probeGodotDialog: dismissed carries dialog + button", async () => {
  _resetDialogDismissStateForTests();
  const result = await probeGodotDialog({
    env: { GODOT_OPEN_MCP_DIALOG_POLICY: "auto" },
    platform: "darwin",
    probe: stubProbe({
      kind: "dismissed",
      button: "Reimport",
      dialog: "reimport",
    }),
  });
  assert.equal(result.outcome, "dismissed");
  assert.equal(result.dialog, "reimport");
  assert.equal(result.button, "Reimport");
});

test("probeGodotDialog: detected carries dialog + detectedButtons", async () => {
  _resetDialogDismissStateForTests();
  const result = await probeGodotDialog({
    env: { GODOT_OPEN_MCP_DIALOG_POLICY: "ignore" },
    platform: "darwin",
    probe: stubProbe({
      kind: "detected",
      dialog: "reimport",
      detectedButtons: ["Reimport", "Cancel"],
    }),
  });
  assert.equal(result.outcome, "detected");
  assert.equal(result.dialog, "reimport");
  assert.deepEqual(result.detectedButtons, ["Reimport", "Cancel"]);
  // ignore is detect-only → clickPolicy false.
  assert.equal(result.clickPolicy, false);
  assert.equal(result.detectOnly, true);
});

test("probeGodotDialog: blocked carries dialog + message", async () => {
  _resetDialogDismissStateForTests();
  const result = await probeGodotDialog({
    env: { GODOT_OPEN_MCP_DIALOG_POLICY: "auto" },
    platform: "darwin",
    probe: stubProbe({
      kind: "blocked",
      dialog: "unsaved_changes",
      message: "destructive",
    }),
  });
  assert.equal(result.outcome, "blocked");
  assert.equal(result.dialog, "unsaved_changes");
  assert.match(result.message ?? "", /destructive/);
});

test("probeGodotDialog: unsupported carries platform note", async () => {
  _resetDialogDismissStateForTests();
  const result = await probeGodotDialog({
    env: {},
    platform: "linux",
    probe: stubProbe({
      kind: "unsupported",
      message: `${LINUX_XDOTOOL_MISSING_PREFIX}. install it.`,
    }),
  });
  assert.equal(result.outcome, "unsupported");
  assert.match(result.message ?? "", /xdotool not found/);
  assert.match(result.platformNote ?? "", /xdotool/);
});

test("probeGodotDialog: error carries message", async () => {
  _resetDialogDismissStateForTests();
  const result = await probeGodotDialog({
    env: { GODOT_OPEN_MCP_DIALOG_POLICY: "auto" },
    platform: "darwin",
    probe: stubProbe({ kind: "error", message: "not permitted" }),
  });
  assert.equal(result.outcome, "error");
  assert.equal(result.message, "not permitted");
});

test("probeGodotDialog: override is reflected in policyOverridden", async () => {
  _resetDialogDismissStateForTests();
  setActiveDialogPolicyOverride("cancel");
  const result = await probeGodotDialog({
    env: { GODOT_OPEN_MCP_DIALOG_POLICY: "auto" },
    platform: "win32",
    probe: stubProbe({ kind: "not-found" }),
  });
  assert.equal(result.policy, "cancel");
  assert.equal(result.policyOverridden, true);
  _resetDialogDismissStateForTests();
});

test("probeGodotDialog: detectOnly forces detect regardless of policy", async () => {
  _resetDialogDismissStateForTests();
  // auto policy but detectOnly → the probe is invoked with detectOnly true.
  let observed: DismissProbeOptions | null = null;
  const result = await probeGodotDialog({
    env: { GODOT_OPEN_MCP_DIALOG_POLICY: "auto" },
    detectOnly: true,
    platform: "darwin",
    probe: async (opts) => {
      observed = opts;
      return { kind: "detected", dialog: "reimport", detectedButtons: [] };
    },
  });
  assert.equal(observed!.detectOnly, true);
  assert.equal(result.detectOnly, true);
  assert.equal(result.outcome, "detected");
});

test("probeGodotDialog: manual short-circuits without spawning the probe", async () => {
  _resetDialogDismissStateForTests();
  let probeCalled = false;
  const result = await probeGodotDialog({
    env: { GODOT_OPEN_MCP_DIALOG_POLICY: "manual" },
    platform: "darwin",
    probe: async () => {
      probeCalled = true;
      return { kind: "not-found" };
    },
  });
  assert.equal(probeCalled, false, "manual must not spawn the desktop probe");
  assert.equal(result.policy, "manual");
  assert.equal(result.outcome, "not-found");
  assert.match(result.message ?? "", /manual/);
});
