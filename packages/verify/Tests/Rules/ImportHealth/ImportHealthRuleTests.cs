#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Editor;
using GodotOpenMcp.Verify.Rules.ImportHealth;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Rules.ImportHealth
{
    /// <summary>
    /// P3.4 tests for the import-health verify rule (orphan <c>.import</c> sidecars + duplicate uids).
    /// Greenfield fidelity: no Unity equivalent, so the test structure mirrors the sibling P3.2/P3.3 rule
    /// tests (valid fixture → no issues; broken fixture → expected issues; false-positive guards; run-mode
    /// split; robustness; scope filtering; gate-delta stability; fix-matching contract). The rule is
    /// exercised through its internal constructor with an <see cref="InMemoryResolver"/> + an in-memory
    /// sidecar map, so no Godot API and no disk access — the binary-less xUnit host runs the full rule.
    ///
    /// <para>
    /// Fixture text is hand-written to mirror real Godot <c>.import</c> sidecar serialization (the
    /// <c>[remap]</c> header shape with <c>source=</c>/<c>uid=</c>/<c>path=</c>/<c>importer=</c> keys), so
    /// the parser is validated against the exact format the editor writes, not a synthetic subset.
    /// </para>
    /// </summary>
    // Shares the VerifyRunner static-registry collection with VerifyRunnerTests so the registration
    // tests below do not race with the Core runner tests under xUnit's default parallel execution.
    [Collection("VerifyRunnerCollection")]
    public class ImportHealthRuleTests
    {
        // ---- Sidecar fixtures -------------------------------------------------
        //
        // Real Godot .import sidecar shape: an INI file with a single [remap] section. The source= is the
        // res:// path to the imported asset; uid= is the uid:// identifier; path= is the engine cache
        // path; importer= is the importer name. Field order and presence vary across versions — the
        // parser tolerates any subset.

        private const string HealthyTextureImport = @"[remap]

importer=""texture""
type=""CompressedTexture2D""
uid=""uid://caei3fpei47ss""
path=""res://.godot/imported/Player.png-abc123.png""
source=""res://Sprites/Player.png""

[deps]
source_file=""res://Sprites/Player.png""
";

        // Same as HealthyTextureImport but source points at a deleted file → orphan.
        private const string OrphanImport = @"[remap]

importer=""texture""
type=""CompressedTexture2D""
uid=""uid://orph1234567""
path=""res://.godot/imported/Gone.png-def.png""
source=""res://Sprites/Gone.png""
";

        // A sidecar with NO source= at all — structurally malformed. The rule must not flag it (not its
        // domain; a future structural rule handles that) and must not throw.
        private const string MalformedNoSource = @"[remap]

importer=""texture""
uid=""uid://malformed0001""
";

        // Two sidecars that share a uid → duplicate-uid collision.
        private const string DuplicateUidFirst = @"[remap]

importer=""texture""
uid=""uid://collide0001""
source=""res://Sprites/A.png""
path=""res://.godot/imported/A.png-aaa.png""
";

        private const string DuplicateUidSecond = @"[remap]

importer=""texture""
uid=""uid://collide0001""
source=""res://Sprites/B.png""
path=""res://.godot/imported/B.png-bbb.png""
";

        // A sidecar with no uid (older Godot, or a type that gets no uid). Must not participate in the
        // duplicate check and must not flag anything else when its source exists.
        private const string NoUidImport = @"[remap]

importer=""texture""
source=""res://Sprites/Plain.png""
path=""res://.godot/imported/Plain.png-ppp.png""
";

        // ---- Helpers -----------------------------------------------------------

        /// <summary>
        /// In-memory resolver + sidecar map. <see cref="ExistingFiles"/> is the set of source paths that
        /// "exist on disk"; <see cref="Files"/> is the sidecar-text map keyed by sidecar path. The
        /// resolver's <c>EnumerateImportSidecars</c> returns whatever the test staged in
        /// <see cref="EnumeratedSidecars"/> (default empty — a scoped check that names .import files
        /// directly does not need enumeration).
        /// </summary>
        private sealed class InMemoryResolver : IImportHealthResolver
        {
            private readonly HashSet<string> _existingFiles;

            public Dictionary<string, string> Files { get; } = new();
            public List<string> EnumeratedSidecars { get; } = new();

            public InMemoryResolver(string[]? existingFiles = null)
            {
                _existingFiles = new HashSet<string>(existingFiles ?? Array.Empty<string>(), StringComparer.Ordinal);
            }

            public bool FileExists(string? resPath)
                => resPath != null && _existingFiles.Contains(resPath);

            public bool UidExists(string? uid) => true; // not load-bearing for P3.4; the duplicate check indexes sidecars directly

            public IReadOnlyList<string> EnumerateImportSidecars(string? resRoot)
                => EnumeratedSidecars;
        }

        private static (ImportHealthRule rule, InMemoryResolver resolver) BuildRule(
            string[]? existingFiles = null, Dictionary<string, string>? sidecars = null,
            List<string>? enumeratedSidecars = null)
        {
            var resolver = new InMemoryResolver(existingFiles);
            if (sidecars != null)
                foreach (var kv in sidecars) resolver.Files[kv.Key] = kv.Value;
            if (enumeratedSidecars != null)
                resolver.EnumeratedSidecars.AddRange(enumeratedSidecars);
            string? Reader(string p) => resolver.Files.TryGetValue(p, out var t) ? t : null;
            return (new ImportHealthRule(resolver, Reader), resolver);
        }

        private static List<VerifyIssue> RunScan(ImportHealthRule rule, string resPath,
            VerifyRunMode mode = VerifyRunMode.Full)
        {
            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(new[] { resPath }), mode, sink);
            return sink;
        }

        private static List<VerifyIssue> RunScan(ImportHealthRule rule, string[] resPaths,
            VerifyRunMode mode = VerifyRunMode.Full)
        {
            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(resPaths), mode, sink);
            return sink;
        }

        // ---- Baseline: healthy sidecar produces no issues ---------------------

        [Fact]
        public void Scan_HealthySidecar_FullMode_EmitsNoIssues()
        {
            var (rule, _) = BuildRule(
                existingFiles: new[] { "res://Sprites/Player.png" },
                sidecars: new() { ["res://Sprites/Player.png.import"] = HealthyTextureImport });

            var issues = RunScan(rule, "res://Sprites/Player.png.import", VerifyRunMode.Full);

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_HealthySidecar_CheckpointMode_EmitsNoIssues()
        {
            var (rule, _) = BuildRule(
                existingFiles: new[] { "res://Sprites/Player.png" },
                sidecars: new() { ["res://Sprites/Player.png.import"] = HealthyTextureImport });

            var issues = RunScan(rule, "res://Sprites/Player.png.import", VerifyRunMode.Checkpoint);

            Assert.Empty(issues);
        }

        // ---- Orphan import (the load-bearing acceptance criterion) ------------

        [Fact]
        public void Scan_OrphanImport_SourceMissing_EmitsWarning()
        {
            var (rule, _) = BuildRule(
                existingFiles: Array.Empty<string>(), // source is gone
                sidecars: new() { ["res://Sprites/Gone.png.import"] = OrphanImport });

            var issues = RunScan(rule, "res://Sprites/Gone.png.import", VerifyRunMode.Full);

            var issue = Assert.Single(issues);
            Assert.Equal("import_health", issue.RuleId);
            Assert.Equal(VerifySeverity.Warning, issue.Severity); // orphan is cruft, not a load break
            Assert.Equal("orphan_import", issue.IssueCode);
            Assert.Equal("res://Sprites/Gone.png.import", issue.AssetPath);
            Assert.Equal("orphan_import", issue.Evidence!["kind"]);
            Assert.Equal("res://Sprites/Gone.png", issue.Evidence!["source"]);
            Assert.Equal("texture", issue.Evidence!["importer"]);
            Assert.Equal("res://.godot/imported/Gone.png-def.png", issue.Evidence!["path"]);
        }

        [Fact]
        public void Scan_OrphanImport_CheckpointMode_StillEmitsWarning()
        {
            // The checkpoint path MUST catch orphan sidecars — the source-deletion signal is cheap (one
            // existence probe) and must run on every mutation. Only the duplicate-uid walk is
            // checkpoint-skippable.
            var (rule, _) = BuildRule(
                existingFiles: Array.Empty<string>(),
                sidecars: new() { ["res://Sprites/Gone.png.import"] = OrphanImport });

            var issues = RunScan(rule, "res://Sprites/Gone.png.import", VerifyRunMode.Checkpoint);

            var issue = Assert.Single(issues);
            Assert.Equal("orphan_import", issue.IssueCode);
        }

        // ---- False-positive guards --------------------------------------------

        [Fact]
        public void Scan_HealthySourceExists_DoesNotFlagOrphan()
        {
            // Source present → healthy, no issue. The false-positive guard: a sidecar whose source
            // resolves must not be flagged.
            var (rule, _) = BuildRule(
                existingFiles: new[] { "res://Sprites/Player.png" },
                sidecars: new() { ["res://Sprites/Player.png.import"] = HealthyTextureImport });

            var issues = RunScan(rule, "res://Sprites/Player.png.import");

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_SidecarWithoutSource_DoesNotFlagOrphan()
        {
            // A malformed sidecar with no source= is not this rule's domain — flagging it would be noise.
            // It must contribute no orphan issue and must not throw.
            var (rule, _) = BuildRule(
                existingFiles: Array.Empty<string>(),
                sidecars: new() { ["res://Sprites/Weird.png.import"] = MalformedNoSource });

            var orphanIssues = RunScan(rule, "res://Sprites/Weird.png.import")
                .Where(i => i.IssueCode == "orphan_import");

            Assert.Empty(orphanIssues);
        }

        [Fact]
        public void Scan_SidecarWithoutUid_DoesNotParticipateInDuplicateCheck()
        {
            // A sidecar with no uid= must not be indexed for the duplicate check (a null uid is not a
            // collision). With a healthy source it contributes no issues at all.
            var (rule, _) = BuildRule(
                existingFiles: new[] { "res://Sprites/Plain.png" },
                sidecars: new() { ["res://Sprites/Plain.png.import"] = NoUidImport });

            var issues = RunScan(rule, "res://Sprites/Plain.png.import");

            Assert.Empty(issues);
        }

        // ---- Duplicate uid (the load-bearing acceptance criterion) ------------

        [Fact]
        public void Scan_DuplicateUid_FullMode_EmitsErrorPerClaimant()
        {
            var (rule, _) = BuildRule(
                existingFiles: new[] { "res://Sprites/A.png", "res://Sprites/B.png" },
                sidecars: new()
                {
                    ["res://Sprites/A.png.import"] = DuplicateUidFirst,
                    ["res://Sprites/B.png.import"] = DuplicateUidSecond,
                });

            var issues = RunScan(rule,
                new[] { "res://Sprites/A.png.import", "res://Sprites/B.png.import" },
                VerifyRunMode.Full);

            // Two issues — one per claiming sidecar — each anchored on that sidecar's asset path so the
            // gate delta has a distinct key per claimant and a fix can resolve one at a time.
            Assert.Equal(2, issues.Count);
            Assert.All(issues, i => Assert.Equal("duplicate_uid", i.IssueCode));
            Assert.All(issues, i => Assert.Equal(VerifySeverity.Error, i.Severity));
            Assert.All(issues, i => Assert.Equal("import_health", i.RuleId));

            var a = issues.Single(i => i.AssetPath == "res://Sprites/A.png.import");
            Assert.Equal("uid://collide0001", a.Evidence!["uid"]);
            Assert.Contains("res://Sprites/B.png.import", a.Evidence!["conflictingPaths"]);

            var b = issues.Single(i => i.AssetPath == "res://Sprites/B.png.import");
            Assert.Contains("res://Sprites/A.png.import", b.Evidence!["conflictingPaths"]);
        }

        [Fact]
        public void Scan_DuplicateUid_DirectlyScopedSidecars_CheckpointStillRuns()
        {
            // When the scope lists .import paths directly, the duplicate check runs even on checkpoint:
            // the working set was assembled without an extra tree walk, so the check is free and bounded.
            var (rule, _) = BuildRule(
                existingFiles: new[] { "res://Sprites/A.png", "res://Sprites/B.png" },
                sidecars: new()
                {
                    ["res://Sprites/A.png.import"] = DuplicateUidFirst,
                    ["res://Sprites/B.png.import"] = DuplicateUidSecond,
                });

            var issues = RunScan(rule,
                new[] { "res://Sprites/A.png.import", "res://Sprites/B.png.import" },
                VerifyRunMode.Checkpoint);

            Assert.Equal(2, issues.Count);
            Assert.All(issues, i => Assert.Equal("duplicate_uid", i.IssueCode));
        }

        [Fact]
        public void Scan_DuplicateUid_EnumeratedSubtree_CheckpointSkips()
        {
            // When the sidecars are reached via a directory enumeration (not directly scoped), the
            // duplicate check is checkpoint-skippable: it needs a complete subtree to be correct, and a
            // header-only checkpoint should not pay for the walk. The orphan check still runs (cheap).
            var (rule, _) = BuildRule(
                existingFiles: new[] { "res://Sprites/A.png", "res://Sprites/B.png" },
                sidecars: new()
                {
                    ["res://Sprites/A.png.import"] = DuplicateUidFirst,
                    ["res://Sprites/B.png.import"] = DuplicateUidSecond,
                },
                enumeratedSidecars: new() { "res://Sprites/A.png.import", "res://Sprites/B.png.import" });

            var issues = RunScan(rule, "res://Sprites", VerifyRunMode.Checkpoint);

            // No orphan issues (sources exist), no duplicate issues (checkpoint + enumeration path).
            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_DuplicateUid_DeterministicOrdering()
        {
            // The conflicting-paths evidence is sorted path-ascending so the same collision always
            // produces the same evidence string — gate-delta stability.
            var (rule, _) = BuildRule(
                existingFiles: new[] { "res://Sprites/A.png", "res://Sprites/B.png" },
                sidecars: new()
                {
                    ["res://Sprites/A.png.import"] = DuplicateUidFirst,
                    ["res://Sprites/B.png.import"] = DuplicateUidSecond,
                });

            var first = RunScan(rule,
                new[] { "res://Sprites/A.png.import", "res://Sprites/B.png.import" });
            var second = RunScan(rule,
                new[] { "res://Sprites/B.png.import", "res://Sprites/A.png.import" }); // reversed input

            // Both runs report the same conflicting-paths string for the same sidecar (order-independent).
            var firstA = first.Single(i => i.AssetPath == "res://Sprites/A.png.import");
            var secondA = second.Single(i => i.AssetPath == "res://Sprites/A.png.import");
            Assert.Equal(firstA.Evidence!["conflictingPaths"], secondA.Evidence!["conflictingPaths"]);
        }

        // ---- Scope filtering & enumeration ------------------------------------

        [Fact]
        public void Scan_DirectoryPath_EnumeratesSidecars()
        {
            // A directory path triggers enumeration; the rule then parses each enumerated sidecar. Here
            // the subtree holds an orphan → flagged.
            var (rule, _) = BuildRule(
                existingFiles: Array.Empty<string>(),
                sidecars: new() { ["res://Sprites/Gone.png.import"] = OrphanImport },
                enumeratedSidecars: new() { "res://Sprites/Gone.png.import" });

            var issues = RunScan(rule, "res://Sprites");

            var orphan = Assert.Single(issues);
            Assert.Equal("orphan_import", orphan.IssueCode);
            Assert.Equal("res://Sprites/Gone.png.import", orphan.AssetPath);
        }

        [Fact]
        public void Scan_DeduplicatesSidecarNamedTwice()
        {
            // A scope that names a directory AND its child .import must not double-count. The working set
            // de-dupes by path.
            var (rule, _) = BuildRule(
                existingFiles: Array.Empty<string>(),
                sidecars: new() { ["res://Sprites/Gone.png.import"] = OrphanImport },
                enumeratedSidecars: new() { "res://Sprites/Gone.png.import" });

            var issues = RunScan(rule,
                new[] { "res://Sprites", "res://Sprites/Gone.png.import" });

            var orphan = Assert.Single(issues);
            Assert.Equal("orphan_import", orphan.IssueCode);
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
        public void Scan_NoSidecarsInScope_EmitsNothing()
        {
            // A scope over a .tscn (not a .import) with no enumerated sidecars contributes nothing.
            var (rule, _) = BuildRule(
                existingFiles: new[] { "res://Main.tscn" });

            var issues = RunScan(rule, "res://Main.tscn");

            Assert.Empty(issues);
        }

        // ---- Robustness: malformed input never throws -------------------------

        [Fact]
        public void Scan_MalformedSidecar_DoesNotThrow()
        {
            // Unbalanced quotes, no [remap] header, garbage lines — the parser yields what it can and the
            // rule never throws. This is the IVerifyRule "must not throw on ordinary malformed input"
            // contract.
            var malformed = "garbage line\n[remap\nimporter=\"texture\nsource=\"res://X.png\n";
            var (rule, _) = BuildRule(
                existingFiles: Array.Empty<string>(),
                sidecars: new() { ["res://Sprites/X.png.import"] = malformed });

            var ex = Record.Exception(() => RunScan(rule, "res://Sprites/X.png.import"));

            Assert.Null(ex);
        }

        [Fact]
        public void Scan_EmptySidecar_EmitsNoIssues()
        {
            var (rule, _) = BuildRule(
                sidecars: new() { ["res://Sprites/X.png.import"] = "" });

            var issues = RunScan(rule, "res://Sprites/X.png.import");

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_UnreadableSidecar_EmitsNoIssuesAndDoesNotThrow()
        {
            // A reader that throws simulates a file that vanished between checkpoint and validate. The
            // rule swallows it and contributes no issues.
            var resolver = new InMemoryResolver();
            string? Reader(string _) => throw new InvalidOperationException("disk gone");

            var rule = new ImportHealthRule(resolver, Reader);

            var ex = Record.Exception(() => RunScan(rule, "res://Sprites/X.png.import"));

            Assert.Null(ex);
        }

        // ---- Stability for the gate delta -------------------------------------

        [Fact]
        public void Scan_OrphanIssuesProduceStableIssueKeys()
        {
            // The gate delta and MCP capabilities consume IssueKey.Build(issue). Every emitted issue must
            // round-trip through IssueKey.TryParse — the "stable enough for gate delta" acceptance criterion.
            var (rule, _) = BuildRule(
                existingFiles: Array.Empty<string>(),
                sidecars: new() { ["res://Sprites/Gone.png.import"] = OrphanImport });

            var issues = RunScan(rule, "res://Sprites/Gone.png.import", VerifyRunMode.Full);

            foreach (var issue in issues)
            {
                var key = IssueKey.Build(issue);
                Assert.True(IssueKey.TryParse(key, out var ruleId, out var sev, out var path, out var code),
                    $"Issue key '{key}' should be parseable");
                Assert.Equal("import_health", ruleId);
                Assert.Equal(VerifySeverity.Warning, sev);
                Assert.Equal("res://Sprites/Gone.png.import", path);
                Assert.Equal("orphan_import", code);
            }
        }

        [Fact]
        public void Scan_DuplicateUidIssuesProduceStableIssueKeys()
        {
            var (rule, _) = BuildRule(
                existingFiles: new[] { "res://Sprites/A.png", "res://Sprites/B.png" },
                sidecars: new()
                {
                    ["res://Sprites/A.png.import"] = DuplicateUidFirst,
                    ["res://Sprites/B.png.import"] = DuplicateUidSecond,
                });

            var issues = RunScan(rule,
                new[] { "res://Sprites/A.png.import", "res://Sprites/B.png.import" },
                VerifyRunMode.Full);

            foreach (var issue in issues)
            {
                var key = IssueKey.Build(issue);
                Assert.True(IssueKey.TryParse(key, out var ruleId, out var sev, out var path, out var code),
                    $"Issue key '{key}' should be parseable");
                Assert.Equal("import_health", ruleId);
                Assert.Equal(VerifySeverity.Error, sev);
                Assert.Equal("duplicate_uid", code);
                // Each issue is anchored on a distinct sidecar so the gate delta has a distinct key per
                // claimant.
                Assert.True(path == "res://Sprites/A.png.import" || path == "res://Sprites/B.png.import");
            }
        }

        // ---- Rule integrates with fix matching contract -----------------------

        [Fact]
        public void Issue_FixMatchingContract_OrphanImportRoutesCorrectly()
        {
            // Acceptance criterion: "Rule integrates with fix matching contract (ruleId + issueCode)".
            // A future remove_orphan_import fix will CanFix on ruleId|issueCode. This test pins the stable
            // tuple this rule emits so a future fix provider keys off exactly this pair.
            var (rule, _) = BuildRule(
                existingFiles: Array.Empty<string>(),
                sidecars: new() { ["res://Sprites/Gone.png.import"] = OrphanImport });

            var issues = RunScan(rule, "res://Sprites/Gone.png.import", VerifyRunMode.Full);

            var issue = Assert.Single(issues);
            Assert.Equal("import_health|orphan_import", $"{issue.RuleId}|{issue.IssueCode}");
        }

        [Fact]
        public void Issue_FixMatchingContract_DuplicateUidRoutesCorrectly()
        {
            var (rule, _) = BuildRule(
                existingFiles: new[] { "res://Sprites/A.png", "res://Sprites/B.png" },
                sidecars: new()
                {
                    ["res://Sprites/A.png.import"] = DuplicateUidFirst,
                    ["res://Sprites/B.png.import"] = DuplicateUidSecond,
                });

            var issues = RunScan(rule,
                new[] { "res://Sprites/A.png.import", "res://Sprites/B.png.import" },
                VerifyRunMode.Full);

            Assert.All(issues, i =>
                Assert.Equal("import_health|duplicate_uid", $"{i.RuleId}|{i.IssueCode}"));
        }

        // ---- Integration with VerifyRunner (rule is auto-registered) ---------

        [Fact]
        public void VerifyRunner_AutoRegistersImportHealthRule()
        {
            // The rule must appear in the default registration so the gate runs it on every mutation
            // without explicit wiring (P3.4 acceptance: rule runtime is bounded for scoped paths_hint).
            VerifyRunner.ClearRules();
            try
            {
                VerifyRunner.RegisterDefaults();
                Assert.Contains(VerifyRunner.Rules, r => r.Id == "import_health");
            }
            finally
            {
                VerifyRunner.ClearRules();
            }
        }
    }
}
