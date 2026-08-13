// dialog-policy.ts unit tests (P18.4).
//
// Pins the pure policy tables: the 5-variant policy enum, parseDialogPolicy,
// normalize/classify, the per-kind-per-policy token tables, the destructive
// (unsaved_changes) opt-in gate, generic fallback, and buildTokenTable shape.
// Adapted from Unity Open MCP's dialog-policy.test.ts (copy the table-assertion
// shape; the assertions are Godot-specific).

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  DIALOG_POLICY_VALUES,
  DEFAULT_DIALOG_POLICY,
  DIALOG_POLICY_ENV,
  DIALOG_TITLE_FRAGMENTS,
  parseDialogPolicy,
  normalizeDialogLabel,
  classifyDialogTitle,
  preferenceTokensForPolicy,
  genericFallbackTokens,
  blockedKindsForPolicy,
  isUnsavedBlocked,
  preferredDialogButtonLabel,
  preferredGenericButtonLabel,
  policyClicks,
  buildTokenTable,
  type DialogPolicy,
  type DialogKind,
} from "./dialog-policy.js";

// ---------------------------------------------------------------------------
// Policy taxonomy
// ---------------------------------------------------------------------------

test("policy taxonomy is exactly 5 variants (no safe-mode)", () => {
  // Godot has no Safe Mode, so Unity's `safe-mode` is intentionally dropped.
  assert.deepEqual([...DIALOG_POLICY_VALUES], [
    "ignore",
    "auto",
    "recover",
    "cancel",
    "manual",
  ]);
  assert.equal(DEFAULT_DIALOG_POLICY, "ignore");
  assert.ok(!(DIALOG_POLICY_VALUES as readonly string[]).includes("safe-mode"));
  assert.equal(DIALOG_POLICY_ENV, "GODOT_OPEN_MCP_DIALOG_POLICY");
});

test("parseDialogPolicy: unset → default", () => {
  assert.equal(parseDialogPolicy({}), DEFAULT_DIALOG_POLICY);
  assert.equal(parseDialogPolicy({ [DIALOG_POLICY_ENV]: "" }), DEFAULT_DIALOG_POLICY);
});

test("parseDialogPolicy: valid values normalize (case/whitespace) and round-trip", () => {
  for (const p of DIALOG_POLICY_VALUES) {
    assert.equal(parseDialogPolicy({ [DIALOG_POLICY_ENV]: p }), p);
    assert.equal(parseDialogPolicy({ [DIALOG_POLICY_ENV]: p.toUpperCase() }), p);
    assert.equal(parseDialogPolicy({ [DIALOG_POLICY_ENV]: `  ${p}  ` }), p);
  }
});

test("parseDialogPolicy: invalid → default + warning", () => {
  const warnings: string[] = [];
  const got = parseDialogPolicy(
    { [DIALOG_POLICY_ENV]: "safe-mode" },
    (m) => warnings.push(m),
  );
  assert.equal(got, DEFAULT_DIALOG_POLICY);
  assert.equal(warnings.length, 1);
  assert.match(warnings[0], /invalid GODOT_OPEN_MCP_DIALOG_POLICY/);
  assert.match(warnings[0], /safe-mode/);
});

test("parseDialogPolicy: warning sink defaults to console.warn (no throw)", () => {
  // Smoke: calling without an explicit sink must not throw on an invalid value.
  const got = parseDialogPolicy({ [DIALOG_POLICY_ENV]: "bogus" });
  assert.equal(got, DEFAULT_DIALOG_POLICY);
});

// ---------------------------------------------------------------------------
// normalize + classify
// ---------------------------------------------------------------------------

test("normalizeDialogLabel strips punctuation + lowercases", () => {
  assert.equal(normalizeDialogLabel("Save Changes?"), "savechanges");
  assert.equal(normalizeDialogLabel("Re-import"), "reimport");
  assert.equal(normalizeDialogLabel("Script has been reloaded..."), "scripthasbeenreloaded");
  assert.equal(normalizeDialogLabel(""), "");
  assert.equal(normalizeDialogLabel("   "), "");
});

test("classifyDialogTitle classifies each Godot modal family", () => {
  // unsaved_changes (several spellings across Godot versions).
  assert.equal(classifyDialogTitle("Save Changes?"), "unsaved_changes");
  assert.equal(classifyDialogTitle("There are unsaved changes"), "unsaved_changes");
  assert.equal(classifyDialogTitle("Scene modified externally"), "unsaved_changes");
  assert.equal(classifyDialogTitle("Do you want to save?"), "unsaved_changes");
  // reimport.
  assert.equal(classifyDialogTitle("Reimport"), "reimport");
  assert.equal(classifyDialogTitle("Re-import affected resources"), "reimport");
  // script_reload.
  assert.equal(classifyDialogTitle("Reload"), "script_reload");
  assert.equal(classifyDialogTitle("Script has been reloaded"), "script_reload");
  assert.equal(classifyDialogTitle("Reload tool"), "script_reload");
});

test("classifyDialogTitle returns null for non-matching / empty titles", () => {
  assert.equal(classifyDialogTitle("Project Settings"), null);
  assert.equal(classifyDialogTitle(""), null);
  assert.equal(classifyDialogTitle("   "), null);
});

test("classifyDialogTitle prefers unsaved_changes over script_reload (specificity)", () => {
  // A title carrying both a save-changes fragment and "reload" should classify
  // as the more specific unsaved_changes kind, not the broad script_reload.
  assert.equal(
    classifyDialogTitle("Save changes and reload?"),
    "unsaved_changes",
  );
});

test("DIALOG_TITLE_FRAGMENTS covers all three Godot kinds", () => {
  const kinds = Object.keys(DIALOG_TITLE_FRAGMENTS) as DialogKind[];
  assert.deepEqual([...kinds].sort(), ["reimport", "script_reload", "unsaved_changes"]);
  for (const kind of kinds) {
    assert.ok(
      DIALOG_TITLE_FRAGMENTS[kind].length > 0,
      `${kind} must carry at least one fragment`,
    );
  }
});

// ---------------------------------------------------------------------------
// preferenceTokensForPolicy
// ---------------------------------------------------------------------------

test("ignore + manual never carry tokens for any kind", () => {
  for (const kind of Object.keys(DIALOG_TITLE_FRAGMENTS) as DialogKind[]) {
    assert.equal(preferenceTokensForPolicy(kind, "ignore", true), null);
    assert.equal(preferenceTokensForPolicy(kind, "manual", true), null);
  }
});

test("unsaved_changes is blocked by default regardless of policy", () => {
  // The destructive opt-in gate: without it, no policy clicks.
  for (const policy of DIALOG_POLICY_VALUES) {
    assert.equal(
      preferenceTokensForPolicy("unsaved_changes", policy, false),
      null,
      `${policy} must not dismiss unsaved_changes without the opt-in`,
    );
  }
});

test("unsaved_changes with the opt-in: auto/recover prefer Save; cancel prefers Don't Save", () => {
  const auto = preferenceTokensForPolicy("unsaved_changes", "auto", true)!;
  assert.ok(auto !== null);
  assert.equal(auto[0], "save", "auto prefers Save (preserve work)");
  const recover = preferenceTokensForPolicy("unsaved_changes", "recover", true)!;
  assert.equal(recover[0], "save");
  const cancel = preferenceTokensForPolicy("unsaved_changes", "cancel", true)!;
  assert.equal(cancel[0], "dontsave", "cancel prefers Don't Save");
});

test("reimport: auto/recover prefer Reimport; cancel prefers Cancel", () => {
  assert.equal(preferenceTokensForPolicy("reimport", "auto")![0], "reimport");
  assert.equal(preferenceTokensForPolicy("reimport", "recover")![0], "reimport");
  assert.equal(preferenceTokensForPolicy("reimport", "cancel")![0], "cancel");
});

test("script_reload: auto/recover prefer Reload; cancel prefers Cancel", () => {
  assert.equal(preferenceTokensForPolicy("script_reload", "auto")![0], "reload");
  assert.equal(preferenceTokensForPolicy("script_reload", "recover")![0], "reload");
  assert.equal(preferenceTokensForPolicy("script_reload", "cancel")![0], "cancel");
});

test("policyClicks: only auto/recover/cancel click", () => {
  assert.equal(policyClicks("ignore"), false);
  assert.equal(policyClicks("manual"), false);
  assert.equal(policyClicks("auto"), true);
  assert.equal(policyClicks("recover"), true);
  assert.equal(policyClicks("cancel"), true);
});

// ---------------------------------------------------------------------------
// blockedKindsForPolicy + isUnsavedBlocked
// ---------------------------------------------------------------------------

test("blockedKindsForPolicy lists unsaved_changes unless the opt-in is set", () => {
  for (const policy of DIALOG_POLICY_VALUES) {
    const blocked = blockedKindsForPolicy(policy, false);
    assert.deepEqual([...blocked], ["unsaved_changes"]);
    const allowed = blockedKindsForPolicy(policy, true);
    assert.deepEqual([...allowed], []);
  }
});

test("isUnsavedBlocked is true unless the opt-in is set (independent of policy)", () => {
  for (const policy of DIALOG_POLICY_VALUES) {
    assert.equal(isUnsavedBlocked(policy, false), true);
    assert.equal(isUnsavedBlocked(policy, true), false);
  }
});

// ---------------------------------------------------------------------------
// preferredDialogButtonLabel + preferredGenericButtonLabel
// ---------------------------------------------------------------------------

test("preferredDialogButtonLabel picks the highest-priority matching button", () => {
  // reimport under auto: tokens [reimport, ok, yes, continue]; present buttons
  // [Cancel, Reimport] → Reimport wins.
  const got = preferredDialogButtonLabel("reimport", ["Cancel", "Reimport"], "auto");
  assert.deepEqual(got, { button: "Reimport", token: "reimport" });
});

test("preferredDialogButtonLabel returns null when the policy declines", () => {
  // ignore/manual never click.
  assert.equal(
    preferredDialogButtonLabel("reimport", ["Reimport"], "ignore"),
    null,
  );
  assert.equal(
    preferredDialogButtonLabel("reimport", ["Reimport"], "manual"),
    null,
  );
  // unsaved_changes without opt-in.
  assert.equal(
    preferredDialogButtonLabel("unsaved_changes", ["Save"], "auto"),
    null,
  );
});

test("preferredDialogButtonLabel returns null when no token matches", () => {
  // reimport under auto but only a non-matching button present.
  assert.equal(
    preferredDialogButtonLabel("reimport", ["Something Else"], "auto"),
    null,
  );
});

test("preferredDialogButtonLabel: unsaved opt-in path", () => {
  // With the opt-in, auto picks Save when Save + Don't Save are both present.
  const got = preferredDialogButtonLabel(
    "unsaved_changes",
    ["Don't Save", "Cancel", "Save"],
    "auto",
    { allowUnsavedDismiss: true },
  );
  assert.deepEqual(got, { button: "Save", token: "save" });
  // cancel picks Don't Save.
  const cancelGot = preferredDialogButtonLabel(
    "unsaved_changes",
    ["Save", "Don't Save", "Cancel"],
    "cancel",
    { allowUnsavedDismiss: true },
  );
  assert.deepEqual(cancelGot, { button: "Don't Save", token: "dontsave" });
});

test("preferredDialogButtonLabel: exact match beats substring (Save vs Don't Save)", () => {
  // Regression pin: token `save` substring-matches "Don't Save" (norm
  // `dontsave` contains `save`), but the EXACT match on "Save" must win so the
  // destructive Don't Save never shadows the safe Save under auto/recover.
  const got = preferredDialogButtonLabel(
    "unsaved_changes",
    ["Don't Save", "Save"],
    "auto",
    { allowUnsavedDismiss: true },
  );
  assert.deepEqual(got, { button: "Save", token: "save" });
});

test("preferredGenericButtonLabel returns null for ignore/manual", () => {
  assert.equal(preferredGenericButtonLabel(["OK"], "ignore"), null);
  assert.equal(preferredGenericButtonLabel(["OK"], "manual"), null);
});

test("preferredGenericButtonLabel matches a generic token for clicking policies", () => {
  assert.deepEqual(preferredGenericButtonLabel(["Cancel", "Reimport"], "auto"), {
    button: "Reimport",
    token: "reimport",
  });
});

// ---------------------------------------------------------------------------
// genericFallbackTokens
// ---------------------------------------------------------------------------

test("genericFallbackTokens: ignore/manual → empty; clicking policies non-empty", () => {
  assert.deepEqual([...genericFallbackTokens("ignore")], []);
  assert.deepEqual([...genericFallbackTokens("manual")], []);
  for (const policy of ["auto", "recover", "cancel"] as DialogPolicy[]) {
    assert.ok(genericFallbackTokens(policy).length > 0, `${policy} generic tokens empty`);
  }
});

// ---------------------------------------------------------------------------
// buildTokenTable
// ---------------------------------------------------------------------------

test("buildTokenTable: ignore → click false + every kind tokens null", () => {
  const table = buildTokenTable("ignore", false);
  assert.equal(table.click, false);
  for (const kind of Object.keys(table.kinds) as DialogKind[]) {
    assert.equal(table.kinds[kind].tokens, null);
  }
  assert.deepEqual([...table.blocked], ["unsaved_changes"]);
  assert.deepEqual([...table.genericTokens], []);
});

test("buildTokenTable: auto (no opt-in) → click true + unsaved_changes tokens null", () => {
  const table = buildTokenTable("auto", false);
  assert.equal(table.click, true);
  assert.equal(table.kinds.unsaved_changes.tokens, null, "opt-in gate holds");
  assert.notEqual(table.kinds.reimport.tokens, null);
  assert.notEqual(table.kinds.script_reload.tokens, null);
  assert.deepEqual([...table.blocked], ["unsaved_changes"]);
  assert.ok(table.genericTokens.length > 0);
});

test("buildTokenTable: auto + opt-in → unsaved_changes carries tokens", () => {
  const table = buildTokenTable("auto", true);
  assert.notEqual(table.kinds.unsaved_changes.tokens, null);
  assert.deepEqual([...table.blocked], []);
});

test("buildTokenTable: manual → click false", () => {
  const table = buildTokenTable("manual", false);
  assert.equal(table.click, false);
});

test("buildTokenTable carries fragments for every kind", () => {
  const table = buildTokenTable("auto", false);
  for (const kind of Object.keys(table.kinds) as DialogKind[]) {
    assert.ok(table.kinds[kind].fragments.length > 0, `${kind} fragments empty`);
  }
});
