#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Editor;
using GodotOpenMcp.Verify.Rules.MaterialsShaderHealth;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Rules.MaterialsShaderHealth
{
    /// <summary>
    /// P14.3 tests for the materials-shader verify rule (missing shader, builtin-shader-only, orphan
    /// shader include, duplicate material, unused material). Adapted fidelity: Unity's <c>Materials</c> +
    /// <c>ShaderAnalysis</c> rules are the structural reference for the missing-shader / builtin /
    /// duplicate / unused detection patterns (Unity loads materials/shaders via <c>AssetDatabase</c>;
    /// Godot's <c>.tres</c>/<c>.gdshader</c> are text-serialized and parseable offline). The
    /// <c>.gdshader</c> include resolution is greenfield for Godot; SRP-batcher / GPU-instancing /
    /// render-queue / material-variant signals are skipped (no Godot twin in v1). The test structure
    /// mirrors the sibling P3.2–P3.4 + P14.1/P14.2 rule tests (valid fixture → no issues; broken fixture →
    /// expected issues; false-positive guards; run-mode split; robustness; scope filtering; gate-delta
    /// stability). The rule is exercised through its internal constructor with an
    /// <see cref="InMemoryResolver"/> + an in-memory file map, so no Godot API and no disk access — the
    /// binary-less xUnit host runs the full rule.
    /// </summary>
    // Shares the VerifyRunner static-registry collection with VerifyRunnerTests so the registration
    // tests below do not race with the Core runner tests under xUnit's default parallel execution.
    [Collection("VerifyRunnerCollection")]
    public class MaterialsShaderHealthRuleTests
    {
        // ---- Material fixtures (.tres) ---------------------------------------
        //
        // Minimal Godot .tres text exercising each material shape. The parser inspects the
        // [gd_resource type=] header, the [ext_resource id= path= uid=] declarations, and the body
        // `shader = ExtResource("id")` slot + property overrides, so these are the smallest valid
        // examples for each case.

        private const string HealthyShaderMaterial = @"[gd_resource type=""ShaderMaterial"" load_steps=3 format=3]

[ext_resource type=""Shader"" path=""res://Shaders/Valid.gdshader"" id=""1_shad""]

[resource]
shader = ExtResource(""1_shad"")
shader_parameter/color = Color(1, 0, 0, 1)
";

        private const string MissingShaderDanglingId = @"[gd_resource type=""ShaderMaterial"" load_steps=2 format=3]

[ext_resource type=""Texture2D"" path=""res://Icon.png"" id=""1_tex""]

[resource]
shader = ExtResource(""9_gone"")
";

        private const string MissingShaderUnresolvedPath = @"[gd_resource type=""ShaderMaterial"" load_steps=2 format=3]

[ext_resource type=""Shader"" path=""res://Shaders/Deleted.gdshader"" id=""1_shad""]

[resource]
shader = ExtResource(""1_shad"")
";

        private const string MissingShaderUnresolvedUid = @"[gd_resource type=""ShaderMaterial"" load_steps=2 format=3]

[ext_resource type=""Shader"" uid=""uid://bbbbbbbbbbbb"" path=""res://Shaders/Deleted.gdshader"" id=""1_shad""]

[resource]
shader = ExtResource(""1_shad"")
";

        private const string BuiltinShaderOnlyStandard = @"[gd_resource type=""StandardMaterial3D"" load_steps=1 format=3]

[resource]
resource_name = ""BuiltinDefault""
";

        private const string BuiltinShaderOnlyOrm = @"[gd_resource type=""ORMMaterial3D"" load_steps=1 format=3]

[resource]
";

        private const string ConfiguredStandardMaterial = @"[gd_resource type=""StandardMaterial3D"" load_steps=1 format=3]

[resource]
resource_name = ""Configured""
albedo_color = Color(0.1, 0.2, 0.3, 1)
metallic = 0.5
";

        // Two .tres materials with identical property sets → duplicates. Differ only in resource_name
        // (identity, excluded from the fingerprint) and the asset path.
        private const string DuplicateA = @"[gd_resource type=""StandardMaterial3D"" load_steps=1 format=3]

[resource]
resource_name = ""WoodA""
albedo_color = Color(0.5, 0.3, 0.1, 1)
metallic = 0.2
";

        private const string DuplicateB = @"[gd_resource type=""StandardMaterial3D"" load_steps=1 format=3]

[resource]
resource_name = ""WoodB""
albedo_color = Color(0.5, 0.3, 0.1, 1)
metallic = 0.2
";

        // A ShaderMaterial whose shader resolves — healthy. Used as a non-builtin control.
        private const string HealthyShaderMaterialNoOverrides = @"[gd_resource type=""ShaderMaterial"" load_steps=3 format=3]

[ext_resource type=""Shader"" path=""res://Shaders/Plain.gdshader"" id=""1_shad""]

[resource]
shader = ExtResource(""1_shad"")
";

        // A non-material .tres (a custom Resource, not a Material) — this rule must SKIP it.
        private const string NonMaterialResource = @"[gd_resource type=""DemoData"" load_steps=1 format=3]

[resource]
value = 42
";

        // A header-less / broken file — this rule must SKIP it (project_health's domain).
        private const string HeaderlessTres = @"resource_name = ""Broken""
albedo_color = Color(1, 0, 0, 1)
";

        // ---- Shader fixtures (.gdshader) -------------------------------------

        private const string HealthyShaderWithInclude = @"shader_type spatial;
#include ""res://Shaders/common.gdshaderinc""

void fragment() { }
";

        private const string ShaderWithUnresolvedRelativeInclude = @"shader_type spatial;
#include ""missing.gdshaderinc""

void fragment() { }
";

        private const string ShaderWithNoIncludes = @"shader_type spatial;

void fragment() { }
";

        private const string ShaderWithCommentedInclude = @"shader_type spatial;
// #include ""res://Shaders/ignored.gdshaderinc""
void fragment() { }
";

        // ---- In-memory resolver ----------------------------------------------

        /// <summary>
        /// In-memory resolver modeling a directory tree + reference resolution. Mirrors the sibling rules'
        /// InMemoryResolver, plus a path/uid existence set and a referenced-target set (for unused_material).
        /// <see cref="ExistingPaths"/> is the set of <c>res://</c> paths that exist; <see cref="ExistingUids"/>
        /// the registered uids; <see cref="ReferencedTargets"/> the targets the reverse-edge scan reports
        /// as referenced.
        /// </summary>
        private sealed class InMemoryResolver : IMaterialsShaderResolver
        {
            public Dictionary<string, List<string>> Directories { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
            public HashSet<string> ExistingPaths { get; } = new(StringComparer.Ordinal);
            public HashSet<string> ExistingUids { get; } = new(StringComparer.Ordinal);
            public HashSet<string> ReferencedTargets { get; } = new(StringComparer.Ordinal);

            public IReadOnlyList<MaterialsFolderEntry> ListDirectory(string? resDir)
            {
                var key = NormalizeDir(resDir);
                if (key == null || !Directories.TryGetValue(key, out var children))
                    return Array.Empty<MaterialsFolderEntry>();

                var dirs = new List<MaterialsFolderEntry>();
                var files = new List<MaterialsFolderEntry>();
                foreach (var name in children)
                {
                    var childRes = key + name;
                    var childDirKey = childRes + "/";
                    if (Directories.ContainsKey(childDirKey))
                        dirs.Add(new MaterialsFolderEntry(childDirKey, name, isDirectory: true));
                    else
                        files.Add(new MaterialsFolderEntry(childRes, name, isDirectory: false));
                }
                dirs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                var combined = new List<MaterialsFolderEntry>(dirs.Count + files.Count);
                combined.AddRange(dirs);
                combined.AddRange(files);
                return combined;
            }

            public bool PathExists(string? resPath)
                => resPath != null && ExistingPaths.Contains(resPath);

            public bool UidExists(string? uid)
                => uid != null && ExistingUids.Contains(uid);

            public bool IsReferenced(string? resPathOrUid, string? ownerResPath)
                => resPathOrUid != null && ReferencedTargets.Contains(resPathOrUid);
        }

        private static string? NormalizeDir(string? resDir)
        {
            if (string.IsNullOrEmpty(resDir)) return "res://";
            var d = resDir!;
            if (!d.StartsWith("res://", StringComparison.Ordinal)) return null;
            if (!d.EndsWith("/")) d += "/";
            return d;
        }

        private static (MaterialsShaderHealthRule rule, InMemoryResolver resolver) BuildRule(
            Dictionary<string, List<string>>? directories = null,
            Dictionary<string, string>? files = null,
            HashSet<string>? existingPaths = null,
            HashSet<string>? existingUids = null,
            HashSet<string>? referencedTargets = null)
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
            if (referencedTargets != null)
                foreach (var t in referencedTargets) resolver.ReferencedTargets.Add(t);
            string? Reader(string p) => resolver.Files.TryGetValue(p, out var t) ? t : null;
            return (new MaterialsShaderHealthRule(resolver, Reader), resolver);
        }

        private static List<VerifyIssue> RunScan(MaterialsShaderHealthRule rule, string resPath,
            VerifyRunMode mode = VerifyRunMode.Full)
        {
            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(new[] { resPath }), mode, sink);
            return sink;
        }

        private static List<VerifyIssue> RunScan(MaterialsShaderHealthRule rule, string[] resPaths,
            VerifyRunMode mode = VerifyRunMode.Full)
        {
            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(resPaths), mode, sink);
            return sink;
        }

        // =====================================================================
        // missing_shader
        // =====================================================================

        [Fact]
        public void Scan_HealthyShaderMaterial_EmitsNoIssues()
        {
            // Mark referenced so unused_material does not fire (it is Full-mode); the healthy material
            // otherwise has no issues.
            var (rule, _) = BuildRule(
                files: new() { ["res://M.tres"] = HealthyShaderMaterial },
                existingPaths: new() { "res://Shaders/Valid.gdshader" },
                referencedTargets: new() { "res://M.tres" });

            var issues = RunScan(rule, "res://M.tres");

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_DanglingShaderId_EmitsMissingShaderError()
        {
            var (rule, _) = BuildRule(files: new() { ["res://M.tres"] = MissingShaderDanglingId });

            var issue = Assert.Single(RunScan(rule, "res://M.tres")
                .Where(i => i.IssueCode == "materials_missing_shader"));

            Assert.Equal(VerifySeverity.Error, issue.Severity);
            Assert.Equal("res://M.tres", issue.AssetPath);
            Assert.Equal("missing_shader", issue.Evidence!["kind"]);
            Assert.Equal("ShaderMaterial", issue.Evidence!["resourceType"]);
            Assert.Equal("9_gone", issue.Evidence!["shaderId"]);
            Assert.Equal("true", issue.Evidence!["dangling"]);
            Assert.Contains("never declared", issue.Description);
        }

        [Fact]
        public void Scan_UnresolvedShaderPath_EmitsMissingShaderError()
        {
            // The shader's res:// path does not exist and no uid is registered → unresolved.
            var (rule, _) = BuildRule(files: new() { ["res://M.tres"] = MissingShaderUnresolvedPath });

            var issue = Assert.Single(RunScan(rule, "res://M.tres")
                .Where(i => i.IssueCode == "materials_missing_shader"));

            Assert.Equal(VerifySeverity.Error, issue.Severity);
            Assert.Equal("false", issue.Evidence!["dangling"]);
            Assert.Equal("res://Shaders/Deleted.gdshader", issue.Evidence!["shaderTarget"]);
            Assert.Contains("could not be resolved", issue.Description);
        }

        [Fact]
        public void Scan_ResolvedByUid_NotMissingShader()
        {
            // Path is missing but the uid is registered → Godot resolves by uid → NOT missing.
            var (rule, _) = BuildRule(
                files: new() { ["res://M.tres"] = MissingShaderUnresolvedUid },
                existingUids: new() { "uid://bbbbbbbbbbbb" });

            var missing = RunScan(rule, "res://M.tres")
                .Where(i => i.IssueCode == "materials_missing_shader");

            Assert.Empty(missing);
        }

        [Fact]
        public void Scan_ResolvedByPath_NotMissingShader()
        {
            // The shader's res:// path exists → NOT missing.
            var (rule, _) = BuildRule(
                files: new() { ["res://M.tres"] = MissingShaderUnresolvedPath },
                existingPaths: new() { "res://Shaders/Deleted.gdshader" });

            var missing = RunScan(rule, "res://M.tres")
                .Where(i => i.IssueCode == "materials_missing_shader");

            Assert.Empty(missing);
        }

        [Fact]
        public void Scan_ShaderMaterialWithoutShaderSlot_NotMissingShader()
        {
            // A ShaderMaterial with no shader = line at all is odd but not a missing_shader signal here
            // (there is no ExtResource to resolve). The rule should not crash and should not flag it.
            const string noSlot = "[gd_resource type=\"ShaderMaterial\" load_steps=1 format=3]\n\n[resource]\nshader_parameter/color = Color(1,0,0,1)\n";
            var (rule, _) = BuildRule(files: new() { ["res://M.tres"] = noSlot });

            var missing = RunScan(rule, "res://M.tres")
                .Where(i => i.IssueCode == "materials_missing_shader");

            Assert.Empty(missing);
        }

        // =====================================================================
        // builtin_shader_only
        // =====================================================================

        [Fact]
        public void Scan_BuiltinStandardMaterial_EmitsBuiltinShaderOnlyWarning()
        {
            var (rule, _) = BuildRule(files: new() { ["res://M.tres"] = BuiltinShaderOnlyStandard });

            var issue = Assert.Single(RunScan(rule, "res://M.tres")
                .Where(i => i.IssueCode == "materials_builtin_shader_only"));

            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("builtin_shader_only", issue.Evidence!["kind"]);
            Assert.Equal("StandardMaterial3D", issue.Evidence!["resourceType"]);
        }

        [Fact]
        public void Scan_BuiltinOrmMaterial_EmitsBuiltinShaderOnlyWarning()
        {
            var (rule, _) = BuildRule(files: new() { ["res://M.tres"] = BuiltinShaderOnlyOrm });

            var issue = Assert.Single(RunScan(rule, "res://M.tres")
                .Where(i => i.IssueCode == "materials_builtin_shader_only"));

            Assert.Equal("ORMMaterial3D", issue.Evidence!["resourceType"]);
        }

        [Fact]
        public void Scan_ConfiguredStandardMaterial_NotBuiltinShaderOnly()
        {
            var (rule, _) = BuildRule(files: new() { ["res://M.tres"] = ConfiguredStandardMaterial });

            var builtin = RunScan(rule, "res://M.tres")
                .Where(i => i.IssueCode == "materials_builtin_shader_only");

            Assert.Empty(builtin);
        }

        [Fact]
        public void Scan_ResourceNameOnly_IsNotCountedAsOverride()
        {
            // A StandardMaterial3D with ONLY resource_name (no real props) is still builtin-only.
            const string nameOnly = "[gd_resource type=\"StandardMaterial3D\" load_steps=1 format=3]\n\n[resource]\nresource_name = \"JustAName\"\n";
            var (rule, _) = BuildRule(files: new() { ["res://M.tres"] = nameOnly });

            var issue = Assert.Single(RunScan(rule, "res://M.tres")
                .Where(i => i.IssueCode == "materials_builtin_shader_only"));
            Assert.Equal("StandardMaterial3D", issue.Evidence!["resourceType"]);
        }

        [Fact]
        public void Scan_ShaderMaterial_NotBuiltinShaderOnly()
        {
            // A ShaderMaterial is never builtin-only (builtin signal is for StandardMaterial3D/ORMMaterial3D).
            var (rule, _) = BuildRule(
                files: new() { ["res://M.tres"] = HealthyShaderMaterialNoOverrides },
                existingPaths: new() { "res://Shaders/Plain.gdshader" });

            var builtin = RunScan(rule, "res://M.tres")
                .Where(i => i.IssueCode == "materials_builtin_shader_only");

            Assert.Empty(builtin);
        }

        // =====================================================================
        // orphan_shader_include
        // =====================================================================

        [Fact]
        public void Scan_ResolvedResInclude_NotOrphan()
        {
            var (rule, _) = BuildRule(
                files: new() { ["res://S.gdshader"] = HealthyShaderWithInclude },
                existingPaths: new() { "res://Shaders/common.gdshaderinc" });

            var orphan = RunScan(rule, "res://S.gdshader")
                .Where(i => i.IssueCode == "materials_orphan_shader_include");

            Assert.Empty(orphan);
        }

        [Fact]
        public void Scan_UnresolvedResInclude_EmitsOrphanShaderIncludeWarning()
        {
            var (rule, _) = BuildRule(files: new() { ["res://S.gdshader"] = HealthyShaderWithInclude });

            var issue = Assert.Single(RunScan(rule, "res://S.gdshader")
                .Where(i => i.IssueCode == "materials_orphan_shader_include"));

            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("orphan_shader_include", issue.Evidence!["kind"]);
            Assert.Equal("res://Shaders/common.gdshaderinc", issue.Evidence!["includeTarget"]);
        }

        [Fact]
        public void Scan_UnresolvedRelativeInclude_EmitsOrphanShaderIncludeWarning()
        {
            var (rule, _) = BuildRule(files: new() { ["res://Shaders/S.gdshader"] = ShaderWithUnresolvedRelativeInclude });

            var issue = Assert.Single(RunScan(rule, "res://Shaders/S.gdshader")
                .Where(i => i.IssueCode == "materials_orphan_shader_include"));

            Assert.Equal("missing.gdshaderinc", issue.Evidence!["includeTarget"]);
        }

        [Fact]
        public void Scan_ResolvedRelativeIncludeInSameDir_NotOrphan()
        {
            // A relative include resolved against the shader's own directory.
            const string shader = "shader_type spatial;\n#include \"local.gdshaderinc\"\nvoid fragment() {}\n";
            var (rule, _) = BuildRule(
                files: new() { ["res://Shaders/S.gdshader"] = shader },
                existingPaths: new() { "res://Shaders/local.gdshaderinc" });

            var orphan = RunScan(rule, "res://Shaders/S.gdshader")
                .Where(i => i.IssueCode == "materials_orphan_shader_include");

            Assert.Empty(orphan);
        }

        [Fact]
        public void Scan_ResolvedRelativeIncludeInParentDir_NotOrphan()
        {
            // Godot's common layout: shaders/ + shaders/includes/. A relative include one level up.
            const string shader = "shader_type spatial;\n#include \"common.gdshaderinc\"\nvoid fragment() {}\n";
            var (rule, _) = BuildRule(
                files: new() { ["res://Shaders/includes/S.gdshader"] = shader },
                existingPaths: new() { "res://Shaders/common.gdshaderinc" });

            var orphan = RunScan(rule, "res://Shaders/includes/S.gdshader")
                .Where(i => i.IssueCode == "materials_orphan_shader_include");

            Assert.Empty(orphan);
        }

        [Fact]
        public void Scan_ResolvedUidInclude_NotOrphan()
        {
            const string shader = "shader_type spatial;\n#include \"uid://cccccccccccc\"\nvoid fragment() {}\n";
            var (rule, _) = BuildRule(
                files: new() { ["res://S.gdshader"] = shader },
                existingUids: new() { "uid://cccccccccccc" });

            var orphan = RunScan(rule, "res://S.gdshader")
                .Where(i => i.IssueCode == "materials_orphan_shader_include");

            Assert.Empty(orphan);
        }

        [Fact]
        public void Scan_ShaderWithNoIncludes_EmitsNoOrphanInclude()
        {
            var (rule, _) = BuildRule(files: new() { ["res://S.gdshader"] = ShaderWithNoIncludes });

            Assert.Empty(RunScan(rule, "res://S.gdshader"));
        }

        [Fact]
        public void Scan_CommentedInclude_NotTreatedAsInclude()
        {
            var (rule, _) = BuildRule(
                files: new() { ["res://S.gdshader"] = ShaderWithCommentedInclude },
                // If the commented include WERE parsed, this path would be needed to avoid a flag.
                existingPaths: new HashSet<string>());

            Assert.Empty(RunScan(rule, "res://S.gdshader"));
        }

        // =====================================================================
        // duplicate_material (Full mode only)
        // =====================================================================

        [Fact]
        public void Scan_DuplicateMaterials_FullMode_EmitsDuplicateMaterialWarningPerMember()
        {
            var (rule, _) = BuildRule(
                files: new() { ["res://A.tres"] = DuplicateA, ["res://B.tres"] = DuplicateB },
                // Mark both referenced so unused_material does not fire and pollute the assertions.
                referencedTargets: new() { "res://A.tres", "res://B.tres" });

            var dups = RunScan(rule, new[] { "res://A.tres", "res://B.tres" })
                .Where(i => i.IssueCode == "materials_duplicate_material")
                .OrderBy(i => i.AssetPath).ToList();

            Assert.Equal(2, dups.Count);
            Assert.All(dups, i =>
            {
                Assert.Equal(VerifySeverity.Warning, i.Severity);
                Assert.Equal("duplicate_material", i.Evidence!["kind"]);
                Assert.Equal("2", i.Evidence!["duplicateCount"]);
            });
            Assert.Equal("res://B.tres", dups[0].Evidence!["siblings"]);
            Assert.Equal("res://A.tres", dups[1].Evidence!["siblings"]);
        }

        [Fact]
        public void Scan_DuplicateMaterials_CheckpointMode_DoesNotRunCrossAsset()
        {
            // The cross-asset passes (duplicate/unused) are Validate/Full only.
            var (rule, _) = BuildRule(
                files: new() { ["res://A.tres"] = DuplicateA, ["res://B.tres"] = DuplicateB },
                referencedTargets: new() { "res://A.tres", "res://B.tres" });

            var dups = RunScan(rule, new[] { "res://A.tres", "res://B.tres" }, VerifyRunMode.Checkpoint)
                .Where(i => i.IssueCode == "materials_duplicate_material");

            Assert.Empty(dups);
        }

        [Fact]
        public void Scan_DistinctMaterials_NotDuplicates()
        {
            // Two materials differing in a property value are NOT duplicates.
            const string other = "[gd_resource type=\"StandardMaterial3D\" load_steps=1 format=3]\n\n[resource]\nresource_name = \"Stone\"\nalbedo_color = Color(0.1, 0.1, 0.1, 1)\nmetallic = 0.9\n";
            var (rule, _) = BuildRule(
                files: new() { ["res://A.tres"] = DuplicateA, ["res://C.tres"] = other },
                referencedTargets: new() { "res://A.tres", "res://C.tres" });

            var dups = RunScan(rule, new[] { "res://A.tres", "res://C.tres" })
                .Where(i => i.IssueCode == "materials_duplicate_material");

            Assert.Empty(dups);
        }

        [Fact]
        public void Scan_DuplicatesDifferingOnlyByResourceName_AreDuplicates()
        {
            // resource_name is identity, excluded from the fingerprint → still duplicates.
            var dups = RunScan(BuildRule(
                files: new() { ["res://A.tres"] = DuplicateA, ["res://B.tres"] = DuplicateB },
                referencedTargets: new() { "res://A.tres", "res://B.tres" }).rule,
                new[] { "res://A.tres", "res://B.tres" })
                .Where(i => i.IssueCode == "materials_duplicate_material");
            Assert.Equal(2, dups.Count());
        }

        // =====================================================================
        // unused_material (Full mode only)
        // =====================================================================

        [Fact]
        public void Scan_UnreferencedMaterial_FullMode_EmitsUnusedMaterialWarning()
        {
            var (rule, _) = BuildRule(files: new() { ["res://Orphan.tres"] = ConfiguredStandardMaterial },
                referencedTargets: new HashSet<string>()); // nothing references it

            var issue = Assert.Single(RunScan(rule, "res://Orphan.tres")
                .Where(i => i.IssueCode == "materials_unused_material"));

            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("unused_material", issue.Evidence!["kind"]);
            Assert.Equal("res://Orphan.tres", issue.AssetPath);
        }

        [Fact]
        public void Scan_ReferencedMaterial_NotUnused()
        {
            var (rule, _) = BuildRule(files: new() { ["res://Used.tres"] = ConfiguredStandardMaterial },
                referencedTargets: new() { "res://Used.tres" });

            var unused = RunScan(rule, "res://Used.tres")
                .Where(i => i.IssueCode == "materials_unused_material");

            Assert.Empty(unused);
        }

        [Fact]
        public void Scan_ReferencedByUid_NotUnused()
        {
            // The material's path is unreferenced, but its own uid IS referenced → used.
            const string mat = "[gd_resource type=\"StandardMaterial3D\" load_steps=1 format=3]\n\n[resource]\nalbedo_color = Color(1,0,0,1)\n";
            // Set the material's own uid into the text via [gd_resource uid=...] so the parser records it.
            const string matWithUid = "[gd_resource type=\"StandardMaterial3D\" uid=\"uid://dddddddddddd\" load_steps=1 format=3]\n\n[resource]\nalbedo_color = Color(1,0,0,1)\n";
            var (rule, _) = BuildRule(
                files: new() { ["res://M.tres"] = matWithUid },
                referencedTargets: new() { "uid://dddddddddddd" }); // referenced by uid, not by path

            var unused = RunScan(rule, "res://M.tres")
                .Where(i => i.IssueCode == "materials_unused_material");

            Assert.Empty(unused);
        }

        [Fact]
        public void Scan_UnusedMaterial_CheckpointMode_DoesNotRun()
        {
            var (rule, _) = BuildRule(files: new() { ["res://Orphan.tres"] = ConfiguredStandardMaterial },
                referencedTargets: new HashSet<string>());

            var unused = RunScan(rule, "res://Orphan.tres", VerifyRunMode.Checkpoint)
                .Where(i => i.IssueCode == "materials_unused_material");

            Assert.Empty(unused);
        }

        // =====================================================================
        // Run-mode split (per-asset detections run on Checkpoint)
        // =====================================================================

        [Fact]
        public void Scan_MissingShader_RunsOnCheckpointForDirectlyScopedMaterial()
        {
            // A directly-scoped .tres is analyzed in every mode (cheap, no walk) — missing_shader fires
            // on Checkpoint too, so a gated mutation on a single material catches a regression.
            var (rule, _) = BuildRule(files: new() { ["res://M.tres"] = MissingShaderDanglingId });

            var issue = Assert.Single(RunScan(rule, "res://M.tres", VerifyRunMode.Checkpoint)
                .Where(i => i.IssueCode == "materials_missing_shader"));

            Assert.Equal(VerifySeverity.Error, issue.Severity);
        }

        [Fact]
        public void Scan_BuiltinShader_RunsOnCheckpointForDirectlyScopedMaterial()
        {
            var (rule, _) = BuildRule(files: new() { ["res://M.tres"] = BuiltinShaderOnlyStandard });

            Assert.Single(RunScan(rule, "res://M.tres", VerifyRunMode.Checkpoint)
                .Where(i => i.IssueCode == "materials_builtin_shader_only"));
        }

        [Fact]
        public void Scan_OrphanInclude_RunsOnCheckpointForDirectlyScopedShader()
        {
            var (rule, _) = BuildRule(files: new() { ["res://S.gdshader"] = HealthyShaderWithInclude });

            Assert.Single(RunScan(rule, "res://S.gdshader", VerifyRunMode.Checkpoint)
                .Where(i => i.IssueCode == "materials_orphan_shader_include"));
        }

        [Fact]
        public void Scan_DirectoryWalk_IsValidateFullOnly()
        {
            // A directory scope on Checkpoint does not walk the subtree.
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Materials" }, ["res://Materials/"] = new() { "M.tres" } },
                files: new() { ["res://Materials/M.tres"] = MissingShaderDanglingId });

            var issues = RunScan(rule, "res://", VerifyRunMode.Checkpoint);

            Assert.Empty(issues);
        }

        [Fact]
        public void Scan_DirectoryWalk_FullMode_AnalyzesEveryMaterial()
        {
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Materials" }, ["res://Materials/"] = new() { "M.tres", "S.gdshader" } },
                files: new() { ["res://Materials/M.tres"] = MissingShaderDanglingId, ["res://Materials/S.gdshader"] = HealthyShaderWithInclude });

            var issues = RunScan(rule, "res://");

            Assert.Contains(issues, i => i.IssueCode == "materials_missing_shader" && i.AssetPath == "res://Materials/M.tres");
            Assert.Contains(issues, i => i.IssueCode == "materials_orphan_shader_include" && i.AssetPath == "res://Materials/S.gdshader");
        }

        // =====================================================================
        // Scope filtering / de-dup
        // =====================================================================

        [Fact]
        public void Scan_NonMaterialTres_Skipped()
        {
            // A custom Resource (not a Material) is not this rule's input.
            var (rule, _) = BuildRule(files: new() { ["res://D.tres"] = NonMaterialResource });

            Assert.Empty(RunScan(rule, "res://D.tres"));
        }

        [Fact]
        public void Scan_HeaderlessTres_Skipped()
        {
            // A header-less / broken .tres is project_health's domain — skip, emit nothing.
            var (rule, _) = BuildRule(files: new() { ["res://Broken.tres"] = HeaderlessTres });

            Assert.Empty(RunScan(rule, "res://Broken.tres"));
        }

        [Fact]
        public void Scan_NonMaterialNonShaderFile_Skipped()
        {
            // A .tscn or .gd is not this rule's input.
            var (rule, _) = BuildRule(files: new() { ["res://Main.tscn"] = "[gd_scene load_steps=1 format=3]\n\n[node name=\"Root\" type=\"Node\"]\n" });

            Assert.Empty(RunScan(rule, "res://Main.tscn"));
        }

        [Fact]
        public void Scan_OverlappingDirectoryRoots_WalkedOnce()
        {
            // A scope naming "res://" AND "res://Materials" must not double-walk Materials.
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Materials" }, ["res://Materials/"] = new() { "M.tres" } },
                files: new() { ["res://Materials/M.tres"] = MissingShaderDanglingId });

            var missing = RunScan(rule, new[] { "res://", "res://Materials" })
                .Where(i => i.IssueCode == "materials_missing_shader" && i.AssetPath == "res://Materials/M.tres");

            Assert.Single(missing);
        }

        [Fact]
        public void Scan_OverlappingDirectoryRoots_FullWalkCollectsMaterialsOnce()
        {
            // Duplicate detection must not see the same material twice (no phantom dup group).
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Materials" }, ["res://Materials/"] = new() { "A.tres", "B.tres" } },
                files: new() { ["res://Materials/A.tres"] = DuplicateA, ["res://Materials/B.tres"] = DuplicateB },
                referencedTargets: new() { "res://Materials/A.tres", "res://Materials/B.tres" });

            var dups = RunScan(rule, new[] { "res://", "res://Materials" })
                .Where(i => i.IssueCode == "materials_duplicate_material");

            Assert.Equal(2, dups.Count());
        }

        // =====================================================================
        // Robustness (never throws)
        // =====================================================================

        [Fact]
        public void Scan_ThrowingResolver_DoesNotCrash()
        {
            var resolver = new ThrowingResolver();
            string? Reader(string _) => "irrelevant";
            var rule = new MaterialsShaderHealthRule(resolver, Reader);

            var issues = RunScan(rule, "res://");

            Assert.Empty(issues); // a throwing ListDirectory contributes no issues
        }

        [Fact]
        public void Scan_ThrowingFileReader_DoesNotCrash()
        {
            var resolver = new InMemoryResolver
            {
                Directories = { ["res://"] = new() { "M.tres" } },
                Files = { ["res://M.tres"] = "anything" },
            };
            string? ThrowingReader(string _) => throw new InvalidOperationException("disk read failed");
            var rule = new MaterialsShaderHealthRule(resolver, ThrowingReader);

            var issues = RunScan(rule, "res://");

            Assert.Empty(issues); // an unreadable material contributes no issues
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

        private sealed class ThrowingResolver : IMaterialsShaderResolver
        {
            public IReadOnlyList<MaterialsFolderEntry> ListDirectory(string? resDir) => throw new InvalidOperationException("boom");
            public bool PathExists(string? resPath) => false;
            public bool UidExists(string? uid) => false;
            public bool IsReferenced(string? resPathOrUid, string? ownerResPath) => false;
        }

        // =====================================================================
        // Gate-delta stability (IssueKey round-trip)
        // =====================================================================

        [Fact]
        public void Scan_IssueKey_RoundTripsStably()
        {
            // Two scans of the same fixture produce identical IssueKeys — the gate delta relies on this.
            // The dangling material also trips unused_material (it is unreferenced); both keys must be stable
            // across scans. The missing_shader key pins to its canonical form.
            var (rule, _) = BuildRule(files: new() { ["res://M.tres"] = MissingShaderDanglingId });

            var keys1 = RunScan(rule, "res://M.tres").Select(IssueKey.Build).ToList();
            var keys2 = RunScan(rule, "res://M.tres").Select(IssueKey.Build).ToList();

            Assert.Equal(keys1, keys2);
            Assert.Contains("materials_shader_health|ERROR|res://M.tres|materials_missing_shader", keys1);
        }

        [Fact]
        public void Scan_AllIssueCodes_BelongToThisRule()
        {
            // Every emitted issue carries this rule's id + a known code (catalog-drift guard).
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Materials" }, ["res://Materials/"] = new() { "M.tres", "S.gdshader" } },
                files: new() { ["res://Materials/M.tres"] = MissingShaderDanglingId, ["res://Materials/S.gdshader"] = HealthyShaderWithInclude });

            var issues = RunScan(rule, "res://");
            var knownCodes = new HashSet<string>
            {
                "materials_missing_shader", "materials_builtin_shader_only",
                "materials_orphan_shader_include", "materials_duplicate_material", "materials_unused_material",
            };

            Assert.All(issues, i =>
            {
                Assert.Equal("materials_shader_health", i.RuleId);
                Assert.Contains(i.IssueCode, knownCodes);
            });
        }

        // =====================================================================
        // Fix-matching contract (codes are stable; fixIds empty in v1)
        // =====================================================================

        [Fact]
        public void Scan_MissingShader_CodeMatchesFutureFixShape()
        {
            // The missing_shader code is reserved for the future reassign_missing_shader fix. The code
            // string is the link key — it must stay stable so the future fix can match it.
            Assert.Equal("materials_missing_shader", IssueCodes.MissingShader);
        }

        [Fact]
        public void IssueCodes_ArePrefixedAndStable()
        {
            // Catalog-drift guard: every code shares the materials_ prefix and matches the freeze roster.
            Assert.Equal("materials_missing_shader", IssueCodes.MissingShader);
            Assert.Equal("materials_builtin_shader_only", IssueCodes.BuiltinShaderOnly);
            Assert.Equal("materials_orphan_shader_include", IssueCodes.OrphanShaderInclude);
            Assert.Equal("materials_duplicate_material", IssueCodes.DuplicateMaterial);
            Assert.Equal("materials_unused_material", IssueCodes.UnusedMaterial);
        }

        // =====================================================================
        // VerifyRunner auto-registration
        // =====================================================================

        [Fact]
        public void VerifyRunner_RegisterDefaults_RegistersMaterialsShaderHealthRule()
        {
            VerifyRunner.ClearRules();
            try
            {
                VerifyRunner.RegisterDefaults();

                var ids = VerifyRunner.Rules.Select(r => r.Id).ToList();
                Assert.Contains("materials_shader_health", ids);
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
        public void Parser_HealthyShaderMaterial_ExtractsShaderTarget()
        {
            var analysis = MaterialsShaderParser.ParseMaterial(HealthyShaderMaterial);

            Assert.False(analysis.Skipped);
            Assert.NotNull(analysis.Material);
            Assert.Equal("ShaderMaterial", analysis.Material!.ResourceType);
            Assert.Equal("res://Shaders/Valid.gdshader", analysis.Material.ShaderTarget);
            Assert.Equal("1_shad", analysis.Material.ShaderUsageId);
            Assert.False(analysis.Material.ShaderUsageDangling);
            Assert.True(analysis.Material.HasOverrides); // shader_parameter/color
        }

        [Fact]
        public void Parser_DanglingId_MarksDangling()
        {
            var analysis = MaterialsShaderParser.ParseMaterial(MissingShaderDanglingId);

            Assert.Equal("9_gone", analysis.Material!.ShaderUsageId);
            Assert.True(analysis.Material.ShaderUsageDangling);
            Assert.Null(analysis.Material.ShaderTarget); // id never declared → no target resolved
        }

        [Fact]
        public void Parser_NonMaterialResource_PreservesTypeForRuleSkip()
        {
            // The parser does not know which types are Materials — that classification is the rule's job
            // (so a custom Material subclass is not silently dropped here). The parser must return the
            // resource type verbatim so the rule can decide whether to analyze it.
            var analysis = MaterialsShaderParser.ParseMaterial(NonMaterialResource);

            Assert.False(analysis.Skipped);
            Assert.Equal("DemoData", analysis.Material!.ResourceType);
        }

        [Fact]
        public void Parser_EmptyText_IsSkipped()
        {
            Assert.True(MaterialsShaderParser.ParseMaterial("").Skipped);
            Assert.True(MaterialsShaderParser.ParseMaterial(null).Skipped);
        }

        [Fact]
        public void Parser_ExtractsOwnUid()
        {
            const string mat = "[gd_resource type=\"StandardMaterial3D\" uid=\"uid://abc123\" load_steps=1 format=3]\n\n[resource]\nalbedo_color = Color(1,0,0,1)\n";
            var analysis = MaterialsShaderParser.ParseMaterial(mat);

            Assert.Equal("uid://abc123", analysis.Material!.OwnUid);
        }

        [Fact]
        public void Parser_DuplicateFingerprint_NormalizesSpacing()
        {
            // Two materials with the same properties but different cosmetic spacing produce the same fingerprint.
            const string a = "[gd_resource type=\"StandardMaterial3D\" load_steps=1 format=3]\n\n[resource]\nalbedo_color = Color(0.5, 0.3, 0.1, 1)\nmetallic = 0.2\n";
            const string b = "[gd_resource type=\"StandardMaterial3D\" load_steps=1 format=3]\n\n[resource]\nalbedo_color=Color(0.5,0.3,0.1,1)\nmetallic=0.2\n";

            var fa = MaterialsShaderParser.ParseMaterial(a).Material!.Fingerprint;
            var fb = MaterialsShaderParser.ParseMaterial(b).Material!.Fingerprint;

            Assert.Equal(fa, fb);
        }

        [Fact]
        public void Parser_ShaderExtractsIncludes()
        {
            var analysis = MaterialsShaderParser.ParseShader(HealthyShaderWithInclude);

            Assert.False(analysis.Skipped);
            Assert.Single(analysis.Shader!.Includes);
            Assert.Equal("res://Shaders/common.gdshaderinc", analysis.Shader.Includes[0].Target);
        }

        [Fact]
        public void Parser_ShaderWithNoIncludes_EmptyList()
        {
            var analysis = MaterialsShaderParser.ParseShader(ShaderWithNoIncludes);

            Assert.Empty(analysis.Shader!.Includes);
        }

        [Fact]
        public void Parser_ShaderSkipsCommentedInclude()
        {
            var analysis = MaterialsShaderParser.ParseShader(ShaderWithCommentedInclude);

            Assert.Empty(analysis.Shader!.Includes);
        }

        [Fact]
        public void Parser_ShaderAngleBracketInclude_NotRecognized()
        {
            // Godot shaders use the quoted form only; the angle-bracket form is not an include here.
            const string shader = "shader_type spatial;\n#include <global_inc>\nvoid fragment() {}\n";
            var analysis = MaterialsShaderParser.ParseShader(shader);

            Assert.Empty(analysis.Shader!.Includes);
        }

        [Fact]
        public void Parser_NeverThrowsOnMalformedInput()
        {
            // A truncated/garbled material must not throw.
            var analysis = MaterialsShaderParser.ParseMaterial("[gd_resource type=\"ShaderMaterial\"\nshader = ExtResource(");
            Assert.NotNull(analysis); // did not throw; returned some analysis (skip or partial)
        }
    }
}
