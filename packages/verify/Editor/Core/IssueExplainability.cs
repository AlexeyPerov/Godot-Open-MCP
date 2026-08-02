#nullable enable
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Core
{
    /// <summary>
    /// The explainability taxonomy: a stable, machine-readable <c>rootCause</c> code + clean
    /// user-visible <c>remediation</c> copy for every <c>(ruleId, issueCode)</c> pair the verify
    /// rules emit. Adapted (copy of the taxonomy shape) from Unity Open MCP's
    /// <c>IssueExplainability</c>: Unity keys its table the same way (<c>{ruleId}|{issueCode}</c>) and
    /// exposes the same <c>TryGet</c> helper. The codes are a flat, stable set an agent can branch on;
    /// the remediation text is the single source of truth for the human-facing fix guidance, kept here so
    /// it is not duplicated per rule.
    ///
    /// <para>
    /// <b>Adaptation for Godot (P14.5):</b> Unity keeps the rootCause/remediation as *static per-class*
    /// metadata resolved at lookup time and never repeats them on a <see cref="VerifyIssue"/>. The Godot
    /// contract *materializes* <see cref="VerifyIssue.RootCause"/> + <see cref="VerifyIssue.Remediation"/>
    /// onto each instance (the catalog drift test asserts "every emitted issue carries a rootCause", and
    /// the MCP <c>validate_edit</c> / <c>scan_paths</c> envelopes can carry them without a second lookup).
    /// Each rule's <c>MakeIssue</c> helper calls <see cref="TryGet"/> and forwards the pair to the
    /// <see cref="VerifyIssue"/> constructor. The pair is still intrinsic to the issue code (identical
    /// across every instance of the same code), so the table remains the single source of truth — the
    /// instance just caches it.
    /// </para>
    ///
    /// <para>
    /// <b>Stability contract.</b> The <c>rootCause</c> codes are part of the public contract — agents may
    /// branch on them, and the MCP <c>RuleIssueDescriptor.rootCause</c> field pins them. Never rename a
    /// code; add new ones instead. Backfill is additive and non-breaking: <see cref="VerifyIssue.RootCause"/>
    /// / <see cref="VerifyIssue.Remediation"/> are optional, and the gate delta (<see cref="IssueKey"/>)
    /// ignores them entirely (it keys on ruleId|severity|assetPath|issueCode).
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so it compiles into the binary-less xUnit
    /// host and the bridge test host that link <see cref="VerifyIssue"/>.
    /// </para>
    /// </summary>
    public static class IssueExplainability
    {
        /// <summary>
        /// The stable root-cause code set. Mirrors Unity's taxonomy verbatim (the 10 Unity codes) and adds
        /// the 3 Godot-only codes the reference model needs (Godot has uids where Unity has GUIDs, and
        /// Godot's <c>.import</c>/<c>.gd.uid</c> sidecars have no Unity twin). Branching on any string not
        /// in this set is a bug. See <see cref="Entry.RootCause"/>.
        /// </summary>
        public static class RootCauses
        {
            // --- Unity-ported codes (verbatim) ---------------------------------

            /// <summary>A serialized reference points at a GUID/uid no asset owns (the target was deleted/moved).</summary>
            public const string MissingGuidReference = "missing_guid_reference";

            /// <summary>The GUID/uid resolves but the sub-object id is not a top-level object in the target asset.</summary>
            public const string MissingFileIdReference = "missing_fileid_reference";

            /// <summary>A node's script class is gone (deleted, renamed, or its assembly no longer compiles).</summary>
            public const string MissingScriptClass = "missing_script_class";

            /// <summary>A forward dependency edge targets a resource that does not resolve.</summary>
            public const string MissingDependency = "missing_dependency";

            /// <summary>A settings/flags/value disagrees with intent (a configuration-level mismatch).</summary>
            public const string ConfigurationMismatch = "configuration_mismatch";

            /// <summary>Counts/depth exceed a health budget (structural complexity, not an integrity break).</summary>
            public const string StructuralComplexity = "structural_complexity";

            /// <summary>A sidecar/metadata file has no companion asset on disk (orphaned metadata).</summary>
            public const string OrphanedMeta = "orphaned_meta";

            /// <summary>Two+ assets share one GUID (Unity) — the engine cannot reliably resolve references to it.</summary>
            public const string DuplicateGuid = "duplicate_guid";

            /// <summary>A referenced shader/texture/clip resource is missing.</summary>
            public const string ResourceMissing = "resource_missing";

            /// <summary>The asset cannot compile/load (a hard build blocker).</summary>
            public const string BuildBlocker = "build_blocker";

            // --- Godot-only codes (no Unity twin) ------------------------------

            /// <summary>
            /// A <c>[ext_resource uid=]</c> / <c>uid://</c> reference does not resolve through Godot's
            /// <c>ResourceUID</c> table (the Godot analog of Unity's <c>missing_guid_reference</c> — Godot
            /// keys its canonical relocation-stable handle on uids, not GUIDs).
            /// </summary>
            public const string MissingUidReference = "missing_uid_reference";

            /// <summary>
            /// Two or more <c>.import</c> sidecars declare the same <c>uid://</c> (the Godot analog of
            /// Unity's <c>duplicate_guid</c>).
            /// </summary>
            public const string DuplicateUid = "duplicate_uid";

            /// <summary>
            /// A Godot <c>.import</c> / <c>.gd.uid</c> sidecar is orphaned (its source asset is gone), or a
            /// folder holds only stale uid sidecars. The Godot analog of Unity's <c>orphaned_meta</c>.
            /// </summary>
            public const string OrphanedImport = "orphaned_import";
        }

        /// <summary>
        /// One explainability entry: the stable root-cause code + the clean remediation copy for a given
        /// <c>(ruleId, issueCode)</c> pair. Mirrors Unity's <c>IssueExplainability.Entry</c>.
        /// </summary>
        public sealed class Entry
        {
            /// <summary>Stable root-cause code (one of <see cref="RootCauses"/>).</summary>
            public string RootCause { get; }

            /// <summary>
            /// Clean, user-visible remediation guidance (no internal ids / phase / spec references — see
            /// AGENTS.md §No internal references). Identical across every instance of the same issue code.
            /// </summary>
            public string Remediation { get; }

            public Entry(string rootCause, string remediation)
            {
                RootCause = rootCause;
                Remediation = remediation;
            }
        }

        /// <summary>
        /// The single source of truth: <c>{ruleId}|{issueCode}</c> → rootCause + remediation. Covers every
        /// code the implemented Godot rules emit (P3 broken_references/missing_scripts/import_health +
        /// P14 project_health/scene_structure_health/materials_shader_health/script_audit/animation_analysis).
        /// When a new code is added to a rule's <c>IssueCodes</c>, add a row here so the taxonomy-coverage
        /// test fails until the entry exists. Mirrors Unity's <c>_table</c>.
        /// </summary>
        private static readonly Dictionary<string, Entry> Table = new()
        {
            // -----------------------------------------------------------------
            // broken_references (P3.2)
            // -----------------------------------------------------------------
            ["broken_references|broken_scene_reference"] = new Entry(
                RootCauses.MissingUidReference,
                "The serialized reference (an [ext_resource] uid/path, or an ExtResource/SubResource id) does not resolve. Find the intended target and relink the reference, or remove it."),

            // -----------------------------------------------------------------
            // missing_scripts (P3.3)
            // -----------------------------------------------------------------
            ["missing_scripts|missing_script"] = new Entry(
                RootCauses.MissingScriptClass,
                "The node's script = ExtResource attachment could not be resolved — the script was deleted, moved, or its id was never declared. Re-add the correct script, or remove the missing-script attachment."),

            // -----------------------------------------------------------------
            // import_health (P3.4)
            // -----------------------------------------------------------------
            ["import_health|orphan_import"] = new Entry(
                RootCauses.OrphanedImport,
                "A .import sidecar's source file no longer exists on disk. Remove the orphaned sidecar, or restore the source asset."),
            ["import_health|duplicate_uid"] = new Entry(
                RootCauses.DuplicateUid,
                "Two or more .import sidecars share one uid, so Godot cannot deterministically resolve uid:// references to it. Regenerate the uid on the less-referenced sidecar — choose the target deliberately."),

            // -----------------------------------------------------------------
            // project_health (P14.1)
            // -----------------------------------------------------------------
            ["project_health|project_empty_folder"] = new Entry(
                RootCauses.StructuralComplexity,
                "An empty folder adds clutter without content. Delete it, or add the intended content."),
            ["project_health|project_uid_only_folder"] = new Entry(
                RootCauses.OrphanedImport,
                "A folder holds only .gd.uid sidecars — stale metadata left after the scripts moved away. Remove the orphaned sidecars, or restore the scripts."),
            ["project_health|project_deep_nesting"] = new Entry(
                RootCauses.StructuralComplexity,
                "Folder nesting exceeds the configured depth, making paths long and hard to navigate. Flatten the directory structure."),
            ["project_health|project_large_folder"] = new Entry(
                RootCauses.StructuralComplexity,
                "A folder holds more direct children than the configured budget, slowing the editor and obscuring structure. Split it into sub-folders."),
            ["project_health|project_broken_asset"] = new Entry(
                RootCauses.BuildBlocker,
                "The .tres/.tscn failed to parse (corrupted or unimportable). Reimport the asset, or restore it from version control."),
            ["project_health|project_empty_scene"] = new Entry(
                RootCauses.StructuralComplexity,
                "The scene has only a root node and is effectively empty. Populate it, or delete it if unused."),

            // -----------------------------------------------------------------
            // scene_structure_health (P14.2)
            // -----------------------------------------------------------------
            ["scene_structure_health|scene_deep_nesting"] = new Entry(
                RootCauses.StructuralComplexity,
                "A node is nested deeper than the configured depth, making the scene fragile and slow to edit. Flatten the hierarchy."),
            ["scene_structure_health|scene_high_node_count"] = new Entry(
                RootCauses.StructuralComplexity,
                "The scene exceeds the configured node budget. Reduce the node count, move static content into instanced scenes, or raise the budget deliberately."),
            ["scene_structure_health|scene_wide_sibling_list"] = new Entry(
                RootCauses.StructuralComplexity,
                "A parent has more children than the configured budget, hurting navigability. Group the children under intermediate parents."),
            ["scene_structure_health|scene_duplicate_node_name"] = new Entry(
                RootCauses.ConfigurationMismatch,
                "Two siblings share a name, which makes $NodePath / get_node lookups ambiguous. Rename one of the siblings so each is unique."),
            ["scene_structure_health|scene_empty_node_branch"] = new Entry(
                RootCauses.StructuralComplexity,
                "A non-root branch has no content (no script, instance, or concrete leaf) — leftover scaffolding. Remove it, or add the intended content."),

            // -----------------------------------------------------------------
            // materials_shader_health (P14.3)
            // -----------------------------------------------------------------
            ["materials_shader_health|materials_missing_shader"] = new Entry(
                RootCauses.ResourceMissing,
                "The ShaderMaterial's shader reference is dangling or resolves to nothing — it falls back to the pink error shader. Reassign a valid shader."),
            ["materials_shader_health|materials_builtin_shader_only"] = new Entry(
                RootCauses.ConfigurationMismatch,
                "The material uses only builtin defaults (no property overrides). Configure the material, or ignore if the default is intended."),
            ["materials_shader_health|materials_orphan_shader_include"] = new Entry(
                RootCauses.MissingDependency,
                "A .gdshader #include path does not resolve on disk. Fix the include path, or restore the included file. (A global-search-path-only include may be a false positive offline.)"),
            ["materials_shader_health|materials_duplicate_material"] = new Entry(
                RootCauses.StructuralComplexity,
                "Two or more .tres materials share an identical content fingerprint. Consolidate them into one shared material."),
            ["materials_shader_health|materials_unused_material"] = new Entry(
                RootCauses.StructuralComplexity,
                "The material is not referenced by any scene/resource/script in the scanned subtree. Delete it if truly unused."),

            // -----------------------------------------------------------------
            // script_audit (P14.4)
            // -----------------------------------------------------------------
            ["script_audit|script_class_mismatch"] = new Entry(
                RootCauses.ConfigurationMismatch,
                "The scene/resource header records a script_class that differs from the class the resolved script declares. Update the recorded script_class, or rename the script's class to match."),
            ["script_audit|script_missing_class_name"] = new Entry(
                RootCauses.ConfigurationMismatch,
                "An attached .gd declares no class_name, so it cannot be registered as a global type. Add a class_name if the script is meant to be a named type."),
            ["script_audit|script_cyclic_class_name"] = new Entry(
                RootCauses.ConfigurationMismatch,
                "Two .gd files declare the same class_name — Godot refuses to load one. Rename one of the classes so each is unique."),

            // -----------------------------------------------------------------
            // animation_analysis (P14.5)
            // -----------------------------------------------------------------
            ["animation_analysis|missing_clip"] = new Entry(
                RootCauses.ResourceMissing,
                "An AnimationPlayer references a clip resource that is missing. Reassign a valid Animation/AnimationLibrary, or remove the reference."),
            ["animation_analysis|empty_clip"] = new Entry(
                RootCauses.ConfigurationMismatch,
                "An Animation clip declares no tracks, so it animates nothing. Add tracks to the clip, or remove it if unused."),
            ["animation_analysis|unreachable_state"] = new Entry(
                RootCauses.StructuralComplexity,
                "An AnimationNodeStateMachine state has no inbound transition (and is not the entry state), so it can never be reached. Add a transition to it, or remove the state."),
            ["animation_analysis|parameter_mismatch"] = new Entry(
                RootCauses.ConfigurationMismatch,
                "A state-machine transition/condition references a parameter not declared in the blackboard. Re-add the parameter, or fix the condition."),
            ["animation_analysis|duplicate_clip"] = new Entry(
                RootCauses.StructuralComplexity,
                "Two or more clips have identical track data. Consolidate them into one shared clip."),
        };

        /// <summary>
        /// Look up the rootCause + remediation for a <c>(ruleId, issueCode)</c> pair. Returns false for
        /// null/empty inputs or an unknown pair — never throws. The canonical lookup the rules'
        /// <c>MakeIssue</c> helpers call to materialize explainability onto a <see cref="VerifyIssue"/>.
        /// </summary>
        public static bool TryGet(string? ruleId, string? issueCode, out Entry? entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(ruleId) || string.IsNullOrEmpty(issueCode)) return false;
            return Table.TryGetValue($"{ruleId}|{issueCode}", out entry);
        }
    }
}
