#nullable enable
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Fixes;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Fixes
{
    /// <summary>
    /// P3.1 contract tests for the fix registry: provider registration (dedup by FixId), the
    /// rule→issue→fix linkage (<see cref="FixProviderRegistry.TryGetFixInfo"/> /
    /// <see cref="FixProviderRegistry.FixesForIssue"/> / <see cref="FixProviderRegistry.CandidatesForIssue"/>),
    /// the real <c>Safe</c> flag reporting, and the unsafe-default-on-throw guard. These are the
    /// Godot analogs of Unity Open MCP's fix-registry behavior, written greenfield against the
    /// copy-fidelity port of <c>FixProviderRegistry</c> (Unity's fix tests are per-provider fixture
    /// tests that need a live AssetDatabase; the registry contract itself is pure-managed and is what
    /// P3.1 pins here).
    ///
    /// <para>
    /// The <c>Safe</c> flag is the load-bearing assertion: <c>validate_edit</c> / <c>scan_paths</c>
    /// only auto-suggest <c>Safe: true</c> fixes, so the registry MUST surface the provider's real
    /// <see cref="IFixProvider.Describe"/> flag and MUST default to unsafe when Describe throws — a bug
    /// here would let the gate auto-apply a destructive fix.
    /// </para>
    /// </summary>
    public class FixProviderRegistryTests
    {
        public FixProviderRegistryTests()
        {
            FixProviderRegistry.Clear();
        }

        public void Dispose() => FixProviderRegistry.Clear();

        [Fact]
        public void Register_AddsProviderFindableByFixId()
        {
            FixProviderRegistry.Register(new StubProvider("fix_a", safe: true));

            var found = FixProviderRegistry.Find("fix_a");
            Assert.NotNull(found);
            Assert.Equal("fix_a", found!.FixId);
        }

        [Fact]
        public void Register_DedupesByFixId()
        {
            FixProviderRegistry.Register(new StubProvider("fix_a", safe: true));
            FixProviderRegistry.Register(new StubProvider("fix_a", safe: false));

            // Registering the same FixId twice keeps a single entry. The default provider
            // (remove_missing_script) registered by EnsureDefaultsRegistered may also be present — assert
            // the dedup invariant (fix_a appears exactly once) rather than the full registry contents.
            var ids = FixProviderRegistry.AvailableFixIds();
            Assert.Single(ids, id => id == "fix_a");
        }

        [Fact]
        public void Find_UnknownFixId_ReturnsNull()
        {
            Assert.Null(FixProviderRegistry.Find("nope"));
        }

        [Fact]
        public void TryGetFixInfo_ResolvedProviderReportsRealSafeFlag()
        {
            // Use a synthetic rule+code no default provider handles, so the stub is the first match.
            // (P13.3 registered real providers for broken_references/import_health, which would
            // shadow a stub registered on those codes.)
            FixProviderRegistry.Register(new StubProvider("safe_fix", safe: true, canFixRule: "synthetic_rule", canFixCode: "synthetic_code"));

            var ok = FixProviderRegistry.TryGetFixInfo("synthetic_rule", "synthetic_code", out var fixId, out var safe);

            Assert.True(ok);
            Assert.Equal("safe_fix", fixId);
            Assert.True(safe);
        }

        [Fact]
        public void TryGetFixInfo_UnsafeProviderReportsUnsafe()
        {
            FixProviderRegistry.Register(new StubProvider("risky_fix", safe: false, canFixRule: "synthetic_rule", canFixCode: "synthetic_code"));

            var ok = FixProviderRegistry.TryGetFixInfo("synthetic_rule", "synthetic_code", out var fixId, out var safe);

            Assert.True(ok);
            Assert.Equal("risky_fix", fixId);
            Assert.False(safe);
        }

        [Fact]
        public void TryGetFixInfo_NoProvider_ReturnsFalse()
        {
            var ok = FixProviderRegistry.TryGetFixInfo("rule_x", "code_y", out var fixId, out var safe);
            Assert.False(ok);
            Assert.Null(fixId);
            Assert.False(safe);
        }

        [Fact]
        public void TryGetFixInfo_UsesRuleAndIssueCodeOnlyAcrossSeverities()
        {
            // CanFix only inspects ruleId+issueCode, so the same provider resolves an issue regardless
            // of the synthetic severity in the test key. This is why the registry builds a synthetic
            // ERROR key internally — providers never see the real per-issue severity.
            FixProviderRegistry.Register(new StubProvider("fix_a", safe: true, canFixRule: "r", canFixCode: "c"));

            Assert.True(FixProviderRegistry.TryGetFixInfo("r", "c", out _, out _));
        }

        [Fact]
        public void TryGetFixInfo_DescribeThrows_DefaultsToUnsafe()
        {
            // If Describe throws for any reason, the registry must default to unsafe so the gate never
            // auto-applies something it cannot reason about. Use a synthetic rule+code no default
            // provider handles (P13.3 registered real providers on broken_references/import_health).
            FixProviderRegistry.Register(new ThrowingDescribeProvider("broken_fix", "synthetic_rule", "synthetic_code"));

            var ok = FixProviderRegistry.TryGetFixInfo("synthetic_rule", "synthetic_code", out var fixId, out var safe);

            Assert.True(ok);
            Assert.Equal("broken_fix", fixId);
            Assert.False(safe);
        }

        [Fact]
        public void FixesForIssue_ReturnsAllMatchingProviderIds()
        {
            FixProviderRegistry.Register(new StubProvider("fix_one", safe: true, canFixRule: "r", canFixCode: "c"));
            FixProviderRegistry.Register(new StubProvider("fix_two", safe: false, canFixRule: "r", canFixCode: "c"));
            FixProviderRegistry.Register(new StubProvider("fix_other", safe: true, canFixRule: "r", canFixCode: "other"));

            var fixes = FixProviderRegistry.FixesForIssue("r|ERROR|res://A.tscn|c");

            Assert.Equal(new[] { "fix_one", "fix_two" }, fixes);
        }

        [Fact]
        public void FixesForIssue_EmptyIssueId_ReturnsEmpty()
        {
            Assert.Empty(FixProviderRegistry.FixesForIssue(""));
        }

        [Fact]
        public void CandidatesForIssue_ReportsPerCandidateSafeFlag()
        {
            FixProviderRegistry.Register(new StubProvider("safe_one", safe: true, canFixRule: "r", canFixCode: "c"));
            FixProviderRegistry.Register(new StubProvider("risky_one", safe: false, canFixRule: "r", canFixCode: "c"));

            var candidates = FixProviderRegistry.CandidatesForIssue("r", "c");

            Assert.Equal(2, candidates.Length);
            Assert.Contains(candidates, x => x.FixId == "safe_one" && x.Safe);
            Assert.Contains(candidates, x => x.FixId == "risky_one" && !x.Safe);
        }

        [Fact]
        public void CandidatesForIssue_EmptyInputs_ReturnsEmpty()
        {
            Assert.Empty(FixProviderRegistry.CandidatesForIssue("", "c"));
            Assert.Empty(FixProviderRegistry.CandidatesForIssue("r", ""));
        }

        [Fact]
        public void AvailableFixIds_ListsRegisteredProviders()
        {
            FixProviderRegistry.Register(new StubProvider("fix_a", safe: true));
            FixProviderRegistry.Register(new StubProvider("fix_b", safe: false));

            // Both stubs are listed. EnsureDefaultsRegistered may also surface the default
            // remove_missing_script provider; assert the stubs are present rather than the full set.
            var ids = FixProviderRegistry.AvailableFixIds();
            Assert.Contains("fix_a", ids);
            Assert.Contains("fix_b", ids);
        }

        // --- Stubs ---------------------------------------------------------------------------------

        /// <summary>
        /// Minimal provider that matches a single ruleId+issueCode pair and reports a fixed Safe flag.
        /// Apply is not exercised by the registry contract (it is the bridge/apply_fix tool's job), so
        /// the stub returns a no-op success.
        /// </summary>
        sealed class StubProvider : IFixProvider
        {
            readonly bool _safe;
            readonly string _canFixRule;
            readonly string _canFixCode;

            public StubProvider(string fixId, bool safe, string canFixRule = "r", string canFixCode = "c")
            {
                FixId = fixId;
                _safe = safe;
                _canFixRule = canFixRule;
                _canFixCode = canFixCode;
            }

            public string FixId { get; }

            public bool CanFix(string issueId)
            {
                if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var code))
                    return false;
                return ruleId == _canFixRule && code == _canFixCode;
            }

            public FixDescription Describe(string issueId)
            {
                IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _);
                return new FixDescription
                {
                    FixId = FixId,
                    IssueId = issueId,
                    AssetPath = assetPath ?? "",
                    Description = "stub",
                    Safe = _safe
                };
            }

            public FixResult Apply(string issueId) =>
                new FixResult { Success = true, Description = "stub", TouchedPaths = new string[0] };
        }

        /// <summary>A provider whose Describe throws — guards the unsafe-default-on-throw path.</summary>
        sealed class ThrowingDescribeProvider : IFixProvider
        {
            readonly string _canFixRule;
            readonly string _canFixCode;

            public ThrowingDescribeProvider(string fixId, string canFixRule, string canFixCode)
            {
                FixId = fixId;
                _canFixRule = canFixRule;
                _canFixCode = canFixCode;
            }

            public string FixId { get; }

            public bool CanFix(string issueId)
            {
                if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var code))
                    return false;
                return ruleId == _canFixRule && code == _canFixCode;
            }

            public FixDescription Describe(string issueId) => throw new System.InvalidOperationException("boom");

            public FixResult Apply(string issueId) =>
                new FixResult { Success = false, Description = "boom", TouchedPaths = new string[0] };
        }
    }
}
