// Drift-detection tests for the rule + fix catalog (P3.8).
//
// Pins the TS catalog against the C# verify package so any rule/fix change in C# that is not mirrored
// here surfaces as a test failure. Per packages/verify/AGENTS.md §Capability catalog sync, the catalog
// MUST stay in sync on every rule/fix change.
//
// The C# constants live at:
//   - packages/verify/Editor/Rules/BrokenReferences/IssueCodes.cs  → broken_scene_reference
//   - packages/verify/Editor/Rules/MissingScripts/IssueCodes.cs    → missing_script
//   - packages/verify/Editor/Rules/ImportHealth/IssueCodes.cs      → orphan_import, duplicate_uid
//   - packages/verify/Editor/Rules/*/{Rule}Rule.cs                 → RuleId const + severity
//   - packages/verify/Editor/Fixes/RemoveMissingScriptFix.cs       → FixId + CanFix ruleId/code

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  RULE_CATALOG,
  FIX_CATALOG,
  implementedRules,
  plannedRules,
  implementedFixes,
  plannedFixes,
} from "./rule-catalog.js";

// ---------------------------------------------------------------------------
// Rule catalog — mirrors the C# verify package
// ---------------------------------------------------------------------------

test("rule catalog lists the three implemented Godot rules", () => {
  const ids = RULE_CATALOG.filter((r) => r.implemented).map((r) => r.id);
  assert.deepEqual(ids.sort(), ["broken_references", "import_health", "missing_scripts"]);
});

test("no planned rules in v1", () => {
  assert.equal(plannedRules().length, 0);
});

test("broken_references emits broken_scene_reference (Error)", () => {
  // C# source of truth: BrokenReferencesIssueCodes.BrokenSceneReference = "broken_scene_reference";
  // BrokenReferencesRule.MakeIssue sets VerifySeverity.Error.
  const rule = RULE_CATALOG.find((r) => r.id === "broken_references");
  assert.ok(rule);
  const issue = rule!.issues.find((i) => i.code === "broken_scene_reference");
  assert.ok(issue, "broken_scene_reference code present");
  assert.equal(issue!.severity, "Error");
  assert.deepEqual(issue!.fixIds, ["relink_broken_reference"]);
});

test("missing_scripts emits missing_script (Error) with remove_missing_script fix", () => {
  // C# source of truth: MissingScriptsIssueCodes.MissingScript = "missing_script";
  // MissingScriptsRule.MakeIssue sets VerifySeverity.Error; RemoveMissingScriptFix.CanFix matches
  // missing_scripts|missing_script.
  const rule = RULE_CATALOG.find((r) => r.id === "missing_scripts");
  assert.ok(rule);
  const issue = rule!.issues.find((i) => i.code === "missing_script");
  assert.ok(issue, "missing_script code present");
  assert.equal(issue!.severity, "Error");
  assert.deepEqual(issue!.fixIds, ["remove_missing_script"]);
});

test("import_health emits orphan_import (Warning) and duplicate_uid (Error)", () => {
  // C# source of truth: ImportHealthIssueCodes.OrphanImport = "orphan_import" (Warning);
  // ImportHealthIssueCodes.DuplicateUid = "duplicate_uid" (Error).
  const rule = RULE_CATALOG.find((r) => r.id === "import_health");
  assert.ok(rule);
  const orphan = rule!.issues.find((i) => i.code === "orphan_import");
  assert.ok(orphan, "orphan_import code present");
  assert.equal(orphan!.severity, "Warning");
  assert.deepEqual(orphan!.fixIds, ["remove_orphan_import"]);
  const dup = rule!.issues.find((i) => i.code === "duplicate_uid");
  assert.ok(dup, "duplicate_uid code present");
  assert.equal(dup!.severity, "Error");
  assert.deepEqual(dup!.fixIds, ["fix_duplicate_uid"]);
});

test("every implemented rule declares at least one issue code", () => {
  for (const rule of implementedRules()) {
    assert.ok(rule.issues.length > 0, `rule ${rule.id} declares no issue codes`);
  }
});

test("every issue severity is Error or Warning", () => {
  for (const rule of RULE_CATALOG) {
    for (const issue of rule.issues) {
      assert.ok(
        issue.severity === "Error" || issue.severity === "Warning",
        `issue ${rule.id}/${issue.code} has invalid severity ${issue.severity}`,
      );
    }
  }
});

// ---------------------------------------------------------------------------
// Fix catalog — mirrors FixProviderRegistry.RegisterDefaults
// ---------------------------------------------------------------------------

test("fix catalog lists all four implemented fixes", () => {
  const ids = FIX_CATALOG.filter((f) => f.implemented).map((f) => f.id).sort();
  assert.deepEqual(ids, [
    "fix_duplicate_uid",
    "relink_broken_reference",
    "remove_missing_script",
    "remove_orphan_import",
  ]);
});

test("remove_missing_script is Safe:true", () => {
  // C# source of truth: FixProviderRegistry.RegisterDefaults adds RemoveMissingScriptFix;
  // RemoveMissingScriptFix.FixId = "remove_missing_script"; Describe().Safe = true for .tscn/.tres.
  const fix = FIX_CATALOG.find((f) => f.id === "remove_missing_script");
  assert.ok(fix);
  assert.equal(fix!.implemented, true);
  assert.equal(fix!.safe, true);
  assert.deepEqual(fix!.rules, ["missing_scripts"]);
  assert.deepEqual(fix!.issueCodes, ["missing_script"]);
});

test("every fix references at least one rule + issue code", () => {
  for (const fix of FIX_CATALOG) {
    assert.ok(fix.rules.length > 0, `fix ${fix.id} declares no rules`);
    assert.ok(fix.issueCodes.length > 0, `fix ${fix.id} declares no issue codes`);
  }
});

test("every fix's rule + issue code exists in the rule catalog", () => {
  // A fix that names a rule/code the catalog does not know about is a drift bug.
  const known = new Map<string, Set<string>>();
  for (const rule of RULE_CATALOG) {
    known.set(rule.id, new Set(rule.issues.map((i) => i.code)));
  }
  for (const fix of FIX_CATALOG) {
    for (const ruleId of fix.rules) {
      const codes = known.get(ruleId);
      assert.ok(codes, `fix ${fix.id} references unknown rule ${ruleId}`);
      for (const code of fix.issueCodes) {
        assert.ok(codes!.has(code), `fix ${fix.id} references unknown issue ${ruleId}/${code}`);
      }
    }
  }
});

test("implementedFixes / plannedFixes partition the catalog", () => {
  assert.equal(implementedFixes().length, FIX_CATALOG.filter((f) => f.implemented).length);
  assert.equal(plannedFixes().length, FIX_CATALOG.filter((f) => !f.implemented).length);
  assert.equal(implementedFixes().length + plannedFixes().length, FIX_CATALOG.length);
});
