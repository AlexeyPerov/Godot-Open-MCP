#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Editor;
using GodotOpenMcp.Verify.Rules.AnimationAnalysis;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Rules.AnimationAnalysis
{
    /// <summary>
    /// P14.5 tests for the animation-analysis verify rule (missing clip, empty clip, unreachable state,
    /// parameter mismatch, duplicate clip). Adapted fidelity: Unity's <c>AnimationAnalysis</c> rule is the
    /// structural reference for the clip/state signals (Unity resolves controllers/clips through
    /// AssetDatabase + the AnimatorController API; Godot's <c>.tres</c> text format for
    /// AnimationPlayer / AnimationLibrary / Animation / AnimationNodeStateMachine is parseable offline).
    /// The Godot <c>AnimationNodeStateMachine</c> reachability BFS + the <c>.tres</c> text parsing are
    /// greenfield. Unity's curve-density / curve-count / AnyState / complexity signals are skipped (see
    /// IssueCodes). The test structure mirrors the sibling P3.2–P3.4 + P14.1/P14.2/P14.3/P14.4 rule tests
    /// (valid fixture → no issues; broken fixture → expected issues; false-positive guards; run-mode split;
    /// robustness; scope filtering; gate-delta stability; rootCause backfill). The rule is exercised
    /// through its internal constructor with an <see cref="InMemoryResolver"/> + an in-memory file map, so
    /// no Godot API and no disk access — the binary-less xUnit host runs the full rule.
    /// </summary>
    // Shares the VerifyRunner static-registry collection with VerifyRunnerTests so the registration tests
    // below do not race with the Core runner tests under xUnit's default parallel execution.
    [Collection("VerifyRunnerCollection")]
    public class AnimationAnalysisRuleTests
    {
        // ---- .tres fixtures ---------------------------------------------------
        //
        // Minimal Godot text exercising each shape. The parser inspects [gd_resource type=] headers,
        // [ext_resource path= uid= id=] declarations, [sub_resource type="Animation" / "AnimationPlayer" /
        // "AnimationNodeStateMachine"] sections, and the states/N + transitions/N array entries a state
        // machine serializes — so these are the smallest valid examples for each case.

        // An AnimationPlayer whose library reference resolves (live uid) → healthy (no missing_clip).
        private const string PlayerWithLiveLibrary = @"[gd_resource type=""AnimationPlayer"" load_steps=2 format=3]

[ext_resource type=""AnimationLibrary"" path=""res://Anims/lib.tres"" uid=""uid://goodlib"" id=""1_lib""]

[resource]
libraries = {
"""": ExtResource(""1_lib"")
}
";

        // An AnimationPlayer whose library reference is broken (both path + uid missing) → missing_clip.
        private const string PlayerWithBrokenLibrary = @"[gd_resource type=""AnimationPlayer"" load_steps=2 format=3]

[ext_resource type=""AnimationLibrary"" path=""res://Anims/gone.tres"" uid=""uid://gonelib"" id=""1_lib""]

[resource]
libraries = {
"""": ExtResource(""1_lib"")
}
";

        // A .tres carrying an Animation clip WITH tracks → healthy (no empty_clip).
        private const string ClipWithTracks = @"[gd_resource type=""AnimationLibrary"" load_steps=2 format=3]

[sub_resource type=""Animation"" resource_name=""idle"" id=""Anim_idle""]
tracks/0/type = ""position_3d""
tracks/0/path = NodePath(""."")

[resource]
";

        // A .tres carrying an Animation clip with ZERO tracks → empty_clip.
        private const string ClipWithNoTracks = @"[gd_resource type=""AnimationLibrary"" load_steps=2 format=3]

[sub_resource type=""Animation"" resource_name=""empty"" id=""Anim_empty""]

[resource]
";

        // A .tres carrying an AnimationNodeStateMachine where every state is reachable from Start → healthy.
        private const string StateMachineAllReachable = @"[gd_resource type=""AnimationNodeStateMachine"" load_steps=3 format=3]

states/0/node = ""Idle""
states/1/node = ""Run""
transitions/0/from = ""Start""
transitions/0/to = ""Idle""
transitions/1/from = ""Idle""
transitions/1/to = ""Run""

[resource]
";

        // A .tres carrying an AnimationNodeStateMachine where "Run" has no inbound transition → unreachable.
        private const string StateMachineWithUnreachable = @"[gd_resource type=""AnimationNodeStateMachine"" load_steps=3 format=3]

states/0/node = ""Idle""
states/1/node = ""Run""
transitions/0/from = ""Start""
transitions/0/to = ""Idle""

[resource]
";

        // A .tres with no Animation / state-machine content → nothing to analyze.
        private const string NonAnimationResource = @"[gd_resource type=""Resource"" load_steps=1 format=3]

[resource]
";

        // Two .tres carrying identical Animation clip bodies → duplicate_clip (Full mode).
        private const string ClipDupA = @"[gd_resource type=""AnimationLibrary"" load_steps=2 format=3]

[sub_resource type=""Animation"" resource_name=""walk"" id=""Anim_walk""]
tracks/0/type = ""position_3d""

[resource]
";
        private const string ClipDupB = @"[gd_resource type=""AnimationLibrary"" load_steps=2 format=3]

[sub_resource type=""Animation"" resource_name=""walk_copy"" id=""Anim_walk"">
tracks/0/type = ""position_3d""

[resource]
";

        // ---- In-memory resolver ----------------------------------------------

        /// <summary>
        /// In-memory resolver modeling a directory tree + path/uid existence. Mirrors the sibling rules'
        /// InMemoryResolver. File content is modeled by the <see cref="Files"/> map the rule reads through
        /// its injected file reader.
        /// </summary>
        private sealed class InMemoryResolver : IAnimationAnalysisResolver
        {
            public Dictionary<string, List<string>> Directories { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
            public HashSet<string> ExistingPaths { get; } = new(StringComparer.Ordinal);
            public HashSet<string> ExistingUids { get; } = new(StringComparer.Ordinal);

            public IReadOnlyList<AnimationFolderEntry> ListDirectory(string? resDir)
            {
                var key = NormalizeDir(resDir);
                if (key == null || !Directories.TryGetValue(key, out var children))
                    return Array.Empty<AnimationFolderEntry>();

                var dirs = new List<AnimationFolderEntry>();
                var files = new List<AnimationFolderEntry>();
                foreach (var name in children)
                {
                    var childRes = key + name;
                    var childDirKey = childRes + "/";
                    if (Directories.ContainsKey(childDirKey))
                        dirs.Add(new AnimationFolderEntry(childDirKey, name, isDirectory: true));
                    else
                        files.Add(new AnimationFolderEntry(childRes, name, isDirectory: false));
                }
                dirs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                var combined = new List<AnimationFolderEntry>(dirs.Count + files.Count);
                combined.AddRange(dirs);
                combined.AddRange(files);
                return combined;
            }

            public bool PathExists(string? resPath)
                => resPath != null && ExistingPaths.Contains(resPath);

            public bool UidExists(string? uid)
                => uid != null && ExistingUids.Contains(uid);
        }

        private static string? NormalizeDir(string? resDir)
        {
            if (string.IsNullOrEmpty(resDir)) return "res://";
            var d = resDir!;
            if (!d.StartsWith("res://", StringComparison.Ordinal)) return null;
            if (!d.EndsWith("/")) d += "/";
            return d;
        }

        private static (AnimationAnalysisRule rule, InMemoryResolver resolver) BuildRule(
            Dictionary<string, List<string>>? directories = null,
            Dictionary<string, string>? files = null,
            HashSet<string>? existingPaths = null,
            HashSet<string>? existingUids = null)
        {
            var resolver = new InMemoryResolver();
            if (directories != null)
                foreach (var kv in directories) resolver.Directories[kv.Key] = new List<string>(kv.Value);
            if (files != null)
                foreach (var kv in files) resolver.Files[kv.Key] = kv.Value;
            if (existingPaths != null)
                foreach (var p in existingPaths) resolver.ExistingPaths.Add(p);
            if (existingUids != null)
                foreach (var u in existingUids) resolver.ExistingUids.Add(u);
            string? Reader(string p) => resolver.Files.TryGetValue(p, out var t) ? t : null;
            return (new AnimationAnalysisRule(resolver, Reader), resolver);
        }

        private static List<VerifyIssue> RunScan(AnimationAnalysisRule rule, string resPath,
            VerifyRunMode mode = VerifyRunMode.Full)
        {
            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(new[] { resPath }), mode, sink);
            return sink;
        }

        private static List<VerifyIssue> RunScan(AnimationAnalysisRule rule, string[] resPaths,
            VerifyRunMode mode = VerifyRunMode.Full)
        {
            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(resPaths), mode, sink);
            return sink;
        }

        // =====================================================================
        // missing_clip
        // =====================================================================

        [Fact]
        public void Scan_PlayerWithLiveLibrary_EmitsNoMissingClip()
        {
            var (rule, _) = BuildRule(
                files: new() { ["res://Player.tres"] = PlayerWithLiveLibrary },
                existingPaths: new() { "res://Anims/lib.tres" },
                existingUids: new() { "uid://goodlib" });

            Assert.Empty(RunScan(rule, "res://Player.tres")
                .Where(i => i.IssueCode == "missing_clip"));
        }

        [Fact]
        public void Scan_PlayerWithBrokenLibrary_EmitsMissingClipError()
        {
            var (rule, _) = BuildRule(
                files: new() { ["res://Player.tres"] = PlayerWithBrokenLibrary },
                // Neither path nor uid registered → broken.
                existingPaths: new HashSet<string>(),
                existingUids: new HashSet<string>());

            var issue = Assert.Single(RunScan(rule, "res://Player.tres")
                .Where(i => i.IssueCode == "missing_clip"));

            Assert.Equal(VerifySeverity.Error, issue.Severity);
            Assert.Equal("res://Player.tres", issue.AssetPath);
            Assert.Equal("missing_clip", issue.Evidence!["kind"]);
            // target prefers path (more readable) — matches MaterialsShaderHealth.DescribeShaderTarget.
            Assert.Equal("res://Anims/gone.tres", issue.Evidence!["target"]);
            Assert.Equal("uid://gonelib", issue.Evidence!["libraryUid"]);
        }

        [Fact]
        public void Scan_PlayerWithStalePathButLiveUid_NoMissingClip()
        {
            // False-positive guard: a stale path with a live uid (the normal post-relocation state) must
            // NOT flag. Mirrors BrokenReferences / MaterialsShaderHealth.
            var (rule, _) = BuildRule(
                files: new() { ["res://Player.tres"] = PlayerWithBrokenLibrary },
                existingPaths: new HashSet<string>(),
                existingUids: new() { "uid://gonelib" });

            Assert.Empty(RunScan(rule, "res://Player.tres")
                .Where(i => i.IssueCode == "missing_clip"));
        }

        // =====================================================================
        // empty_clip
        // =====================================================================

        [Fact]
        public void Scan_ClipWithTracks_EmitsNoEmptyClip()
        {
            var (rule, _) = BuildRule(files: new() { ["res://Lib.tres"] = ClipWithTracks });

            Assert.Empty(RunScan(rule, "res://Lib.tres")
                .Where(i => i.IssueCode == "empty_clip"));
        }

        [Fact]
        public void Scan_ClipWithNoTracks_EmitsEmptyClipWarning()
        {
            var (rule, _) = BuildRule(files: new() { ["res://Lib.tres"] = ClipWithNoTracks });

            var issue = Assert.Single(RunScan(rule, "res://Lib.tres")
                .Where(i => i.IssueCode == "empty_clip"));

            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("empty_clip", issue.Evidence!["kind"]);
            Assert.Equal("0", issue.Evidence!["trackCount"]);
            Assert.Equal("empty", issue.Evidence!["clipName"]);
            Assert.Contains("empty", issue.Description);
        }

        [Fact]
        public void Scan_EmptyClip_RunsOnCheckpointForDirectlyScopedResource()
        {
            // Per-asset detections run in every mode (cheap, no walk).
            var (rule, _) = BuildRule(files: new() { ["res://Lib.tres"] = ClipWithNoTracks });

            Assert.Single(RunScan(rule, "res://Lib.tres", VerifyRunMode.Checkpoint)
                .Where(i => i.IssueCode == "empty_clip"));
        }

        // =====================================================================
        // unreachable_state (Full mode only via directory walk; direct file runs every mode)
        // =====================================================================

        [Fact]
        public void Scan_StateMachineAllReachable_EmitsNoUnreachable()
        {
            var (rule, _) = BuildRule(files: new() { ["res://SM.tres"] = StateMachineAllReachable });

            Assert.Empty(RunScan(rule, "res://SM.tres")
                .Where(i => i.IssueCode == "unreachable_state"));
        }

        [Fact]
        public void Scan_StateMachineWithUnreachable_EmitsUnreachableWarning()
        {
            var (rule, _) = BuildRule(files: new() { ["res://SM.tres"] = StateMachineWithUnreachable });

            var issue = Assert.Single(RunScan(rule, "res://SM.tres")
                .Where(i => i.IssueCode == "unreachable_state"));

            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("unreachable_state", issue.Evidence!["kind"]);
            Assert.Equal("Run", issue.Evidence!["state"]);
            Assert.Contains("Run", issue.Description);
        }

        // =====================================================================
        // duplicate_clip (Full mode only)
        // =====================================================================

        [Fact]
        public void Scan_DuplicateClips_FullMode_EmitsDuplicateWarningPerMember()
        {
            var (rule, _) = BuildRule(files: new()
            {
                ["res://A.tres"] = ClipDupA,
                ["res://B.tres"] = ClipDupB,
            });

            var dups = RunScan(rule, new[] { "res://A.tres", "res://B.tres" })
                .Where(i => i.IssueCode == "duplicate_clip")
                .OrderBy(i => i.AssetPath).ToList();

            Assert.Equal(2, dups.Count);
            Assert.All(dups, i =>
            {
                Assert.Equal(VerifySeverity.Warning, i.Severity);
                Assert.Equal("duplicate_clip", i.Evidence!["kind"]);
                Assert.Equal("2", i.Evidence!["duplicateCount"]);
            });
            Assert.Equal("res://B.tres", dups[0].Evidence!["siblings"]);
            Assert.Equal("res://A.tres", dups[1].Evidence!["siblings"]);
        }

        [Fact]
        public void Scan_DuplicateClips_CheckpointMode_DoesNotRun()
        {
            // The duplicate pass is Full/Validate only.
            var (rule, _) = BuildRule(files: new() { ["res://A.tres"] = ClipDupA, ["res://B.tres"] = ClipDupB });

            Assert.Empty(RunScan(rule, new[] { "res://A.tres", "res://B.tres" }, VerifyRunMode.Checkpoint)
                .Where(i => i.IssueCode == "duplicate_clip"));
        }

        // =====================================================================
        // Scope filtering / domain boundaries
        // =====================================================================

        [Fact]
        public void Scan_NonAnimationResource_Skipped()
        {
            var (rule, _) = BuildRule(files: new() { ["res://R.tres"] = NonAnimationResource });

            Assert.Empty(RunScan(rule, "res://R.tres"));
        }

        [Fact]
        public void Scan_NonTresFile_Skipped()
        {
            // A .tscn or .png is not this rule's input.
            var (rule, _) = BuildRule(files: new() { ["res://S.tscn"] = "anything" });

            Assert.Empty(RunScan(rule, "res://S.tscn"));
        }

        [Fact]
        public void Scan_DirectoryWalk_IsValidateFullOnly()
        {
            // A directory scope on Checkpoint does not walk the subtree.
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "A.tres", "B.tres" } },
                files: new() { ["res://A.tres"] = ClipDupA, ["res://B.tres"] = ClipDupB });

            Assert.Empty(RunScan(rule, "res://", VerifyRunMode.Checkpoint));
        }

        [Fact]
        public void Scan_DirectoryWalk_FullMode_AnalyzesEveryTres()
        {
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Anims" },
                    ["res://Anims/"] = new() { "A.tres", "B.tres", "Empty.tres" },
                },
                files: new()
                {
                    ["res://Anims/A.tres"] = ClipDupA,
                    ["res://Anims/B.tres"] = ClipDupB,
                    ["res://Anims/Empty.tres"] = ClipWithNoTracks,
                });

            var issues = RunScan(rule, "res://");

            Assert.Contains(issues, i => i.IssueCode == "duplicate_clip" && i.AssetPath == "res://Anims/A.tres");
            Assert.Contains(issues, i => i.IssueCode == "duplicate_clip" && i.AssetPath == "res://Anims/B.tres");
            Assert.Contains(issues, i => i.IssueCode == "empty_clip" && i.AssetPath == "res://Anims/Empty.tres");
        }

        [Fact]
        public void Scan_OverlappingDirectoryRoots_WalkedOnce()
        {
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Anims" }, ["res://Anims/"] = new() { "A.tres", "B.tres" } },
                files: new() { ["res://Anims/A.tres"] = ClipDupA, ["res://Anims/B.tres"] = ClipDupB });

            var dups = RunScan(rule, new[] { "res://", "res://Anims" })
                .Where(i => i.IssueCode == "duplicate_clip" && i.AssetPath == "res://Anims/A.tres");

            Assert.Single(dups);
        }

        // =====================================================================
        // Robustness (never throws)
        // =====================================================================

        [Fact]
        public void Scan_ThrowingResolver_DoesNotCrash()
        {
            var resolver = new ThrowingResolver();
            string? Reader(string _) => "irrelevant";
            var rule = new AnimationAnalysisRule(resolver, Reader);

            Assert.Empty(RunScan(rule, "res://"));
        }

        [Fact]
        public void Scan_ThrowingFileReader_DoesNotCrash()
        {
            var resolver = new InMemoryResolver
            {
                Directories = { ["res://"] = new() { "A.tres" } },
                Files = { ["res://A.tres"] = "anything" },
            };
            string? ThrowingReader(string _) => throw new InvalidOperationException("disk read failed");
            var rule = new AnimationAnalysisRule(resolver, ThrowingReader);

            Assert.Empty(RunScan(rule, "res://"));
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
        public void Scan_NullPaths_EmitsNothing()
        {
            var (rule, _) = BuildRule();
            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(null), VerifyRunMode.Full, sink);

            Assert.Empty(sink);
        }

        private sealed class ThrowingResolver : IAnimationAnalysisResolver
        {
            public IReadOnlyList<AnimationFolderEntry> ListDirectory(string? resDir)
                => throw new InvalidOperationException("boom");
            public bool PathExists(string? resPath) => throw new InvalidOperationException("boom");
            public bool UidExists(string? uid) => throw new InvalidOperationException("boom");
        }

        // =====================================================================
        // Gate-delta stability (IssueKey round-trip) + rootCause (P14.5)
        // =====================================================================

        [Fact]
        public void Scan_IssueKey_RoundTripsStably()
        {
            // Two scans of the same fixture produce identical IssueKeys — the gate delta relies on this.
            var (rule, _) = BuildRule(
                files: new() { ["res://Lib.tres"] = ClipWithNoTracks },
                existingPaths: new HashSet<string>(),
                existingUids: new HashSet<string>());

            var keys1 = RunScan(rule, "res://Lib.tres").Select(IssueKey.Build).ToList();
            var keys2 = RunScan(rule, "res://Lib.tres").Select(IssueKey.Build).ToList();

            Assert.Equal(keys1, keys2);
            // IssueKey.Build emits the canonical "WARN" abbreviation (IssueKey.SeverityToken).
            Assert.Contains("animation_analysis|WARN|res://Lib.tres|empty_clip", keys1);
        }

        [Fact]
        public void Scan_AllIssueCodes_BelongToThisRule()
        {
            // Every emitted issue carries this rule's id + a known code (catalog-drift guard).
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "A.tres", "B.tres", "Empty.tres" } },
                files: new()
                {
                    ["res://A.tres"] = ClipDupA,
                    ["res://B.tres"] = ClipDupB,
                    ["res://Empty.tres"] = ClipWithNoTracks,
                });

            var issues = RunScan(rule, "res://");
            var knownCodes = new HashSet<string>
            {
                "missing_clip", "empty_clip", "unreachable_state", "parameter_mismatch", "duplicate_clip",
            };

            Assert.All(issues, i =>
            {
                Assert.Equal("animation_analysis", i.RuleId);
                Assert.Contains(i.IssueCode, knownCodes);
            });
        }

        [Fact]
        public void Scan_EveryIssueCarriesRootCauseAndRemediation()
        {
            // P14.5 — every emitted issue materializes the explainability taxonomy pair.
            var (rule, _) = BuildRule(
                files: new()
                {
                    ["res://Player.tres"] = PlayerWithBrokenLibrary,
                    ["res://Lib.tres"] = ClipWithNoTracks,
                    ["res://SM.tres"] = StateMachineWithUnreachable,
                },
                existingPaths: new HashSet<string>(),
                existingUids: new HashSet<string>());

            var issues = RunScan(rule, new[] { "res://Player.tres", "res://Lib.tres", "res://SM.tres" });

            Assert.NotEmpty(issues);
            Assert.All(issues, i =>
            {
                Assert.False(string.IsNullOrEmpty(i.RootCause), $"{i.IssueCode} missing rootCause");
                Assert.False(string.IsNullOrEmpty(i.Remediation), $"{i.IssueCode} missing remediation");
            });
            // Pin the specific rootCause codes against the IssueExplainability table.
            Assert.Contains(issues, i => i.IssueCode == "missing_clip" && i.RootCause == "resource_missing");
            Assert.Contains(issues, i => i.IssueCode == "empty_clip" && i.RootCause == "configuration_mismatch");
            Assert.Contains(issues, i => i.IssueCode == "unreachable_state" && i.RootCause == "structural_complexity");
        }

        // =====================================================================
        // Fix-matching contract (codes are stable; fixIds empty in v1)
        // =====================================================================

        [Fact]
        public void IssueCodes_AreStableAndMatchFreezeRoster()
        {
            // Catalog-drift guard: every code matches the P14.5 freeze roster.
            Assert.Equal("missing_clip", IssueCodes.MissingClip);
            Assert.Equal("empty_clip", IssueCodes.EmptyClip);
            Assert.Equal("unreachable_state", IssueCodes.UnreachableState);
            Assert.Equal("parameter_mismatch", IssueCodes.ParameterMismatch);
            Assert.Equal("duplicate_clip", IssueCodes.DuplicateClip);
        }

        // =====================================================================
        // VerifyRunner auto-registration
        // =====================================================================

        [Fact]
        public void VerifyRunner_RegisterDefaults_RegistersAnimationAnalysisRule()
        {
            VerifyRunner.ClearRules();
            try
            {
                VerifyRunner.RegisterDefaults();

                var ids = VerifyRunner.Rules.Select(r => r.Id).ToList();
                Assert.Contains("animation_analysis", ids);
            }
            finally
            {
                VerifyRunner.ClearRules();
            }
        }

        [Fact]
        public void RegisterDefaults_IsIdempotent()
        {
            VerifyRunner.ClearRules();
            try
            {
                VerifyRunner.RegisterDefaults();
                var countAfterFirst = VerifyRunner.Rules.Count;
                VerifyRunner.RegisterDefaults();
                var countAfterSecond = VerifyRunner.Rules.Count;

                Assert.Equal(countAfterFirst, countAfterSecond);
            }
            finally
            {
                VerifyRunner.ClearRules();
            }
        }

        // =====================================================================
        // Parser unit tests (in isolation)
        // =====================================================================

        [Fact]
        public void Parser_PlayerLibraries_ExtractsLibraryRefs()
        {
            var ext = AnimationParser.CollectExtResources(PlayerWithBrokenLibrary);
            var refs = AnimationParser.ParsePlayerLibraries(PlayerWithBrokenLibrary, "res://P.tres", ext);

            var r = Assert.Single(refs);
            Assert.Equal("1_lib", r.LibraryUsageId);
            Assert.Equal("res://Anims/gone.tres", r.LibraryPath);
            Assert.Equal("uid://gonelib", r.LibraryUid);
        }

        [Fact]
        public void Parser_CollectExtResources_IndexesById()
        {
            var ext = AnimationParser.CollectExtResources(PlayerWithLiveLibrary);
            Assert.True(ext.ContainsKey("1_lib"));
            Assert.Equal("res://Anims/lib.tres", ext["1_lib"].Path);
            Assert.Equal("uid://goodlib", ext["1_lib"].Uid);
            Assert.Equal("AnimationLibrary", ext["1_lib"].Type);
        }

        [Fact]
        public void Parser_Clips_CountsTracks()
        {
            var withTracks = AnimationParser.ParseClips(ClipWithTracks, "res://L.tres");
            var clip = Assert.Single(withTracks);
            Assert.Equal(1, clip.TrackCount);

            var noTracks = AnimationParser.ParseClips(ClipWithNoTracks, "res://L.tres");
            var empty = Assert.Single(noTracks);
            Assert.Equal(0, empty.TrackCount);
        }

        [Fact]
        public void Parser_Clips_FingerprintIgnoresResourceName()
        {
            // Two clips differing only by resource_name share a fingerprint → duplicate.
            var a = AnimationParser.ParseClips(ClipDupA, "res://A.tres");
            var b = AnimationParser.ParseClips(ClipDupB, "res://B.tres");
            Assert.Equal(a[0].Fingerprint, b[0].Fingerprint);
        }

        [Fact]
        public void Parser_StateMachine_ExtractsStatesAndTransitions()
        {
            var machine = AnimationParser.ParseStateMachine(StateMachineAllReachable, "res://SM.tres");

            Assert.NotNull(machine);
            Assert.Equal(2, machine!.States.Count);
            Assert.Contains("Idle", machine.States);
            Assert.Contains("Run", machine.States);
            Assert.Equal(2, machine.Transitions.Count);
            Assert.Equal("Idle", machine.StartState); // transition from Start → Idle
        }

        [Fact]
        public void Parser_StateMachine_NoMachine_ReturnsNull()
        {
            var machine = AnimationParser.ParseStateMachine(NonAnimationResource, "res://R.tres");
            Assert.Null(machine);
        }

        [Fact]
        public void Parser_NeverThrowsOnMalformedInput()
        {
            // Truncated/garbled inputs must not throw.
            Assert.NotNull(AnimationParser.ParsePlayerLibraries("[gd_resource", "res://P.tres",
                new Dictionary<string, (string?, string?, string?)>()));
            Assert.NotNull(AnimationParser.ParseClips("[gd_resource", "res://L.tres"));
            // ParseStateMachine may return null on garbage, but must not throw.
            var _ = AnimationParser.ParseStateMachine("[gd_resource type=\"", "res://SM.tres");
        }
    }
}
