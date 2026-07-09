#nullable enable
using System.Collections.Generic;
using GodotOpenMcp.Bridge.Editor;
using GodotOpenMcp.Verify.Core;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// Tests <see cref="VerifyGateAdapter.ComputeDelta"/>: the before/after issue delta that drives
    /// the gate decision. The delta is pure set math over canonical <see cref="IssueKey"/> strings, so
    /// these tests build synthetic <see cref="CheckpointFingerprint"/> + <see cref="VerifyResult"/>
    /// inputs and assert the new/resolved counts and keys — no Godot APIs, no live verify rules.
    ///
    /// <para>
    /// Ported (copy) from Unity Open MCP's delta-correctness pattern. The Godot issue codes used here
    /// (<c>broken_scene_reference</c>, <c>missing_script</c>) match the P3.2/P3.3 rules so the test
    /// fixtures stay realistic.
    /// </para>
    /// </para>
    /// </summary>
    public class VerifyGateAdapterTests
    {
        // -------------------------------------------------------------------
        // ComputeDelta — the before/after set difference
        // -------------------------------------------------------------------

        [Fact]
        public void ComputeDelta_NoIssues_CleanDelta()
        {
            var before = Checkpoint(
                ("broken_references", 0, 0));
            var after = Result(); // no issues

            var delta = VerifyGateAdapter.ComputeDelta(before, after);

            Assert.Equal(0, delta.NewErrors);
            Assert.Equal(0, delta.NewWarnings);
            Assert.Equal(0, delta.ResolvedErrors);
            Assert.Equal(0, delta.ResolvedWarnings);
            Assert.Empty(delta.NewIssueKeys);
            Assert.Empty(delta.ResolvedIssueKeys);
        }

        [Fact]
        public void ComputeDelta_NewError_CountsAsNewError()
        {
            var before = Checkpoint(("broken_references", 0, 0));
            var after = Result(Issue("broken_references", VerifySeverity.Error,
                "res://Main.tscn", "broken_scene_reference"));

            var delta = VerifyGateAdapter.ComputeDelta(before, after);

            Assert.Equal(1, delta.NewErrors);
            Assert.Equal(0, delta.NewWarnings);
            Assert.NotEmpty(delta.NewIssueKeys);
        }

        [Fact]
        public void ComputeDelta_PreExistingIssue_IsNotNew()
        {
            // An issue present at BOTH checkpoint and validate must NOT count as new — it was there
            // before the mutation. This is the load-bearing guard: every call must not re-fail on
            // pre-existing errors.
            var issue = Issue("broken_references", VerifySeverity.Error,
                "res://Main.tscn", "broken_scene_reference");
            var before = Checkpoint(("broken_references", 1, 0, IssueKey.Build(issue)));
            var after = Result(issue);

            var delta = VerifyGateAdapter.ComputeDelta(before, after);

            Assert.Equal(0, delta.NewErrors);
            Assert.Equal(0, delta.ResolvedErrors);
        }

        [Fact]
        public void ComputeDelta_ResolvedIssue_CountsAsResolved()
        {
            var issue = Issue("broken_references", VerifySeverity.Error,
                "res://Main.tscn", "broken_scene_reference");
            var before = Checkpoint(("broken_references", 1, 0, IssueKey.Build(issue)));
            var after = Result(); // the mutation fixed it

            var delta = VerifyGateAdapter.ComputeDelta(before, after);

            Assert.Equal(0, delta.NewErrors);
            Assert.Equal(1, delta.ResolvedErrors);
            Assert.NotEmpty(delta.ResolvedIssueKeys);
        }

        [Fact]
        public void ComputeDelta_NewWarning_CountsAsNewWarning()
        {
            var before = Checkpoint(("import_health", 0, 0));
            var after = Result(Issue("import_health", VerifySeverity.Warning,
                "res://icon.svg.import", "orphan_import"));

            var delta = VerifyGateAdapter.ComputeDelta(before, after);

            Assert.Equal(0, delta.NewErrors);
            Assert.Equal(1, delta.NewWarnings);
        }

        [Fact]
        public void ComputeDelta_MixedNewAndResolved_SeparatesCorrectly()
        {
            // Before: one broken ref error + one import warning.
            var errKey = IssueKey.Build(Issue("broken_references", VerifySeverity.Error,
                "res://A.tscn", "broken_scene_reference"));
            var warnKey = IssueKey.Build(Issue("import_health", VerifySeverity.Warning,
                "res://b.import", "orphan_import"));
            var before = Checkpoint(
                ("broken_references", 1, 0, errKey),
                ("import_health", 0, 1, warnKey));

            // After: the broken ref is resolved, but a new missing_script error and a new import warning appeared.
            var after = Result(
                Issue("missing_scripts", VerifySeverity.Error, "res://C.tscn", "missing_script"),
                Issue("import_health", VerifySeverity.Warning, "res://d.import", "orphan_import"),
                Issue("import_health", VerifySeverity.Warning, "res://e.import", "orphan_import"));

            var delta = VerifyGateAdapter.ComputeDelta(before, after);

            Assert.Equal(1, delta.NewErrors);     // missing_script
            Assert.Equal(2, delta.NewWarnings);   // two new orphan_import (d, e) — b's warning was pre-existing
            Assert.Equal(1, delta.ResolvedErrors); // broken_scene_reference on A.tscn resolved
        }

        // -------------------------------------------------------------------
        // helpers — build synthetic fingerprints / results
        // -------------------------------------------------------------------

        /// <summary>
        /// Build a <see cref="CheckpointFingerprint"/> from one or more (ruleId, errors, warnings[, key])
        /// tuples. Each tuple becomes one rule's fingerprint. Pass the canonical issue keys (from
        /// <see cref="IssueKey.Build"/>) for issues that should be present at checkpoint.
        /// </summary>
        static CheckpointFingerprint Checkpoint(params (string ruleId, int errors, int warnings, string? key)[] rules)
        {
            var fps = new Dictionary<string, RuleFingerprint>();
            foreach (var (ruleId, errors, warnings, key) in rules)
            {
                var keys = new HashSet<string>();
                if (key != null) keys.Add(key);
                fps[ruleId] = new RuleFingerprint(errors, warnings, keys);
            }
            return new CheckpointFingerprint("cp_test", fps);
        }

        static CheckpointFingerprint Checkpoint(params (string ruleId, int errors, int warnings)[] rules)
        {
            var fps = new Dictionary<string, RuleFingerprint>();
            foreach (var (ruleId, errors, warnings) in rules)
            {
                fps[ruleId] = new RuleFingerprint(errors, warnings, new HashSet<string>());
            }
            return new CheckpointFingerprint("cp_test", fps);
        }

        /// <summary>Build a <see cref="VerifyResult"/> from zero or more issues.</summary>
        static VerifyResult Result(params VerifyIssue[] issues)
        {
            var list = new List<VerifyIssue>(issues);
            return new VerifyResult(list, new string[0], 0);
        }

        static VerifyIssue Issue(string ruleId, VerifySeverity severity, string assetPath, string issueCode) =>
            new VerifyIssue(ruleId, severity, assetPath, issueCode, "test issue");
    }
}
