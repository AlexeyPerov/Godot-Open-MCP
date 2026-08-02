#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Verify.Rules.ProjectHealth
{
    /// <summary>
    /// Detects project-level structural cruft and integrity breaks across the <c>res://</c> tree: empty
    /// folders, uid-sidecar-only folders, deep folder nesting, oversized flat folders, structurally
    /// broken <c>.tres</c>/<c>.tscn</c> assets, and root-only (empty) scenes. Adapted from Unity Open
    /// MCP's <c>ProjectHealth</c> + <c>OfflineIntegrity</c> rules — folder-walk heuristics and the
    /// empty/deep/large thresholds are direct ports; the broken-asset and empty-scene checks are adapted
    /// to Godot's text-serialized formats (parse the text rather than call
    /// <c>AssetDatabase.LoadMainAssetAtPath</c>).
    ///
    /// <para>
    /// <b>Relationship to <c>import_health</c> (P3.4):</b> the import-health rule already covers orphan
    /// <c>.import</c> sidecars and duplicate <c>uid://</c> declarations. <c>project_health</c> covers the
    /// folder/structure/broken-asset/empty-scene signals only — no overlap (see
    /// <c>specs/execution/P14/P14.1.md</c> Design Decision 1). A folder whose only children are
    /// <c>.import</c> sidecars is NOT flagged here (those sidecars are the import-health rule's domain,
    /// and their sources may be healthy); only a folder whose only children are <c>*.uid</c> sidecars
    /// (a script moved away, leaving <c>.gd.uid</c> companions) is flagged as <c>uid_only_folder</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Scope handling.</b> The scope paths may be:
    /// <list type="bullet">
    ///   <item><b>A directory</b> (e.g. <c>res://Sprites</c>, <c>res://</c>): the rule walks the subtree
    ///     via <see cref="IProjectHealthResolver.ListDirectory"/>, applying the folder checks to every
    ///     directory it reaches and the asset checks to every <c>.tres</c>/<c>.tscn</c> file it
    ///     reaches.</item>
    ///   <item><b>A <c>.tres</c>/<c>.tscn</c> file</b> (e.g. <c>res://Main.tscn</c>): the rule runs the
    ///     asset checks on that one file. Folder checks do not apply (the scope names a file, not a
    ///     subtree).</item>
    ///   <item><b>An arbitrary other file</b> (a <c>.gd</c>, a <c>.png</c>): not this rule's input. Skip.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Run-mode behavior.</b> <c>project_health</c> is a whole-project rule by nature — folder depth,
    /// child counts, and the broken-asset/empty-scene inventory all need a subtree walk to be correct,
    /// which a header-only checkpoint mutation should not pay for. The rule therefore runs only in
    /// <see cref="VerifyRunMode.Validate"/> and <see cref="VerifyRunMode.Full"/>; a checkpoint pass
    /// contributes nothing (mirrors Unity's <c>ProjectHealthRule</c>, which early-returns when
    /// <c>mode != Full</c>, and the P3.2/P3.3/P3.4 precedent that cross-file walks are
    /// checkpoint-skippable). The exception is a scope that names a <c>.tres</c>/<c>.tscn</c> directly:
    /// the broken-asset + empty-scene checks on that one file are cheap (one parse, no walk), so they
    /// run on checkpoint too — a gated mutation on a single scene should still catch a hand-edit that
    /// truncated it.
    /// </para>
    ///
    /// <para>
    /// <b>Thresholds.</b> Empty ≤ 0 recursive files; deep > 8; large > 200 direct children. Constants for
    /// v1 (see <c>specs/execution/P14/P14.1.md</c> Design Decision 2); configurable later via
    /// <c>.godot-open-mcp/settings.json</c>. The depth is measured from the <c>res://</c> root
    /// (<c>res://</c> = depth 0; <c>res://Sprites</c> = depth 1; ...), matching how a developer reads
    /// the project tree.
    /// </para>
    ///
    /// <para>
    /// <b>Deterministic ordering:</b> folder issues are emitted in walk order (the resolver returns
    /// directories first, sorted by name, at each level); asset issues are emitted in walk order too.
    /// Two identical scans produce identical issue sequences — important for gate-delta stability.
    /// </para>
    ///
    /// <para>
    /// <b>File reading:</b> the rule reads <c>.tres</c>/<c>.tscn</c> text through a seam
    /// (<see cref="ReadFileText"/>) so tests inject fixture content without touching disk. Production
    /// reads via <c>File.ReadAllText</c>; the verify gate runs in-editor against the real project tree.
    /// A read failure (file gone between checkpoint and validate) is swallowed — that file contributes
    /// no issues rather than crashing the scan, matching the "must not throw" contract.
    /// </para>
    /// </summary>
    public sealed class ProjectHealthRule : IVerifyRule
    {
        /// <summary>The stable rule id surfaced in MCP responses, the capability catalog, and the gate delta.</summary>
        public const string RuleId = "project_health";

        /// <inheritdoc />
        public string Id => RuleId;

        // ---- Thresholds (v1 constants; see Design Decision 2) -----------------

        /// <summary>
        /// Folders deeper than this from <c>res://</c> are flagged <c>deep_nesting</c>. Default 8 —
        /// matches Unity's <c>ProjectHealth</c> default and the value in the canonical issue roster.
        /// </summary>
        private const int DeepNestingThreshold = 8;

        /// <summary>
        /// Folders with more than this many direct child entries (files + sub-directories) are flagged
        /// <c>large_folder</c>. Default 200 — matches Unity's <c>ProjectHealth</c> default.
        /// </summary>
        private const int LargeFolderThreshold = 200;

        private readonly IProjectHealthResolver _resolver;
        private readonly Func<string, string?> _readFileText;

        /// <summary>
        /// Production constructor: lists directories + checks existence through
        /// <see cref="LiveProjectHealthResolver"/> and reads files from disk via
        /// <see cref="File.ReadAllText"/>. Used by <see cref="Core.VerifyRunner.RegisterDefaults"/>.
        /// </summary>
        public ProjectHealthRule() : this(GetLiveResolver(), File.ReadAllText) { }

        /// <summary>
        /// Testable constructor: inject the resolver and file reader. Both are pure seams — no Godot API
        /// surface — so the rule compiles and runs in the binary-less xUnit host.
        /// </summary>
        internal ProjectHealthRule(IProjectHealthResolver resolver, Func<string, string?> readFileText)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _readFileText = readFileText ?? throw new ArgumentNullException(nameof(readFileText));
        }

        /// <inheritdoc />
        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            // The folder walk is Validate/Full only — it needs a subtree enumeration to be correct, which
            // a header-only checkpoint should not pay for. The single-file asset checks CAN run on
            // checkpoint when the scope names a .tres/.tscn directly (cheap, no walk), so we do not
            // early-return wholesale; we branch per scoped path below.
            var fullScan = mode != VerifyRunMode.Checkpoint;

            // De-dupe scoped directory roots so a scope that names "res://" AND "res://Sprites" does not
            // walk Sprites twice. Tracked by canonical res:// directory path.
            var walkedDirs = new HashSet<string>(StringComparer.Ordinal);

            foreach (var resPath in scope.Paths)
            {
                if (string.IsNullOrEmpty(resPath)) continue;

                if (IsSceneOrResourcePath(resPath))
                {
                    // A directly-scoped .tres/.tscn: run the asset checks on it in every mode (cheap, no
                    // walk). This catches a hand-edit that truncated a scene even on a checkpoint pass.
                    ScanAsset(resPath, sink);
                    continue;
                }

                if (!fullScan) continue;

                if (IsLikelyDirectory(resPath))
                {
                    // A directory subtree: walk it once per root. A scope that names a parent and its child
                    // walks the shared subtree once.
                    if (!walkedDirs.Add(resPath)) continue;
                    WalkDirectory(resPath, depth: DepthOf(resPath), sink, walkedDirs);
                }
                // else: a non-.tres/.tscn file (a .gd, a .png) — not this rule's input. Skip.
            }
        }

        // ---- Directory walk ---------------------------------------------------

        /// <summary>
        /// Recursively walk a <c>res://</c> directory, applying the folder checks to <paramref name="dirRes"/>
        /// and the asset checks to every <c>.tres</c>/<c>.tscn</c> reached. <paramref name="walkedDirs"/>
        /// accumulates every directory entered so a later scoped root that contains this one does not
        /// re-walk it.
        /// </summary>
        private void WalkDirectory(string dirRes, int depth, List<VerifyIssue> sink, HashSet<string> walkedDirs)
        {
            // The scoped root itself is "entered"; mark it so a sibling scope that names it again is a
            // no-op. (The Scan loop already de-dupes top-level scoped roots; this covers sub-directories
            // that two overlapping scoped roots would both reach.)
            walkedDirs.Add(dirRes);

            IReadOnlyList<ProjectFolderEntry> entries;
            try
            {
                entries = _resolver.ListDirectory(dirRes);
            }
            catch
            {
                // A throwing resolver simulates a permissions failure or a vanished directory between
                // checkpoint and validate. Contribute no issues rather than crash the scan.
                return;
            }

            // Folder checks run on the directory's OWN contents (empty / uid-only / large). Deep-nesting
            // runs on the directory itself (its depth from res://).
            CheckFolderStructure(dirRes, depth, entries, sink);

            // Recurse into sub-directories; parse .tres/.tscn files reached.
            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (!walkedDirs.Add(entry.ResPath)) continue; // already walked under another root
                    WalkDirectory(entry.ResPath, depth + 1, sink, walkedDirs);
                }
                else if (IsSceneOrResourcePath(entry.ResPath))
                {
                    ScanAsset(entry.ResPath, sink);
                }
            }
        }

        /// <summary>
        /// Apply the four folder checks (empty, uid-only, deep, large) to one directory. The empty and
        /// uid-only checks are mutually exclusive on the same directory (a truly empty folder reports
        /// empty; a folder with only uid sidecars reports uid-only) — empty wins because it is the more
        /// surprising signal, matching Unity's ProjectHealth precedent that a zero-file folder reports as
        /// its empty classification.
        /// </summary>
        private static void CheckFolderStructure(
            string dirRes, int depth, IReadOnlyList<ProjectFolderEntry> entries, List<VerifyIssue> sink)
        {
            // Empty / uid-only: classify by whether the directory has any non-uid files at all (the walk
            // already recursed, so "empty" here means no immediate children of any kind; a directory with
            // only sub-directories is NOT empty — it holds structure).
            var hasFiles = false;
            var uidSidecarCount = 0;
            foreach (var entry in entries)
            {
                if (entry.IsDirectory) continue;
                hasFiles = true;
                if (IsUidSidecar(entry.Name)) uidSidecarCount++;
            }

            // Large-folder: direct child count (files + sub-dirs) over the threshold. Checked on the full
            // entry list (not just files) — a folder with 300 sub-directories is just as hostile to
            // navigation as one with 300 files.
            if (entries.Count > LargeFolderThreshold)
            {
                sink.Add(MakeFolderIssue(dirRes, VerifySeverity.Warning, IssueCodes.LargeFolder,
                    $"{entries.Count} direct children in folder \"{dirRes}\" exceed threshold {LargeFolderThreshold}",
                    BuildFolderEvidence(EvidenceKinds.LargeFolder, dirRes, childCount: entries.Count.ToString(),
                        threshold: LargeFolderThreshold.ToString())));
            }

            // Deep-nesting: depth from res:// over the threshold. res:// itself is depth 0; a folder at
            // depth 9 flags (9 > 8).
            if (depth > DeepNestingThreshold)
            {
                sink.Add(MakeFolderIssue(dirRes, VerifySeverity.Warning, IssueCodes.DeepNesting,
                    $"folder \"{dirRes}\" nesting depth {depth} exceeds threshold {DeepNestingThreshold}",
                    BuildFolderEvidence(EvidenceKinds.DeepNesting, dirRes, depth: depth.ToString(),
                        threshold: DeepNestingThreshold.ToString())));
            }

            if (hasFiles)
            {
                // Has files — check whether ALL of them are uid sidecars (a script moved away, leaving
                // .gd.uid companions). A folder with at least one non-uid file is healthy from this rule's
                // point of view (the non-uid file is a real asset).
                if (uidSidecarCount > 0 && uidSidecarCount == CountFiles(entries))
                {
                    sink.Add(MakeFolderIssue(dirRes, VerifySeverity.Warning, IssueCodes.UidOnlyFolder,
                        $"folder \"{dirRes}\" contains only uid sidecars ({uidSidecarCount}) — scripts were moved or deleted",
                        BuildFolderEvidence(EvidenceKinds.UidOnlyFolder, dirRes,
                            sidecarCount: uidSidecarCount.ToString())));
                }
                return;
            }

            // No immediate file children. If it also has no sub-directories, it is empty (a leaf folder
            // with nothing in it). A directory with only sub-directories is structural, not empty.
            var hasSubDirs = false;
            foreach (var entry in entries)
            {
                if (entry.IsDirectory) { hasSubDirs = true; break; }
            }

            if (!hasSubDirs)
            {
                sink.Add(MakeFolderIssue(dirRes, VerifySeverity.Warning, IssueCodes.EmptyFolder,
                    $"folder \"{dirRes}\" is empty (no files, no sub-directories)",
                    BuildFolderEvidence(EvidenceKinds.EmptyFolder, dirRes)));
            }
        }

        /// <summary>Count the file (non-directory) entries in a listing.</summary>
        private static int CountFiles(IReadOnlyList<ProjectFolderEntry> entries)
        {
            var n = 0;
            foreach (var e in entries) if (!e.IsDirectory) n++;
            return n;
        }

        // ---- Asset checks (broken-asset + empty-scene) ------------------------

        /// <summary>
        /// Parse one <c>.tres</c>/<c>.tscn</c> and emit a <c>broken_asset</c> finding if it fails to
        /// parse, or an <c>empty_scene</c> finding if it is a root-only scene. Never throws — a read or
        /// parse failure is swallowed and contributes no issues (a vanished file is not a broken asset).
        /// </summary>
        private void ScanAsset(string resPath, List<VerifyIssue> sink)
        {
            string text;
            try
            {
                var raw = _readFileText(resPath);
                if (raw == null) return;
                text = raw;
            }
            catch
            {
                // File vanished between checkpoint and validate, or unreadable. Contribute no issues
                // rather than throw — the "must not throw on ordinary malformed assets" contract.
                return;
            }

            ProjectAssetParseResult parsed;
            try
            {
                parsed = ProjectAssetParser.Parse(text);
            }
            catch
            {
                // The parser is defensive, but guard the call so a future parser change can never crash a
                // scoped gate check.
                return;
            }

            if (!parsed.IsValid)
            {
                // Broken asset — structural parse failure. Error: a broken asset can fail scene load.
                sink.Add(MakeAssetIssue(resPath, VerifySeverity.Error, IssueCodes.BrokenAsset,
                    $"asset \"{resPath}\" failed to parse: {parsed.FailureDetail ?? "unknown parse failure"}",
                    BuildAssetEvidence(EvidenceKinds.BrokenAsset, resPath, parsed.FailureDetail)));
                return;
            }

            // Empty scene — a .tscn with only a root node (NodeCount == 1). Godot scenes always have
            // exactly one root, so a single-node scene is the Godot analogue of Unity's zero-root scene.
            if (parsed.IsScene && parsed.NodeCount == 1)
            {
                sink.Add(MakeAssetIssue(resPath, VerifySeverity.Warning, IssueCodes.EmptyScene,
                    $"scene \"{resPath}\" has only a root node — no children (effectively empty)",
                    BuildAssetEvidence(EvidenceKinds.EmptyScene, resPath, nodeCount: "1")));
            }
        }

        // ---- Issue construction ------------------------------------------------

        // P14.5: both helpers materialize the explainability taxonomy (rootCause + remediation) onto the
        // issue. The pair is resolved by issueCode, so each project_health code gets its own rootCause.
        private static VerifyIssue MakeFolderIssue(
            string assetPath, VerifySeverity severity, string issueCode, string description,
            IReadOnlyDictionary<string, string> evidence)
        {
            IssueExplainability.TryGet(RuleId, issueCode, out var ex);
            return new VerifyIssue(RuleId, severity, assetPath, issueCode, description, evidence, ex?.RootCause, ex?.Remediation);
        }

        private static VerifyIssue MakeAssetIssue(
            string assetPath, VerifySeverity severity, string issueCode, string description,
            IReadOnlyDictionary<string, string> evidence)
        {
            IssueExplainability.TryGet(RuleId, issueCode, out var ex);
            return new VerifyIssue(RuleId, severity, assetPath, issueCode, description, evidence, ex?.RootCause, ex?.Remediation);
        }

        private static IReadOnlyDictionary<string, string> BuildFolderEvidence(
            string kind, string folderPath, string? childCount = null, string? depth = null,
            string? threshold = null, string? sidecarCount = null)
        {
            var ev = new Dictionary<string, string> { ["kind"] = kind, ["folderPath"] = folderPath };
            if (childCount != null) ev["childCount"] = childCount;
            if (depth != null) ev["depth"] = depth;
            if (threshold != null) ev["threshold"] = threshold;
            if (sidecarCount != null) ev["sidecarCount"] = sidecarCount;
            return ev;
        }

        private static IReadOnlyDictionary<string, string> BuildAssetEvidence(
            string kind, string assetPath, string? detail = null, string? nodeCount = null)
        {
            var ev = new Dictionary<string, string> { ["kind"] = kind, ["assetPath"] = assetPath };
            if (detail != null) ev["detail"] = detail;
            if (nodeCount != null) ev["nodeCount"] = nodeCount;
            return ev;
        }

        // ---- Path helpers ------------------------------------------------------

        private static bool IsSceneOrResourcePath(string resPath)
            => resPath.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
               || resPath.EndsWith(".tres", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a name is a Godot uid sidecar (<c>*.uid</c>). Godot 4.4+ writes a <c>&lt;asset&gt;.uid</c>
        /// companion next to scripts/resources; the most common case is <c>.gd.uid</c> (a script's uid
        /// sidecar). Case-insensitive to match Godot's filesystem tolerance.
        /// </summary>
        private static bool IsUidSidecar(string name)
            => name.EndsWith(".uid", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a <c>res://</c> path looks like a directory rather than a file — same heuristic as
        /// <see cref="ImportHealth.ImportHealthRule"/>: the leaf segment (after the last <c>/</c>) has no
        /// <c>.</c> extension. <c>res://</c>, <c>res://Sprites</c>, and <c>res://Sprites/</c> are
        /// directories; <c>res://Main.tscn</c> is a file.
        /// </summary>
        private static bool IsLikelyDirectory(string resPath)
        {
            var trimmed = resPath.TrimEnd('/');
            var lastSlash = trimmed.LastIndexOf('/');
            var leaf = lastSlash >= 0 ? trimmed.Substring(lastSlash + 1) : trimmed;
            return !leaf.Contains('.');
        }

        /// <summary>
        /// Depth of a <c>res://</c> directory from the project root. <c>res://</c> = 0;
        /// <c>res://Sprites</c> = 1; <c>res://Sprites/Hero</c> = 2. Used by the deep-nesting check.
        /// A trailing slash is tolerated (<c>res://Sprites/</c> = 1).
        /// </summary>
        private static int DepthOf(string resDir)
        {
            // Strip the "res://" scheme BEFORE trimming the trailing slash — the same trap
            // LiveImportHealthResolver.EnumerateImportSidecars documents: a naive TrimEnd('/') on the
            // project root "res://" yields "res:", and the StartsWith("res://") check then fails,
            // mis-classifying the root. Strip the scheme first, then count slash-separated segments.
            if (!resDir.StartsWith("res://", StringComparison.Ordinal)) return 0;
            var withoutScheme = resDir.Substring("res://".Length).Trim('/');
            if (withoutScheme.Length == 0) return 0; // res:// itself
            // "Sprites" → 1 segment → depth 1; "Sprites/Hero" → 2 segments → depth 2.
            var depth = 0;
            foreach (var c in withoutScheme) if (c == '/') depth++;
            return depth + 1;
        }

        // ---- Live resolver wiring (mirrors P3.2/P3.3/P3.4) --------------------

        private static IProjectHealthResolver GetLiveResolver()
        {
            // Single #if TOOLS boundary, identical pattern to P3.2/P3.3/P3.4. Without TOOLS (the
            // binary-less test host links the rule directly), LiveProjectHealthResolver does not exist
            // and we fall back to a resolver that lists nothing — the production path is never taken from
            // tests; tests always use the internal constructor.
#if TOOLS
            return LiveProjectHealthResolver.Instance;
#else
            return new NullResolver();
#endif
        }

#if !TOOLS
        /// <summary>
        /// Fallback resolver for the non-TOOLS compile path (binary-less test host). Tests never reach it
        /// — they inject through the internal constructor — but the parameterless constructor must still
        /// compile. Treats every directory as empty and every file as missing so the production-less host
        // never emits spurious issues if it is ever accidentally exercised. Mirrors P3.2/P3.3/P3.4's
        // NullResolver.
        /// </summary>
        private sealed class NullResolver : IProjectHealthResolver
        {
            public IReadOnlyList<ProjectFolderEntry> ListDirectory(string? resDir)
                => Array.Empty<ProjectFolderEntry>();
            public bool FileExists(string? resPath) => true;
        }
#endif
    }

    /// <summary>
    /// Values placed in <c>Evidence["kind"]</c> to distinguish project-health failure modes. Kept
    /// internal because the stable surface is the issue codes in <see cref="IssueCodes"/>. Agent-facing
    /// diagnostics can branch on this; future fix providers can use it to pick a strategy.
    /// </summary>
    internal static class EvidenceKinds
    {
        public const string EmptyFolder = "empty_folder";
        public const string UidOnlyFolder = "uid_only_folder";
        public const string DeepNesting = "deep_nesting";
        public const string LargeFolder = "large_folder";
        public const string BrokenAsset = "broken_asset";
        public const string EmptyScene = "empty_scene";
    }
}
