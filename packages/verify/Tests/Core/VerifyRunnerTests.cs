#nullable enable
using System;
using System.Collections.Generic;
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
    /// </summary>
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
            Assert.Equal(2, result.CategoriesRun.Length);
        }

        [Fact]
        public void RunScoped_EmptyRuleIds_RunsAllRegistered()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));

            var scope = new VerifyScope(new[] { "res://dummy.tscn" });
            var result = VerifyRunner.RunScoped(scope, new string[0], VerifyRunMode.Checkpoint);

            Assert.False(result.HasUnknownRules);
            Assert.Equal(new[] { "rule_a" }, result.CategoriesRun);
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

            Assert.Equal(2, result.CategoriesRun.Length);
            Assert.Single(result.Issues);
            Assert.Equal("stable", result.Issues[0].RuleId);
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
            Assert.Equal(2, cp.Fingerprints.Count);
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
