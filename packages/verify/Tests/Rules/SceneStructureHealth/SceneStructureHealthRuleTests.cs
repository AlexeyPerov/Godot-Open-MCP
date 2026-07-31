#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Editor;
using GodotOpenMcp.Verify.Rules.SceneStructureHealth;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Rules.SceneStructureHealth
{
    /// <summary>
    /// P14.2 tests for the scene-structure verify rule (deep nesting, high node count, wide sibling
    /// lists, duplicate sibling names, empty branches). Adapted fidelity: Unity's <c>ScenePrefabHealth</c>
    /// rule is the structural reference only for the depth/width/count threshold <i>philosophy</i> —
    /// Unity's <c>deep_nesting</c> measures prefab-variant nesting (no Godot twin), and Unity ships no
    /// width/duplicate-name/empty-branch signals, so those four are greenfield for Godot. The test
    /// structure mirrors the sibling P3.2–P3.4 + P14.1 rule tests (valid fixture → no issues; broken
    /// fixture → expected issues; false-positive guards; run-mode split; robustness; scope filtering;
    /// gate-delta stability). The rule is exercised through its internal constructor with an
    /// <see cref="InMemoryResolver"/> + an in-memory file map, so no Godot API and no disk access — the
    /// binary-less xUnit host runs the full rule.
    /// </summary>
    public class SceneStructureHealthRuleTests
    {
        // ---- Scene fixtures ----------------------------------------------------
        //
        // Minimal Godot .tscn text exercising each structural shape. The parser inspects the [gd_scene]
        // header and the [node name= parent= type=] declarations (plus a script = body line), so these are
        // the smallest valid examples for each case.

        private const string HealthyScene = @"[gd_scene load_steps=2 format=3]

[ext_resource type=""Script"" path=""res://Scripts/ValidFixture.gd"" id=""1_valid""]

[node name=""Root"" type=""Node""]

[node name=""Player"" type=""CharacterBody2D"" parent="".""]
script = ExtResource(""1_valid"")

[node name=""Sprite"" type=""Sprite2D"" parent=""Player""]
";

        // A deep bare-Node chain (root=depth0 ... depth N). Built programmatically below.
        // A wide sibling list (one parent with N children). Built programmatically below.
        // A high node count scene. Built programmatically below.

        private const string DuplicateNameScene = @"[gd_scene load_steps=1 format=3]

[node name=""Root"" type=""Node""]

[node name=""Dup"" type=""Node"" parent="".""]

[node name=""Dup"" type=""Node"" parent="".""]
";

        // A non-root branch of bare Nodes with no content leaf.
        private const string EmptyBranchScene = @"[gd_scene load_steps=1 format=3]

[node name=""Root"" type=""Node""]

[node name=""Container"" type=""Node"" parent="".""]

[node name=""Inner"" type=""Node"" parent=""Container""]
";

        // A scene that is healthy w.r.t. structure but would be flagged as broken by project_health
        // (no header) — this rule must SKIP it and emit nothing.
        private const string NoHeaderScene = @"[ext_resource type=""Script"" path=""res://x.gd"" id=""1""]

[node name=""Root"" type=""Node""]
";

        private const string EmptyFile = "";

        // ---- Programmatic scene builders --------------------------------------

        /// <summary>
        /// Build a scene with a chain of <c>depth+1</c> bare <c>Node</c>s (root "Root" at depth 0, leaf
        /// N{depth} at depth <c>depth</c>). Parent paths follow Godot convention: N1 is
        /// <c>parent="."</c> (child of root), N2 is <c>parent="N1"</c>, N3 is <c>parent="N1/N2"</c>, etc.
        /// </summary>
        private static string BuildDeepChainScene(int depth)
        {
            var sb = new StringBuilder("[gd_scene load_steps=1 format=3]\n\n");
            sb.Append("[node name=\"Root\" type=\"Node\"]\n");
            var parent = ".";
            for (var i = 1; i <= depth; i++)
            {
                sb.Append($"[node name=\"N{i}\" type=\"Node\" parent=\"{parent}\"]\n");
                // The next parent key is the just-added node's key: "N1", then "N1/N2", etc.
                parent = i == 1 ? "N1" : "N1/" + string.Join("/", Enumerable.Range(2, i - 1).Select(n => "N" + n));
            }
            return sb.ToString();
        }

        /// <summary>Build a scene whose root has <c>childCount</c> direct children (named C0..C{N-1}).</summary>
        private static string BuildWideSiblingScene(int childCount)
        {
            var sb = new StringBuilder("[gd_scene load_steps=1 format=3]\n\n");
            sb.Append("[node name=\"Root\" type=\"Node\"]\n");
            for (var i = 0; i < childCount; i++)
                sb.Append($"[node name=\"C{i}\" type=\"Node\" parent=\".\"]\n");
            return sb.ToString();
        }

        /// <summary>Build a scene with exactly <c>nodeCount</c> [node] declarations (a flat root + children).</summary>
        private static string BuildHighCountScene(int nodeCount)
        {
            // nodeCount includes the root.
            var sb = new StringBuilder("[gd_scene load_steps=1 format=3]\n\n");
            sb.Append("[node name=\"Root\" type=\"Node\"]\n");
            for (var i = 1; i < nodeCount; i++)
                sb.Append($"[node name=\"N{i}\" type=\"Node\" parent=\".\"]\n");
            return sb.ToString();
        }

        // ---- In-memory resolver (mirrors ProjectHealthRuleTests.InMemoryResolver) ----

        /// <summary>
        /// In-memory resolver modeling a directory tree. <see cref="Directories"/> maps a canonical
        /// <c>res://</c> directory path (with trailing <c>/</c>) to its immediate children; a child is a
        /// directory when its path is itself a key in <see cref="Directories"/>, otherwise a file.
        /// <see cref="Files"/> is the file-text map keyed by canonical <c>res://</c> path.
        /// </summary>
        private sealed class InMemoryResolver : ISceneStructureResolver
        {
            public Dictionary<string, List<string>> Directories { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

            public IReadOnlyList<SceneFolderEntry> ListDirectory(string? resDir)
            {
                var key = NormalizeDir(resDir);
                if (key == null || !Directories.TryGetValue(key, out var children))
                    return Array.Empty<SceneFolderEntry>();

                var dirs = new List<SceneFolderEntry>();
                var files = new List<SceneFolderEntry>();
                foreach (var name in children)
                {
                    var childRes = key + name;
                    var childDirKey = childRes + "/";
                    if (Directories.ContainsKey(childDirKey))
                        dirs.Add(new SceneFolderEntry(childDirKey, name, isDirectory: true));
                    else
                        files.Add(new SceneFolderEntry(childRes, name, isDirectory: false));
                }
                dirs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                var combined = new List<SceneFolderEntry>(dirs.Count + files.Count);
                combined.AddRange(dirs);
                combined.AddRange(files);
                return combined;
            }

            public bool FileExists(string? resPath)
                => resPath != null && Files.ContainsKey(resPath);
        }

        private static string? NormalizeDir(string? resDir)
        {
            if (string.IsNullOrEmpty(resDir)) return "res://";
            var d = resDir!;
            if (!d.StartsWith("res://", StringComparison.Ordinal)) return null;
            if (!d.EndsWith("/")) d += "/";
            return d;
        }

        private static (SceneStructureHealthRule rule, InMemoryResolver resolver) BuildRule(
            Dictionary<string, List<string>>? directories = null,
            Dictionary<string, string>? files = null)
        {
            var resolver = new InMemoryResolver();
            if (directories != null)
                foreach (var kv in directories) resolver.Directories[kv.Key] = new List<string>(kv.Value);
            if (files != null)
                foreach (var kv in files) resolver.Files[kv.Key] = kv.Value;
            string? Reader(string p) => resolver.Files.TryGetValue(p, out var t) ? t : null;
            return (new SceneStructureHealthRule(resolver, Reader), resolver);
        }

        private static List<VerifyIssue> RunScan(SceneStructureHealthRule rule, string resPath,
            VerifyRunMode mode = VerifyRunMode.Full)
        {
            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(new[] { resPath }), mode, sink);
            return sink;
        }

        private static List<VerifyIssue> RunScan(SceneStructureHealthRule rule, string[] resPaths,
            VerifyRunMode mode = VerifyRunMode.Full)
        {
            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(resPaths), mode, sink);
            return sink;
        }

        // ---- Baseline: healthy scene produces no issues ------------------------

        [Fact]
        public void Scan_HealthyScene_FullMode_EmitsNoIssues()
        {
            var (rule, _) = BuildRule(files: new() { ["res://Main.tscn"] = HealthyScene });

            var issues = RunScan(rule, "res://Main.tscn");

            Assert.Empty(issues);
        }

        // ---- deep_nesting ------------------------------------------------------

        [Fact]
        public void Scan_DeepChain_EmitsDeepNestingWarningPerDeepNode()
        {
            // depth 12 chain → root(0) ... N12(12). Nodes at depth 11 and 12 exceed threshold 10.
            var (rule, _) = BuildRule(files: new() { ["res://Deep.tscn"] = BuildDeepChainScene(12) });

            var deep = RunScan(rule, "res://Deep.tscn")
                .Where(i => i.IssueCode == "scene_deep_nesting").OrderBy(i => i.Evidence!["depth"]).ToList();

            Assert.Equal(2, deep.Count);
            Assert.All(deep, i =>
            {
                Assert.Equal("scene_structure_health", i.RuleId);
                Assert.Equal(VerifySeverity.Warning, i.Severity);
                Assert.Equal("res://Deep.tscn", i.AssetPath);
                Assert.Equal("deep_nesting", i.Evidence!["kind"]);
                Assert.Equal("10", i.Evidence!["threshold"]);
            });
            Assert.Equal("11", deep[0].Evidence!["depth"]);
            Assert.Equal("12", deep[1].Evidence!["depth"]);
            Assert.EndsWith("/N11", deep[0].Evidence!["nodePath"]);
            Assert.EndsWith("/N12", deep[1].Evidence!["nodePath"]);
        }

        [Fact]
        public void Scan_ChainAtThreshold_NotDeepNesting()
        {
            // depth 10 → deepest node is exactly at threshold 10. The check is strictly >, so no flag.
            var (rule, _) = BuildRule(files: new() { ["res://Ok.tscn"] = BuildDeepChainScene(10) });

            var deep = RunScan(rule, "res://Ok.tscn")
                .Where(i => i.IssueCode == "scene_deep_nesting");

            Assert.Empty(deep);
        }

        // ---- high_node_count ---------------------------------------------------

        [Fact]
        public void Scan_HighNodeCount_EmitsHighNodeCountWarning()
        {
            // 1001 nodes > threshold 1000.
            var (rule, _) = BuildRule(files: new() { ["res://Big.tscn"] = BuildHighCountScene(1001) });

            var issue = Assert.Single(RunScan(rule, "res://Big.tscn")
                .Where(i => i.IssueCode == "scene_high_node_count"));

            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("res://Big.tscn", issue.AssetPath);
            Assert.Equal("high_node_count", issue.Evidence!["kind"]);
            Assert.Equal("1001", issue.Evidence!["nodeCount"]);
            Assert.Equal("1000", issue.Evidence!["threshold"]);
        }

        [Fact]
        public void Scan_NodeCountAtThreshold_NotHigh()
        {
            // Exactly 1000 nodes == threshold → NOT flagged (strictly >).
            var (rule, _) = BuildRule(files: new() { ["res://Ok.tscn"] = BuildHighCountScene(1000) });

            var high = RunScan(rule, "res://Ok.tscn")
                .Where(i => i.IssueCode == "scene_high_node_count");

            Assert.Empty(high);
        }

        // ---- wide_sibling_list -------------------------------------------------

        [Fact]
        public void Scan_WideSiblingList_EmitsWideSiblingListWarning()
        {
            // 101 children under the root > threshold 100.
            var (rule, _) = BuildRule(files: new() { ["res://Wide.tscn"] = BuildWideSiblingScene(101) });

            var issue = Assert.Single(RunScan(rule, "res://Wide.tscn")
                .Where(i => i.IssueCode == "scene_wide_sibling_list"));

            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("res://Wide.tscn", issue.AssetPath);
            Assert.Equal("wide_sibling_list", issue.Evidence!["kind"]);
            Assert.Equal("Root", issue.Evidence!["nodePath"]); // the parent is the root
            Assert.Equal("101", issue.Evidence!["childCount"]);
            Assert.Equal("100", issue.Evidence!["threshold"]);
        }

        [Fact]
        public void Scan_SiblingsAtThreshold_NotWide()
        {
            // Exactly 100 children == threshold → NOT flagged (strictly >).
            var (rule, _) = BuildRule(files: new() { ["res://Ok.tscn"] = BuildWideSiblingScene(100) });

            var wide = RunScan(rule, "res://Ok.tscn")
                .Where(i => i.IssueCode == "scene_wide_sibling_list");

            Assert.Empty(wide);
        }

        // ---- duplicate_node_name -----------------------------------------------

        [Fact]
        public void Scan_DuplicateSiblingName_EmitsDuplicateNodeNameWarning()
        {
            var (rule, _) = BuildRule(files: new() { ["res://Dup.tscn"] = DuplicateNameScene });

            var issue = Assert.Single(RunScan(rule, "res://Dup.tscn")
                .Where(i => i.IssueCode == "scene_duplicate_node_name"));

            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("res://Dup.tscn", issue.AssetPath);
            Assert.Equal("duplicate_node_name", issue.Evidence!["kind"]);
            Assert.Equal("Root", issue.Evidence!["nodePath"]); // parent path (root)
            Assert.Equal("Dup", issue.Evidence!["nodeName"]);
            Assert.Equal("2", issue.Evidence!["count"]);
        }

        [Fact]
        public void Scan_UniqueSiblingNames_NoDuplicate()
        {
            var (rule, _) = BuildRule(files: new() { ["res://Ok.tscn"] = BuildWideSiblingScene(5) });

            var dups = RunScan(rule, "res://Ok.tscn")
                .Where(i => i.IssueCode == "scene_duplicate_node_name");

            Assert.Empty(dups);
        }

        // ---- empty_node_branch -------------------------------------------------

        [Fact]
        public void Scan_EmptyBranch_EmitsEmptyNodeBranchWarning()
        {
            var (rule, _) = BuildRule(files: new() { ["res://Empty.tscn"] = EmptyBranchScene });

            var issue = Assert.Single(RunScan(rule, "res://Empty.tscn")
                .Where(i => i.IssueCode == "scene_empty_node_branch"));

            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("res://Empty.tscn", issue.AssetPath);
            Assert.Equal("empty_node_branch", issue.Evidence!["kind"]);
            // The topmost reportable empty branch is "Container" (Root is the scene root, excluded).
            Assert.Equal("Root/Container", issue.Evidence!["nodePath"]);
        }

        [Fact]
        public void Scan_EmptyBranchDeDupedToTopmost()
        {
            // A chain Root → Container → Inner, all bare Nodes with no content: only the topmost
            // non-root empty branch (Container) is reported, not Inner.
            var scene = @"[gd_scene load_steps=1 format=3]

[node name=""Root"" type=""Node""]

[node name=""Container"" type=""Node"" parent="".""]

[node name=""Inner"" type=""Node"" parent=""Container""]
";
            var (rule, _) = BuildRule(files: new() { ["res://Chain.tscn"] = scene });

            var empty = RunScan(rule, "res://Chain.tscn")
                .Where(i => i.IssueCode == "scene_empty_node_branch").ToList();

            var issue = Assert.Single(empty);
            Assert.Equal("Root/Container", issue.Evidence!["nodePath"]);
        }

        [Fact]
        public void Scan_BranchWithScriptedLeaf_NotEmpty()
        {
            // A branch whose subtree contains a script-bearing node is NOT empty.
            var scene = @"[gd_scene load_steps=2 format=3]

[ext_resource type=""Script"" path=""res://x.gd"" id=""1_x""]

[node name=""Root"" type=""Node""]

[node name=""Container"" type=""Node"" parent="".""]

[node name=""Inner"" type=""Node"" parent=""Container""]
script = ExtResource(""1_x"")
";
            var (rule, _) = BuildRule(files: new() { ["res://Ok.tscn"] = scene });

            var empty = RunScan(rule, "res://Ok.tscn")
                .Where(i => i.IssueCode == "scene_empty_node_branch");

            Assert.Empty(empty);
        }

        [Fact]
        public void Scan_BranchWithConcreteLeaf_NotEmpty()
        {
            // A leaf with a concrete type (Sprite2D, not bare Node) is content — the branch is not empty.
            var scene = @"[gd_scene load_steps=1 format=3]

[node name=""Root"" type=""Node""]

[node name=""Container"" type=""Node"" parent="".""]

[node name=""Sprite"" type=""Sprite2D"" parent=""Container""]
";
            var (rule, _) = BuildRule(files: new() { ["res://Ok.tscn"] = scene });

            var empty = RunScan(rule, "res://Ok.tscn")
                .Where(i => i.IssueCode == "scene_empty_node_branch");

            Assert.Empty(empty);
        }

        [Fact]
        public void Scan_RootOnlyScene_NotEmptyNodeBranch()
        {
            // A root-only scene is project_empty_scene's domain — not an empty branch here.
            var scene = @"[gd_scene load_steps=1 format=3]

[node name=""Root"" type=""Node""]
";
            var (rule, _) = BuildRule(files: new() { ["res://Root.tscn"] = scene });

            var empty = RunScan(rule, "res://Root.tscn")
                .Where(i => i.IssueCode == "scene_empty_node_branch");

            Assert.Empty(empty);
        }

        // ---- Skipped scenes (project_health's domain, not this rule's) --------

        [Fact]
        public void Scan_BrokenScene_NoHeader_EmitsNothing()
        {
            // A header-less file is a broken asset (project_health). This rule skips it.
            var (rule, _) = BuildRule(files: new() { ["res://Broken.tscn"] = NoHeaderScene });

            var issues = RunScan(rule, "res://Broken.tscn");

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_EmptyFile_EmitsNothing()
        {
            var (rule, _) = BuildRule(files: new() { ["res://Empty.tscn"] = EmptyFile });

            Assert.Empty(RunScan(rule, "res://Empty.tscn"));
        }

        [Fact]
        public void Scan_ResourceFile_NotAnalyzed()
        {
            // A .tres has no node tree — skip.
            var resource = @"[gd_resource type=""Resource"" format=3]

[resource]
title = ""Demo""
";
            var (rule, _) = BuildRule(files: new() { ["res://Data.tres"] = resource });

            Assert.Empty(RunScan(rule, "res://Data.tres"));
        }

        // ---- Run-mode behavior -------------------------------------------------

        [Fact]
        public void Scan_DirectoryWalk_CheckpointMode_SkipsAnalysis()
        {
            // The directory walk is Validate/Full only. A checkpoint pass over a directory scope must not
            // enumerate scenes.
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Scenes" },
                    ["res://Scenes/"] = new() { "Deep.tscn" },
                },
                files: new() { ["res://Scenes/Deep.tscn"] = BuildDeepChainScene(12) });

            var issues = RunScan(rule, "res://", VerifyRunMode.Checkpoint);

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_DirectlyScopedScene_CheckpointStillRunsAnalysis()
        {
            // A directly-scoped .tscn is analyzed even on checkpoint (cheap, no walk) — a gated mutation
            // on a single scene should still catch a structural regression.
            var (rule, _) = BuildRule(files: new() { ["res://Deep.tscn"] = BuildDeepChainScene(12) });

            var issues = RunScan(rule, "res://Deep.tscn", VerifyRunMode.Checkpoint);

            Assert.Contains(issues, i => i.IssueCode == "scene_deep_nesting");
        }

        // ---- Scope filtering ---------------------------------------------------

        [Fact]
        public void Scan_NonSceneFile_Skipped()
        {
            // A .gd is not this rule's input.
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Player.gd" } },
                files: new() { ["res://Player.gd"] = "extends Node" });

            Assert.Empty(RunScan(rule, "res://Player.gd"));
        }

        [Fact]
        public void Scan_EmptyScope_EmitsNothing()
        {
            var (rule, _) = BuildRule();

            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(Array.Empty<string>()), VerifyRunMode.Full, sink);

            Assert.Empty(sink);
        }

        [Fact]
        public void Scan_DirectoryRootDeDuped()
        {
            // A scope naming a parent AND its child directory must not walk the shared subtree twice.
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Scenes" },
                    ["res://Scenes/"] = new() { "Deep.tscn" },
                },
                files: new() { ["res://Scenes/Deep.tscn"] = BuildDeepChainScene(12) });

            var issues = RunScan(rule, new[] { "res://", "res://Scenes/" });

            var deep = issues.Where(i => i.IssueCode == "scene_deep_nesting").ToList();
            // The two deep nodes are flagged once each (not doubled by the overlapping scope).
            Assert.Equal(2, deep.Count);
        }

        // ---- Robustness: malformed input never throws -------------------------

        [Fact]
        public void Scan_ThrowingResolver_DoesNotThrow()
        {
            var resolver = new ThrowingResolver();
            var rule = new SceneStructureHealthRule(resolver, _ => null);

            var ex = Record.Exception(() => RunScan(rule, "res://", VerifyRunMode.Full));

            Assert.Null(ex);
        }

        [Fact]
        public void Scan_ThrowingReader_DoesNotThrow()
        {
            var resolver = new InMemoryResolver();
            resolver.Directories["res://"] = new() { "Deep.tscn" };
            string? Reader(string _) => throw new InvalidOperationException("disk gone");
            var rule = new SceneStructureHealthRule(resolver, Reader);

            var ex = Record.Exception(() => RunScan(rule, "res://", VerifyRunMode.Full));

            Assert.Null(ex);
        }

        [Fact]
        public void Scan_UnreadableScene_EmitsNothing()
        {
            // A reader returning null (file not in the map) contributes no issues.
            var (rule, _) = BuildRule(directories: new() { ["res://"] = new() { "Gone.tscn" } });

            var issues = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.AssetPath == "res://Gone.tscn");

            Assert.Empty(issues);
        }

        private sealed class ThrowingResolver : ISceneStructureResolver
        {
            public IReadOnlyList<SceneFolderEntry> ListDirectory(string? resDir)
                => throw new UnauthorizedAccessException("permissions");
            public bool FileExists(string? resPath) => false;
        }

        // ---- Stability for the gate delta --------------------------------------

        [Fact]
        public void Scan_IssuesProduceStableIssueKeys()
        {
            // Every emitted issue must round-trip through IssueKey.Build / TryParse.
            var (rule, _) = BuildRule(files: new()
            {
                ["res://Deep.tscn"] = BuildDeepChainScene(12),
                ["res://Dup.tscn"] = DuplicateNameScene,
                ["res://Empty.tscn"] = EmptyBranchScene,
            });

            var issues = RunScan(rule, new[] { "res://Deep.tscn", "res://Dup.tscn", "res://Empty.tscn" });

            Assert.NotEmpty(issues);
            foreach (var issue in issues)
            {
                var key = IssueKey.Build(issue);
                Assert.True(IssueKey.TryParse(key, out var ruleId, out var sev, out var path, out var code),
                    $"Issue key '{key}' should be parseable");
                Assert.Equal("scene_structure_health", ruleId);
                Assert.StartsWith("scene_", code, StringComparison.Ordinal);
            }
        }

        // ---- Fix matching contract --------------------------------------------

        [Fact]
        public void Issue_FixMatchingContract_RoutesCorrectly()
        {
            // No fix providers in v1 — this test pins the stable ruleId|issueCode tuples a future fix
            // provider would key off.
            var (rule, _) = BuildRule(files: new()
            {
                ["res://Scene.tscn"] = DuplicateNameScene,
            });

            var issues = RunScan(rule, "res://Scene.tscn");
            var tuples = issues.Select(i => $"{i.RuleId}|{i.IssueCode}").ToHashSet();

            Assert.All(tuples, t => Assert.StartsWith("scene_structure_health|", t));
            Assert.Contains("scene_structure_health|scene_duplicate_node_name", tuples);
        }

        [Fact]
        public void Issue_AllCodesArePinned()
        {
            // Every code in the canonical roster must be emittable and route under the rule id. Exercises
            // the full set across multiple fixtures.
            var (rule, _) = BuildRule(files: new()
            {
                ["res://Deep.tscn"] = BuildDeepChainScene(12),
                ["res://Big.tscn"] = BuildHighCountScene(1001),
                ["res://Wide.tscn"] = BuildWideSiblingScene(101),
                ["res://Dup.tscn"] = DuplicateNameScene,
                ["res://Empty.tscn"] = EmptyBranchScene,
            });

            var issues = RunScan(rule,
                new[] { "res://Deep.tscn", "res://Big.tscn", "res://Wide.tscn", "res://Dup.tscn", "res://Empty.tscn" });
            var codes = issues.Select(i => i.IssueCode).ToHashSet();

            Assert.Contains("scene_deep_nesting", codes);
            Assert.Contains("scene_high_node_count", codes);
            Assert.Contains("scene_wide_sibling_list", codes);
            Assert.Contains("scene_duplicate_node_name", codes);
            Assert.Contains("scene_empty_node_branch", codes);
        }

        // ---- Integration with VerifyRunner (rule is auto-registered) ----------

        [Fact]
        public void VerifyRunner_AutoRegistersSceneStructureHealthRule()
        {
            VerifyRunner.ClearRules();
            try
            {
                VerifyRunner.RegisterDefaults();
                Assert.Contains(VerifyRunner.Rules, r => r.Id == "scene_structure_health");
            }
            finally
            {
                VerifyRunner.ClearRules();
            }
        }

        // ---- SceneStructureParser unit tests (the parser in isolation) --------

        [Fact]
        public void Parser_HealthyScene_NotSkipped()
        {
            var result = SceneStructureParser.Parse(HealthyScene);
            Assert.False(result.Skipped);
            Assert.NotNull(result.Root);
            Assert.Equal("Root", result.Root!.Name);
            Assert.Equal(3, result.NodeCount); // Root, Player, Sprite
            Assert.Empty(result.DuplicateNames);
        }

        [Fact]
        public void Parser_DeepChain_AssignsDepth()
        {
            var result = SceneStructureParser.Parse(BuildDeepChainScene(3));
            Assert.False(result.Skipped);
            Assert.Equal(4, result.NodeCount); // Root(0), N1(1), N2(2), N3(3)
            // Find N3 and confirm depth 3.
            var n3 = result.Nodes.First(n => n.Name == "N3");
            Assert.Equal(3, n3.Depth);
        }

        [Fact]
        public void Parser_DuplicateSiblingNames_RecordedNotAborted()
        {
            // The TS buildHierarchy throws on this; the C# parser must record it and keep going.
            var result = SceneStructureParser.Parse(DuplicateNameScene);
            Assert.False(result.Skipped);
            Assert.Equal(3, result.NodeCount); // Root + two Dups, all counted
            var dup = Assert.Single(result.DuplicateNames);
            Assert.Equal("Dup", dup.Name);
            Assert.Equal(2, dup.Count);
        }

        [Fact]
        public void Parser_EmptyText_Skipped()
        {
            var result = SceneStructureParser.Parse("");
            Assert.True(result.Skipped);
        }

        [Fact]
        public void Parser_NoHeader_Skipped()
        {
            var result = SceneStructureParser.Parse("[node name=\"X\"]\n");
            Assert.True(result.Skipped);
            Assert.Contains("gd_scene", result.SkipReason!);
        }

        [Fact]
        public void Parser_ResourceNotScene_Skipped()
        {
            var result = SceneStructureParser.Parse("[gd_resource type=\"Resource\" format=3]\n\n[resource]\n");
            Assert.True(result.Skipped);
            Assert.Contains("not a scene", result.SkipReason!);
        }

        [Fact]
        public void Parser_NeverThrowsOnGarbage()
        {
            var ex = Record.Exception(() => SceneStructureParser.Parse("garbage\n[broken\n;;;\n"));
            Assert.Null(ex);
        }
    }
}
