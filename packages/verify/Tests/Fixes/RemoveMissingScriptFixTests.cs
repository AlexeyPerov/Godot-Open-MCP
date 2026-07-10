#nullable enable
using System.IO;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Fixes;
using GodotOpenMcp.Verify.Rules.MissingScripts;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Fixes
{
    /// <summary>
    /// P3.7 tests for <see cref="RemoveMissingScriptFix"/> — the first <c>Safe: true</c> provider. Pins the
    /// <c>CanFix</c> matching (ruleId + issueCode), the <c>Describe</c> Safe flag (true for .tscn/.tres, false
    /// otherwise), and the <c>Apply</c> text-edit: removing the broken <c>script = ExtResource("id")</c> line
    /// and the orphaned <c>[ext_resource]</c> declaration when no remaining usage references it.
    ///
    /// <para>
    /// The <c>Apply</c> tests write a synthetic <c>.tscn</c> to a temp file and assert the post-edit bytes
    /// directly (the fix is pure text — no live Godot load/save), mirroring how the gate re-validates the
    /// file on disk.
    /// </para>
    /// </summary>
    public class RemoveMissingScriptFixTests
    {
        const string ScriptIssue = "missing_scripts|ERROR|{0}|missing_script";

        static string IssueFor(string path) => string.Format(ScriptIssue, path);

        [Fact]
        public void FixId_IsStable()
        {
            var fix = new RemoveMissingScriptFix();
            Assert.Equal("remove_missing_script", fix.FixId);
        }

        [Theory]
        [InlineData("missing_scripts|ERROR|res://A.tscn|missing_script", true)]
        [InlineData("missing_scripts|WARN|res://A.tscn|missing_script", true)]
        [InlineData("missing_scripts|ERROR|res://A.tres|missing_script", true)]
        [InlineData("broken_references|ERROR|res://A.tscn|broken_scene_reference", false)]
        [InlineData("import_health|ERROR|res://A.png.import|orphan_import", false)]
        [InlineData("missing_scripts|ERROR|res://A.tscn|other_code", false)]
        [InlineData("not-a-key", false)]
        public void CanFix_MatchesRuleIdAndIssueCode(string issueId, bool expected)
        {
            var fix = new RemoveMissingScriptFix();
            Assert.Equal(expected, fix.CanFix(issueId));
        }

        [Theory]
        [InlineData(".tscn", true)]
        [InlineData(".tres", true)]
        [InlineData(".TSCN", true)] // case-insensitive extension
        [InlineData(".scn", false)] // binary scene — not supported by the text edit
        [InlineData(".res", false)] // binary resource
        [InlineData(".png", false)]
        [InlineData("", false)]
        public void Describe_SafeFlagReflectsSupportedExtension(string ext, bool expectedSafe)
        {
            var fix = new RemoveMissingScriptFix();
            var path = string.IsNullOrEmpty(ext) ? "res://A" : "res://A" + ext;
            var issueId = $"missing_scripts|ERROR|{path}|missing_script";

            var desc = fix.Describe(issueId);

            Assert.Equal("remove_missing_script", desc.FixId);
            Assert.Equal(expectedSafe, desc.Safe);
        }

        [Fact]
        public void Apply_RefusesUnsupportedExtension()
        {
            var fix = new RemoveMissingScriptFix();
            var issueId = "missing_scripts|ERROR|res://A.png|missing_script";

            var result = fix.Apply(issueId);

            Assert.False(result.Success);
            Assert.Contains(".tscn/.tres", result.Description);
        }

        [Fact]
        public void Apply_RemovesScriptLineAndOrphanedExtResource()
        {
            var path = Path.GetTempFileName() + ".tscn";
            File.WriteAllText(path,
                "[gd_scene load_steps=2 format=3]\n" +
                "[ext_resource type=\"Script\" path=\"res://missing.gd\" id=\"1_abc\"]\n" +
                "[node name=\"Player\"]\n" +
                "script = ExtResource(\"1_abc\")\n");
            try
            {
                var fix = new RemoveMissingScriptFix();

                var result = fix.Apply(IssueFor(path));

                Assert.True(result.Success, result.Description);
                Assert.NotNull(result.TouchedPaths);
                Assert.Equal(path, result.TouchedPaths![0]);

                var edited = File.ReadAllText(path);
                // Script attachment line removed.
                Assert.DoesNotContain("script = ExtResource(\"1_abc\")", edited);
                // Orphaned declaration removed (no remaining usage).
                Assert.DoesNotContain("[ext_resource", edited);
                // Node header preserved.
                Assert.Contains("[node name=\"Player\"]", edited);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Apply_KeepsExtResourceWhenStillUsed()
        {
            var path = Path.GetTempFileName() + ".tscn";
            File.WriteAllText(path,
                "[gd_scene load_steps=2 format=3]\n" +
                "[ext_resource type=\"Script\" path=\"res://keep.gd\" id=\"1_keep\"]\n" +
                "[node name=\"A\"]\n" +
                "script = ExtResource(\"1_keep\")\n" +
                "[node name=\"B\"]\n" +
                "script = ExtResource(\"1_keep\")\n");
            try
            {
                var fix = new RemoveMissingScriptFix();

                var result = fix.Apply(IssueFor(path));

                Assert.True(result.Success);
                var edited = File.ReadAllText(path);
                // The fix removes the FIRST script attachment; the second node still uses 1_keep, so the
                // ext_resource declaration must be retained.
                Assert.Contains("[ext_resource", edited);
                Assert.Contains("script = ExtResource(\"1_keep\")", edited);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Apply_NoScriptLine_IsNoOpSuccess()
        {
            var path = Path.GetTempFileName() + ".tscn";
            File.WriteAllText(path,
                "[gd_scene load_steps=1 format=3]\n" +
                "[node name=\"Player\"]\n");
            try
            {
                var fix = new RemoveMissingScriptFix();

                var result = fix.Apply(IssueFor(path));

                Assert.True(result.Success);
                Assert.Null(result.TouchedPaths);
                Assert.Contains("already been resolved", result.Description);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Apply_MalformedIssueId_Fails()
        {
            var fix = new RemoveMissingScriptFix();

            var result = fix.Apply("not-a-key");

            Assert.False(result.Success);
            Assert.Contains("Cannot parse", result.Description);
        }

        [Fact]
        public void Registry_RegisterDefaults_RegistersRemoveMissingScript()
        {
            FixProviderRegistry.Clear();
            try
            {
                FixProviderRegistry.RegisterDefaults();

                // The defaults registration surfaces the provider through every lookup the gate uses.
                Assert.True(FixProviderRegistry.TryGetFixInfo(
                    MissingScriptsRule.RuleId, IssueCodes.MissingScript, out var fixId, out var safe));
                Assert.Equal("remove_missing_script", fixId);
                Assert.True(safe);

                var forIssue = FixProviderRegistry.FixesForIssue(
                    $"missing_scripts|ERROR|res://A.tscn|{IssueCodes.MissingScript}");
                Assert.Contains("remove_missing_script", forIssue);

                Assert.Contains("remove_missing_script", FixProviderRegistry.AvailableFixIds());
            }
            finally
            {
                FixProviderRegistry.Clear();
            }
        }
    }
}
