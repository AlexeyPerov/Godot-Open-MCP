#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Rules.BrokenReferences;
using GodotOpenMcp.Verify.Rules.MissingScripts;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Rules.MissingScripts
{
    /// <summary>
    /// P3.3 tests for the missing-scripts verify rule. Adapted fidelity: the test structure mirrors
    /// Unity's <c>MissingReferencesRuleTests</c> and the sibling P3.2 <c>BrokenReferencesRuleTests</c>
    /// (valid fixture → no issues; broken fixture → expected issues; false-positive guards; run-mode
    /// split; robustness; scope filtering; gate-delta stability). The rule is exercised through its
    /// internal constructor with an <see cref="InMemoryResolver"/> + an in-memory file map, so no Godot
    /// API and no disk access — the binary-less xUnit host runs the full rule.
    ///
    /// <para>
    /// Fixture text is hand-written to mirror real Godot <c>.tscn</c> serialization (the
    /// <c>[gd_scene]</c>/<c>[ext_resource]</c>/<c>[node]</c>/<c>script = ExtResource("id")</c> shapes from
    /// the Godot-MCP harness scene and the editor's own output), so the parser is validated against the
    /// exact format the editor writes, not a synthetic subset.
    /// </para>
    /// </summary>
    public class MissingScriptsRuleTests
    {
        // ---- Fixtures ----------------------------------------------------------

        // A complete, valid scene: a root node with a script whose ext_resource resolves by BOTH uid and
        // path, plus a child node with a different script that resolves by path only. Baseline for the
        // false-positive guard: this MUST produce zero issues in every mode. Uses the real header shape
        // from Godot-MCP's Harness/Main.tscn (script = ExtResource on an indented body line).
        private const string ValidScene = @"[gd_scene load_steps=3 format=3 uid=""uid://validscene123""]

[ext_resource type=""Script"" uid=""uid://scriptok123"" path=""res://Player.gd"" id=""1_abc""]
[ext_resource type=""Script"" path=""res://UI/Hud.gd"" id=""2_def""]

[node name=""Player"" type=""Node2D""]
script = ExtResource(""1_abc"")

[node name=""Hud"" type=""CanvasLayer"" parent=""Player""]
script = ExtResource(""2_def"")
";

        // The script's ext_resource is declared but both uid and path point at nothing → the script file
        // was deleted. Flagged as a missing script on the node that attaches it.
        private const string DeletedScriptBothMissing = @"[gd_scene load_steps=2 format=3]

[ext_resource type=""Script"" uid=""uid://gonemissing"" path=""res://Gone.gd"" id=""1_abc""]

[node name=""Player"" type=""Node2D""]
script = ExtResource(""1_abc"")
";

        // Stale path but live uid — the relocation case (script moved, uid stable). MUST NOT flag.
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

        // A node whose script references an id no ext_resource declares — dangling. Full/Validate only.
        private const string DanglingScriptId = @"[gd_scene load_steps=1 format=3]

[ext_resource type=""Script"" path=""res://Player.gd"" id=""1_abc""]

[node name=""Player"" type=""Node2D""]
script = ExtResource(""99_nope"")
";

        // Two nodes attach scripts; one resolves (1_abc), the other is dangling (99_nope). Ensures the
        // rule attributes the right script to the right node and only flags the broken one.
        private const string MixedResolvedAndDangling = @"[gd_scene load_steps=1 format=3]

[ext_resource type=""Script"" path=""res://Player.gd"" id=""1_abc""]

[node name=""Player"" type=""Node2D""]
script = ExtResource(""1_abc"")

[node name=""Ghost"" type=""Node"" parent=""Player""]
script = ExtResource(""99_nope"")
";

        // A .tres resource (not a scene) with a script attached to its resource root. Godot serializes a
        // resource script as `script = ExtResource` on the [gd_resource] body — the rule must cover .tres
        // too, not just .tscn.
        private const string ValidTresWithScript = @"[gd_resource type=""Resource"" load_steps=2 format=3]

[ext_resource type=""Script"" path=""res://Config.gd"" id=""1_cfg""]

[resource]
script = ExtResource(""1_cfg"")
";

        // ---- Helpers ------------------------------------------------------------

        /// <summary>
        /// In-memory resolver + file map. Identical seam to <c>BrokenReferencesRuleTests</c>: the
        /// constructor takes the set of paths/uids that exist; the file reader pulls scene text from
        /// <see cref="Files"/>. Reuses <see cref="IResourceResolver"/> from P3.2 so both rules share one
        /// resolution model in tests.
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

        private static (MissingScriptsRule rule, InMemoryResolver resolver) BuildRule(
            string[]? existingPaths = null, string[]? existingUids = null, Dictionary<string, string>? files = null)
        {
            var resolver = new InMemoryResolver(existingPaths, existingUids);
            if (files != null)
                foreach (var kv in files) resolver.Files[kv.Key] = kv.Value;
            string? Reader(string p) => resolver.Files.TryGetValue(p, out var t) ? t : null;
            return (new MissingScriptsRule(resolver, Reader), resolver);
        }

        private static List<VerifyIssue> RunScan(MissingScriptsRule rule, string resPath,
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
                existingPaths: new[] { "res://Player.gd", "res://UI/Hud.gd" },
                existingUids: new[] { "uid://scriptok123" },
                files: new() { ["res://Main.tscn"] = ValidScene });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_ValidScene_CheckpointMode_EmitsNoIssues()
        {
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd", "res://UI/Hud.gd" },
                existingUids: new[] { "uid://scriptok123" },
                files: new() { ["res://Main.tscn"] = ValidScene });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Checkpoint);

            Assert.Empty(issues);
        }

        // ---- Deleted script (the load-bearing acceptance criterion) -----------

        [Fact]
        public void Scan_DeletedScript_BothUidAndPathMissing_EmitsMissingScriptError()
        {
            var (rule, _) = BuildRule(
                existingPaths: Array.Empty<string>(),
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = DeletedScriptBothMissing });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            var issue = Assert.Single(issues);
            Assert.Equal("missing_scripts", issue.RuleId);
            Assert.Equal(VerifySeverity.Error, issue.Severity);
            Assert.Equal("missing_script", issue.IssueCode);
            Assert.Equal("res://Main.tscn", issue.AssetPath);
            // Node-level evidence is this rule's specialization over P3.2.
            Assert.Equal("script_resource_missing", issue.Evidence!["kind"]);
            Assert.Equal("Player", issue.Evidence!["nodeName"]);
            Assert.Equal("Player", issue.Evidence!["nodePath"]);
            Assert.Equal("1_abc", issue.Evidence!["scriptExtId"]);
            Assert.Equal("uid://gonemissing", issue.Evidence!["target"]); // uid preferred when both present
            Assert.Equal("6", issue.Evidence!["line"]); // 1-based: script line is line 6
        }

        [Fact]
        public void Scan_DeletedScript_CheckpointMode_StillEmitsError()
        {
            // The checkpoint path MUST catch deleted scripts — that is the whole point of running the rule
            // on every mutation. Only the dangling-id walk is checkpoint-skippable.
            var (rule, _) = BuildRule(
                existingPaths: Array.Empty<string>(),
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = DeletedScriptBothMissing });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Checkpoint);

            var issue = Assert.Single(issues);
            Assert.Equal("missing_script", issue.IssueCode);
            Assert.Equal("script_resource_missing", issue.Evidence!["kind"]);
        }

        // ---- False-positive guards (shared with P3.2's resolution model) -------

        [Fact]
        public void Scan_StalePathLiveUid_DoesNotFlag()
        {
            // Relocation case: Godot moved the script, the path is stale but the uid resolves. Flagging
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

        [Fact]
        public void Scan_PathOnlyScript_ResolvesByPath_DoesNotFlag()
        {
            // A script ext_resource with no uid (older Godot export) that resolves by path is healthy.
            // Ensures the absent-uid identifier is neither a pass nor a fail.
            var pathOnly = "[gd_scene load_steps=1 format=3]\n\n" +
                           "[ext_resource type=\"Script\" path=\"res://Player.gd\" id=\"1_abc\"]\n\n" +
                           "[node name=\"Player\" type=\"Node2D\"]\nscript = ExtResource(\"1_abc\")\n";
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd" },
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = pathOnly });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            Assert.Empty(issues);
        }

        // ---- Dangling script id (Validate/Full only) --------------------------

        [Fact]
        public void Scan_DanglingScriptId_FullMode_EmitsError()
        {
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd" }, // 99_nope's target was never declared
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = DanglingScriptId });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            var issue = Assert.Single(issues);
            Assert.Equal("missing_script", issue.IssueCode);
            Assert.Equal("script_id_dangling", issue.Evidence!["kind"]);
            Assert.Equal("Player", issue.Evidence!["nodeName"]);
            Assert.Equal("99_nope", issue.Evidence!["scriptExtId"]);
        }

        [Fact]
        public void Scan_DanglingScriptId_CheckpointMode_DoesNotEmit()
        {
            // Checkpoint skips the dangling-id walk. The dangling id here is NOT a deleted resource (no
            // ext_resource declares it, so there is nothing to resolve), so checkpoint must return zero.
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd" },
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = DanglingScriptId });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Checkpoint);

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_MixedResolvedAndDangling_OnlyFlagsDangling()
        {
            // Two nodes, two scripts: 1_abc resolves, 99_nope is dangling. Only the dangling node is
            // flagged, and the node-path evidence distinguishes the child (Player/Ghost) from the root.
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd" },
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = MixedResolvedAndDangling });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            var issue = Assert.Single(issues);
            Assert.Equal("Ghost", issue.Evidence!["nodeName"]);
            Assert.Equal("Player/Ghost", issue.Evidence!["nodePath"]);
            Assert.Equal("99_nope", issue.Evidence!["scriptExtId"]);
        }

        // ---- Node-path normalization ------------------------------------------

        [Fact]
        public void Scan_NodePath_ReconstructedFromParentAttribute()
        {
            // A deeply nested node carries parent="A/B"; its full path is A/B/C. This is the path an agent
            // would pass to a node tool and what the P3.7 fix needs to locate the node.
            var nested = "[gd_scene load_steps=1 format=3]\n\n" +
                         "[ext_resource type=\"Script\" uid=\"uid://gone1\" path=\"res://Gone.gd\" id=\"1\"]\n\n" +
                         "[node name=\"A\" type=\"Node\"]\n\n" +
                         "[node name=\"B\" type=\"Node\" parent=\"A\"]\n\n" +
                         "[node name=\"C\" type=\"Node\" parent=\"A/B\"]\nscript = ExtResource(\"1\")\n";
            var (rule, _) = BuildRule(
                existingPaths: Array.Empty<string>(),
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = nested });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            var issue = Assert.Single(issues);
            Assert.Equal("C", issue.Evidence!["nodeName"]);
            Assert.Equal("A/B/C", issue.Evidence!["nodePath"]);
        }

        [Fact]
        public void Scan_NodePath_FirstLevelChildWithDotParent()
        {
            // Godot writes parent="." for first-level children of the scene root. The path is just the
            // node name (no leading ".").
            var dotParent = "[gd_scene load_steps=1 format=3]\n\n" +
                            "[ext_resource type=\"Script\" uid=\"uid://gone1\" path=\"res://Gone.gd\" id=\"1\"]\n\n" +
                            "[node name=\"Root\" type=\"Node\"]\n\n" +
                            "[node name=\"Child\" type=\"Node\" parent=\".\"]\nscript = ExtResource(\"1\")\n";
            var (rule, _) = BuildRule(
                existingPaths: Array.Empty<string>(),
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = dotParent });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            var issue = Assert.Single(issues);
            Assert.Equal("Child", issue.Evidence!["nodeName"]);
            Assert.Equal("Child", issue.Evidence!["nodePath"]);
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
            // .tres uses the same node/resource + ext_resource model — the rule must cover it.
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Config.gd" },
                files: new() { ["res://Config.tres"] = ValidTresWithScript });

            var issues = RunScan(rule, "res://Config.tres");

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

        // ---- Robustness: malformed input never throws -------------------------

        [Fact]
        public void Scan_MalformedNodeHeader_DoesNotThrow()
        {
            // Truncated node header, a `script =` with no ExtResource, unbalanced quotes — the parser
            // yields what it can and the rule never throws. This is the IVerifyRule "must not throw"
            // contract.
            var malformed = "[node name=\"Player\" type=\"Node2D\n" +
                            "script = \n" +
                            "script = ExtResource(\"1_abc\"\n";
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd" },
                files: new() { ["res://Main.tscn"] = malformed });

            var ex = Record.Exception(() => RunScan(rule, "res://Main.tscn"));

            Assert.Null(ex);
        }

        [Fact]
        public void Scan_NodeWithoutScript_NotFlagged()
        {
            // A node with no script attachment contributes nothing. Ensures the parser does not emit a
            // phantom attachment for a node that simply has no script.
            var noScript = "[gd_scene load_steps=1 format=3]\n\n" +
                           "[ext_resource type=\"Script\" path=\"res://Player.gd\" id=\"1_abc\"]\n\n" +
                           "[node name=\"Plain\" type=\"Node\"]\n";
            var (rule, _) = BuildRule(
                existingPaths: new[] { "res://Player.gd" },
                files: new() { ["res://Main.tscn"] = noScript });

            var issues = RunScan(rule, "res://Main.tscn");

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_NonScriptPropertyNamedScript_IsNotAttributed()
        {
            // A property like `my_script = ExtResource("1_abc")` must NOT be treated as the node's script
            // attachment (it is a different property whose name merely contains "script"). Only a property
            // keyed exactly `script` counts.
            var decoy = "[gd_scene load_steps=1 format=3]\n\n" +
                        "[ext_resource type=\"Script\" uid=\"uid://gone1\" path=\"res://Gone.gd\" id=\"1_abc\"]\n\n" +
                        "[node name=\"Player\" type=\"Node\"]\nmy_script = ExtResource(\"1_abc\")\n";
            var (rule, _) = BuildRule(
                existingPaths: Array.Empty<string>(),
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = decoy });

            var issues = RunScan(rule, "res://Main.tscn");

            Assert.Empty(issues);
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
            // A reader that throws simulates a file that vanished between checkpoint and validate. The
            // rule swallows it and contributes no issues.
            var resolver = new InMemoryResolver();
            string? Reader(string _) => throw new InvalidOperationException("disk gone");

            var rule = new MissingScriptsRule(resolver, Reader);

            var ex = Record.Exception(() => RunScan(rule, "res://Main.tscn"));

            Assert.Null(ex);
        }

        // ---- Stability for the gate delta -------------------------------------

        [Fact]
        public void Scan_IssuesProduceStableIssueKeys()
        {
            // The gate delta and MCP capabilities consume IssueKey.Build(issue). Every emitted issue must
            // round-trip through IssueKey.TryParse — the "stable enough for gate delta" acceptance criterion.
            var (rule, _) = BuildRule(
                existingPaths: Array.Empty<string>(),
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = DeletedScriptBothMissing });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            foreach (var issue in issues)
            {
                var key = IssueKey.Build(issue);
                Assert.True(IssueKey.TryParse(key, out var ruleId, out var sev, out var path, out var code),
                    $"Issue key '{key}' should be parseable");
                Assert.Equal("missing_scripts", ruleId);
                Assert.Equal(VerifySeverity.Error, sev);
                Assert.Equal("res://Main.tscn", path);
                Assert.Equal("missing_script", code);
            }
        }

        [Fact]
        public void Scan_DeterministicOrdering()
        {
            // The gate delta compares issue sets by key; ordering within a file should still be stable (by
            // node appearance order) so two identical scans produce identical issue sequences.
            var (rule, _) = BuildRule(
                existingPaths: Array.Empty<string>(),
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = MixedResolvedAndDangling });

            var first = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);
            var second = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            Assert.Equal(first.Select(i => i.Evidence!["nodePath"]), second.Select(i => i.Evidence!["nodePath"]));
        }

        // ---- Rule integrates with fix matching contract -----------------------

        [Fact]
        public void Issue_FixMatchingContract_RuleIdAndIssueCodeRouteTogether()
        {
            // Acceptance criterion: "Rule integrates with fix matching contract (ruleId + issueCode)".
            // The P3.7 remove_missing_script fix will CanFix on the ruleId|issueCode tuple. This test
            // pins the stable tuple this rule emits so a future fix provider keys off exactly this pair.
            var (rule, _) = BuildRule(
                existingPaths: Array.Empty<string>(),
                existingUids: Array.Empty<string>(),
                files: new() { ["res://Main.tscn"] = DeletedScriptBothMissing });

            var issues = RunScan(rule, "res://Main.tscn", VerifyRunMode.Full);

            var issue = Assert.Single(issues);
            // The fix-matching contract key — exactly what FixProviderRegistry.CanFix parses.
            var issueId = $"{issue.RuleId}|{issue.IssueCode}";
            Assert.Equal("missing_scripts|missing_script", issueId);
        }
    }
}
