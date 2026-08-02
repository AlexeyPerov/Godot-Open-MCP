#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Verify.Rules.ScriptAudit
{
    /// <summary>
    /// Detects script-class integrity problems: a scene/resource whose recorded <c>script_class=</c> no
    /// longer matches the class the resolved script file declares (<c>script_class_mismatch</c>), an
    /// attached <c>.gd</c> with no <c>class_name</c> (<c>script_missing_class_name</c>), and two <c>.gd</c>
    /// files declaring the same <c>class_name</c> (<c>script_cyclic_class_name</c>). Adapted from Unity
    /// Open MCP's <c>MissingReferences</c> rule's script-class subset — but the C# type resolution is
    /// heuristic (declaration parse → file-name stem) without compile, and the GDScript <c>class_name</c>
    /// parsing is greenfield. Unity's <c>missing_method</c>/<c>type_mismatch</c>/<c>duplicate_component</c>
    /// signals are skipped (Godot has no Unity component model). See <see cref="IssueCodes"/> for the
    /// fidelity breakdown.
    ///
    /// <para>
    /// <b>Relationship to sibling rules:</b> a script whose <c>[ext_resource]</c> does not resolve (file
    /// gone) is <c>missing_scripts</c>' (P3.3) domain — this rule skips it. A class_name mismatch only fires
    /// when the file <i>exists</i> but its class differs from what the scene recorded, a distinct failure
    /// mode the file-resolution rule cannot see. A duplicate <c>class_name</c> is structural and surfaces
    /// here rather than under <c>import_health</c> (which keys on <c>.import</c> uid sidecars, not script
    /// class registration).
    /// </para>
    ///
    /// <para>
    /// <b>Scope handling.</b> The scope paths may be:
    /// <list type="bullet">
    ///   <item><b>A directory</b> (e.g. <c>res://Scripts</c>, <c>res://</c>): the rule walks the subtree
    ///     via <see cref="IScriptAuditResolver.ListDirectory"/> and analyzes every <c>.tscn</c>/<c>.tres</c>
    ///     (for mismatch + missing-class via attachment) and <c>.gd</c> (for the cyclic pass) it reaches.</item>
    ///   <item><b>A <c>.tscn</c>/<c>.tres</c></b>: the rule analyzes that one file's attachments.</item>
    ///   <item><b>A <c>.gd</c>/<c>.cs</c></b>: contributes to the cyclic index (Full mode) but emits no
    ///     per-asset finding on its own (the missing-class signal only fires for an <i>attached</i> script,
    ///     which requires a scene to reference it).</item>
    ///   <item><b>Any other file</b>: not this rule's input. Skip.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Run-mode behavior.</b> Per-asset detections (<c>script_class_mismatch</c>,
    /// <c>script_missing_class_name</c>) run in every mode including <see cref="VerifyRunMode.Checkpoint"/>
    /// (cheap: one parse per scene + one script-file read, no subtree enumeration) so a gated mutation on a
    /// single scene still catches a regression it introduced. The cross-asset detection
    /// (<c>script_cyclic_class_name</c>) needs the full <c>.gd</c> set as context, so it runs only in
    /// <see cref="VerifyRunMode.Validate"/>/<see cref="VerifyRunMode.Full"/>. Mirrors Unity's
    /// <c>fullScan</c> split and the sibling P14 rules.
    /// </para>
    ///
    /// <para>
    /// <b>File reading:</b> the rule reads file text through a seam (<see cref="ReadFileText"/>) so tests
    /// inject fixture content without touching disk. Production reads via <c>File.ReadAllText</c>. A missing
    /// script file (reader returns null) is skipped — that is <c>missing_scripts</c>' signal.
    /// </para>
    /// </summary>
    public sealed class ScriptAuditRule : IVerifyRule
    {
        /// <summary>The stable rule id surfaced in MCP responses, the capability catalog, and the gate delta.</summary>
        public const string RuleId = "script_audit";

        /// <inheritdoc />
        public string Id => RuleId;

        private readonly IScriptAuditResolver _resolver;
        private readonly Func<string, string?> _readFileText;

        /// <summary>
        /// Production constructor: lists directories through <see cref="LiveScriptAuditResolver"/> and reads
        /// files from disk via <c>File.ReadAllText</c>. Used by <see cref="Core.VerifyRunner.RegisterDefaults"/>.
        /// </summary>
        public ScriptAuditRule() : this(GetLiveResolver(), TryReadAllText) { }

        /// <summary>
        /// Testable constructor: inject the resolver and file reader. Both are pure seams — no Godot API
        /// surface — so the rule compiles and runs in the binary-less xUnit host. The resolver type is
        /// <see cref="IScriptAuditResolver"/>; the file reader returns <c>null</c> for a missing file
        /// (the rule treats that as "not this rule's domain").
        /// </summary>
        internal ScriptAuditRule(IScriptAuditResolver resolver, Func<string, string?> readFileText)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _readFileText = readFileText ?? throw new ArgumentNullException(nameof(readFileText));
        }

        /// <inheritdoc />
        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            // Cross-asset detection (cyclic class_name) needs the full .gd set — Full/Validate only. Per-asset
            // detections run in every mode.
            var fullScan = mode != VerifyRunMode.Checkpoint;

            // De-dupe scoped directory roots so a scope naming "res://" AND "res://Scripts" does not walk
            // Scripts twice. Identical structure to the sibling P14 rules.
            var walkedDirs = new HashSet<string>(StringComparer.Ordinal);
            // De-dupe analyzed files so a scene reached through two overlapping scoped roots is analyzed
            // once. The sibling P14 rules de-dupe directories but not files; script-audit adds file de-dup
            // because the missing-class signal is de-duplicated by script path and would otherwise be
            // re-emitted on each walk.
            var analyzedFiles = new HashSet<string>(StringComparer.Ordinal);

            // Collected .gd files for the cyclic pass (Full mode): (resPath, text).
            var gdFiles = new List<(string ResPath, string Text)>();
            // Flagged missing-class script paths — a script attached in many scenes surfaces once.
            var flaggedMissingClass = new HashSet<string>(StringComparer.Ordinal);

            foreach (var resPath in scope.Paths)
            {
                if (string.IsNullOrEmpty(resPath)) continue;

                if (IsSceneOrResourcePath(resPath))
                {
                    if (!analyzedFiles.Add(resPath)) continue;
                    AnalyzeSceneOrResource(resPath, sink, flaggedMissingClass);
                    continue;
                }

                if (fullScan && IsGdPath(resPath))
                {
                    // A directly-scoped .gd contributes to the cyclic index. Read + cache it now so the
                    // cyclic pass does not re-read. (Missing-class is not checked here: it requires an
                    // attachment, which only a scene provides.)
                    if (!analyzedFiles.Add(resPath)) continue;
                    var text = ReadText(resPath);
                    if (text != null) gdFiles.Add((resPath, text));
                    continue;
                }

                if (!fullScan) continue;

                if (IsLikelyDirectory(resPath))
                {
                    if (!walkedDirs.Add(resPath)) continue;
                    WalkDirectory(resPath, sink, walkedDirs, analyzedFiles, fullScan, gdFiles, flaggedMissingClass);
                }
                // else: a .cs or other file — not a per-asset input. A directly-scoped .cs contributes
                // nothing here (cyclic is .gd-only; .cs collisions need the assembly).
            }

            if (fullScan) DetectCyclicClassNames(gdFiles, sink);
        }

        // ---- Directory walk (reaches every .tscn/.tres + .gd) ----------------

        private void WalkDirectory(string dirRes, List<VerifyIssue> sink, HashSet<string> walkedDirs,
            HashSet<string> analyzedFiles, bool fullScan,
            List<(string ResPath, string Text)> gdFiles, HashSet<string> flaggedMissingClass)
        {
            walkedDirs.Add(dirRes);

            IReadOnlyList<ScriptFolderEntry> entries;
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
                    WalkDirectory(entry.ResPath, sink, walkedDirs, analyzedFiles, fullScan, gdFiles, flaggedMissingClass);
                }
                else if (IsSceneOrResourcePath(entry.ResPath))
                {
                    if (!analyzedFiles.Add(entry.ResPath)) continue;
                    AnalyzeSceneOrResource(entry.ResPath, sink, flaggedMissingClass);
                }
                else if (IsGdPath(entry.ResPath))
                {
                    if (!analyzedFiles.Add(entry.ResPath)) continue;
                    var text = ReadText(entry.ResPath);
                    if (text != null) gdFiles.Add((entry.ResPath, text));
                }
                // else: a .cs or other file — not collected here (cyclic is .gd-only).
            }
        }

        // ---- Per-asset: scene/resource ---------------------------------------

        /// <summary>
        /// Parse one <c>.tscn</c>/<c>.tres</c> and emit the per-asset findings (<c>script_class_mismatch</c>,
        /// <c>script_missing_class_name</c>). Never throws. A scene with no script attachment contributes
        /// nothing; a dangling script id (no declared <c>[ext_resource]</c>) is skipped (missing_scripts'
        /// domain); a script file that cannot be read is skipped (missing_scripts' domain).
        /// </summary>
        private void AnalyzeSceneOrResource(
            string resPath, List<VerifyIssue> sink, HashSet<string> flaggedMissingClass)
        {
            string? text = ReadText(resPath);
            if (text == null) return;

            ScriptAttachment attachment;
            try { attachment = ScriptClassParser.ParseSceneScriptAttachment(text); }
            catch { return; }

            // No script attached → nothing to audit.
            if (attachment.ScriptUsageId == null) return;
            // Dangling id (no declared [ext_resource]) → missing_scripts' domain, not this rule's.
            if (attachment.ScriptUsageDangling) return;
            // No resolvable path (uid-only declaration, or declaration with no path/uid) → cannot read the
            // file offline; skip rather than guess. (A uid-only script is rare; Godot writes path + uid.)
            if (string.IsNullOrEmpty(attachment.ScriptPath)) return;
            var scriptPath = attachment.ScriptPath!;

            // Read the script file. A missing/unreadable script is missing_scripts' domain — skip.
            var scriptText = ReadText(scriptPath);
            if (scriptText == null) return;

            ScriptClass scriptClass;
            try
            {
                scriptClass = IsGdPath(scriptPath)
                    ? ScriptClassParser.ParseGdClass(scriptText)
                    : IsCsPath(scriptPath)
                        ? ScriptClassParser.ParseCsClass(scriptText, Path.GetFileName(scriptPath))
                        : new ScriptClass(className: null, hasClassName: false, resolution: "unknown_kind");
            }
            catch { return; }

            // script_class_mismatch — the recorded class differs from the resolved class. Only checked when
            // both sides are known: Godot only writes script_class= for a custom script class, and a
            // class_name-less .gd has no ClassName to compare (that is the missing-class signal below, not a
            // mismatch). A .cs always has a candidate (declaration or file-name fallback); comparing against
            // a file-name-fallback resolution is intentionally advisory (Warning), since the heuristic may
            // be wrong.
            if (!string.IsNullOrEmpty(attachment.RecordedClass)
                && !string.IsNullOrEmpty(scriptClass.ClassName)
                && !string.Equals(attachment.RecordedClass, scriptClass.ClassName, StringComparison.Ordinal))
            {
                sink.Add(MakeIssue(resPath, VerifySeverity.Warning, IssueCodes.ClassMismatch,
                    $"script_class=\"{attachment.RecordedClass}\" but \"{scriptPath}\" declares class \"{scriptClass.ClassName}\"",
                    BuildMismatchEvidence(resPath, recordedClass: attachment.RecordedClass!,
                        resolvedClass: scriptClass.ClassName!, scriptPath: scriptPath,
                        resolution: scriptClass.Resolution)));
            }

            // script_missing_class_name — an attached .gd with no class_name. De-duplicated by script path so
            // a script attached in many scenes surfaces once (on the script's own path, where the fix lives).
            if (IsGdPath(scriptPath) && !scriptClass.HasClassName)
            {
                if (flaggedMissingClass.Add(scriptPath))
                {
                    sink.Add(MakeIssue(scriptPath, VerifySeverity.Warning, IssueCodes.MissingClassName,
                        $"attached script \"{scriptPath}\" declares no class_name (limits editor type registration)",
                        BuildScriptEvidence(scriptPath)));
                }
            }
        }

        // ---- Cross-asset: cyclic class_name (Full only) ----------------------

        /// <summary>
        /// Group <c>.gd</c> files by <c>class_name</c> and emit <c>script_cyclic_class_name</c> for every
        /// member of a group with ≥ 2. Each member reports its sibling paths so an agent can rename.
        /// Deterministic order: groups sorted by class name, members within a group sorted by path. C# class
        /// collisions are not flagged — they need the compiled assembly to resolve reliably, which is out of
        /// scope for an offline text rule.
        /// </summary>
        private static void DetectCyclicClassNames(
            List<(string ResPath, string Text)> gdFiles, List<VerifyIssue> sink)
        {
            if (gdFiles.Count == 0) return;

            Dictionary<string, List<string>> byClass;
            try { byClass = ScriptClassParser.CollectGdClassNames(gdFiles); }
            catch { return; }

            // Sort group keys for deterministic emission.
            var keys = new List<string>(byClass.Keys);
            keys.Sort(StringComparer.Ordinal);

            foreach (var className in keys)
            {
                var group = byClass[className];
                group.Sort(StringComparer.Ordinal);
                if (group.Count < 2) continue;

                foreach (var path in group)
                {
                    var siblings = new List<string>(group.Count - 1);
                    foreach (var s in group) if (s != path) siblings.Add(s);
                    sink.Add(MakeIssue(path, VerifySeverity.Warning, IssueCodes.CyclicClassName,
                        $"class_name \"{className}\" is also declared in {siblings.Count} other .gd file(s): {string.Join(", ", siblings)}",
                        BuildCyclicEvidence(path, className: className,
                            duplicateCount: group.Count.ToString(), siblings: string.Join(", ", siblings))));
                }
            }
        }

        // ---- Issue construction ------------------------------------------------

        private static VerifyIssue MakeIssue(
            string assetPath, VerifySeverity severity, string issueCode, string description,
            IReadOnlyDictionary<string, string> evidence)
            => new VerifyIssue(RuleId, severity, assetPath, issueCode, description, evidence);

        private static IReadOnlyDictionary<string, string> BuildMismatchEvidence(
            string assetPath, string recordedClass, string resolvedClass, string scriptPath, string resolution)
        {
            // Flat, small, additive — matches the sibling P14 rules' Evidence builders. The recorded vs
            // resolved class and the resolution basis (gd_class_name / cs_declaration / cs_filename) are
            // what an agent keys on to decide whether the mismatch is real (declaration) or a heuristic
            // false positive (file-name fallback).
            return new Dictionary<string, string>
            {
                ["kind"] = EvidenceKinds.ClassMismatch,
                ["assetPath"] = assetPath,
                ["recordedClass"] = recordedClass,
                ["resolvedClass"] = resolvedClass,
                ["scriptPath"] = scriptPath,
                ["resolution"] = resolution,
            };
        }

        private static IReadOnlyDictionary<string, string> BuildScriptEvidence(string scriptPath)
        {
            return new Dictionary<string, string>
            {
                ["kind"] = EvidenceKinds.MissingClassName,
                ["assetPath"] = scriptPath,
                ["scriptPath"] = scriptPath,
            };
        }

        private static IReadOnlyDictionary<string, string> BuildCyclicEvidence(
            string assetPath, string className, string duplicateCount, string siblings)
        {
            return new Dictionary<string, string>
            {
                ["kind"] = EvidenceKinds.CyclicClassName,
                ["assetPath"] = assetPath,
                ["className"] = className,
                ["duplicateCount"] = duplicateCount,
                ["siblings"] = siblings,
            };
        }

        // ---- Path helpers (mirror the sibling P14 rules) ----------------------

        private static bool IsSceneOrResourcePath(string resPath)
        {
            return resPath.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
                   || resPath.EndsWith(".tres", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGdPath(string resPath)
            => resPath.EndsWith(".gd", StringComparison.OrdinalIgnoreCase);

        private static bool IsCsPath(string resPath)
            => resPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

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

        // ---- File reading -----------------------------------------------------

        /// <summary>
        /// Read a file's text through the seam, returning null on any failure (missing file, unreadable,
        /// throwing reader). A null return means "skip" — the rule treats unreadable input as "not this
        /// rule's domain" rather than emitting a phantom issue.
        /// </summary>
        private string? ReadText(string resPath)
        {
            try
            {
                return _readFileText(resPath);
            }
            catch
            {
                // File vanished between checkpoint and validate, or unreadable. Contribute no issues.
                return null;
            }
        }

        /// <summary>
        /// <c>File.ReadAllText</c> wrapper that returns null when the file does not exist (rather than
        /// throwing). Used as the production file reader.
        /// </summary>
        private static string? TryReadAllText(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path);
        }

        // ---- Live resolver wiring (mirrors the sibling P14 rules) -------------

        private static IScriptAuditResolver GetLiveResolver()
        {
#if TOOLS
            return LiveScriptAuditResolver.Instance;
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
        private sealed class NullResolver : IScriptAuditResolver
        {
            public IReadOnlyList<ScriptFolderEntry> ListDirectory(string? resDir)
                => Array.Empty<ScriptFolderEntry>();
        }
#endif
    }
}
