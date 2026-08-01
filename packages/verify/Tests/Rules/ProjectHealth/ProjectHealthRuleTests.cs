#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Editor;
using GodotOpenMcp.Verify.Rules.ProjectHealth;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Rules.ProjectHealth
{
    /// <summary>
    /// P14.1 tests for the project-health verify rule (empty/uid-only/deep/large folders, broken
    /// assets, empty scenes). Adapted fidelity: Unity's <c>ProjectHealth</c> rule is the structural
    /// reference (folder-walk heuristics, empty/deep/large thresholds, broken-asset detection). The
    /// test structure mirrors the sibling P3.2–P3.4 rule tests (valid fixture → no issues; broken
    /// fixture → expected issues; false-positive guards; run-mode split; robustness; scope filtering;
    /// gate-delta stability). The rule is exercised through its internal constructor with an
    /// <see cref="InMemoryResolver"/> + an in-memory file map, so no Godot API and no disk access —
    /// the binary-less xUnit host runs the full rule.
    ///
    /// <para>
    /// The <see cref="InMemoryResolver"/> models a directory tree as a map of <c>res://</c> directory
    /// path → immediate children (each a name + kind). The resolver's <see cref="IProjectHealthResolver.ListDirectory"/>
    /// returns the staged children for the requested directory (directories first, sorted by name, then
    /// files sorted by name — matching the production resolver's contract). <see cref="IProjectHealthResolver.FileExists"/>
    /// answers from the file-text map so a scope that names a <c>.tres</c>/<c>.tscn</c> directly parses.
    /// </para>
    /// </summary>
    // Shares the VerifyRunner static-registry collection with VerifyRunnerTests so the registration
    // tests below do not race with the Core runner tests under xUnit's default parallel execution.
    [Collection("VerifyRunnerCollection")]
    public class ProjectHealthRuleTests
    {
        // ---- Asset fixtures ----------------------------------------------------
        //
        // Minimal Godot .tscn/.tres text. The parser only checks the header line and (for scenes) the
        // [node] count, so these are the smallest valid/broken examples.

        private const string HealthyScene = @"[gd_scene load_steps=2 format=3]

[ext_resource type=""Script"" path=""res://Scripts/ValidFixture.gd"" id=""1_valid""]

[node name=""Root"" type=""Node""]

[node name=""Child"" type=""Node"" parent="".""]
";

        private const string HealthyResource = @"[gd_resource type=""Resource"" format=3]

[ext_resource type=""Script"" path=""res://Scripts/DemoData.gd"" id=""1_data""]

[resource]
title = ""Demo""
";

        // A scene with only a root node — the empty-scene signal.
        private const string RootOnlyScene = @"[gd_scene load_steps=1 format=3]

[node name=""Root"" type=""Node""]
";

        // A .tscn with no [gd_scene] header — structurally broken.
        private const string BrokenSceneNoHeader = @"[ext_resource type=""Script"" path=""res://Scripts/X.gd"" id=""1_x""]

[node name=""Root"" type=""Node""]
";

        // A .tscn with a header but zero [node] declarations — structurally broken (a scene needs a root).
        private const string BrokenSceneNoNodes = @"[gd_scene load_steps=1 format=3]

[ext_resource type=""Script"" path=""res://Scripts/X.gd"" id=""1_x""]
";

        // A completely empty file.
        private const string EmptyFile = "";

        // ---- In-memory resolver ------------------------------------------------

        /// <summary>
        /// In-memory resolver modeling a directory tree. <see cref="Directories"/> maps a canonical
        /// <c>res://</c> directory path (with trailing <c>/</c>) to its immediate children; each child
        /// is a name, and the resolver classifies it as a directory when the name is a key in
        /// <see cref="Directories"/> (or ends with <c>/</c>), otherwise a file. <see cref="Files"/> is
        /// the file-text map keyed by canonical <c>res://</c> path (used by both the rule's reader seam
        /// and <see cref="IProjectHealthResolver.FileExists"/>).
        /// </summary>
        private sealed class InMemoryResolver : IProjectHealthResolver
        {
            // directory res path (with trailing /) → list of child names (no kind; kind derived below).
            public Dictionary<string, List<string>> Directories { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

            public IReadOnlyList<ProjectFolderEntry> ListDirectory(string? resDir)
            {
                var key = NormalizeDir(resDir);
                if (key == null || !Directories.TryGetValue(key, out var children))
                    return Array.Empty<ProjectFolderEntry>();

                var dirs = new List<ProjectFolderEntry>();
                var files = new List<ProjectFolderEntry>();
                foreach (var name in children)
                {
                    // A child is a directory when its full path is itself a known directory key. This
                    // models a pre-staged tree without requiring the caller to mark kind explicitly.
                    var childRes = key + name;
                    var childDirKey = childRes + "/";
                    if (Directories.ContainsKey(childDirKey))
                        dirs.Add(new ProjectFolderEntry(childDirKey, name, isDirectory: true));
                    else
                        files.Add(new ProjectFolderEntry(childRes, name, isDirectory: false));
                }
                dirs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                var combined = new List<ProjectFolderEntry>(dirs.Count + files.Count);
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

        private static (ProjectHealthRule rule, InMemoryResolver resolver) BuildRule(
            Dictionary<string, List<string>>? directories = null,
            Dictionary<string, string>? files = null)
        {
            var resolver = new InMemoryResolver();
            if (directories != null)
                foreach (var kv in directories) resolver.Directories[kv.Key] = new List<string>(kv.Value);
            if (files != null)
                foreach (var kv in files) resolver.Files[kv.Key] = kv.Value;
            string? Reader(string p) => resolver.Files.TryGetValue(p, out var t) ? t : null;
            return (new ProjectHealthRule(resolver, Reader), resolver);
        }

        private static List<VerifyIssue> RunScan(ProjectHealthRule rule, string resPath,
            VerifyRunMode mode = VerifyRunMode.Full)
        {
            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(new[] { resPath }), mode, sink);
            return sink;
        }

        private static List<VerifyIssue> RunScan(ProjectHealthRule rule, string[] resPaths,
            VerifyRunMode mode = VerifyRunMode.Full)
        {
            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(resPaths), mode, sink);
            return sink;
        }

        // ---- Baseline: healthy tree produces no issues ------------------------

        [Fact]
        public void Scan_HealthyTree_FullMode_EmitsNoIssues()
        {
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Scenes", "Main.tscn" },
                    ["res://Scenes/"] = new() { "Main.tscn" },
                },
                files: new()
                {
                    ["res://Main.tscn"] = HealthyScene,
                    ["res://Scenes/Main.tscn"] = HealthyScene,
                });

            var issues = RunScan(rule, "res://", VerifyRunMode.Full);

            Assert.Empty(issues);
        }

        // ---- empty_folder ------------------------------------------------------

        [Fact]
        public void Scan_EmptyLeafFolder_EmitsEmptyFolderWarning()
        {
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Empty" },
                    ["res://Empty/"] = new(), // no children at all
                });

            var issues = RunScan(rule, "res://", VerifyRunMode.Full);

            var issue = Assert.Single(issues);
            Assert.Equal("project_health", issue.RuleId);
            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("project_empty_folder", issue.IssueCode);
            Assert.Equal("res://Empty/", issue.AssetPath);
            Assert.Equal("empty_folder", issue.Evidence!["kind"]);
            Assert.Equal("res://Empty/", issue.Evidence!["folderPath"]);
        }

        [Fact]
        public void Scan_FolderWithOnlySubDirs_NotEmpty()
        {
            // A folder whose only children are sub-directories is structural, not empty — must NOT flag.
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Struct" },
                    ["res://Struct/"] = new() { "Child" },
                    ["res://Struct/Child/"] = new(),
                });

            var empties = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.IssueCode == "project_empty_folder").ToList();
            // res://Struct/ has a sub-dir → not empty. res://Struct/Child/ is empty → flagged.
            Assert.Single(empties);
            Assert.Equal("res://Struct/Child/", empties[0].AssetPath);
        }

        // ---- uid_only_folder ---------------------------------------------------

        [Fact]
        public void Scan_FolderWithOnlyUidSidecars_EmitsUidOnlyFolderWarning()
        {
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Stale" },
                    ["res://Stale/"] = new() { "Moved.gd.uid", "Other.gd.uid" },
                },
                files: new()
                {
                    ["res://Stale/Moved.gd.uid"] = "uid://moved0001",
                    ["res://Stale/Other.gd.uid"] = "uid://other0001",
                });

            var issues = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.IssueCode == "project_uid_only_folder").ToList();

            var issue = Assert.Single(issues);
            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("res://Stale/", issue.AssetPath);
            Assert.Equal("uid_only_folder", issue.Evidence!["kind"]);
            Assert.Equal("2", issue.Evidence!["sidecarCount"]);
        }

        [Fact]
        public void Scan_FolderWithUidSidecarAndRealFile_NotUidOnly()
        {
            // A folder with a uid sidecar AND a real asset is healthy — the real asset is the content.
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Mixed" },
                    ["res://Mixed/"] = new() { "Player.gd.uid", "Player.gd" },
                },
                files: new()
                {
                    ["res://Mixed/Player.gd.uid"] = "uid://player0001",
                    ["res://Mixed/Player.gd"] = "extends Node",
                });

            var uidOnly = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.IssueCode == "project_uid_only_folder");

            Assert.Empty(uidOnly);
        }

        // ---- deep_nesting ------------------------------------------------------

        [Fact]
        public void Scan_DeepFolder_EmitsDeepNestingWarning()
        {
            // Build a chain 10 deep under res:// (depth 10 > threshold 8).
            var dirs = new Dictionary<string, List<string>> { ["res://"] = new() { "a" } };
            var chain = "res://";
            for (var i = 1; i <= 10; i++)
            {
                chain += "a/";
                dirs[chain] = i < 10 ? new List<string> { "a" } : new List<string>();
            }
            var (rule, _) = BuildRule(directories: dirs);

            var deep = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.IssueCode == "project_deep_nesting").OrderBy(i => i.AssetPath).ToList();

            // Every folder at depth > 8 flags: depths 9 and 10.
            Assert.Equal(2, deep.Count);
            Assert.All(deep, i => Assert.Equal(VerifySeverity.Warning, i.Severity));
            Assert.Equal("res://a/a/a/a/a/a/a/a/a/", deep[0].AssetPath); // depth 9
            Assert.Equal("res://a/a/a/a/a/a/a/a/a/a/", deep[1].AssetPath); // depth 10
            Assert.Equal("deep_nesting", deep[0].Evidence!["kind"]);
            Assert.Equal("9", deep[0].Evidence!["depth"]);
            Assert.Equal("8", deep[0].Evidence!["threshold"]);
        }

        [Fact]
        public void Scan_ShallowFolder_NotDeepNesting()
        {
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Sprites" },
                    ["res://Sprites/"] = new() { "Player.png" },
                },
                files: new() { ["res://Sprites/Player.png"] = "png-bytes" });

            var deep = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.IssueCode == "project_deep_nesting");

            Assert.Empty(deep);
        }

        // ---- large_folder ------------------------------------------------------

        [Fact]
        public void Scan_LargeFolder_EmitsLargeFolderWarning()
        {
            // 201 direct children > threshold 200.
            var children = new List<string>();
            for (var i = 0; i < 201; i++) children.Add($"file{i:D3}.png");
            var files = new Dictionary<string, string>();
            foreach (var name in children) files["res://Big/" + name] = "x";
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Big" }, ["res://Big/"] = children },
                files: files);

            var large = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.IssueCode == "project_large_folder").ToList();

            var issue = Assert.Single(large);
            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("res://Big/", issue.AssetPath);
            Assert.Equal("large_folder", issue.Evidence!["kind"]);
            Assert.Equal("201", issue.Evidence!["childCount"]);
            Assert.Equal("200", issue.Evidence!["threshold"]);
        }

        [Fact]
        public void Scan_FolderAtThreshold_NotLarge()
        {
            // Exactly 200 children == threshold → NOT flagged (the check is strictly >).
            var children = new List<string>();
            for (var i = 0; i < 200; i++) children.Add($"f{i:D3}.png");
            var files = new Dictionary<string, string>();
            foreach (var name in children) files["res://Ok/" + name] = "x";
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Ok" }, ["res://Ok/"] = children },
                files: files);

            var large = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.IssueCode == "project_large_folder");

            Assert.Empty(large);
        }

        // ---- broken_asset ------------------------------------------------------

        [Fact]
        public void Scan_BrokenScene_NoHeader_EmitsBrokenAssetError()
        {
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Broken.tscn" },
                },
                files: new() { ["res://Broken.tscn"] = BrokenSceneNoHeader });

            var issues = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.IssueCode == "project_broken_asset").ToList();

            var issue = Assert.Single(issues);
            Assert.Equal(VerifySeverity.Error, issue.Severity); // integrity break, not cruft
            Assert.Equal("res://Broken.tscn", issue.AssetPath);
            Assert.Equal("broken_asset", issue.Evidence!["kind"]);
            Assert.Contains("gd_scene", issue.Evidence!["detail"]);
        }

        [Fact]
        public void Scan_BrokenScene_NoNodes_EmitsBrokenAssetError()
        {
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "NoNodes.tscn" } },
                files: new() { ["res://NoNodes.tscn"] = BrokenSceneNoNodes });

            var issues = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.IssueCode == "project_broken_asset").ToList();

            var issue = Assert.Single(issues);
            Assert.Equal(VerifySeverity.Error, issue.Severity);
            Assert.Contains("no [node]", issue.Evidence!["detail"]);
        }

        [Fact]
        public void Scan_EmptyFile_EmitsBrokenAssetError()
        {
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Empty.tscn" } },
                files: new() { ["res://Empty.tscn"] = EmptyFile });

            var issues = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.IssueCode == "project_broken_asset").ToList();

            var issue = Assert.Single(issues);
            Assert.Equal(VerifySeverity.Error, issue.Severity);
            Assert.Contains("empty", issue.Evidence!["detail"]);
        }

        [Fact]
        public void Scan_HealthyResource_NotBroken()
        {
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Data.tres" } },
                files: new() { ["res://Data.tres"] = HealthyResource });

            var broken = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.IssueCode == "project_broken_asset");

            Assert.Empty(broken);
        }

        // ---- empty_scene -------------------------------------------------------

        [Fact]
        public void Scan_RootOnlyScene_EmitsEmptySceneWarning()
        {
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Empty.tscn" } },
                files: new() { ["res://Empty.tscn"] = RootOnlyScene });

            var issues = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.IssueCode == "project_empty_scene").ToList();

            var issue = Assert.Single(issues);
            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("res://Empty.tscn", issue.AssetPath);
            Assert.Equal("empty_scene", issue.Evidence!["kind"]);
            Assert.Equal("1", issue.Evidence!["nodeCount"]);
        }

        [Fact]
        public void Scan_SceneWithChildren_NotEmpty()
        {
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Main.tscn" } },
                files: new() { ["res://Main.tscn"] = HealthyScene });

            var empty = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.IssueCode == "project_empty_scene");

            Assert.Empty(empty);
        }

        [Fact]
        public void Scan_ResourceNeverEmptyScene()
        {
            // A .tres is never an empty-scene candidate (scenes only).
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Data.tres" } },
                files: new() { ["res://Data.tres"] = HealthyResource });

            var emptyScene = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.IssueCode == "project_empty_scene");

            Assert.Empty(emptyScene);
        }

        // ---- Run-mode behavior -------------------------------------------------

        [Fact]
        public void Scan_FolderWalk_CheckpointMode_SkipsFolderChecks()
        {
            // The folder walk is Validate/Full only. A checkpoint pass over a directory scope must not
            // pay for the subtree walk.
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Empty" },
                    ["res://Empty/"] = new(),
                });

            var issues = RunScan(rule, "res://", VerifyRunMode.Checkpoint);

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_DirectlyScopedScene_CheckpointStillRunsAssetChecks()
        {
            // A directly-scoped .tscn runs the asset checks even on checkpoint (cheap, no walk) — a gated
            // mutation on a single scene should still catch a hand-edit that truncated it.
            var (rule, _) = BuildRule(
                files: new() { ["res://Broken.tscn"] = BrokenSceneNoHeader });

            var issues = RunScan(rule, "res://Broken.tscn", VerifyRunMode.Checkpoint);

            var issue = Assert.Single(issues);
            Assert.Equal("project_broken_asset", issue.IssueCode);
        }

        // ---- Scope filtering ---------------------------------------------------

        [Fact]
        public void Scan_NonAssetFile_Skipped()
        {
            // A .gd/.png is not this rule's input — no walk, no asset check.
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Player.gd" } },
                files: new() { ["res://Player.gd"] = "extends Node" });

            var issues = RunScan(rule, "res://Player.gd");

            Assert.Empty(issues);
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
            // A scope that names a parent AND its child directory must not walk the shared subtree twice.
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Empty" },
                    ["res://Empty/"] = new(),
                });

            var issues = RunScan(rule, new[] { "res://", "res://Empty/" });

            var empties = issues.Where(i => i.IssueCode == "project_empty_folder").ToList();
            Assert.Single(empties); // res://Empty/ flagged once, not twice
        }

        // ---- Robustness: malformed input never throws -------------------------

        [Fact]
        public void Scan_ThrowingResolver_DoesNotThrow()
        {
            // A resolver that throws simulates a permissions failure mid-walk. The rule swallows it.
            var resolver = new ThrowingResolver();
            var rule = new ProjectHealthRule(resolver, _ => null);

            var ex = Record.Exception(() => RunScan(rule, "res://", VerifyRunMode.Full));

            Assert.Null(ex);
        }

        [Fact]
        public void Scan_ThrowingReader_DoesNotThrow()
        {
            // A reader that throws simulates a file that vanished between checkpoint and validate. The
            // rule swallows it and contributes no issues for that file.
            var resolver = new InMemoryResolver();
            resolver.Directories["res://"] = new() { "Broken.tscn" };
            string? Reader(string _) => throw new InvalidOperationException("disk gone");
            var rule = new ProjectHealthRule(resolver, Reader);

            var ex = Record.Exception(() => RunScan(rule, "res://", VerifyRunMode.Full));

            Assert.Null(ex);
        }

        [Fact]
        public void Scan_UnreadableScene_EmitsNoBrokenAsset()
        {
            // A reader returning null (file not in the map) contributes no issues — a vanished file is
            // not a broken asset.
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Gone.tscn" } });

            var issues = RunScan(rule, "res://", VerifyRunMode.Full)
                .Where(i => i.AssetPath == "res://Gone.tscn");

            Assert.Empty(issues);
        }

        private sealed class ThrowingResolver : IProjectHealthResolver
        {
            public IReadOnlyList<ProjectFolderEntry> ListDirectory(string? resDir)
                => throw new UnauthorizedAccessException("permissions");
            public bool FileExists(string? resPath) => false;
        }

        // ---- Stability for the gate delta --------------------------------------

        [Fact]
        public void Scan_IssuesProduceStableIssueKeys()
        {
            // Every emitted issue must round-trip through IssueKey.Build / TryParse — the "stable enough
            // for gate delta" acceptance criterion.
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Empty", "Broken.tscn" },
                    ["res://Empty/"] = new(),
                },
                files: new() { ["res://Broken.tscn"] = BrokenSceneNoHeader });

            var issues = RunScan(rule, "res://", VerifyRunMode.Full);

            foreach (var issue in issues)
            {
                var key = IssueKey.Build(issue);
                Assert.True(IssueKey.TryParse(key, out var ruleId, out var sev, out var path, out var code),
                    $"Issue key '{key}' should be parseable");
                Assert.Equal("project_health", ruleId);
                Assert.True(path == "res://Empty/" || path == "res://Broken.tscn");
                Assert.True(code == "project_empty_folder" || code == "project_broken_asset");
            }
        }

        // ---- Fix matching contract --------------------------------------------

        [Fact]
        public void Issue_FixMatchingContract_RoutesCorrectly()
        {
            // Acceptance criterion: "Rule integrates with fix matching contract (ruleId + issueCode)".
            // No fix providers in v1 — this test pins the stable ruleId|issueCode tuples a future fix
            // provider would key off.
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Empty", "Stale", "Broken.tscn", "EmptyScene.tscn" },
                    ["res://Empty/"] = new(),
                    ["res://Stale/"] = new() { "X.gd.uid" },
                },
                files: new()
                {
                    ["res://Stale/X.gd.uid"] = "uid://x",
                    ["res://Broken.tscn"] = BrokenSceneNoHeader,
                    ["res://EmptyScene.tscn"] = RootOnlyScene,
                });

            var issues = RunScan(rule, "res://", VerifyRunMode.Full);
            var tuples = issues.Select(i => $"{i.RuleId}|{i.IssueCode}").ToHashSet();

            Assert.All(tuples, t => Assert.StartsWith("project_health|", t));
            Assert.Contains("project_health|project_empty_folder", tuples);
            Assert.Contains("project_health|project_uid_only_folder", tuples);
            Assert.Contains("project_health|project_broken_asset", tuples);
            Assert.Contains("project_health|project_empty_scene", tuples);
        }

        // ---- Integration with VerifyRunner (rule is auto-registered) ----------

        [Fact]
        public void VerifyRunner_AutoRegistersProjectHealthRule()
        {
            // The rule must appear in the default registration so the gate runs it on every mutation
            // without explicit wiring (P14.1 acceptance: rule registered in RegisterDefaults).
            VerifyRunner.ClearRules();
            try
            {
                VerifyRunner.RegisterDefaults();
                Assert.Contains(VerifyRunner.Rules, r => r.Id == "project_health");
            }
            finally
            {
                VerifyRunner.ClearRules();
            }
        }

        // ---- ProjectAssetParser unit tests (the parser in isolation) ----------

        [Fact]
        public void Parser_HealthyScene_ValidWithNodeCount()
        {
            var result = ProjectAssetParser.Parse(HealthyScene);
            Assert.True(result.IsValid);
            Assert.True(result.IsScene);
            Assert.Equal(2, result.NodeCount);
            Assert.Null(result.FailureDetail);
        }

        [Fact]
        public void Parser_HealthyResource_ValidNotScene()
        {
            var result = ProjectAssetParser.Parse(HealthyResource);
            Assert.True(result.IsValid);
            Assert.False(result.IsScene);
            Assert.Equal(0, result.NodeCount);
        }

        [Fact]
        public void Parser_EmptyText_Fails()
        {
            var result = ProjectAssetParser.Parse("");
            Assert.False(result.IsValid);
            Assert.NotNull(result.FailureDetail);
        }

        [Fact]
        public void Parser_NoHeader_Fails()
        {
            var result = ProjectAssetParser.Parse("[node name=\"X\"]\n");
            Assert.False(result.IsValid);
            Assert.Contains("gd_scene", result.FailureDetail!);
        }

        [Fact]
        public void Parser_SceneWithZeroNodes_Fails()
        {
            var result = ProjectAssetParser.Parse("[gd_scene load_steps=1 format=3]\n");
            Assert.False(result.IsValid);
            Assert.Contains("no [node]", result.FailureDetail!);
        }

        [Fact]
        public void Parser_NeverThrowsOnGarbage()
        {
            var ex = Record.Exception(() => ProjectAssetParser.Parse("garbage\n[broken\n;;;\n"));
            Assert.Null(ex);
        }
    }
}
