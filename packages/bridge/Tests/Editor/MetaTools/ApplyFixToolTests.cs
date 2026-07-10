#nullable enable
using System.Text.Json;
using GodotOpenMcp.Bridge.Editor;
using GodotOpenMcp.Verify.Fixes;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P3.7 tests for <see cref="ApplyFixTool"/>'s structured-output paths. The tool routes through
    /// <see cref="FixProviderRegistry"/>; the binary-less host cannot run a real provider's <c>Apply</c>
    /// (the <c>remove_missing_script</c> provider reads/writes files and the gate re-validates via live
    /// Godot APIs), so what is unit-tested here is the JSON-builder + registry-interaction surface:
    /// <list type="bullet">
    ///   <item><c>missing_parameter</c> (empty issue_id).</item>
    ///   <item><c>invalid_issue_id</c> (malformed key).</item>
    ///   <item>list-fixes-when-no-fix_id (surfaces <c>availableFixIds</c>).</item>
    ///   <item><c>unknown_fix</c> (Ok-body with available + applicable ids).</item>
    ///   <item>dry-run preview (Describe → dryRun/fixId/safe).</item>
    /// </list>
    /// The healthy apply path (provider.Apply + gate + rollback) is covered by the headless-Godot smoke.
    /// Ported (copy for the structured-output test shape) from Unity Open MCP's apply-fix tests.
    /// </summary>
    public class ApplyFixToolTests
    {
        const string ValidIssue = "missing_scripts|ERROR|res://Main.tscn|missing_script";

        public ApplyFixToolTests()
        {
            // Start each test from a clean registry so a prior test's defaults/stubs cannot leak.
            FixProviderRegistry.Clear();
        }

        [Theory]
        [InlineData("{}")]                                          // absent
        [InlineData("{\"issue_id\":null}")]                         // literal null
        [InlineData("{\"fix_id\":\"remove_missing_script\"}")]      // fix_id but no issue_id
        public void Execute_AbsentOrEmptyIssueId_ReturnsMissingParameter(string body)
        {
            var result = ApplyFixTool.Execute(body);
            Assert.False(result.Success);
            Assert.Equal("missing_parameter", result.ErrorCode);
        }

        [Theory]
        [InlineData("{\"issue_id\":\"not-a-key\"}")]
        [InlineData("{\"issue_id\":\"missing_scripts|Main.tscn|missing_script\"}")]      // 3 parts, no severity
        [InlineData("{\"issue_id\":\"missing_scripts|ERROR|res://Main.tscn\"}")]         // 3 parts, no code
        public void Execute_MalformedIssueId_ReturnsInvalidIssueId(string body)
        {
            var result = ApplyFixTool.Execute(body);
            Assert.False(result.Success);
            Assert.Equal("invalid_issue_id", result.ErrorCode);
        }

        [Fact]
        public void Execute_NoFixId_ListsAvailableFixes()
        {
            // Register the real default so the list reflects the production surface.
            FixProviderRegistry.RegisterDefaults();

            var result = ApplyFixTool.Execute("{\"issue_id\":\"" + ValidIssue + "\"}");
            Assert.True(result.Success);
            using var doc = JsonDocument.Parse(result.Output!);
            Assert.True(doc.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.Equal(ValidIssue, doc.RootElement.GetProperty("issueId").GetString());
            // remove_missing_script can fix this issue.
            var ids = doc.RootElement.GetProperty("availableFixIds").EnumerateArray();
            Assert.Contains(ids, e => e.GetString() == "remove_missing_script");
        }

        [Fact]
        public void Execute_UnknownFixId_ReturnsOkBodyWithAvailableIds()
        {
            FixProviderRegistry.RegisterDefaults();

            var result = ApplyFixTool.Execute(
                "{\"issue_id\":\"" + ValidIssue + "\",\"fix_id\":\"does_not_exist\"}");
            // unknown_fix is an Ok-body (the tool ran; the agent asked for a non-existent fix).
            Assert.True(result.Success);
            using var doc = JsonDocument.Parse(result.Output!);
            Assert.Equal("unknown_fix", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            // availableFixIds lists the real provider; applicableFixIdsForIssue narrows to the issue.
            var available = doc.RootElement.GetProperty("error").GetProperty("availableFixIds").EnumerateArray();
            Assert.Contains(available, e => e.GetString() == "remove_missing_script");
        }

        [Fact]
        public void Execute_DryRun_ReturnsDescribeSafeFlag()
        {
            FixProviderRegistry.RegisterDefaults();

            var result = ApplyFixTool.Execute(
                "{\"issue_id\":\"" + ValidIssue + "\",\"fix_id\":\"remove_missing_script\",\"dry_run\":true}");
            Assert.True(result.Success);
            using var doc = JsonDocument.Parse(result.Output!);
            Assert.True(doc.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.Equal("remove_missing_script", doc.RootElement.GetProperty("fixId").GetString());
            // remove_missing_script on a .tscn is Safe: true.
            Assert.True(doc.RootElement.GetProperty("safe").GetBoolean());
        }

        [Fact]
        public void Execute_FixNotApplicable_ReturnsFixNotApplicable()
        {
            FixProviderRegistry.RegisterDefaults();
            // remove_missing_script cannot fix a broken_references issue.
            var issue = "broken_references|ERROR|res://Main.tscn|broken_scene_reference";

            var result = ApplyFixTool.Execute(
                "{\"issue_id\":\"" + issue + "\",\"fix_id\":\"remove_missing_script\",\"dry_run\":false}");

            Assert.False(result.Success);
            Assert.Equal("fix_not_applicable", result.ErrorCode);
        }
    }
}
