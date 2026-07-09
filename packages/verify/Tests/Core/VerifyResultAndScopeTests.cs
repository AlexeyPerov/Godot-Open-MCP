#nullable enable
using System.Collections.Generic;
using GodotOpenMcp.Verify.Core;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Core
{
    /// <summary>
    /// P3.1 contract tests for the verify result/scope/fingerprint models. Ported (adapted) from
    /// Unity Open MCP's <c>VerifyResultTests</c> + <c>CheckpointFingerprintTests</c> — the model
    /// shapes are copy-fidelity ports; only the asset paths and the test framework changed. These pin
    /// the constructor + default behavior of the result/scope/fingerprint types before the gate tools
    /// (P3.6) serialize them.
    /// </summary>
    public class VerifyResultTests
    {
        [Fact]
        public void Constructor_SetsProperties()
        {
            var issues = new List<VerifyIssue>();
            var result = new VerifyResult(issues, new[] { "rule_a" }, 42L);

            Assert.Same(issues, result.Issues);
            Assert.Equal(new[] { "rule_a" }, result.CategoriesRun);
            Assert.Equal(42L, result.DurationMs);
        }

        [Fact]
        public void HasUnknownRules_DefaultFalse()
        {
            var result = new VerifyResult(new List<VerifyIssue>(), new string[0], 0);
            Assert.False(result.HasUnknownRules);
            Assert.Empty(result.UnknownRuleIds);
            Assert.Empty(result.AvailableRuleIds);
        }

        [Fact]
        public void HasUnknownRules_TrueWhenProvided()
        {
            var result = new VerifyResult(
                new List<VerifyIssue>(), new string[0], 0,
                unknownRuleIds: new[] { "ghost" },
                availableRuleIds: new[] { "real" });

            Assert.True(result.HasUnknownRules);
            Assert.Equal(new[] { "ghost" }, result.UnknownRuleIds);
            Assert.Equal(new[] { "real" }, result.AvailableRuleIds);
        }
    }

    public class VerifyScopeTests
    {
        [Fact]
        public void Constructor_SetsPaths()
        {
            var scope = new VerifyScope(new[] { "res://A.tscn" });
            Assert.Equal(new[] { "res://A.tscn" }, scope.Paths);
            Assert.False(scope.IncludeDependents);
        }

        [Fact]
        public void Constructor_IncludeDependents()
        {
            var scope = new VerifyScope(new[] { "res://A.tscn" }, true);
            Assert.True(scope.IncludeDependents);
        }
    }

    public class CheckpointFingerprintTests
    {
        [Fact]
        public void Constructor_SetsProperties()
        {
            var fps = new Dictionary<string, RuleFingerprint>
            {
                ["rule_a"] = new RuleFingerprint(1, 2, new HashSet<string> { "key1" })
            };
            var cp = new CheckpointFingerprint("cp_test", fps);

            Assert.Equal("cp_test", cp.CheckpointId);
            Assert.Single(cp.Fingerprints);
            Assert.Equal(1, cp.Fingerprints["rule_a"].Errors);
            Assert.Equal(2, cp.Fingerprints["rule_a"].Warnings);
            Assert.Contains("key1", cp.Fingerprints["rule_a"].IssueKeys);
        }
    }
}
