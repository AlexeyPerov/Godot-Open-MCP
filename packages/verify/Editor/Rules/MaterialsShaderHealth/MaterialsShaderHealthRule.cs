#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Verify.Rules.MaterialsShaderHealth
{
    /// <summary>
    /// Detects material + shader integrity problems: a <c>ShaderMaterial</c> whose <c>shader =</c> slot is
    /// dangling or unresolved (<c>missing_shader</c>), a <c>StandardMaterial3D</c>/<c>ORMMaterial3D</c>
    /// using only defaults (<c>builtin_shader_only</c>), a <c>.gdshader</c> whose <c>#include</c> does not
    /// resolve (<c>orphan_shader_include</c>), duplicate <c>.tres</c> materials
    /// (<c>duplicate_material</c>), and materials not referenced anywhere (<c>unused_material</c>).
    /// Adapted from Unity Open MCP's <c>Materials</c> + <c>ShaderAnalysis</c> rules — but the SRP-batcher /
    /// GPU-instancing / render-queue / material-variant signals are skipped (Godot's renderer differs; no
    /// twin in v1), and the <c>.gdshader</c> include resolution is greenfield. See
    /// <see cref="IssueCodes"/> for the fidelity breakdown.
    ///
    /// <para>
    /// <b>Relationship to sibling rules:</b> a broken <c>[ext_resource]</c> may also be flagged by
    /// <c>broken_references</c> as <c>broken_scene_reference</c>; the two rules surface it under different
    /// framings (generic broken ref vs. materials-specific shader slot) with different future fixes. A
    /// structurally broken <c>.tres</c> (no header) is <c>project_health</c>'s domain — this rule skips
    /// such a file and emits nothing rather than duplicating that finding.
    /// </para>
    ///
    /// <para>
    /// <b>Scope handling.</b> The scope paths may be:
    /// <list type="bullet">
    ///   <item><b>A directory</b> (e.g. <c>res://Materials</c>, <c>res://</c>): the rule walks the subtree
    ///     via <see cref="IMaterialsShaderResolver.ListDirectory"/> and analyzes every <c>.tres</c> material
    ///     and <c>.gdshader</c> it reaches.</item>
    ///   <item><b>A <c>.tres</c> material or <c>.gdshader</c></b>: the rule analyzes that one file.</item>
    ///   <item><b>A <c>.tscn</c> or other file</b>: not this rule's input. Skip.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Run-mode behavior.</b> Per-asset detections (<c>missing_shader</c>, <c>builtin_shader_only</c>,
    /// <c>orphan_shader_include</c>) run in every mode including <see cref="VerifyRunMode.Checkpoint"/>
    /// (cheap, one parse + a handful of resolver probes, no subtree enumeration) so a gated mutation on a
    /// single material still catches a regression it introduced. The cross-asset detections
    /// (<c>duplicate_material</c>, <c>unused_material</c>) need the full material set as context and (for
    /// unused) a project-wide reverse-edge scan, so they run only in <see cref="VerifyRunMode.Validate"/>/
    /// <see cref="VerifyRunMode.Full"/>. Mirrors Unity's <c>fullScan</c> split.
    /// </para>
    ///
    /// <para>
    /// <b>File reading:</b> the rule reads file text through a seam (<see cref="ReadFileText"/>) so tests
    /// inject fixture content without touching disk. Production reads via <c>File.ReadAllText</c>.
    /// </para>
    /// </summary>
    public sealed class MaterialsShaderHealthRule : IVerifyRule
    {
        /// <summary>The stable rule id surfaced in MCP responses, the capability catalog, and the gate delta.</summary>
        public const string RuleId = "materials_shader_health";

        /// <inheritdoc />
        public string Id => RuleId;

        // ---- Resource-type classification -------------------------------------

        /// <summary>Resource types whose body is a fixed builtin-shader material (the builtin-shader-only signal).</summary>
        private static readonly HashSet<string> BuiltinMaterialTypes = new(StringComparer.Ordinal)
        {
            "StandardMaterial3D",
            "ORMMaterial3D",
            "StandardMaterial",
            // OrthogonalMaterial/SpatialMaterial are the 3.x names; included for forward-compat.
            "SpatialMaterial",
        };

        /// <summary>
        /// Material-derived resource types this rule analyzes. A <c>.tres</c> whose serialized
        /// <c>[gd_resource type=]</c> is not in this set is some other Resource (a custom data object, a
        /// tileset, etc.) — not this rule's input. Godot materials subclass <c>Material</c>; the concrete
        /// shipped types are <c>StandardMaterial3D</c>, <c>ORMMaterial3D</c>, <c>ShaderMaterial</c> (and
        /// their 3.x <c>SpatialMaterial</c> name). Custom <c>Material</c> subclasses authored in C#/GDScript
        /// are rare and would serialize under their own type string — v1 keys on the shipped concrete types
        /// (a custom subclass lands as "not analyzed" rather than a false positive, which is the safer
        /// default). A bare <c>type="Material"</c> (the abstract base) is included for completeness.
        /// </summary>
        private static readonly HashSet<string> MaterialTypes = new(StringComparer.Ordinal)
        {
            "Material",
            "StandardMaterial3D",
            "ORMMaterial3D",
            "ShaderMaterial",
            "StandardMaterial",
            "SpatialMaterial",
        };

        private readonly IMaterialsShaderResolver _resolver;
        private readonly Func<string, string?> _readFileText;

        /// <summary>
        /// Production constructor: lists directories + resolves references through
        /// <see cref="LiveMaterialsShaderResolver"/> and reads files from disk via
        /// <see cref="File.ReadAllText"/>. Used by <see cref="Core.VerifyRunner.RegisterDefaults"/>.
        /// </summary>
        public MaterialsShaderHealthRule() : this(GetLiveResolver(), File.ReadAllText) { }

        /// <summary>
        /// Testable constructor: inject the resolver and file reader. Both are pure seams — no Godot API
        /// surface — so the rule compiles and runs in the binary-less xUnit host.
        /// </summary>
        internal MaterialsShaderHealthRule(IMaterialsShaderResolver resolver, Func<string, string?> readFileText)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _readFileText = readFileText ?? throw new ArgumentNullException(nameof(readFileText));
        }

        /// <inheritdoc />
        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            // Cross-asset detections (duplicate, unused) need the full material set + a project-wide
            // reverse-edge scan — Full/Validate only. Per-asset detections run in every mode.
            var fullScan = mode != VerifyRunMode.Checkpoint;

            // De-dupe scoped directory roots so a scope naming "res://" AND "res://Materials" does not walk
            // Materials twice. Identical structure to the sibling P14 rules.
            var walkedDirs = new HashSet<string>(StringComparer.Ordinal);
            // De-dupe analyzed files so a material reached through two overlapping scoped roots (e.g.
            // "res://" AND "res://Materials") is analyzed once, not twice. The sibling P14 rules de-dupe
            // directories but not files; materials-shader adds file de-dup because the cross-asset passes
            // (duplicate/unused) would otherwise see a phantom dup group / double-issue the same finding.
            var analyzedFiles = new HashSet<string>(StringComparer.Ordinal);

            // Collected materials for the cross-asset passes (Full mode).
            var materials = new List<(string ResPath, MaterialData Data)>();

            foreach (var resPath in scope.Paths)
            {
                if (string.IsNullOrEmpty(resPath)) continue;

                if (IsMaterialPath(resPath))
                {
                    if (!analyzedFiles.Add(resPath)) continue;
                    var data = ScanMaterial(resPath, sink);
                    if (fullScan && data != null) materials.Add((resPath, data));
                    continue;
                }

                if (IsShaderPath(resPath))
                {
                    if (!analyzedFiles.Add(resPath)) continue;
                    ScanShader(resPath, sink);
                    continue;
                }

                if (!fullScan) continue;

                if (IsLikelyDirectory(resPath))
                {
                    if (!walkedDirs.Add(resPath)) continue;
                    WalkDirectory(resPath, sink, walkedDirs, analyzedFiles, fullScan, materials);
                }
                // else: a non-material/shader file (.tscn, .gd, .png) — not this rule's input. Skip.
            }

            if (fullScan)
            {
                if (materials.Count > 0) DetectDuplicateMaterials(materials, sink);
                if (materials.Count > 0) DetectUnusedMaterials(materials, sink);
            }
        }

        // ---- Directory walk (reaches every .tres material + .gdshader) --------

        private void WalkDirectory(string dirRes, List<VerifyIssue> sink, HashSet<string> walkedDirs,
            HashSet<string> analyzedFiles, bool fullScan, List<(string ResPath, MaterialData Data)> materials)
        {
            walkedDirs.Add(dirRes);

            IReadOnlyList<MaterialsFolderEntry> entries;
            try { entries = _resolver.ListDirectory(dirRes); }
            catch
            {
                // A throwing resolver simulates a permissions failure or a vanished directory. Contribute
                // no issues rather than crash the scan.
                return;
            }

            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (!walkedDirs.Add(entry.ResPath)) continue; // already walked under another root
                    WalkDirectory(entry.ResPath, sink, walkedDirs, analyzedFiles, fullScan, materials);
                }
                else if (IsMaterialPath(entry.ResPath))
                {
                    if (!analyzedFiles.Add(entry.ResPath)) continue;
                    var data = ScanMaterial(entry.ResPath, sink);
                    if (fullScan && data != null) materials.Add((entry.ResPath, data));
                }
                else if (IsShaderPath(entry.ResPath))
                {
                    if (!analyzedFiles.Add(entry.ResPath)) continue;
                    ScanShader(entry.ResPath, sink);
                }
            }
        }

        // ---- Per-asset: material ----------------------------------------------

        /// <summary>
        /// Parse one <c>.tres</c> material and emit the per-asset findings (<c>missing_shader</c>,
        /// <c>builtin_shader_only</c>). Returns the parsed data for the Full-mode cross-asset passes, or
        /// null when the file was skipped (not a material / unreadable). Never throws.
        /// </summary>
        private MaterialData? ScanMaterial(string resPath, List<VerifyIssue> sink)
        {
            string text;
            try
            {
                var raw = _readFileText(resPath);
                if (raw == null) return null;
                text = raw;
            }
            catch
            {
                // File vanished between checkpoint and validate, or unreadable. Contribute no issues.
                return null;
            }

            MaterialsShaderAnalysis analysis;
            try { analysis = MaterialsShaderParser.ParseMaterial(text); }
            catch { return null; }

            if (analysis.Skipped || analysis.Material == null) return null;

            var data = analysis.Material;

            // Only Material-derived types are this rule's input. A .tres whose serialized type is a custom
            // Resource, a TileSet, etc. is not a material — skip it (no overlap with sibling rules).
            if (!MaterialTypes.Contains(data.ResourceType)) return null;

            // missing_shader — a ShaderMaterial whose shader slot is dangling OR whose resolved target
            // does not exist on disk / in the uid table. Godot prefers uid over path; the slot resolves
            // when EITHER the declared path exists OR the declared uid is registered (the normal state
            // after Godot relocates an asset is a stale path + a live uid — mirrors BrokenReferences).
            if (data.ResourceType == "ShaderMaterial")
            {
                if (data.ShaderUsageId != null)
                {
                    var resolved = ResolveShaderTarget(data.ShaderTarget, data.ShaderUid);
                    data.ShaderTargetResolved = resolved;

                    if (!resolved || data.ShaderUsageDangling)
                    {
                        var detail = data.ShaderUsageDangling
                            ? $"shader = ExtResource(\"{data.ShaderUsageId}\") was never declared (no matching [ext_resource])"
                            : $"shader = ExtResource(\"{data.ShaderUsageId}\") → \"{DescribeShaderTarget(data.ShaderTarget, data.ShaderUid)}\" could not be resolved";
                        sink.Add(MakeIssue(resPath, VerifySeverity.Error, IssueCodes.MissingShader,
                            detail, BuildMaterialEvidence(EvidenceKinds.MissingShader, resPath,
                                resourceType: data.ResourceType, shaderId: data.ShaderUsageId,
                                shaderTarget: DescribeShaderTarget(data.ShaderTarget, data.ShaderUid),
                                dangling: data.ShaderUsageDangling)));
                    }
                }
            }

            // builtin_shader_only — a StandardMaterial3D/ORMMaterial3D with no overrides. Informational.
            if (BuiltinMaterialTypes.Contains(data.ResourceType) && !data.HasOverrides)
            {
                sink.Add(MakeIssue(resPath, VerifySeverity.Warning, IssueCodes.BuiltinShaderOnly,
                    $"{data.ResourceType} \"{resPath}\" uses only builtin defaults (no property overrides)",
                    BuildMaterialEvidence(EvidenceKinds.BuiltinShaderOnly, resPath, resourceType: data.ResourceType)));
            }

            return data;
        }

        /// <summary>
        /// Resolve a shader slot's declared targets through the resolver. A dangling usage id (no declared
        /// ext_resource) is treated as unresolved upstream. The slot resolves when EITHER the declared
        /// <c>res://</c> path exists OR the declared <c>uid://</c> is registered (Godot prefers uid; either
        /// is authoritative — the normal state after Godot relocates an asset is a stale path + a live uid).
        /// An empty declaration (neither path nor uid) is unresolved.
        /// </summary>
        private bool ResolveShaderTarget(string? shaderPath, string? shaderUid)
        {
            var pathOk = !string.IsNullOrEmpty(shaderPath)
                && shaderPath!.StartsWith("res://", StringComparison.Ordinal)
                && _resolver.PathExists(shaderPath);
            if (pathOk) return true;

            var uidOk = !string.IsNullOrEmpty(shaderUid)
                && shaderUid!.StartsWith("uid://", StringComparison.Ordinal)
                && _resolver.UidExists(shaderUid);
            return uidOk;
        }

        /// <summary>
        /// Render the shader target for the issue description/evidence. Prefers the path (more readable);
        /// falls back to the uid when only that was declared. Returns <c>"&lt;undeclared&gt;"</c> for an
        /// empty declaration (the rule still flags it).
        /// </summary>
        private static string DescribeShaderTarget(string? shaderPath, string? shaderUid)
        {
            if (!string.IsNullOrEmpty(shaderPath)) return shaderPath!;
            if (!string.IsNullOrEmpty(shaderUid)) return shaderUid!;
            return "<undeclared>";
        }

        // ---- Per-asset: .gdshader ---------------------------------------------

        /// <summary>
        /// Parse one <c>.gdshader</c> and emit <c>orphan_shader_include</c> for each unresolved
        /// <c>#include</c>. Never throws.
        /// </summary>
        private void ScanShader(string resPath, List<VerifyIssue> sink)
        {
            string text;
            try
            {
                var raw = _readFileText(resPath);
                if (raw == null) return;
                text = raw;
            }
            catch { return; }

            MaterialsShaderAnalysis analysis;
            try { analysis = MaterialsShaderParser.ParseShader(text); }
            catch { return; }

            if (analysis.Skipped || analysis.Shader == null) return;

            foreach (var include in analysis.Shader.Includes)
            {
                if (ResolveShaderInclude(include.Target, resPath)) continue;

                sink.Add(MakeIssue(resPath, VerifySeverity.Warning, IssueCodes.OrphanShaderInclude,
                    $"#include \"{include.Target}\" could not be resolved",
                    BuildShaderEvidence(EvidenceKinds.OrphanShaderInclude, resPath,
                        includeTarget: include.Target)));
            }
        }

        /// <summary>
        /// Resolve a <c>.gdshader</c> <c>#include</c> target. Godot 4.x resolves <c>res://</c>-rooted
        /// includes against the project, and bare/relative includes against the shader file's directory
        /// (or the global shader-include search path, which offline cannot query). Resolution basis:
        /// <list type="bullet">
        ///   <item><c>res://...</c> — <see cref="IMaterialsShaderResolver.PathExists"/>.</item>
        ///   <item><c>uid://...</c> — <see cref="IMaterialsShaderResolver.UidExists"/>.</item>
        ///   <item>relative/bare — resolved against the shader's directory (and its parent dir, the
        ///     common Godot layout); if it does not exist there, it is flagged (the global include search
        ///     path may still resolve it — hence Warning, not Error).</item>
        /// </list>
        /// </summary>
        private bool ResolveShaderInclude(string target, string shaderResPath)
        {
            if (string.IsNullOrEmpty(target)) return false;

            if (target.StartsWith("res://", StringComparison.Ordinal))
                return _resolver.PathExists(target);
            if (target.StartsWith("uid://", StringComparison.Ordinal))
                return _resolver.UidExists(target);

            // Relative include: resolve against the shader's directory first, then one level up (Godot's
            // common "shaders/ + shaders/includes/" layout). A bare filename like "common.gdshaderinc"
            // resolves against the same folder.
            var shaderDir = ParentDirectory(shaderResPath);
            if (shaderDir != null)
            {
                var candidate = shaderDir + target;
                if (_resolver.PathExists(candidate)) return true;
                var parentDir = ParentDirectory(shaderDir.TrimEnd('/'));
                if (parentDir != null)
                {
                    var parentCandidate = parentDir + target;
                    if (_resolver.PathExists(parentCandidate)) return true;
                }
            }
            return false;
        }

        /// <summary>Return the directory portion of a <c>res://</c> path, with a trailing slash, or null for a root-level file.</summary>
        private static string? ParentDirectory(string resPath)
        {
            var trimmed = resPath.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            if (slash < "res://".Length - 1) return null;
            return trimmed.Substring(0, slash + 1);
        }

        // ---- Cross-asset: duplicate materials (Full only) ---------------------

        /// <summary>
        /// Group materials by fingerprint and emit <c>duplicate_material</c> for every member of a group
        /// with ≥ 2. Each member reports its sibling paths so an agent can dedupe. Deterministic order:
        /// groups sorted by fingerprint, members within a group sorted by path.
        /// </summary>
        private static void DetectDuplicateMaterials(
            List<(string ResPath, MaterialData Data)> materials, List<VerifyIssue> sink)
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (resPath, data) in materials)
            {
                if (string.IsNullOrEmpty(data.Fingerprint)) continue;
                if (!groups.TryGetValue(data.Fingerprint, out var list))
                {
                    list = new List<string>();
                    groups[data.Fingerprint] = list;
                }
                list.Add(resPath);
            }

            // Sort group keys for deterministic emission.
            var keys = new List<string>(groups.Keys);
            keys.Sort(StringComparer.Ordinal);

            foreach (var key in keys)
            {
                var group = groups[key];
                group.Sort(StringComparer.Ordinal);
                if (group.Count < 2) continue;

                foreach (var path in group)
                {
                    var siblings = new List<string>(group.Count - 1);
                    foreach (var s in group) if (s != path) siblings.Add(s);
                    sink.Add(MakeIssue(path, VerifySeverity.Warning, IssueCodes.DuplicateMaterial,
                        $"duplicate of {siblings.Count} material(s): {string.Join(", ", siblings)}",
                        BuildMaterialEvidence(EvidenceKinds.DuplicateMaterial, path,
                            duplicateCount: group.Count.ToString(), siblings: string.Join(", ", siblings))));
                }
            }
        }

        // ---- Cross-asset: unused materials (Full only) ------------------------

        /// <summary>
        /// For each material, probe the reverse-edge scan for its <c>res://</c> path (and, when known, its
        /// <c>uid://</c> token). A material referenced by neither is <c>unused_material</c>. The scan is
        /// expensive (reads every reference-bearing file under res://), which is why this is Full-only.
        /// </summary>
        private void DetectUnusedMaterials(
            List<(string ResPath, MaterialData Data)> materials, List<VerifyIssue> sink)
        {
            foreach (var (resPath, data) in materials)
            {
                // Probe by path first; if not referenced by path, probe by uid (a referencer may use only
                // the uid token). Either hit means the material is used.
                var byPath = _resolver.IsReferenced(resPath, resPath);
                var used = byPath;
                if (!used && !string.IsNullOrEmpty(data.OwnUid))
                    used = _resolver.IsReferenced(data.OwnUid, resPath);
                if (used) continue;

                sink.Add(MakeIssue(resPath, VerifySeverity.Warning, IssueCodes.UnusedMaterial,
                    $"material \"{resPath}\" is not referenced by any scene/resource/script in the scanned subtree",
                    BuildMaterialEvidence(EvidenceKinds.UnusedMaterial, resPath, uid: data.OwnUid)));
            }
        }

        // ---- Issue construction ------------------------------------------------

        private static VerifyIssue MakeIssue(
            string assetPath, VerifySeverity severity, string issueCode, string description,
            IReadOnlyDictionary<string, string> evidence)
            => new VerifyIssue(RuleId, severity, assetPath, issueCode, description, evidence);

        private static IReadOnlyDictionary<string, string> BuildMaterialEvidence(
            string kind, string assetPath, string? resourceType = null, string? shaderId = null,
            string? shaderTarget = null, bool? dangling = null, string? duplicateCount = null,
            string? siblings = null, string? uid = null)
        {
            var ev = new Dictionary<string, string> { ["kind"] = kind, ["assetPath"] = assetPath };
            if (resourceType != null) ev["resourceType"] = resourceType;
            if (shaderId != null) ev["shaderId"] = shaderId;
            if (shaderTarget != null) ev["shaderTarget"] = shaderTarget;
            if (dangling.HasValue) ev["dangling"] = dangling.Value ? "true" : "false";
            if (duplicateCount != null) ev["duplicateCount"] = duplicateCount;
            if (siblings != null) ev["siblings"] = siblings;
            if (uid != null) ev["uid"] = uid;
            return ev;
        }

        private static IReadOnlyDictionary<string, string> BuildShaderEvidence(
            string kind, string assetPath, string? includeTarget = null)
        {
            var ev = new Dictionary<string, string> { ["kind"] = kind, ["assetPath"] = assetPath };
            if (includeTarget != null) ev["includeTarget"] = includeTarget;
            return ev;
        }

        // ---- Path helpers (mirror the sibling P14 rules) ----------------------

        private static bool IsMaterialPath(string resPath)
            => resPath.EndsWith(".tres", StringComparison.OrdinalIgnoreCase);

        private static bool IsShaderPath(string resPath)
            => resPath.EndsWith(".gdshader", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a <c>res://</c> path looks like a directory rather than a file — same heuristic as the
        /// sibling P14 rules: the leaf segment (after the last <c>/</c>) has no <c>.</c> extension.
        /// </summary>
        private static bool IsLikelyDirectory(string resPath)
        {
            var trimmed = resPath.TrimEnd('/');
            var lastSlash = trimmed.LastIndexOf('/');
            var leaf = lastSlash >= 0 ? trimmed.Substring(lastSlash + 1) : trimmed;
            return !leaf.Contains('.');
        }

        // ---- Live resolver wiring (mirrors the sibling P14 rules) -------------

        private static IMaterialsShaderResolver GetLiveResolver()
        {
#if TOOLS
            return LiveMaterialsShaderResolver.Instance;
#else
            return new NullResolver();
#endif
        }

#if !TOOLS
        /// <summary>
        /// Fallback resolver for the non-TOOLS compile path (binary-less test host). Tests never reach it
        /// — they inject through the internal constructor — but the parameterless constructor must still
        /// compile. Mirrors the sibling rules' NullResolver.
        /// </summary>
        private sealed class NullResolver : IMaterialsShaderResolver
        {
            public IReadOnlyList<MaterialsFolderEntry> ListDirectory(string? resDir)
                => Array.Empty<MaterialsFolderEntry>();
            public bool PathExists(string? resPath) => true;
            public bool UidExists(string? uid) => true;
            public bool IsReferenced(string? resPathOrUid, string? ownerResPath) => false;
        }
#endif
    }

    /// <summary>
    /// Values placed in <c>Evidence["kind"]</c> to distinguish materials/shader failure modes. Kept
    /// internal because the stable surface is the issue codes in <see cref="IssueCodes"/>.
    /// </summary>
    internal static class EvidenceKinds
    {
        public const string MissingShader = "missing_shader";
        public const string BuiltinShaderOnly = "builtin_shader_only";
        public const string OrphanShaderInclude = "orphan_shader_include";
        public const string DuplicateMaterial = "duplicate_material";
        public const string UnusedMaterial = "unused_material";
    }
}
