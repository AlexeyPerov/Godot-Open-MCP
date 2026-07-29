#nullable enable
using System.IO;
using GodotOpenMcp.Verify.Fixes;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Fixes
{
    public class RemoveOrphanImportFixTests
    {
        const string IssueTemplate = "import_health|WARN|{0}|orphan_import";

        [Fact]
        public void FixId_IsStable()
        {
            Assert.Equal("remove_orphan_import", new RemoveOrphanImportFix().FixId);
        }

        [Theory]
        [InlineData("import_health|WARN|res://A.png.import|orphan_import", true)]
        [InlineData("broken_references|ERROR|res://A.tscn|broken_scene_reference", false)]
        public void CanFix_MatchesRuleIdAndIssueCode(string issueId, bool expected)
        {
            Assert.Equal(expected, new RemoveOrphanImportFix().CanFix(issueId));
        }

        [Fact]
        public void Describe_IsSafe()
        {
            var issue = string.Format(IssueTemplate, "res://A.png.import");
            Assert.True(new RemoveOrphanImportFix().Describe(issue).Safe);
        }

        [Fact]
        public void Apply_DeletesOrphanSidecar()
        {
            // Build a sidecar path whose companion source does NOT exist. Path.GetTempFileName()
            // creates an empty file, which would masquerade as the present companion source — so
            // derive the sidecar path from a unique name without pre-creating the companion.
            var unique = Path.GetFileNameWithoutExtension(Path.GetTempFileName());
            var sidecar = Path.Combine(Path.GetTempPath(), unique + ".png.import");
            File.WriteAllText(sidecar, "[remap]\nsource=\"res://gone.png\"\n");
            try
            {
                // pathExists = File.Exists: true for the sidecar (exists), false for the
                // missing companion source (orphaned). The delete stub actually removes it.
                var fix = new RemoveOrphanImportFix(
                    File.Exists,
                    p => { File.Delete(p); return true; },
                    p => p);

                var result = fix.Apply(string.Format(IssueTemplate, sidecar));
                Assert.True(result.Success, result.Description);
                Assert.False(File.Exists(sidecar));
            }
            finally
            {
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
        }

        [Fact]
        public void Apply_NoOpWhenCompanionExists()
        {
            var source = Path.GetTempFileName() + ".png";
            var sidecar = source + ".import";
            File.WriteAllText(source, "png");
            File.WriteAllText(sidecar, "[remap]\nsource=\"res://x.png\"\n");
            try
            {
                var fix = new RemoveOrphanImportFix(
                    File.Exists,
                    _ => true,
                    p => p);

                var result = fix.Apply(string.Format(IssueTemplate, sidecar));
                Assert.True(result.Success, result.Description);
                Assert.Null(result.TouchedPaths);
                Assert.True(File.Exists(sidecar));
            }
            finally
            {
                File.Delete(source);
                File.Delete(sidecar);
            }
        }
    }
}
