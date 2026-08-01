#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Editor;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Core
{
    /// <summary>
    /// P3.1 contract tests for the verify runner: rule registration, scoped dispatch, unknown-rule
    /// reporting, defensive exception handling, duration recording, and checkpoint fingerprinting.
    /// Ported (adapted) from Unity Open MCP's <c>VerifyRunnerTests</c> — the assertions are identical
    /// (copy-fidelity port of the runner logic); only the asset paths and the test framework changed.
    ///
    /// <para>
    /// The fixture swaps <see cref="VerifyLog"/> sinks to no-ops so the thrown-rule warning path and
    /// the (never-exceeded) slow-checkpoint path never P/Invoke into native Godot in the binary-less
    /// host — same seam strategy the bridge tests use for <c>BridgeLog</c>. Stubs never touch the
    /// editor, so no live Godot node is constructed.
    /// </para>
    /// <para>
    /// <b>Test isolation.</b> This class and every rule-test class that touches
    /// <see cref="VerifyRunner"/>'s static registry (<c>RegisterDefaults</c>/<c>ClearRules</c>) carry the
    /// <see cref="VerifyRunnerCollection"/> attribute. xUnit otherwise runs classes in parallel, and the
    /// shared static <c>RegisteredRules</c> + <c>_defaultsRegistered</c> flag would race between a class
    /// tearing down (<c>ClearRules</c>) and one registering — surfacing as phantom checkpoint/fingerprint
    /// failures. The collection serializes them (mirrors the bridge's
    /// <c>MainThreadDispatcherTests</c> collection).
    /// </para>
    /// </summary>
    [CollectionDefinition(nameof(VerifyRunnerCollection), DisableParallelization = true)]
    public sealed class VerifyRunnerCollection { }

    [Collection(nameof(VerifyRunnerCollection))]
    public class VerifyRunnerTests : IDisposable
    {
        public VerifyRunnerTests()
        {
            // No-op sinks: the thrown-rule and slow-checkpoint warning paths route through VerifyLog.
            VerifyLog.SetLoggersForTests(_ => { }, _ => { }, _ => { });
        }

        // xUnit creates a new instance per [Fact], so constructor-based setup + IDisposable teardown
        // (the bridge suite's pattern) works here. ClearRules is idempotent and resets the
        // defaults-registered flag, so each test starts from a clean registry.
        public void Dispose() => VerifyRunner.ClearRules();

        [Fact]
        public void RunScoped_UnknownRuleIds_ReturnsAvailableList()
        {
            VerifyRunner.RegisterRule(new StubRule("known_rule"));

            var scope = new VerifyScope(new[] { "res://dummy.tscn" });
            var result = VerifyRunner.RunScoped(scope, new[] { "nonexistent_rule" }, VerifyRunMode.Checkpoint);

            Assert.True(result.HasUnknownRules);
            Assert.Single(result.UnknownRuleIds);
            Assert.Equal("nonexistent_rule", result.UnknownRuleIds[0]);
            Assert.Contains("known_rule", result.AvailableRuleIds);
        }

        [Fact]
        public void RunScoped_MixedKnownAndUnknown_SplitsCorrectly()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));
            VerifyRunner.RegisterRule(new StubRule("rule_b"));

            var scope = new VerifyScope(new[] { "res://dummy.tscn" });
            var result = VerifyRunner.RunScoped(scope,
                new[] { "rule_a", "ghost_rule" }, VerifyRunMode.Checkpoint);

            Assert.Equal(new[] { "ghost_rule" }, result.UnknownRuleIds);
            Assert.Equal(new[] { "rule_a" }, result.CategoriesRun);
        }

        [Fact]
        public void RunScoped_NoRuleIds_RunsAllRegistered()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));
            VerifyRunner.RegisterRule(new StubRule("rule_b"));

            var scope = new VerifyScope(new[] { "res://dummy.tscn" });
            var result = VerifyRunner.RunScoped(scope, null, VerifyRunMode.Checkpoint);

            Assert.False(result.HasUnknownRules);
            // Both stubs must have run. Assert.Contains (not exact count) so this runner-mechanics test
            // stays stable as RegisterDefaults() gains real rules (broken_references in P3.2, ...).
            Assert.Contains("rule_a", result.CategoriesRun);
            Assert.Contains("rule_b", result.CategoriesRun);
        }

        [Fact]
        public void RunScoped_EmptyRuleIds_RunsAllRegistered()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));

            var scope = new VerifyScope(new[] { "res://dummy.tscn" });
            var result = VerifyRunner.RunScoped(scope, new string[0], VerifyRunMode.Checkpoint);

            Assert.False(result.HasUnknownRules);
            // The stub must have run. Assert.Contains (not exact array) so this runner-mechanics test
            // stays stable as RegisterDefaults() gains real rules (broken_references in P3.2, ...).
            Assert.Contains("rule_a", result.CategoriesRun);
        }

        [Fact]
        public void RunScoped_DispatchesToCorrectRules()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));
            VerifyRunner.RegisterRule(new StubRule("rule_b"));

            var scope = new VerifyScope(new[] { "res://Test.tscn" });
            var result = VerifyRunner.RunScoped(scope, new[] { "rule_b" }, VerifyRunMode.Validate);

            Assert.Equal(new[] { "rule_b" }, result.CategoriesRun);
            Assert.Single(result.Issues);
            Assert.Equal("rule_b", result.Issues[0].RuleId);
        }

        [Fact]
        public void RunScoped_ExceptionInRule_DoesNotPropagate()
        {
            VerifyRunner.RegisterRule(new ThrowingRule("crashy"));

            var scope = new VerifyScope(new[] { "res://Test.tscn" });
            var ex = Record.Exception(() =>
                VerifyRunner.RunScoped(scope, new[] { "crashy" }, VerifyRunMode.Checkpoint));
            Assert.Null(ex);
        }

        [Fact]
        public void RunScoped_ExceptionInRule_StillRunsOtherRules()
        {
            VerifyRunner.RegisterRule(new ThrowingRule("crashy"));
            VerifyRunner.RegisterRule(new StubRule("stable"));

            var scope = new VerifyScope(new[] { "res://Test.tscn" });
            var result = VerifyRunner.RunScoped(scope, null, VerifyRunMode.Checkpoint);

            // Both stub rules must have been dispatched. Assert.Contains (not exact count) so this
            // runner-mechanics test stays stable as RegisterDefaults() gains real rules.
            Assert.Contains("crashy", result.CategoriesRun);
            Assert.Contains("stable", result.CategoriesRun);
            // The throwing rule produced no issues; the stable one produced exactly its stub issue.
            // (Default rules like broken_references may also contribute, so filter to the stable rule.)
            var stableIssues = result.Issues.Where(i => i.RuleId == "stable").ToList();
            Assert.Single(stableIssues);
        }

        [Fact]
        public void RunScoped_RecordsDuration()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));

            var scope = new VerifyScope(new[] { "res://dummy.tscn" });
            var result = VerifyRunner.RunScoped(scope, null, VerifyRunMode.Checkpoint);

            Assert.True(result.DurationMs >= 0);
        }

        [Fact]
        public void RunScoped_IssuesHaveValidKeys()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));

            var scope = new VerifyScope(new[] { "res://dummy.tscn" });
            var result = VerifyRunner.RunScoped(scope, null, VerifyRunMode.Validate);

            foreach (var issue in result.Issues)
            {
                var key = IssueKey.Build(issue);
                Assert.True(IssueKey.TryParse(key, out _, out _, out _, out _),
                    $"Issue key '{key}' should be parseable");
            }
        }

        [Fact]
        public void CreateCheckpoint_ProducesFingerprints()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));
            VerifyRunner.RegisterRule(new StubRule("rule_b"));

            var scope = new VerifyScope(new[] { "res://dummy.tscn" });
            var cp = VerifyRunner.CreateCheckpoint(scope, null);

            Assert.NotNull(cp.CheckpointId);
            Assert.StartsWith("cp_", cp.CheckpointId);
            // Both stub rules must have produced a fingerprint. Assert.ContainsKey (not exact count)
            // so this runner-mechanics test stays stable as RegisterDefaults() gains real rules.
            Assert.True(cp.Fingerprints.ContainsKey("rule_a"));
            Assert.True(cp.Fingerprints.ContainsKey("rule_b"));
        }

        [Fact]
        public void CreateCheckpoint_FingerprintCountsIssues()
        {
            VerifyRunner.RegisterRule(new MultiIssueRule("rule_a"));

            var scope = new VerifyScope(new[] { "res://dummy.tscn" });
            var cp = VerifyRunner.CreateCheckpoint(scope, null);

            var fp = cp.Fingerprints["rule_a"];
            Assert.Equal(1, fp.Errors);
            Assert.Equal(2, fp.Warnings);
            Assert.Equal(3, fp.IssueKeys.Count);
        }

        sealed class StubRule : IVerifyRule
        {
            public string Id { get; }
            public StubRule(string id) { Id = id; }

            public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
            {
                sink.Add(new VerifyIssue(Id, VerifySeverity.Warning, "res://Test.tscn", "stub_issue", "stub description"));
            }
        }

        sealed class ThrowingRule : IVerifyRule
        {
            public string Id { get; }
            public ThrowingRule(string id) { Id = id; }

            public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
            {
                throw new System.InvalidOperationException("test exception");
            }
        }

        sealed class MultiIssueRule : IVerifyRule
        {
            public string Id { get; }
            public MultiIssueRule(string id) { Id = id; }

            public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
            {
                sink.Add(new VerifyIssue(Id, VerifySeverity.Error, "res://A.tscn", "err_1", "error"));
                sink.Add(new VerifyIssue(Id, VerifySeverity.Warning, "res://A.tscn", "warn_1", "warning"));
                sink.Add(new VerifyIssue(Id, VerifySeverity.Warning, "res://B.tscn", "warn_2", "warning"));
            }
        }
    }
}
