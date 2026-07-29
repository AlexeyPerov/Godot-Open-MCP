#nullable enable
using System.Collections.Generic;
using System.IO;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Fixes;
using GodotOpenMcp.Verify.Rules.BrokenReferences;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Fixes
{
    public class RelinkBrokenReferenceFixTests
    {
        const string IssueTemplate = "broken_references|ERROR|{0}|broken_scene_reference";

        sealed class StubResolver : IResourceResolver
        {
            private readonly HashSet<string> _paths = new();
            private readonly HashSet<string> _uids = new();

            public StubResolver(IEnumerable<string> paths, IEnumerable<string> uids)
            {
                foreach (var p in paths) _paths.Add(p);
                foreach (var u in uids) _uids.Add(u);
            }

            public bool PathExists(string? resPath) =>
                !string.IsNullOrEmpty(resPath) && _paths.Contains(resPath);

            public bool UidExists(string? uid) =>
                !string.IsNullOrEmpty(uid) && _uids.Contains(uid);
        }

        [Fact]
        public void FixId_IsStable()
        {
            Assert.Equal("relink_broken_reference", new RelinkBrokenReferenceFix().FixId);
        }

        [Theory]
        [InlineData("broken_references|ERROR|res://A.tscn|broken_scene_reference", true)]
        [InlineData("missing_scripts|ERROR|res://A.tscn|missing_script", false)]
        public void CanFix_MatchesRuleIdAndIssueCode(string issueId, bool expected)
        {
            Assert.Equal(expected, new RelinkBrokenReferenceFix().CanFix(issueId));
        }

        [Fact]
        public void Describe_IsUnsafe()
        {
            var issue = string.Format(IssueTemplate, "res://A.tscn");
            var desc = new RelinkBrokenReferenceFix().Describe(issue);
            Assert.False(desc.Safe);
        }

        [Fact]
        public void Apply_RequiresTarget()
        {
            var path = Path.GetTempFileName() + ".tscn";
            File.WriteAllText(path,
                "[gd_scene load_steps=2 format=3]\n" +
                "[ext_resource type=\"Resource\" path=\"res://Missing.tres\" id=\"1_x\"]\n" +
                "[node name=\"Root\" type=\"Node\"]\n");
            try
            {
                var fix = new RelinkBrokenReferenceFix(
                    new StubResolver(new[] { "res://Target.tres" }, new[] { "uid://target000001" }),
                    File.ReadAllText,
                    File.WriteAllText,
                    p => p);

                var result = fix.Apply(string.Format(IssueTemplate, path));
                Assert.False(result.Success);
                Assert.Contains("target_uid", result.Description);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Apply_RelinksBrokenExtResource()
        {
            var path = Path.GetTempFileName() + ".tscn";
            File.WriteAllText(path,
                "[gd_scene load_steps=2 format=3]\n" +
                "[ext_resource type=\"Resource\" path=\"res://Missing.tres\" id=\"1_x\"]\n" +
                "[node name=\"Root\" type=\"Node\"]\n");
            try
            {
                var fix = new RelinkBrokenReferenceFix(
                    new StubResolver(new[] { "res://Target.tres" }, new[] { "uid://target000001" }),
                    File.ReadAllText,
                    File.WriteAllText,
                    p => p);

                var result = fix.Apply(
                    string.Format(IssueTemplate, path),
                    "uid://target000001",
                    "res://Target.tres");

                Assert.True(result.Success, result.Description);
                var edited = File.ReadAllText(path);
                Assert.Contains("uid://target000001", edited);
                Assert.Contains("res://Target.tres", edited);
                Assert.DoesNotContain("res://Missing.tres", edited);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
