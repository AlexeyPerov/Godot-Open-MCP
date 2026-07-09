#nullable enable
using System;
using GodotOpenMcp.Verify.Core;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Core
{
    /// <summary>
    /// P3.1 contract tests for the canonical issue identity string. Ported (adapted) from Unity Open
    /// MCP's <c>IssueKeyTests</c> — the assertions are identical because <see cref="IssueKey"/> is a
    /// copy-fidelity port; only the asset paths changed from <c>Assets/*.prefab</c> to
    /// <c>res://*.tscn</c> and the test framework from NUnit to xUnit.
    ///
    /// <para>
    /// The case-insensitive severity matcher (<see cref="TryParse"/>) is the load-bearing regression
    /// guard: scan_paths / validate_edit emit <c>"Error"</c>/<c>"Warning"</c> while <see cref="IssueKey.Build"/>
    /// emits <c>"ERROR"</c>/<c>"WARN"</c>, and an agent may transcribe either into <c>apply_fix</c>. If
    /// the parser only accepted the built form, the documented scan→fix loop would fail with
    /// <c>invalid_issue_id</c> across separate calls.
    /// </para>
    /// </summary>
    public class IssueKeyTests
    {
        [Fact]
        public void Build_Error_ProducesCorrectFormat()
        {
            var key = IssueKey.Build("broken_references", VerifySeverity.Error,
                "res://Scenes/Main.tscn", "broken_scene_reference");

            Assert.Equal("broken_references|ERROR|res://Scenes/Main.tscn|broken_scene_reference", key);
        }

        [Fact]
        public void Build_Warning_ProducesCorrectFormat()
        {
            var key = IssueKey.Build("import_health", VerifySeverity.Warning,
                "res://icon.svg", "orphan_import");

            Assert.Equal("import_health|WARN|res://icon.svg|orphan_import", key);
        }

        [Fact]
        public void Build_FromIssue_MatchesDirectBuild()
        {
            var issue = new VerifyIssue("broken_references", VerifySeverity.Error,
                "res://Scenes/Main.tscn", "broken_scene_reference", "desc");

            Assert.Equal(
                IssueKey.Build(issue),
                IssueKey.Build(issue.RuleId, issue.Severity, issue.AssetPath, issue.IssueCode));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("bad|rule")]
        public void Build_RejectsInvalidRuleId(string? ruleId)
        {
            Assert.Throws<ArgumentException>(() =>
                IssueKey.Build(ruleId!, VerifySeverity.Error, "res://A.tscn", "code"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("bad|path")]
        public void Build_RejectsInvalidAssetPath(string assetPath)
        {
            Assert.Throws<ArgumentException>(() =>
                IssueKey.Build("rule", VerifySeverity.Error, assetPath, "code"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("bad|code")]
        public void Build_RejectsInvalidIssueCode(string issueCode)
        {
            Assert.Throws<ArgumentException>(() =>
                IssueKey.Build("rule", VerifySeverity.Error, "res://A.tscn", issueCode));
        }

        [Fact]
        public void TryParse_ValidErrorKey_ReturnsTrue()
        {
            var ok = IssueKey.TryParse("broken_references|ERROR|res://A.tscn|broken_scene_reference",
                out var ruleId, out var sev, out var path, out var code);

            Assert.True(ok);
            Assert.Equal("broken_references", ruleId);
            Assert.Equal(VerifySeverity.Error, sev);
            Assert.Equal("res://A.tscn", path);
            Assert.Equal("broken_scene_reference", code);
        }

        [Fact]
        public void TryParse_ValidWarningKey_ReturnsTrue()
        {
            var ok = IssueKey.TryParse("import_health|WARN|res://B.tscn|orphan_import",
                out var ruleId, out var sev, out var path, out var code);

            Assert.True(ok);
            Assert.Equal("import_health", ruleId);
            Assert.Equal(VerifySeverity.Warning, sev);
            Assert.Equal("res://B.tscn", path);
            Assert.Equal("orphan_import", code);
        }

        [Fact]
        public void TryParse_NullKey_ReturnsFalse()
        {
            Assert.False(IssueKey.TryParse(null, out _, out _, out _, out _));
        }

        [Fact]
        public void TryParse_EmptyKey_ReturnsFalse()
        {
            Assert.False(IssueKey.TryParse("", out _, out _, out _, out _));
        }

        [Theory]
        [InlineData("a|b|c")]
        [InlineData("a|b|c|d|e")]
        public void TryParse_WrongPartCount_ReturnsFalse(string key)
        {
            Assert.False(IssueKey.TryParse(key, out _, out _, out _, out _));
        }

        [Fact]
        public void TryParse_InvalidSeverity_ReturnsFalse()
        {
            Assert.False(IssueKey.TryParse("rule|CRITICAL|res://A.tscn|code",
                out _, out _, out _, out _));
        }

        // Regression guard: scan_paths / validate_edit emit severity as "Error"/"Warning" (Title-case),
        // but the old Unity parser only accepted "ERROR"/"WARN". An agent that copied the issue_id from
        // a scan_paths response into apply_fix got invalid_issue_id. TryParse must accept any case of
        // ERROR / WARN / WARNING so the documented scan→fix loop works across separate calls.
        [Fact]
        public void TryParse_AcceptsScanPathsSeverityCasing()
        {
            Assert.True(IssueKey.TryParse("broken_references|Error|res://A.tscn|broken_scene_reference",
                out var ruleId, out var sev, out var path, out var code));
            Assert.Equal("broken_references", ruleId);
            Assert.Equal(VerifySeverity.Error, sev);
            Assert.Equal("res://A.tscn", path);
            Assert.Equal("broken_scene_reference", code);

            Assert.True(IssueKey.TryParse("import_health|Warning|res://B.tscn|orphan_import",
                out _, out var warnSev, out _, out _));
            Assert.Equal(VerifySeverity.Warning, warnSev);
        }

        [Fact]
        public void TryParse_AcceptsLongFormWarningSpelling()
        {
            Assert.True(IssueKey.TryParse("rule|WARNING|res://A.tscn|code",
                out _, out var sev, out _, out _));
            Assert.Equal(VerifySeverity.Warning, sev);
        }

        [Fact]
        public void TryParse_AcceptsLowercaseSeverity()
        {
            Assert.True(IssueKey.TryParse("rule|error|res://A.tscn|code",
                out _, out var sev, out _, out _));
            Assert.Equal(VerifySeverity.Error, sev);
            Assert.True(IssueKey.TryParse("rule|warn|res://A.tscn|code",
                out _, out var warnSev, out _, out _));
            Assert.Equal(VerifySeverity.Warning, warnSev);
        }

        [Fact]
        public void TryParse_EmptyRuleId_ReturnsFalse()
        {
            Assert.False(IssueKey.TryParse("|ERROR|res://A.tscn|code",
                out _, out _, out _, out _));
        }

        [Fact]
        public void TryParse_EmptyAssetPath_ReturnsFalse()
        {
            Assert.False(IssueKey.TryParse("rule|ERROR||code",
                out _, out _, out _, out _));
        }

        [Fact]
        public void TryParse_EmptyIssueCode_ReturnsFalse()
        {
            Assert.False(IssueKey.TryParse("rule|ERROR|res://A.tscn|",
                out _, out _, out _, out _));
        }

        [Fact]
        public void ValidateKey_Valid_DoesNotThrow()
        {
            var ex = Record.Exception(() =>
                IssueKey.ValidateKey("broken_references|ERROR|res://A.tscn|broken_scene_reference"));
            Assert.Null(ex);
        }

        [Fact]
        public void ValidateKey_Malformed_ThrowsFormatException()
        {
            Assert.Throws<FormatException>(() => IssueKey.ValidateKey("bad-key"));
        }

        [Fact]
        public void BuildRoundTrip_PreservesAllComponents()
        {
            var original = IssueKey.Build("import_health", VerifySeverity.Warning,
                "res://Scenes/Game.tscn", "orphan_import");

            Assert.True(IssueKey.TryParse(original, out var ruleId, out var sev, out var path, out var code));
            Assert.Equal("import_health", ruleId);
            Assert.Equal(VerifySeverity.Warning, sev);
            Assert.Equal("res://Scenes/Game.tscn", path);
            Assert.Equal("orphan_import", code);
        }
    }
}
