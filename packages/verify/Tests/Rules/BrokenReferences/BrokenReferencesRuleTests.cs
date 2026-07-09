#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Rules.BrokenReferences;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Rules.BrokenReferences
{
    /// <summary>
    /// P3.2 tests for the broken-references verify rule. Greenfield fidelity: the Godot scanner is
    /// new, but the test structure mirrors Unity's <c>MissingReferencesRuleTests</c> (valid fixture → no
    /// issues; broken fixture → expected issues; false-positive guards). The scanner is exercised through
    /// the rule's internal constructor with an <see cref="InMemoryResolver"/> + an in-memory file map, so
    /// no Godot API and no disk access — the binary-less xUnit host runs the full rule.
    ///
    /// <para>
    /// Fixture text is hand-written to mirror real Godot <c>.tscn</c> serialization (the
    /// <c>[gd_scene]</c>/<c>[ext_resource]</c>/<c>[node]</c> header shapes), so the parser is validated
    /// against the exact format the editor writes, not a synthetic subset.
    /// </para>
    /// </summary>
    public class BrokenReferencesRuleTests
    {
        // ---- Fixtures ----------------------------------------------------------

        // A complete, valid scene: two ext_resources (a script and a packed scene), both resolvable by
        // uid AND path; a sub_resource; and usages that all reference declared ids. Baseline for the
        // false-positive guard: this MUST produce zero issues in every mode.
        private const string ValidScene = @"[gd_scene load_steps=3 format=3 uid=""uid://validscene123""]

[ext_resource type=""Script"" uid=""uid://scriptok123"" path=""res://Player.gd"" id=""1_abc""]
[ext_resource type=""PackedScene"" uid=""uid://sceneok123"" path=""res://Weapons/Sword.tscn"" id=""2_def""]

[sub_resource type=""AtlasTexture"" id=""atlas_1""]

[node name=""Player"" type=""Node2D""]
script = ExtResource(""1_abc"")

[node name=""Sword"" parent=""."" instance=ExtResource(""2_def"")]
texture = SubResource(""atlas_1"")
";

        // Same as ValidScene but the script ext_resource points at a file that does not exist. Both uid
        // and path are missing → flagged as a broken ext_resource.
        private const string BrokenExtResourceBothMissing = @"[gd_scene load_steps=2 format=3]

[ext_resource type=""Script"" uid=""uid://gonemissing"" path=""res://Gone.gd"" id=""1_abc""]

[node name=""Player"" type=""Node2D""]
script = ExtResource(""1_abc"")
";

        // Stale path but live uid — the common relocation case. MUST NOT flag (Godot relocates by uid).
        private const string StalePathLiveUid = @"[gd_scene load_steps=1 format=3]

[ext_resource type=""Script"" uid=""uid://scriptok123"" path=""res://Moved/Renamed.gd"" id=""1_abc""]

[node name=""Player"" type=""Node2D""]
script = ExtResource(""1_abc"")
";

        // Live path but stale/missing uid — the post-reimport case. MUST NOT flag.
        private const string LivePathStaleUid = @"[gd_scene load_steps=1 format=3]

[ext_resource type=""Script"" uid=""uid://ghostuid000"" path=""res://Player.gd"" id=""1_abc""]

[node name=""Player"" type=""Node2D""]
script = ExtResource(""1_abc"")
";

        // A usage of ExtResource/SubResource whose id was never declared. Dangling-usage detection.
        private const string DanglingUsage = @"[gd_scene load_steps=1 format=3]

[ext_resource type=""Script"" path=""res://Player.gd"" id=""1_abc""]

[node name=""Player"" type=""Node2D""]
script = ExtResource(""1_abc"")
other = ExtResource(""99_nope"")
inner = SubResource(""missing_sub"")
";

        // ---- Helpers ------------------------------------------------------------

        /// <summary>
        /// In-memory resolver + file map. Constructor takes the set of paths and uids that exist; the
        /// rule's file reader pulls scene text from <see cref="Files"/>. Mirrors the seam Unity tests get
        /// for free via <c>AssetDatabase</c> mocking, but with no Godot dependency.
        /// </summary>
        private sealed class InMemoryResolver : IResourceResolver
        {
            private readonly HashSet<string> _paths;
            private readonly HashSet<string> _uids;

            public Dictionary<string, string> Files { get; } = new();

            public InMemoryResolver(string[]? existingPaths = null, string[]? existingUids = null)
            {
                _paths = new HashSet<string>(existingPaths ?? Array.Empty<string>(), StringComparer.Ordinal);
                _uids = new HashSet<string>(existingUids ?? Array.Empty<string>(), StringComparer.Ordinal);
            }

            public bool PathExists(string? resPath)
                => resPath != null && _paths.Contains(resPath);

            public bool UidExists(string? uid)
                => uid != null && _uids.Contains(uid);
        }

        private static (BrokenReferencesRule rule, InMemoryResolver resolver) BuildRule(
            string[]? existingPaths = null, string[]? existingUids = null, Dictionary<string, string>? files = null)
        {
            var resolver = new InMemoryResolver(existingPaths, existingUids);
            if (files != null)
                foreach (var kv in files) resolver.Files[kv.Key] = kv.Value;
            // File reader returns null for unknown paths — the rule treats that as "contributes no issues".
            string? Reader(string p) => resolver.Files.TryGetValue(p, out var t) ? t : null;
            return (new BrokenReferencesRule(resolver, Reader), resolver);
        }

        private static List<VerifyIssue> RunScan(BrokenReferencesRule rule, string resPath,
            VerifyRunMode mode = VerifyRunMode.Full)
        {
            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(new[] { resPath }), mode, sink);
            return sink;
        }

        // ---- Baseline: valid scene produces no issues --------------------------

        [Fact]
        public void Scan_ValidScene_FullMode_EmitsNoIssues()
        {
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd", "res://Weapons/Sword.tscn" },
                existingUids: new[] { "uid://scriptok123", "uid://sceneok123" },
                files: new() { ["res://Main.tscn"] = ValidScene });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_ValidScene_CheckpointMode_EmitsNoIssues()
        {
            // Checkpoint mode skips the dangling-usage walk but still resolves ext_resources. A valid
            // scene has nothing broken on either pass.
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd", "res://Weapons/Sword.tscn" },
                existingUids: new[] { "uid://scriptok123", "uid://sceneok123" },
                files: new() { ["res://Main.tscn"] = ValidScene });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Checkpoint);

            Assert.Empty(issues);
        }

        // ---- Broken ext_resource (the load-bearing acceptance criterion) -------

        [Fact]
        public void Scan_BrokenExtResource_BothUidAndPathMissing_EmitsError()
        {
            var (rule, _) = BuildRule(
                existingPaths: Array.Empty<string>(),
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = BrokenExtResourceBothMissing });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            var issue = Assert.Single(issues);
            Assert.Equal("broken_references", issue.RuleId);
            Assert.Equal(VerifySeverity.Error, issue.Severity);
            Assert.Equal("broken_scene_reference", issue.IssueCode);
            Assert.Equal("res://Main.tscn", issue.AssetPath);
            // With both present and broken, the uid failure mode is preferred (stronger deletion signal).
            Assert.Equal("ext_resource_uid_missing", issue.Evidence!["kind"]);
            Assert.Equal("1_abc", issue.Evidence!["extResourceId"]);
            Assert.Equal("uid://gonemissing", issue.Evidence!["target"]);
            Assert.Equal("3", issue.Evidence!["line"]); // 1-based: blank line 2, ext_resource header on line 3
        }

        [Fact]
        public void Scan_BrokenExtResource_CheckpointMode_StillEmitsError()
        {
            // The checkpoint path MUST catch broken ext_resources — that is the whole point of running
            // the rule on every mutation. Only the dangling-usage walk is checkpoint-skippable.
            var (rule, _) = BuildRule(
                existingPaths: Array.Empty<string>(),
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = BrokenExtResourceBothMissing });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Checkpoint);

            Assert.Single(issues);
            Assert.Equal("broken_scene_reference", issues[0].IssueCode);
        }

        // ---- False-positive guards --------------------------------------------

        [Fact]
        public void Scan_StalePathLiveUid_DoesNotFlag()
        {
            // Relocation case: Godot moved the file, the path is stale but the uid resolves. Flagging
            // here would drown the gate in false positives after every rename.
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd" }, // path in fixture is res://Moved/Renamed.gd
                existingUids: new[] { "uid://scriptok123" },
                files: new() { ["res://Main.tscn"] = StalePathLiveUid });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_LivePathStaleUid_DoesNotFlag()
        {
            // Post-reimport case: the uid was deregistered but the path still loads. Don't flag.
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd" },
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = LivePathStaleUid });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            Assert.Empty(issues);
        }

        // ---- Dangling usages (Validate/Full only) -----------------------------

        [Fact]
        public void Scan_DanglingUsages_FullMode_EmitsTwoErrors()
        {
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd" },
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = DanglingUsage });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            // 99_nope (dangling ExtResource) + missing_sub (dangling SubResource). The declared 1_abc is
            // fine and must not appear.
            Assert.Equal(2, issues.Count);
            Assert.All(issues, i => Assert.Equal("broken_scene_reference", i.IssueCode));

            var danglingExt = issues.Single(i => i.Evidence!["kind"] == "dangling_ext_resource");
            Assert.Equal("99_nope", danglingExt.Evidence!["refId"]);

            var danglingSub = issues.Single(i => i.Evidence!["kind"] == "dangling_sub_resource");
            Assert.Equal("missing_sub", danglingSub.Evidence!["refId"]);
        }

        [Fact]
        public void Scan_DanglingUsages_CheckpointMode_DoesNotEmit()
        {
            // Checkpoint skips the usage walk — it is the expensive half and a header-only mutation
            // should not pay for it. The dangling refs here are NOT broken ext_resources (1_abc is
            // declared and its path resolves), so checkpoint must return zero.
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd" },
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = DanglingUsage });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Checkpoint);

            Assert.Empty(issues);
        }

        // ---- Robustness: malformed input never throws -------------------------

        [Fact]
        public void Scan_MalformedHeader_DoesNotThrow()
        {
            // Truncated header, unbalanced quotes, no id — the parser must yield what it can and the
            // rule must not throw. This is the IVerifyRule "must not throw on ordinary malformed input"
            // contract.
            var malformed = "[ext_resource type=\"Script\" path=\"res://Player.gd\"\n[gd_scene]\n";
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd" },
                files: new() { ["res://Main.tscn"] = malformed });

            var ex = Record.Exception(() => RunScan(rule, "res://Main.tscn"));

            Assert.Null(ex);
        }

        [Fact]
        public void Scan_EmptyFile_EmitsNoIssues()
        {
            var (rule, _) = BuildRule(files: new() { ["res://Main.tscn"] = "" });

            var issues = RunScan(rule, "res://Main.tscn");

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_UnreadableFile_EmitsNoIssuesAndDoesNotThrow()
        {
            // A reader that throws simulates a file that vanished between checkpoint and validate, or a
            // permissions error. The rule swallows it and contributes no issues.
            var resolver = new InMemoryResolver();
            string? Reader(string _) => throw new InvalidOperationException("disk gone");

            var rule = new BrokenReferencesRule(resolver, Reader);

            var ex = Record.Exception(() => RunScan(rule, "res://Main.tscn"));

            Assert.Null(ex);
        }

        // ---- Scope filtering --------------------------------------------------

        [Fact]
        public void Scan_NonScenePath_IsSkipped()
        {
            // Only .tscn/.tres are in scope; a .gd file is silently skipped (not this rule's domain).
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd" },
                files: new() { ["res://Player.gd"] = "extends Node2D\n" });

            var issues = RunScan(rule, "res://Player.gd");

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_TresFile_IsScanned()
        {
            // .tres uses the same ext_resource model — the rule must cover it, not just .tscn.
            var tres = "[gd_resource type=\"Material\"]\n\n[ext_resource type=\"Texture2D\" path=\"res://Missing.png\" id=\"1\"]\n";
            var (rule, _) = BuildRule(
                existingPaths: Array.Empty<string>(),
                files: new() { ["res://Mat.tres"] = tres });

            var issues = RunScan(rule, "res://Mat.tres");

            Assert.Single(issues);
            Assert.Equal("broken_scene_reference", issues[0].IssueCode);
        }

        [Fact]
        public void Scan_EmptyScope_EmitsNothing()
        {
            var (rule, _) = BuildRule();

            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(Array.Empty<string>()), VerifyRunMode.Full, sink);

            Assert.Empty(sink);
        }

        // ---- Stability for the gate delta -------------------------------------

        [Fact]
        public void Scan_IssuesProduceStableIssueKeys()
        {
            // The gate delta and MCP capabilities consume IssueKey.Build(issue). Every emitted issue
            // must round-trip through IssueKey.TryParse — this is the "stable enough for gate delta"
            // acceptance criterion.
            var (rule, _) = BuildRule(
                existingPaths: Array.Empty<string>(),
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = BrokenExtResourceBothMissing });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            foreach (var issue in issues)
            {
                var key = IssueKey.Build(issue);
                Assert.True(IssueKey.TryParse(key, out var ruleId, out var sev, out var path, out var code),
                    $"Issue key '{key}' should be parseable");
                Assert.Equal("broken_references", ruleId);
                Assert.Equal(VerifySeverity.Error, sev);
                Assert.Equal("res://Main.tscn", path);
                Assert.Equal("broken_scene_reference", code);
            }
        }

        [Fact]
        public void Scan_DeterministicOrdering()
        {
            // The gate delta compares issue sets by key; ordering within a file should still be stable
            // (by line) so two identical scans produce identical issue sequences — important for diff
            // readability in MCP responses.
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd" },
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = DanglingUsage });

            var first = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);
            var second = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            Assert.Equal(first.Select(i => i.Evidence!["line"]), second.Select(i => i.Evidence!["line"]));
        }
    }
}
