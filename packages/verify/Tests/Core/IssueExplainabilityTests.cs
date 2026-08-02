#nullable enable
using System.Collections.Generic;
using System.Linq;
using GodotOpenMcp.Verify.Core;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Core
{
    /// <summary>
    /// P14.5 tests for the explainability taxonomy. Verifies:
    /// <list type="bullet">
    ///   <item><see cref="IssueExplainability"/> covers every <c>(ruleId, issueCode)</c> pair the
    ///     implemented Godot rules emit (no class left without a rootCause + remediation) — the
    ///     taxonomy-coverage gate.</item>
    ///   <item>Root-cause codes are in the documented stable set (see
    ///     <see cref="IssueExplainability.RootCauses"/>).</item>
    ///   <item>Remediation copy is clean of internal ids (no phase / spec / execution-plan references leak
    ///     into user-visible text — AGENTS.md §No internal references).</item>
    ///   <item><see cref="VerifyIssue"/> carries the optional <c>RootCause</c> + <c>Remediation</c> via the
    ///     7-arg constructor, and the 5-arg / 6-arg overloads leave them null (backward-compatible).</item>
    ///   <item><see cref="IssueExplainability.TryGet"/> returns false for unknown/null/empty inputs.</item>
    /// </list>
    /// Adapted from Unity Open MCP's <c>IssueExplainabilityTests</c>.
    /// </summary>
    public class IssueExplainabilityTests
    {
        // The full set of (ruleId, issueCode) pairs the implemented Godot rules emit, sourced from the
        // rules' IssueCodes.cs. When a new code is added, add it here so the taxonomy-coverage gate fails
        // until IssueExplainability has an entry for it — and add a row to the MCP catalog drift test.
        public static IEnumerable<object[]> EmittedIssueCodes => new[]
        {
            // broken_references (P3.2)
            new object[] { "broken_references", "broken_scene_reference" },
            // missing_scripts (P3.3)
            new object[] { "missing_scripts", "missing_script" },
            // import_health (P3.4)
            new object[] { "import_health", "orphan_import" },
            new object[] { "import_health", "duplicate_uid" },
            // project_health (P14.1)
            new object[] { "project_health", "project_empty_folder" },
            new object[] { "project_health", "project_uid_only_folder" },
            new object[] { "project_health", "project_deep_nesting" },
            new object[] { "project_health", "project_large_folder" },
            new object[] { "project_health", "project_broken_asset" },
            new object[] { "project_health", "project_empty_scene" },
            // scene_structure_health (P14.2)
            new object[] { "scene_structure_health", "scene_deep_nesting" },
            new object[] { "scene_structure_health", "scene_high_node_count" },
            new object[] { "scene_structure_health", "scene_wide_sibling_list" },
            new object[] { "scene_structure_health", "scene_duplicate_node_name" },
            new object[] { "scene_structure_health", "scene_empty_node_branch" },
            // materials_shader_health (P14.3)
            new object[] { "materials_shader_health", "materials_missing_shader" },
            new object[] { "materials_shader_health", "materials_builtin_shader_only" },
            new object[] { "materials_shader_health", "materials_orphan_shader_include" },
            new object[] { "materials_shader_health", "materials_duplicate_material" },
            new object[] { "materials_shader_health", "materials_unused_material" },
            // script_audit (P14.4)
            new object[] { "script_audit", "script_class_mismatch" },
            new object[] { "script_audit", "script_missing_class_name" },
            new object[] { "script_audit", "script_cyclic_class_name" },
            // animation_analysis (P14.5)
            new object[] { "animation_analysis", "missing_clip" },
            new object[] { "animation_analysis", "empty_clip" },
            new object[] { "animation_analysis", "unreachable_state" },
            new object[] { "animation_analysis", "parameter_mismatch" },
            new object[] { "animation_analysis", "duplicate_clip" },
        };

        // The documented, stable root-cause code set — mirrors IssueExplainability.RootCauses. Branching on
        // any other string is a bug.
        private static readonly HashSet<string> StableRootCauses = new()
        {
            // Unity-ported codes.
            "missing_guid_reference",
            "missing_fileid_reference",
            "missing_script_class",
            "missing_dependency",
            "configuration_mismatch",
            "structural_complexity",
            "orphaned_meta",
            "duplicate_guid",
            "resource_missing",
            "build_blocker",
            // Godot-only codes.
            "missing_uid_reference",
            "duplicate_uid",
            "orphaned_import",
        };

        // Forbidden tokens in user-visible remediation copy (AGENTS.md §No internal references). None should
        // ever appear in remediation.
        private static readonly string[] ForbiddenInternalTokens =
        {
            "P14", "P3", "P13", "P7", "P12", "M3",
            "execution-plan", "specs/", "backlog-", "Plan 1", "Plan 2", "Plan 3",
        };

        [Theory]
        [MemberData(nameof(EmittedIssueCodes))]
        public void EveryEmittedIssueCode_HasExplainabilityEntry(string ruleId, string code)
        {
            Assert.True(IssueExplainability.TryGet(ruleId, code, out var entry),
                $"{ruleId}|{code} has no IssueExplainability entry");
            Assert.False(string.IsNullOrEmpty(entry!.RootCause),
                $"{ruleId}|{code} rootCause must not be null/empty");
            Assert.False(string.IsNullOrEmpty(entry.Remediation),
                $"{ruleId}|{code} remediation must not be null/empty");
        }

        [Theory]
        [MemberData(nameof(EmittedIssueCodes))]
        public void EveryRootCause_IsInStableSet(string ruleId, string code)
        {
            if (!IssueExplainability.TryGet(ruleId, code, out var entry)) return;
            Assert.Contains(entry!.RootCause, StableRootCauses);
        }

        [Fact]
        public void Remediation_IsCleanOfInternalIds()
        {
            foreach (var pair in EmittedIssueCodes)
            {
                var ruleId = (string)pair[0];
                var code = (string)pair[1];
                if (!IssueExplainability.TryGet(ruleId, code, out var entry)) continue;
                foreach (var token in ForbiddenInternalTokens)
                {
                    Assert.False(entry!.Remediation.Contains(token),
                        $"{ruleId}|{code} remediation leaks internal token '{token}'");
                }
            }
        }

        [Fact]
        public void TryGet_UnknownCode_ReturnsFalse()
        {
            Assert.False(IssueExplainability.TryGet("animation_analysis", "totally_made_up", out var entry));
            Assert.Null(entry);
        }

        [Fact]
        public void TryGet_NullOrEmptyArgs_ReturnsFalse()
        {
            Assert.False(IssueExplainability.TryGet(null, "missing_clip", out _));
            Assert.False(IssueExplainability.TryGet("animation_analysis", null, out _));
            Assert.False(IssueExplainability.TryGet("", "missing_clip", out _));
            Assert.False(IssueExplainability.TryGet("animation_analysis", "", out _));
        }

        [Fact]
        public void VerifyIssue_SevenArgConstructor_SetsRootCauseAndRemediation()
        {
            var evidence = new Dictionary<string, string> { ["kind"] = "empty_clip" };
            var issue = new VerifyIssue("animation_analysis", VerifySeverity.Warning,
                "res://L.tres", "empty_clip", "desc", evidence,
                "configuration_mismatch", "Add tracks to the clip.");

            Assert.Equal("configuration_mismatch", issue.RootCause);
            Assert.Equal("Add tracks to the clip.", issue.Remediation);
            Assert.Equal(1, issue.Evidence!.Count);
        }

        [Fact]
        public void VerifyIssue_SixArgConstructor_RootCauseIsNull()
        {
            // Backwards compat: the 6-arg constructor (P14.4 and earlier) leaves RootCause/Remediation null.
            var evidence = new Dictionary<string, string> { ["kind"] = "x" };
            var issue = new VerifyIssue("broken_references", VerifySeverity.Error,
                "res://S.tscn", "broken_scene_reference", "desc", evidence);

            Assert.Null(issue.RootCause);
            Assert.Null(issue.Remediation);
        }

        [Fact]
        public void VerifyIssue_FiveArgConstructor_RootCauseIsNull()
        {
            var issue = new VerifyIssue("broken_references", VerifySeverity.Error,
                "res://S.tscn", "broken_scene_reference", "desc");

            Assert.Null(issue.RootCause);
            Assert.Null(issue.Remediation);
            Assert.Null(issue.Evidence);
        }

        [Fact]
        public void RootCausesConstants_AreStableAndDocumented()
        {
            // Pin the constant strings — these are part of the public contract (agents branch on them).
            Assert.Equal("missing_guid_reference", IssueExplainability.RootCauses.MissingGuidReference);
            Assert.Equal("missing_fileid_reference", IssueExplainability.RootCauses.MissingFileIdReference);
            Assert.Equal("missing_script_class", IssueExplainability.RootCauses.MissingScriptClass);
            Assert.Equal("missing_dependency", IssueExplainability.RootCauses.MissingDependency);
            Assert.Equal("configuration_mismatch", IssueExplainability.RootCauses.ConfigurationMismatch);
            Assert.Equal("structural_complexity", IssueExplainability.RootCauses.StructuralComplexity);
            Assert.Equal("orphaned_meta", IssueExplainability.RootCauses.OrphanedMeta);
            Assert.Equal("duplicate_guid", IssueExplainability.RootCauses.DuplicateGuid);
            Assert.Equal("resource_missing", IssueExplainability.RootCauses.ResourceMissing);
            Assert.Equal("build_blocker", IssueExplainability.RootCauses.BuildBlocker);
            // Godot-only codes.
            Assert.Equal("missing_uid_reference", IssueExplainability.RootCauses.MissingUidReference);
            Assert.Equal("duplicate_uid", IssueExplainability.RootCauses.DuplicateUid);
            Assert.Equal("orphaned_import", IssueExplainability.RootCauses.OrphanedImport);
        }
    }
}
