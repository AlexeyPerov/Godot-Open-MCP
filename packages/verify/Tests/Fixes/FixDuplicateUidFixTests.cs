#nullable enable
using System.IO;
using GodotOpenMcp.Verify.Fixes;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Fixes
{
    public class FixDuplicateUidFixTests
    {
        const string IssueTemplate = "import_health|ERROR|{0}|duplicate_uid";

        [Fact]
        public void FixId_IsStable()
        {
            Assert.Equal("fix_duplicate_uid", new FixDuplicateUidFix().FixId);
        }

        [Theory]
        [InlineData("import_health|ERROR|res://A.png.import|duplicate_uid", true)]
        [InlineData("import_health|WARN|res://A.png.import|orphan_import", false)]
        public void CanFix_MatchesRuleIdAndIssueCode(string issueId, bool expected)
        {
            Assert.Equal(expected, new FixDuplicateUidFix().CanFix(issueId));
        }

        [Fact]
        public void Describe_IsUnsafe()
        {
            var issue = string.Format(IssueTemplate, "res://A.png.import");
            Assert.False(new FixDuplicateUidFix().Describe(issue).Safe);
        }

        [Fact]
        public void Apply_RequiresKeepPath()
        {
            var sidecar = Path.GetTempFileName() + ".import";
            File.WriteAllText(sidecar, "[remap]\nuid=\"uid://collide00001\"\n");
            try
            {
                var fix = new FixDuplicateUidFix(
                    File.ReadAllText,
                    File.WriteAllText,
                    p => p,
                    () => "uid://fresh00000001");

                var result = fix.Apply(string.Format(IssueTemplate, sidecar));
                Assert.False(result.Success);
                Assert.Contains("keep_path", result.Description);
            }
            finally
            {
                File.Delete(sidecar);
            }
        }

        [Fact]
        public void Apply_RefusesWhenIssueIsKeepPath()
        {
            var sidecar = "res://Keep.png.import";
            var fix = new FixDuplicateUidFix(
                _ => "[remap]\nuid=\"uid://collide00001\"\n",
                (_, _) => { },
                p => p,
                () => "uid://fresh00000001");

            var result = fix.Apply(
                string.Format(IssueTemplate, sidecar),
                keepPath: sidecar);
            Assert.False(result.Success);
            Assert.Contains("DIFFERENT", result.Description);
        }

        [Fact]
        public void Apply_RewritesUidOnNonKeepSidecar()
        {
            var sidecar = Path.GetTempFileName() + ".import";
            File.WriteAllText(sidecar, "[remap]\nuid=\"uid://collide00001\"\n");
            try
            {
                var fix = new FixDuplicateUidFix(
                    File.ReadAllText,
                    File.WriteAllText,
                    p => p,
                    () => "uid://fresh00000001");

                var result = fix.Apply(
                    string.Format(IssueTemplate, sidecar),
                    keepPath: "res://Other.png.import");

                Assert.True(result.Success, result.Description);
                var edited = File.ReadAllText(sidecar);
                Assert.Contains("uid://fresh00000001", edited);
                Assert.DoesNotContain("uid://collide00001", edited);
            }
            finally
            {
                File.Delete(sidecar);
            }
        }

        [Fact]
        public void RewriteUidField_ReplacesMatchingUid()
        {
            var text = "[remap]\nuid=\"uid://old\"\n";
            var edited = FixDuplicateUidFix.RewriteUidField(text, "uid://old", "uid://new");
            Assert.Contains("uid://new", edited);
            Assert.DoesNotContain("uid://old", edited);
        }
    }
}
