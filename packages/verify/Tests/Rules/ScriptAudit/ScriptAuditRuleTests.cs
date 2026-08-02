#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Editor;
using GodotOpenMcp.Verify.Rules.ScriptAudit;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Rules.ScriptAudit
{
    /// <summary>
    /// P14.4 tests for the script-audit verify rule (class mismatch, missing class_name, cyclic class_name).
    /// Adapted fidelity: Unity's <c>MissingReferences</c> rule's script-class subset is the structural
    /// reference for the class-name-vs-file mismatch detection (Unity resolves a MonoBehaviour's
    /// <c>m_Script</c> GUID to a C# type via reflection; Godot's <c>.tscn</c>/<c>.tres</c>/<c>.gd</c>/
    /// <c>.cs</c> are text-serialized and parseable offline). The GDScript <c>class_name</c> parsing and
    /// <c>.cs</c> file-name class resolution are greenfield for Godot; Unity's
    /// <c>missing_method</c>/<c>type_mismatch</c>/<c>duplicate_component</c> signals are skipped (no Godot
    /// component model). The test structure mirrors the sibling P3.2–P3.4 + P14.1/P14.2/P14.3 rule tests
    /// (valid fixture → no issues; broken fixture → expected issues; false-positive guards; run-mode split;
    /// robustness; scope filtering; gate-delta stability). The rule is exercised through its internal
    /// constructor with an <see cref="InMemoryResolver"/> + an in-memory file map, so no Godot API and no
    /// disk access — the binary-less xUnit host runs the full rule.
    /// </summary>
    // Shares the VerifyRunner static-registry collection with VerifyRunnerTests so the registration
    // tests below do not race with the Core runner tests under xUnit's default parallel execution.
    [Collection("VerifyRunnerCollection")]
    public class ScriptAuditRuleTests
    {
        // ---- Scene/resource fixtures (.tscn/.tres) ---------------------------
        //
        // Minimal Godot text exercising each attachment shape. The parser inspects the
        // [gd_scene]/[gd_resource script_class=] header, the [ext_resource id= path=] declarations, and the
        // body `script = ExtResource("id")` slot, so these are the smallest valid examples for each case.

        // A resource whose recorded script_class matches the .gd's class_name → healthy.
        private const string HealthyTresMatchingClass = @"[gd_resource type=""Resource"" script_class=""DemoData"" load_steps=2 format=3]

[ext_resource type=""Script"" path=""res://DemoData.gd"" id=""1_demo""]

[resource]
script = ExtResource(""1_demo"")
";

        // A resource whose recorded script_class does NOT match the .gd's class_name → mismatch.
        private const string TresMismatchedClass = @"[gd_resource type=""Resource"" script_class=""Renamed"" load_steps=2 format=3]

[ext_resource type=""Script"" path=""res://OldName.gd"" id=""1_demo""]

[resource]
script = ExtResource(""1_demo"")
";

        // A resource whose script is a .cs; recorded script_class mismatches the C# declaration.
        private const string TresMismatchedCsClass = @"[gd_resource type=""Resource"" script_class=""Player"" load_steps=2 format=3]

[ext_resource type=""Script"" path=""res://Enemy.cs"" id=""1_cs""]

[resource]
script = ExtResource(""1_cs"")
";

        // A scene attaching a class_name-less .gd → script_missing_class_name.
        private const string SceneAttachesNamelessGd = @"[gd_scene load_steps=2 format=3]

[ext_resource type=""Script"" path=""res://Nameless.gd"" id=""1_nl""]

[node name=""Root"" type=""Node""]
script = ExtResource(""1_nl"")
";

        // A scene attaching a .gd WITH class_name → healthy (no missing-class signal).
        private const string SceneAttachesNamedGd = @"[gd_scene load_steps=2 format=3]

[ext_resource type=""Script"" path=""res://Named.gd"" id=""1_nm""]

[node name=""Root"" type=""Node""]
script = ExtResource(""1_nm"")
";

        // A scene with no script attachment at all → nothing to audit.
        private const string SceneNoScript = @"[gd_scene load_steps=1 format=3]

[node name=""Root"" type=""Node""]
";

        // A scene whose script = ExtResource id is dangling (no declared [ext_resource]) → missing_scripts'
        // domain, this rule emits nothing.
        private const string SceneDanglingScriptId = @"[gd_scene load_steps=1 format=3]

[node name=""Root"" type=""Node""]
script = ExtResource(""9_gone"")
";

        // A resource whose script = ExtResource resolves a path that does not exist on disk → the script
        // file is unreadable; missing_scripts' domain, this rule emits nothing.
        private const string TresScriptFileMissing = @"[gd_resource type=""Resource"" script_class=""X"" load_steps=2 format=3]

[ext_resource type=""Script"" path=""res://Vanished.gd"" id=""1_v""]

[resource]
script = ExtResource(""1_v"")
";

        // A resource with a recorded script_class but NO script = body line → no slot to resolve; the
        // recorded class is unchecked (there is nothing to compare against).
        private const string TresRecordedClassNoSlot = @"[gd_resource type=""Resource"" script_class=""X"" load_steps=1 format=3]

[resource]
";

        // ---- .gd fixtures -----------------------------------------------------

        private const string GdWithClassName = @"@tool
extends Resource
class_name DemoData

@export var title: String = ""x""
";

        private const string GdWithRenamedClassName = @"extends Resource
class_name OldName

func _ready(): pass
";

        private const string GdWithoutClassName = @"extends Node

const marker := ""x""
";

        // class_name with the optional icon form: class_name X, "res://icon.png"
        private const string GdWithIconClassName = @"extends Node
class_name IconClass, ""res://icon.png""
";

        // An inner `class X:` (a different construct, not the global registration) must NOT be matched.
        private const string GdWithInnerClassOnly = @"extends Node

func make():
    class Inner:
        pass
";

        // Two .gd files declaring the same class_name → cyclic.
        private const string GdDupA = @"extends Resource
class_name Dup
";
        private const string GdDupB = @"extends Node
class_name Dup
";

        // ---- .cs fixtures -----------------------------------------------------

        private const string CsPublicPartialClass = @"#nullable enable
using Godot;

namespace Demo;

public partial class Enemy : Node
{
    public const string Marker = ""x"";
}
";

        private const string CsNoClassDeclaration = @"// a .cs with only using/namespace — falls back to file-name stem.
using Godot;
namespace Demo;
";

        private const string CsAbstractClass = @"using Godot;
namespace Demo;
public abstract class Base : Node { }
";

        // ---- In-memory resolver ----------------------------------------------

        /// <summary>
        /// In-memory resolver modeling a directory tree. Mirrors the sibling rules' InMemoryResolver
        /// (directory listing). File content + existence is modeled by the <see cref="Files"/> map the
        /// rule reads through its injected file reader.
        /// </summary>
        private sealed class InMemoryResolver : IScriptAuditResolver
        {
            public Dictionary<string, List<string>> Directories { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

            public IReadOnlyList<ScriptFolderEntry> ListDirectory(string? resDir)
            {
                var key = NormalizeDir(resDir);
                if (key == null || !Directories.TryGetValue(key, out var children))
                    return Array.Empty<ScriptFolderEntry>();

                var dirs = new List<ScriptFolderEntry>();
                var files = new List<ScriptFolderEntry>();
                foreach (var name in children)
                {
                    var childRes = key + name;
                    var childDirKey = childRes + "/";
                    if (Directories.ContainsKey(childDirKey))
                        dirs.Add(new ScriptFolderEntry(childDirKey, name, isDirectory: true));
                    else
                        files.Add(new ScriptFolderEntry(childRes, name, isDirectory: false));
                }
                dirs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                var combined = new List<ScriptFolderEntry>(dirs.Count + files.Count);
                combined.AddRange(dirs);
                combined.AddRange(files);
                return combined;
            }
        }

        private static string? NormalizeDir(string? resDir)
        {
            if (string.IsNullOrEmpty(resDir)) return "res://";
            var d = resDir!;
            if (!d.StartsWith("res://", StringComparison.Ordinal)) return null;
            if (!d.EndsWith("/")) d += "/";
            return d;
        }

        private static (ScriptAuditRule rule, InMemoryResolver resolver) BuildRule(
            Dictionary<string, List<string>>? directories = null,
            Dictionary<string, string>? files = null)
        {
            var resolver = new InMemoryResolver();
            if (directories != null)
                foreach (var kv in directories) resolver.Directories[kv.Key] = new List<string>(kv.Value);
            if (files != null)
                foreach (var kv in files) resolver.Files[kv.Key] = kv.Value;
            string? Reader(string p) => resolver.Files.TryGetValue(p, out var t) ? t : null;
            return (new ScriptAuditRule(resolver, Reader), resolver);
        }

        private static List<VerifyIssue> RunScan(ScriptAuditRule rule, string resPath,
            VerifyRunMode mode = VerifyRunMode.Full)
        {
            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(new[] { resPath }), mode, sink);
            return sink;
        }

        private static List<VerifyIssue> RunScan(ScriptAuditRule rule, string[] resPaths,
            VerifyRunMode mode = VerifyRunMode.Full)
        {
            var sink = new List<VerifyIssue>();
            rule.Scan(new VerifyScope(resPaths), mode, sink);
            return sink;
        }

        // =====================================================================
        // script_class_mismatch
        // =====================================================================

        [Fact]
        public void Scan_HealthyMatchingClass_EmitsNoIssues()
        {
            var (rule, _) = BuildRule(files: new()
            {
                ["res://R.tres"] = HealthyTresMatchingClass,
                ["res://DemoData.gd"] = GdWithClassName,
            });

            Assert.Empty(RunScan(rule, "res://R.tres"));
        }

        [Fact]
        public void Scan_MismatchedGdClass_EmitsClassMismatchWarning()
        {
            var (rule, _) = BuildRule(files: new()
            {
                ["res://R.tres"] = TresMismatchedClass,
                ["res://OldName.gd"] = GdWithRenamedClassName,
            });

            var issue = Assert.Single(RunScan(rule, "res://R.tres")
                .Where(i => i.IssueCode == "script_class_mismatch"));

            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("res://R.tres", issue.AssetPath);
            Assert.Equal("class_mismatch", issue.Evidence!["kind"]);
            Assert.Equal("Renamed", issue.Evidence!["recordedClass"]);
            Assert.Equal("OldName", issue.Evidence!["resolvedClass"]);
            Assert.Equal("res://OldName.gd", issue.Evidence!["scriptPath"]);
            Assert.Equal("gd_class_name", issue.Evidence!["resolution"]);
            Assert.Contains("script_class=\"Renamed\"", issue.Description);
            Assert.Contains("OldName", issue.Description);
        }

        [Fact]
        public void Scan_MismatchedCsClass_EmitsClassMismatchWarningWithDeclarationResolution()
        {
            var (rule, _) = BuildRule(files: new()
            {
                ["res://R.tres"] = TresMismatchedCsClass,
                ["res://Enemy.cs"] = CsPublicPartialClass,
            });

            var issue = Assert.Single(RunScan(rule, "res://R.tres")
                .Where(i => i.IssueCode == "script_class_mismatch"));

            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            Assert.Equal("Player", issue.Evidence!["recordedClass"]);
            Assert.Equal("Enemy", issue.Evidence!["resolvedClass"]);
            Assert.Equal("cs_declaration", issue.Evidence!["resolution"]);
        }

        [Fact]
        public void Scan_CsClassResolvedByFileNameFallback_FlagsHeuristic()
        {
            // A .cs with no parseable class declaration falls back to the file-name stem. The mismatch
            // check still fires (advisory), but the resolution basis is recorded so diagnostics can flag
            // the heuristic.
            const string tres = "[gd_resource type=\"Resource\" script_class=\"Player\" load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://Enemy.cs\" id=\"1_cs\"]\n\n[resource]\nscript = ExtResource(\"1_cs\")\n";
            var (rule, _) = BuildRule(files: new()
            {
                ["res://R.tres"] = tres,
                ["res://Enemy.cs"] = CsNoClassDeclaration,
            });

            var issue = Assert.Single(RunScan(rule, "res://R.tres")
                .Where(i => i.IssueCode == "script_class_mismatch"));

            Assert.Equal("Enemy", issue.Evidence!["resolvedClass"]);
            Assert.Equal("cs_filename", issue.Evidence!["resolution"]);
        }

        [Fact]
        public void Scan_CsDeclarationWinsOverFileNameFallback()
        {
            // The .cs declares class Enemy but the file is Named.cs; the declaration is authoritative.
            const string tres = "[gd_resource type=\"Resource\" script_class=\"Player\" load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://Named.cs\" id=\"1_cs\"]\n\n[resource]\nscript = ExtResource(\"1_cs\")\n";
            var (rule, _) = BuildRule(files: new()
            {
                ["res://R.tres"] = tres,
                ["res://Named.cs"] = CsPublicPartialClass, // declares Enemy
            });

            var issue = Assert.Single(RunScan(rule, "res://R.tres")
                .Where(i => i.IssueCode == "script_class_mismatch"));

            Assert.Equal("Enemy", issue.Evidence!["resolvedClass"]);
            Assert.Equal("cs_declaration", issue.Evidence!["resolution"]);
        }

        [Fact]
        public void Scan_CsMatchingClass_NoMismatch()
        {
            // .cs declares Player; header records Player → no mismatch.
            const string csPlayer = "using Godot;\nnamespace D;\npublic partial class Player : Node { }\n";
            const string tres = "[gd_resource type=\"Resource\" script_class=\"Player\" load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://Player.cs\" id=\"1_cs\"]\n\n[resource]\nscript = ExtResource(\"1_cs\")\n";
            var (rule, _) = BuildRule(files: new() { ["res://R.tres"] = tres, ["res://Player.cs"] = csPlayer });

            Assert.Empty(RunScan(rule, "res://R.tres").Where(i => i.IssueCode == "script_class_mismatch"));
        }

        [Fact]
        public void Scan_NoRecordedScriptClass_NoMismatch()
        {
            // A resource with a script attached but NO script_class= header attribute → nothing to compare
            // against → no mismatch (the missing-class signal may fire separately).
            const string tres = "[gd_resource type=\"Resource\" load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://Named.gd\" id=\"1_nm\"]\n\n[resource]\nscript = ExtResource(\"1_nm\")\n";
            var (rule, _) = BuildRule(files: new() { ["res://R.tres"] = tres, ["res://Named.gd"] = GdWithIconClassName });

            Assert.Empty(RunScan(rule, "res://R.tres").Where(i => i.IssueCode == "script_class_mismatch"));
        }

        // =====================================================================
        // script_missing_class_name
        // =====================================================================

        [Fact]
        public void Scan_AttachedNamelessGd_EmitsMissingClassNameWarning()
        {
            var (rule, _) = BuildRule(files: new()
            {
                ["res://S.tscn"] = SceneAttachesNamelessGd,
                ["res://Nameless.gd"] = GdWithoutClassName,
            });

            var issue = Assert.Single(RunScan(rule, "res://S.tscn")
                .Where(i => i.IssueCode == "script_missing_class_name"));

            Assert.Equal(VerifySeverity.Warning, issue.Severity);
            // Emitted on the script's own path (where the fix lives), not the scene.
            Assert.Equal("res://Nameless.gd", issue.AssetPath);
            Assert.Equal("missing_class_name", issue.Evidence!["kind"]);
            Assert.Equal("res://Nameless.gd", issue.Evidence!["scriptPath"]);
        }

        [Fact]
        public void Scan_AttachedNamedGd_NoMissingClassName()
        {
            var (rule, _) = BuildRule(files: new()
            {
                ["res://S.tscn"] = SceneAttachesNamedGd,
                ["res://Named.gd"] = GdWithIconClassName,
            });

            Assert.Empty(RunScan(rule, "res://S.tscn")
                .Where(i => i.IssueCode == "script_missing_class_name"));
        }

        [Fact]
        public void Scan_AttachedNamelessGdInManyScenes_EmittedOnce()
        {
            // De-duplicated by script path: a nameless .gd attached in two scenes surfaces once.
            var (rule, _) = BuildRule(files: new()
            {
                ["res://A.tscn"] = SceneAttachesNamelessGd.Replace("res://Nameless.gd", "res://Nameless.gd"),
                ["res://B.tscn"] = SceneAttachesNamelessGd,
                ["res://Nameless.gd"] = GdWithoutClassName,
            });

            var missing = RunScan(rule, new[] { "res://A.tscn", "res://B.tscn" })
                .Where(i => i.IssueCode == "script_missing_class_name");

            Assert.Single(missing);
        }

        [Fact]
        public void Scan_AttachedCs_NeverMissingClassName()
        {
            // The missing-class signal is .gd-only; a .cs always has a name.
            const string tres = "[gd_resource type=\"Resource\" load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://NoDecl.cs\" id=\"1_cs\"]\n\n[resource]\nscript = ExtResource(\"1_cs\")\n";
            var (rule, _) = BuildRule(files: new() { ["res://R.tres"] = tres, ["res://NoDecl.cs"] = CsNoClassDeclaration });

            Assert.Empty(RunScan(rule, "res://R.tres").Where(i => i.IssueCode == "script_missing_class_name"));
        }

        // =====================================================================
        // script_cyclic_class_name (Full mode only)
        // =====================================================================

        [Fact]
        public void Scan_DuplicateGdClassNames_FullMode_EmitsCyclicWarningPerMember()
        {
            var (rule, _) = BuildRule(files: new()
            {
                ["res://A.gd"] = GdDupA,
                ["res://B.gd"] = GdDupB,
            });

            var cyclic = RunScan(rule, new[] { "res://A.gd", "res://B.gd" })
                .Where(i => i.IssueCode == "script_cyclic_class_name")
                .OrderBy(i => i.AssetPath).ToList();

            Assert.Equal(2, cyclic.Count);
            Assert.All(cyclic, i =>
            {
                Assert.Equal(VerifySeverity.Warning, i.Severity);
                Assert.Equal("cyclic_class_name", i.Evidence!["kind"]);
                Assert.Equal("Dup", i.Evidence!["className"]);
                Assert.Equal("2", i.Evidence!["duplicateCount"]);
            });
            Assert.Equal("res://B.gd", cyclic[0].Evidence!["siblings"]);
            Assert.Equal("res://A.gd", cyclic[1].Evidence!["siblings"]);
        }

        [Fact]
        public void Scan_DuplicateGdClassNames_CheckpointMode_DoesNotRun()
        {
            // The cyclic pass is Full/Validate only.
            var (rule, _) = BuildRule(files: new() { ["res://A.gd"] = GdDupA, ["res://B.gd"] = GdDupB });

            var cyclic = RunScan(rule, new[] { "res://A.gd", "res://B.gd" }, VerifyRunMode.Checkpoint)
                .Where(i => i.IssueCode == "script_cyclic_class_name");

            Assert.Empty(cyclic);
        }

        [Fact]
        public void Scan_DistinctGdClassNames_NotCyclic()
        {
            var (rule, _) = BuildRule(files: new() { ["res://A.gd"] = GdDupA, ["res://Named.gd"] = GdWithIconClassName });

            Assert.Empty(RunScan(rule, new[] { "res://A.gd", "res://Named.gd" })
                .Where(i => i.IssueCode == "script_cyclic_class_name"));
        }

        // =====================================================================
        // Run-mode split (per-asset detections run on Checkpoint)
        // =====================================================================

        [Fact]
        public void Scan_ClassMismatch_RunsOnCheckpointForDirectlyScopedScene()
        {
            // A directly-scoped .tscn/.tres is analyzed in every mode (cheap, no walk) — mismatch fires on
            // Checkpoint too, so a gated mutation on a single scene catches a regression.
            var (rule, _) = BuildRule(files: new()
            {
                ["res://R.tres"] = TresMismatchedClass,
                ["res://OldName.gd"] = GdWithRenamedClassName,
            });

            Assert.Single(RunScan(rule, "res://R.tres", VerifyRunMode.Checkpoint)
                .Where(i => i.IssueCode == "script_class_mismatch"));
        }

        [Fact]
        public void Scan_MissingClassName_RunsOnCheckpointForDirectlyScopedScene()
        {
            var (rule, _) = BuildRule(files: new()
            {
                ["res://S.tscn"] = SceneAttachesNamelessGd,
                ["res://Nameless.gd"] = GdWithoutClassName,
            });

            Assert.Single(RunScan(rule, "res://S.tscn", VerifyRunMode.Checkpoint)
                .Where(i => i.IssueCode == "script_missing_class_name"));
        }

        [Fact]
        public void Scan_DirectoryWalk_IsValidateFullOnly()
        {
            // A directory scope on Checkpoint does not walk the subtree (so the cyclic pass, which needs
            // the walked .gd set, cannot run; nor can a scene reached only via the walk be analyzed).
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Scripts" }, ["res://Scripts/"] = new() { "A.gd", "B.gd" } },
                files: new() { ["res://Scripts/A.gd"] = GdDupA, ["res://Scripts/B.gd"] = GdDupB });

            Assert.Empty(RunScan(rule, "res://", VerifyRunMode.Checkpoint));
        }

        [Fact]
        public void Scan_DirectoryWalk_FullMode_AnalyzesEveryScript()
        {
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Scenes", "Scripts" },
                    ["res://Scenes/"] = new() { "S.tscn" },
                    ["res://Scripts/"] = new() { "A.gd", "B.gd", "Nameless.gd" },
                },
                files: new()
                {
                    ["res://Scenes/S.tscn"] = SceneAttachesNamelessGd.Replace("res://Nameless.gd", "res://Scripts/Nameless.gd"),
                    ["res://Scripts/A.gd"] = GdDupA,
                    ["res://Scripts/B.gd"] = GdDupB,
                    ["res://Scripts/Nameless.gd"] = GdWithoutClassName,
                });

            var issues = RunScan(rule, "res://");

            Assert.Contains(issues, i => i.IssueCode == "script_cyclic_class_name" && i.AssetPath == "res://Scripts/A.gd");
            Assert.Contains(issues, i => i.IssueCode == "script_cyclic_class_name" && i.AssetPath == "res://Scripts/B.gd");
            Assert.Contains(issues, i => i.IssueCode == "script_missing_class_name" && i.AssetPath == "res://Scripts/Nameless.gd");
        }

        // =====================================================================
        // Scope filtering / de-dup / domain boundaries
        // =====================================================================

        [Fact]
        public void Scan_SceneWithNoScript_Skipped()
        {
            var (rule, _) = BuildRule(files: new() { ["res://S.tscn"] = SceneNoScript });

            Assert.Empty(RunScan(rule, "res://S.tscn"));
        }

        [Fact]
        public void Scan_DanglingScriptId_Skipped()
        {
            // A dangling script id is missing_scripts' domain — this rule emits nothing.
            var (rule, _) = BuildRule(files: new() { ["res://S.tscn"] = SceneDanglingScriptId });

            Assert.Empty(RunScan(rule, "res://S.tscn"));
        }

        [Fact]
        public void Scan_ScriptFileMissing_Skipped()
        {
            // The script file does not exist (reader returns null) → missing_scripts' domain.
            var (rule, _) = BuildRule(files: new() { ["res://R.tres"] = TresScriptFileMissing });

            Assert.Empty(RunScan(rule, "res://R.tres"));
        }

        [Fact]
        public void Scan_RecordedClassNoSlot_NoMismatch()
        {
            // A recorded script_class but no script = body line → no slot to resolve → no mismatch.
            var (rule, _) = BuildRule(files: new() { ["res://R.tres"] = TresRecordedClassNoSlot });

            Assert.Empty(RunScan(rule, "res://R.tres"));
        }

        [Fact]
        public void Scan_NonSceneNonScriptFile_Skipped()
        {
            // A .png or other file is not this rule's input.
            var (rule, _) = BuildRule(files: new() { ["res://Icon.png"] = "binary" });

            Assert.Empty(RunScan(rule, "res://Icon.png"));
        }

        [Fact]
        public void Scan_DirectlyScopedCs_ContributesNothing()
        {
            // A directly-scoped .cs emits nothing (cyclic is .gd-only; .cs collisions need the assembly).
            var (rule, _) = BuildRule(files: new() { ["res://Player.cs"] = CsPublicPartialClass });

            Assert.Empty(RunScan(rule, "res://Player.cs"));
        }

        [Fact]
        public void Scan_OverlappingDirectoryRoots_WalkedOnce()
        {
            // A scope naming "res://" AND "res://Scripts" must not double-walk Scripts.
            var (rule, _) = BuildRule(
                directories: new() { ["res://"] = new() { "Scripts" }, ["res://Scripts/"] = new() { "A.gd", "B.gd" } },
                files: new() { ["res://Scripts/A.gd"] = GdDupA, ["res://Scripts/B.gd"] = GdDupB });

            var cyclic = RunScan(rule, new[] { "res://", "res://Scripts" })
                .Where(i => i.IssueCode == "script_cyclic_class_name" && i.AssetPath == "res://Scripts/A.gd");

            Assert.Single(cyclic);
        }

        // =====================================================================
        // Robustness (never throws)
        // =====================================================================

        [Fact]
        public void Scan_ThrowingResolver_DoesNotCrash()
        {
            var resolver = new ThrowingResolver();
            string? Reader(string _) => "irrelevant";
            var rule = new ScriptAuditRule(resolver, Reader);

            Assert.Empty(RunScan(rule, "res://"));
        }

        [Fact]
        public void Scan_ThrowingFileReader_DoesNotCrash()
        {
            var resolver = new InMemoryResolver
            {
                Directories = { ["res://"] = new() { "S.tscn" } },
                Files = { ["res://S.tscn"] = "anything" },
            };
            string? ThrowingReader(string _) => throw new InvalidOperationException("disk read failed");
            var rule = new ScriptAuditRule(resolver, ThrowingReader);

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

        private sealed class ThrowingResolver : IScriptAuditResolver
        {
            public IReadOnlyList<ScriptFolderEntry> ListDirectory(string? resDir)
                => throw new InvalidOperationException("boom");
        }

        // =====================================================================
        // Gate-delta stability (IssueKey round-trip)
        // =====================================================================

        [Fact]
        public void Scan_IssueKey_RoundTripsStably()
        {
            // Two scans of the same fixture produce identical IssueKeys — the gate delta relies on this.
            var (rule, _) = BuildRule(files: new()
            {
                ["res://R.tres"] = TresMismatchedClass,
                ["res://OldName.gd"] = GdWithRenamedClassName,
            });

            var keys1 = RunScan(rule, "res://R.tres").Select(IssueKey.Build).ToList();
            var keys2 = RunScan(rule, "res://R.tres").Select(IssueKey.Build).ToList();

            Assert.Equal(keys1, keys2);
            // IssueKey.Build emits the canonical "WARN" abbreviation (IssueKey.SeverityToken), not the long
            // "WARNING" form.
            Assert.Contains("script_audit|WARN|res://R.tres|script_class_mismatch", keys1);
        }

        [Fact]
        public void Scan_AllIssueCodes_BelongToThisRule()
        {
            // Every emitted issue carries this rule's id + a known code (catalog-drift guard).
            var (rule, _) = BuildRule(
                directories: new()
                {
                    ["res://"] = new() { "Scenes", "Scripts" },
                    ["res://Scenes/"] = new() { "S.tscn" },
                    ["res://Scripts/"] = new() { "A.gd", "B.gd" },
                },
                files: new()
                {
                    ["res://Scenes/S.tscn"] = SceneAttachesNamelessGd.Replace("res://Nameless.gd", "res://Scripts/Nameless.gd"),
                    ["res://Scripts/A.gd"] = GdDupA,
                    ["res://Scripts/B.gd"] = GdDupB,
                    // Nameless.gd intentionally absent so the scene's missing-class does not fire; the focus
                    // here is that every emitted code is a known script_audit code.
                });

            var issues = RunScan(rule, "res://");
            var knownCodes = new HashSet<string>
            {
                "script_class_mismatch", "script_missing_class_name", "script_cyclic_class_name",
            };

            Assert.All(issues, i =>
            {
                Assert.Equal("script_audit", i.RuleId);
                Assert.Contains(i.IssueCode, knownCodes);
            });
        }

        // =====================================================================
        // Fix-matching contract (codes are stable; fixIds empty in v1)
        // =====================================================================

        [Fact]
        public void IssueCodes_ArePrefixedAndStable()
        {
            // Catalog-drift guard: every code shares the script_ prefix and matches the freeze roster.
            Assert.Equal("script_class_mismatch", IssueCodes.ClassMismatch);
            Assert.Equal("script_missing_class_name", IssueCodes.MissingClassName);
            Assert.Equal("script_cyclic_class_name", IssueCodes.CyclicClassName);
        }

        // =====================================================================
        // VerifyRunner auto-registration
        // =====================================================================

        [Fact]
        public void VerifyRunner_RegisterDefaults_RegistersScriptAuditRule()
        {
            VerifyRunner.ClearRules();
            try
            {
                VerifyRunner.RegisterDefaults();

                var ids = VerifyRunner.Rules.Select(r => r.Id).ToList();
                Assert.Contains("script_audit", ids);
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
        public void Parser_HealthyTres_ExtractsRecordedClassAndScriptPath()
        {
            var attachment = ScriptClassParser.ParseSceneScriptAttachment(HealthyTresMatchingClass);

            Assert.Equal("DemoData", attachment.RecordedClass);
            Assert.Equal("1_demo", attachment.ScriptUsageId);
            Assert.Equal("res://DemoData.gd", attachment.ScriptPath);
            Assert.False(attachment.ScriptUsageDangling);
        }

        [Fact]
        public void Parser_DanglingScriptId_MarksDangling()
        {
            var attachment = ScriptClassParser.ParseSceneScriptAttachment(SceneDanglingScriptId);

            Assert.Equal("9_gone", attachment.ScriptUsageId);
            Assert.True(attachment.ScriptUsageDangling);
            Assert.Null(attachment.ScriptPath);
        }

        [Fact]
        public void Parser_NoScriptSlot_NullUsageId()
        {
            var attachment = ScriptClassParser.ParseSceneScriptAttachment(SceneNoScript);

            Assert.Null(attachment.ScriptUsageId);
        }

        [Fact]
        public void Parser_GdWithClassName_ExtractsName()
        {
            var cls = ScriptClassParser.ParseGdClass(GdWithClassName);

            Assert.True(cls.HasClassName);
            Assert.Equal("DemoData", cls.ClassName);
            Assert.Equal("gd_class_name", cls.Resolution);
        }

        [Fact]
        public void Parser_GdWithoutClassName_HasClassNameFalse()
        {
            var cls = ScriptClassParser.ParseGdClass(GdWithoutClassName);

            Assert.False(cls.HasClassName);
            Assert.Null(cls.ClassName);
            Assert.Equal("gd_no_class_name", cls.Resolution);
        }

        [Fact]
        public void Parser_GdWithIconClassName_ExtractsNameWithoutIcon()
        {
            // The optional icon form class_name X, "res://icon.png" — the name is X, not the icon path.
            var cls = ScriptClassParser.ParseGdClass(GdWithIconClassName);

            Assert.True(cls.HasClassName);
            Assert.Equal("IconClass", cls.ClassName);
        }

        [Fact]
        public void Parser_GdInnerClassOnly_NotMatched()
        {
            // An inner `class Inner:` (indented) is not a global class_name registration.
            var cls = ScriptClassParser.ParseGdClass(GdWithInnerClassOnly);

            Assert.False(cls.HasClassName);
        }

        [Fact]
        public void Parser_CsPublicPartialClass_ExtractsName()
        {
            var cls = ScriptClassParser.ParseCsClass(CsPublicPartialClass, "Enemy.cs");

            Assert.Equal("Enemy", cls.ClassName);
            Assert.Equal("cs_declaration", cls.Resolution);
        }

        [Fact]
        public void Parser_CsAbstractClass_ExtractsName()
        {
            var cls = ScriptClassParser.ParseCsClass(CsAbstractClass, "Base.cs");

            Assert.Equal("Base", cls.ClassName);
            Assert.Equal("cs_declaration", cls.Resolution);
        }

        [Fact]
        public void Parser_CsNoDeclaration_FallsBackToFileNameStem()
        {
            var cls = ScriptClassParser.ParseCsClass(CsNoClassDeclaration, "Enemy.cs");

            Assert.Equal("Enemy", cls.ClassName);
            Assert.Equal("cs_filename", cls.Resolution);
        }

        [Fact]
        public void Parser_CollectGdClassNames_GroupsByClassName()
        {
            var byClass = ScriptClassParser.CollectGdClassNames(new[]
            {
                ("res://A.gd", GdDupA),
                ("res://B.gd", GdDupB),
                ("res://Named.gd", GdWithIconClassName),
                ("res://Nameless.gd", GdWithoutClassName),
            });

            Assert.True(byClass.ContainsKey("Dup"));
            Assert.Equal(2, byClass["Dup"].Count);
            Assert.Single(byClass["IconClass"]);
            // Nameless .gd contributes nothing.
            Assert.False(byClass.ContainsKey(""));
        }

        [Fact]
        public void Parser_NeverThrowsOnMalformedInput()
        {
            // Truncated/garbled inputs must not throw.
            var attachment = ScriptClassParser.ParseSceneScriptAttachment("[gd_resource type=\"Resource\"\nscript = ExtResource(");
            Assert.NotNull(attachment); // did not throw

            var gd = ScriptClassParser.ParseGdClass("class_name");
            Assert.NotNull(gd); // did not throw

            var cs = ScriptClassParser.ParseCsClass("public class", "X.cs");
            Assert.NotNull(cs); // did not throw; fell back to file-name
        }
    }
}
