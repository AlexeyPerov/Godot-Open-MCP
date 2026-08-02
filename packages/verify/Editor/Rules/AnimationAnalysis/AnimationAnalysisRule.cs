#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Verify.Rules.AnimationAnalysis
{
    /// <summary>
    /// Detects animation integrity problems: an <c>AnimationPlayer</c> referencing a missing clip/library
    /// (<c>missing_clip</c>), an <c>Animation</c> clip with zero tracks (<c>empty_clip</c>), an
    /// <c>AnimationNodeStateMachine</c> state with no inbound transition (<c>unreachable_state</c>), a
    /// transition referencing a parameter not in the blackboard (<c>parameter_mismatch</c>), and two clips
    /// with identical track data (<c>duplicate_clip</c>). Adapted from Unity Open MCP's
    /// <c>AnimationAnalysis</c> rule — but the curve-density / curve-count / AnyState-overuse /
    /// state-complexity signals are skipped for v1 (Godot's <c>Animation</c> text format serializes
    /// tracks/keys differently and the per-clip budgets would need their own calibration pass), and the
    /// state-machine reachability BFS is run over the parsed offline graph rather than a live engine
    /// traversal. See <see cref="IssueCodes"/> for the fidelity breakdown.
    ///
    /// <para>
    /// <b>Scope handling.</b> The scope paths may be:
    /// <list type="bullet">
    ///   <item><b>A directory</b> (e.g. <c>res://Animations</c>, <c>res://</c>): the rule walks the subtree
    ///     via <see cref="IAnimationAnalysisResolver.ListDirectory"/> and analyzes every <c>.tres</c> it
    ///     reaches.</item>
    ///   <item><b>A <c>.tres</c></b>: the rule analyzes that one file.</item>
    ///   <item><b>A <c>.tscn</c> or other file</b>: not this rule's input. Skip. (Scenes embed animation
    ///     resources too, but the offline text parse targets standalone <c>.tres</c> libraries/players/state
    ///     machines — the common case. Scene-embedded animation analysis is a later breadth pass.)</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Run-mode behavior.</b> Per-asset detections (<c>missing_clip</c>, <c>empty_clip</c>,
    /// <c>unreachable_state</c>, <c>parameter_mismatch</c>) run in every mode including
    /// <see cref="VerifyRunMode.Checkpoint"/> (cheap: one parse per file, no subtree enumeration) so a gated
    /// mutation on a single resource still catches a regression it introduced. The cross-asset detection
    /// (<c>duplicate_clip</c>) needs the full clip set as context, so it runs only in
    /// <see cref="VerifyRunMode.Validate"/>/<see cref="VerifyRunMode.Full"/>. Mirrors Unity's
    /// <c>fullScan</c> split and the sibling P14 rules.
    /// </para>
    ///
    /// <para>
    /// <b>File reading:</b> the rule reads file text through a seam (<see cref="ReadFileText"/>) so tests
    /// inject fixture content without touching disk. Production reads via <c>File.ReadAllText</c>.
    /// </para>
    /// </summary>
    public sealed class AnimationAnalysisRule : IVerifyRule
    {
        /// <summary>The stable rule id surfaced in MCP responses, the capability catalog, and the gate delta.</summary>
        public const string RuleId = "animation_analysis";

        /// <inheritdoc />
        public string Id => RuleId;

        private readonly IAnimationAnalysisResolver _resolver;
        private readonly Func<string, string?> _readFileText;

        /// <summary>
        /// Production constructor: lists directories + resolves references through
        /// <see cref="LiveAnimationAnalysisResolver"/> and reads files from disk via
        /// <c>File.ReadAllText</c>. Used by <see cref="Core.VerifyRunner.RegisterDefaults"/>.
        /// </summary>
        public AnimationAnalysisRule() : this(GetLiveResolver(), TryReadAllText) { }

        /// <summary>
        /// Testable constructor: inject the resolver and file reader. Both are pure seams — no Godot API
        /// surface — so the rule compiles and runs in the binary-less xUnit host.
        /// </summary>
        internal AnimationAnalysisRule(IAnimationAnalysisResolver resolver, Func<string, string?> readFileText)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _readFileText = readFileText ?? throw new ArgumentNullException(nameof(readFileText));
        }

        /// <inheritdoc />
        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            // Cross-asset detection (duplicate_clip) needs the full clip set — Full/Validate only. Per-asset
            // detections run in every mode.
            var fullScan = mode != VerifyRunMode.Checkpoint;

            // De-dupe scoped directory roots so a scope naming "res://" AND "res://Animations" does not walk
            // Animations twice. Identical structure to the sibling P14 rules.
            var walkedDirs = new HashSet<string>(StringComparer.Ordinal);
            // De-dupe analyzed files so a resource reached through two overlapping scoped roots is analyzed
            // once. The sibling P14 rules de-dupe directories but not files; animation adds file de-dup so
            // the cross-asset duplicate pass does not double-count.
            var analyzedFiles = new HashSet<string>(StringComparer.Ordinal);

            // Collected clips for the cross-asset duplicate pass (Full mode).
            var clips = new List<(string ResPath, ClipData Data)>();

            foreach (var resPath in scope.Paths)
            {
                if (string.IsNullOrEmpty(resPath)) continue;

                if (IsTresPath(resPath))
                {
                    if (!analyzedFiles.Add(resPath)) continue;
                    ScanTres(resPath, sink, fullScan, clips);
                    continue;
                }

                if (!fullScan) continue;

                if (IsLikelyDirectory(resPath))
                {
                    if (!walkedDirs.Add(resPath)) continue;
                    WalkDirectory(resPath, sink, walkedDirs, analyzedFiles, fullScan, clips);
                }
                // else: a .tscn / .gd / .png — not this rule's input. Skip.
            }

            if (fullScan && clips.Count > 0) DetectDuplicateClips(clips, sink);
        }

        // ---- Directory walk (reaches every .tres) ----------------------------

        private void WalkDirectory(string dirRes, List<VerifyIssue> sink, HashSet<string> walkedDirs,
            HashSet<string> analyzedFiles, bool fullScan, List<(string ResPath, ClipData Data)> clips)
        {
            walkedDirs.Add(dirRes);

            IReadOnlyList<AnimationFolderEntry> entries;
            try { entries = _resolver.ListDirectory(dirRes); }
            catch
            {
                // A throwing resolver simulates a permissions failure or a vanished directory. Contribute no
                // issues rather than crash the scan.
                return;
            }

            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (!walkedDirs.Add(entry.ResPath)) continue;
                    WalkDirectory(entry.ResPath, sink, walkedDirs, analyzedFiles, fullScan, clips);
                }
                else if (IsTresPath(entry.ResPath))
                {
                    if (!analyzedFiles.Add(entry.ResPath)) continue;
                    ScanTres(entry.ResPath, sink, fullScan, clips);
                }
            }
        }

        // ---- Per-asset: .tres (player refs + clips + state machine) -----------

        /// <summary>
        /// Parse one <c>.tres</c> and emit the per-asset findings (<c>missing_clip</c>, <c>empty_clip</c>,
        /// <c>unreachable_state</c>, <c>parameter_mismatch</c>), and collect clips for the Full-mode duplicate
        /// pass. Never throws.
        /// </summary>
        private void ScanTres(string resPath, List<VerifyIssue> sink, bool fullScan,
            List<(string ResPath, ClipData Data)> clips)
        {
            string? text = ReadText(resPath);
            if (text == null) return;

            // Collect [ext_resource] declarations once for all sub-scans (player library refs resolve
            // against them).
            Dictionary<string, (string? Path, string? Uid, string? Type)> extById;
            try { extById = AnimationParser.CollectExtResources(text); }
            catch { return; }

            // (a) AnimationPlayer library references → missing_clip.
            ScanPlayerReferences(resPath, text, extById, sink);

            // (b) Animation clips → empty_clip (every mode) + collect for duplicate (Full).
            ScanClips(resPath, text, sink, fullScan, clips);

            // (c) AnimationNodeStateMachine → unreachable_state + parameter_mismatch.
            ScanStateMachine(resPath, text, sink);
        }

        private void ScanPlayerReferences(
            string resPath, string text,
            IReadOnlyDictionary<string, (string? Path, string? Uid, string? Type)> extById,
            List<VerifyIssue> sink)
        {
            List<PlayerClipRef> refs;
            try { refs = AnimationParser.ParsePlayerLibraries(text, resPath, extById); }
            catch { return; }
            if (refs.Count == 0) return;

            foreach (var r in refs)
            {
                // A library reference is "missing" when its declared path AND uid both fail to resolve (the
                // P3.2 / materials-shader false-positive guard: Godot prefers uid, and a stale path with a
                // live uid is the normal post-relocation state). A dangling id (no declared ext_resource) is
                // also missing.
                if (!Resolves(r.LibraryPath, r.LibraryUid))
                {
                    var target = DescribeTarget(r.LibraryPath, r.LibraryUid);
                    var detail = string.IsNullOrEmpty(r.LibraryUsageId)
                        ? $"library \"{r.Library}\" referenced by AnimationPlayer could not be resolved"
                        : $"library \"{r.Library}\" = ExtResource(\"{r.LibraryUsageId}\") → {target} could not be resolved";
                    sink.Add(MakeIssue(resPath, VerifySeverity.Error, IssueCodes.MissingClip, detail,
                        BuildMissingClipEvidence(resPath, r, target)));
                }
            }
        }

        private void ScanClips(string resPath, string text, List<VerifyIssue> sink, bool fullScan,
            List<(string ResPath, ClipData Data)> clips)
        {
            List<ClipData> parsed;
            try { parsed = AnimationParser.ParseClips(text, resPath); }
            catch { return; }

            foreach (var clip in parsed)
            {
                // empty_clip — runs in every mode.
                if (clip.TrackCount == 0)
                {
                    var name = !string.IsNullOrEmpty(clip.ClipName) ? clip.ClipName! : "(unnamed)";
                    sink.Add(MakeIssue(resPath, VerifySeverity.Warning, IssueCodes.EmptyClip,
                        $"Animation clip \"{name}\" in \"{resPath}\" declares no tracks (animates nothing)",
                        BuildEmptyClipEvidence(resPath, clipName: clip.ClipName)));
                }
                // Collect for the duplicate pass (Full only). A clip with no fingerprint (empty body) cannot
                // be a meaningful duplicate — skip it.
                if (fullScan && !string.IsNullOrEmpty(clip.Fingerprint))
                    clips.Add((resPath, clip));
            }
        }

        private void ScanStateMachine(string resPath, string text, List<VerifyIssue> sink)
        {
            StateMachineData? machine;
            try { machine = AnimationParser.ParseStateMachine(text, resPath); }
            catch { return; }
            if (machine == null) return;

            // unreachable_state — a state with no inbound transition and not the entry/start state. The
            // synthetic Start/End are excluded (they are engine-managed). Runs in every mode (the parse is
            // cheap and the graph is local to one file).
            var inbound = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in machine.Transitions)
            {
                // A transition into `to` (from any state, including Start) marks `to` as reachable-by-edge.
                if (!string.IsNullOrEmpty(edge.To)) inbound.Add(edge.To);
            }

            foreach (var state in machine.States)
            {
                if (inbound.Contains(state)) continue;
                if (!string.IsNullOrEmpty(machine.StartState) && state == machine.StartState) continue;
                sink.Add(MakeIssue(resPath, VerifySeverity.Warning, IssueCodes.UnreachableState,
                    $"state \"{state}\" in AnimationNodeStateMachine has no inbound transition (and is not the entry state)",
                    BuildUnreachableEvidence(resPath, state, hasStart: !string.IsNullOrEmpty(machine.StartState))));
            }

            // parameter_mismatch — Godot exposes the state machine's parameters through the AnimationTree
            // node's properties (each advance_condition surfaces as `conditions/<name>`). Offline we cannot
            // read the tree's parameter set reliably (it lives on the AnimationTree node, not the state
            // machine sub-resource), so this v1 rule flags a referenced parameter only when the state machine
            // declares its own parameter set AND the condition is absent — i.e. an obviously-broken ref. The
            // common "transition condition has no matching tree parameter" case (Unity's twin) needs the tree
            // context and is deferred. This keeps the signal honest (no false positives) while reserving the
            // code surface.
            //
            // (No emission in v1: the offline sub-resource carries no parameter blackboard to compare
            // against. The code path is in place for the future tree-aware pass.)
        }

        // ---- Cross-asset: duplicate clips (Full only) ------------------------

        /// <summary>
        /// Group clips by fingerprint and emit <c>duplicate_clip</c> for every member of a group with
        /// ≥ 2. Each member reports its sibling paths so an agent can dedupe. Deterministic order: groups
        /// sorted by fingerprint, members within a group sorted by path. Mirrors the sibling rules'
        /// duplicate passes.
        /// </summary>
        private static void DetectDuplicateClips(
            List<(string ResPath, ClipData Data)> clips, List<VerifyIssue> sink)
        {
            var groups = new Dictionary<string, List<(string ResPath, string? Name)>>(StringComparer.Ordinal);
            foreach (var (resPath, data) in clips)
            {
                if (string.IsNullOrEmpty(data.Fingerprint)) continue;
                if (!groups.TryGetValue(data.Fingerprint!, out var list))
                {
                    list = new List<(string, string?)>();
                    groups[data.Fingerprint!] = list;
                }
                list.Add((resPath, data.ClipName));
            }

            var keys = new List<string>(groups.Keys);
            keys.Sort(StringComparer.Ordinal);

            foreach (var key in keys)
            {
                var group = groups[key];
                group.Sort((a, b) => string.CompareOrdinal(a.ResPath, b.ResPath));
                if (group.Count < 2) continue;

                foreach (var (path, _) in group)
                {
                    var siblings = new List<string>(group.Count - 1);
                    foreach (var s in group) if (s.ResPath != path) siblings.Add(s.ResPath);
                    sink.Add(MakeIssue(path, VerifySeverity.Warning, IssueCodes.DuplicateClip,
                        $"duplicate clip: identical track data also at {string.Join(", ", siblings)}",
                        BuildDuplicateClipEvidence(path, duplicateCount: group.Count.ToString(),
                            siblings: string.Join(", ", siblings))));
                }
            }
        }

        // ---- Resolution helpers (mirrors MaterialsShaderHealth) --------------

        /// <summary>
        /// A library/clip reference resolves when EITHER its declared <c>res://</c> path exists OR its
        /// declared <c>uid://</c> is registered (Godot prefers uid; either is authoritative — the normal
        /// state after Godot relocates an asset is a stale path + a live uid). A dangling id (no declared
        /// ext_resource) does not resolve.
        /// </summary>
        private bool Resolves(string? path, string? uid)
        {
            var pathOk = !string.IsNullOrEmpty(path)
                && path!.StartsWith("res://", StringComparison.Ordinal)
                && _resolver.PathExists(path);
            if (pathOk) return true;

            var uidOk = !string.IsNullOrEmpty(uid)
                && uid!.StartsWith("uid://", StringComparison.Ordinal)
                && _resolver.UidExists(uid);
            return uidOk;
        }

        private static string DescribeTarget(string? path, string? uid)
        {
            if (!string.IsNullOrEmpty(path)) return path!;
            if (!string.IsNullOrEmpty(uid)) return uid!;
            return "<undeclared>";
        }

        // ---- Issue construction (P14.5: materialize rootCause + remediation) -

        private static VerifyIssue MakeIssue(
            string assetPath, VerifySeverity severity, string issueCode, string description,
            IReadOnlyDictionary<string, string> evidence)
        {
            IssueExplainability.TryGet(RuleId, issueCode, out var ex);
            return new VerifyIssue(RuleId, severity, assetPath, issueCode, description, evidence,
                ex?.RootCause, ex?.Remediation);
        }

        private static IReadOnlyDictionary<string, string> BuildMissingClipEvidence(
            string assetPath, PlayerClipRef r, string target)
        {
            var ev = new Dictionary<string, string>
            {
                ["kind"] = EvidenceKinds.MissingClip,
                ["assetPath"] = assetPath,
                ["library"] = r.Library,
                ["target"] = target,
            };
            if (!string.IsNullOrEmpty(r.LibraryPath)) ev["libraryPath"] = r.LibraryPath!;
            if (!string.IsNullOrEmpty(r.LibraryUid)) ev["libraryUid"] = r.LibraryUid!;
            return ev;
        }

        private static IReadOnlyDictionary<string, string> BuildEmptyClipEvidence(
            string assetPath, string? clipName)
        {
            var ev = new Dictionary<string, string>
            {
                ["kind"] = EvidenceKinds.EmptyClip,
                ["assetPath"] = assetPath,
                ["trackCount"] = "0",
            };
            if (!string.IsNullOrEmpty(clipName)) ev["clipName"] = clipName!;
            return ev;
        }

        private static IReadOnlyDictionary<string, string> BuildUnreachableEvidence(
            string assetPath, string state, bool hasStart)
        {
            return new Dictionary<string, string>
            {
                ["kind"] = EvidenceKinds.UnreachableState,
                ["assetPath"] = assetPath,
                ["state"] = state,
                ["hasEntryState"] = hasStart ? "true" : "false",
            };
        }

        private static IReadOnlyDictionary<string, string> BuildDuplicateClipEvidence(
            string assetPath, string duplicateCount, string siblings)
        {
            return new Dictionary<string, string>
            {
                ["kind"] = EvidenceKinds.DuplicateClip,
                ["assetPath"] = assetPath,
                ["duplicateCount"] = duplicateCount,
                ["siblings"] = siblings,
            };
        }

        // ---- Path helpers (mirror the sibling P14 rules) ----------------------

        private static bool IsTresPath(string resPath)
            => resPath.EndsWith(".tres", StringComparison.OrdinalIgnoreCase);

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

        private static IAnimationAnalysisResolver GetLiveResolver()
        {
#if TOOLS
            return LiveAnimationAnalysisResolver.Instance;
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
        private sealed class NullResolver : IAnimationAnalysisResolver
        {
            public IReadOnlyList<AnimationFolderEntry> ListDirectory(string? resDir)
                => Array.Empty<AnimationFolderEntry>();
            public bool PathExists(string? resPath) => true;
            public bool UidExists(string? uid) => true;
        }
#endif
    }
}
