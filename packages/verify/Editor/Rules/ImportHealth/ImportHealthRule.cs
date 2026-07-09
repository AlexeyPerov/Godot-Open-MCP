#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Verify.Rules.ImportHealth
{
    /// <summary>
    /// Detects Godot import-metadata integrity problems: orphan <c>.import</c> sidecars (metadata whose
    /// source asset is gone) and duplicate <c>uid://</c> declarations (two sidecars claiming the same
    /// uid). Emits <see cref="IssueCodes.OrphanImport"/> (Warning) and <see cref="IssueCodes.DuplicateUid"/>
    /// (Error). Greenfield for Godot — Unity has no sidecar concept and uid integrity is Godot-specific.
    ///
    /// <para>
    /// <b>What an orphan sidecar is:</b> Godot writes a <c>&lt;asset&gt;.import</c> sidecar next to every
    /// imported source asset. When the source is deleted outside the editor (or moved without a rescan),
    /// the sidecar is left behind as orphan metadata. Left un-cleaned, it can cause nondeterministic
    /// re-imports and phantom editor errors. The check parses the sidecar's <c>source=</c> and asks the
    /// resolver whether that source still exists on disk.
    /// </para>
    ///
    /// <para>
    /// <b>What a duplicate uid is:</b> Godot assigns a globally-unique <c>uid://</c> to every imported
    /// resource and writes it into the sidecar's <c>uid=</c> field. A collision (two sidecars, same uid)
    /// means the engine cannot deterministically resolve a <c>uid://</c> reference, causing load/import
    /// nondeterminism — usually the result of a copy-paste that carried a uid, or a merge that combined
    /// two history lines. The check indexes every sidecar's <c>uid=</c> across the scope and flags any
    /// uid claimed by two or more sidecars.
    /// </para>
    ///
    /// <para>
    /// <b>Scope handling.</b> The scope paths may be:
    /// <list type="bullet">
    ///   <item><b>A <c>.import</c> file</b> (e.g. <c>res://Sprites/Player.png.import</c>): parse it
    ///     directly for the orphan check, and include it in the duplicate-uid index.</item>
    ///   <item><b>A directory or arbitrary path</b> (e.g. <c>res://Sprites</c>, a <c>.tscn</c>): the rule
    ///     enumerates sidecars under that subtree via <see cref="IImportHealthResolver.EnumerateImportSidecars"/>
    ///     to build the working set. A directory scoped path is treated as a subtree root.</item>
    /// </list>
    /// The working set is the union of (a) scoped paths that are themselves <c>.import</c> files and
    /// (b) sidecars enumerated under any non-<c>.import</c> scoped path. The orphan check runs over the
    /// working set; the duplicate-uid check runs over the same set. This keeps a scoped gate check
    /// bounded: a checkpoint over a single scene does not enumerate the whole <c>res://</c> tree.
    /// </para>
    ///
    /// <para>
    /// <b>Run-mode behavior</b> (mirrors the P3.2/P3.3 <c>fullScan = mode != Checkpoint</c> split): the
    /// orphan check runs in every mode — it is cheap (one file-existence probe per sidecar, no tree
    /// walk beyond what the scope already names). The duplicate-uid check is a <c>Validate</c>/<c>Full</c>
    /// -only pass because it must build a cross-sidecar index and a header-only checkpoint mutation
    /// should not pay for the full tree enumeration it would need to be complete. (When the scope
    /// already lists <c>.import</c> paths directly the duplicate check still runs on checkpoint — there
    /// is no extra walk — but the common case of a scene-scoped checkpoint skips it.)
    /// </para>
    ///
    /// <para>
    /// <b>Deterministic ordering:</b> orphan issues are emitted in working-set order; duplicate-uid
    /// issues are sorted by uid (then by the first sidecar path) so two identical scans produce identical
    /// issue sequences — important for gate-delta stability and MCP-response diff readability.
    /// </para>
    ///
    /// <para>
    /// <b>File reading:</b> the rule reads sidecar text through a seam (<see cref="ReadFileText"/>) so
    /// tests inject fixture content without touching disk. Production reads via <c>File.ReadAllText</c>;
    /// the verify gate runs in-editor against the real project tree. A read failure (file gone between
    /// checkpoint and validate) is swallowed — that sidecar contributes no issues rather than crashing
    /// the scan, matching the "must not throw" contract.
    /// </para>
    /// </summary>
    public sealed class ImportHealthRule : IVerifyRule
    {
        /// <summary>The stable rule id surfaced in MCP responses, the capability catalog, and the gate delta.</summary>
        public const string RuleId = "import_health";

        /// <inheritdoc />
        public string Id => RuleId;

        private readonly IImportHealthResolver _resolver;
        private readonly Func<string, string?> _readFileText;

        /// <summary>
        /// Production constructor: resolves through <see cref="LiveImportHealthResolver"/> and reads
        /// files from disk via <see cref="File.ReadAllText"/>. Used by
        /// <see cref="Core.VerifyRunner.RegisterDefaults"/> (P3.4 wiring).
        /// </summary>
        public ImportHealthRule() : this(GetLiveResolver(), File.ReadAllText) { }

        /// <summary>
        /// Testable constructor: inject the resolver and file reader. Both are pure seams — no Godot API
        /// surface — so the rule compiles and runs in the binary-less xUnit host.
        /// </summary>
        internal ImportHealthRule(IImportHealthResolver resolver, Func<string, string?> readFileText)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _readFileText = readFileText ?? throw new ArgumentNullException(nameof(readFileText));
        }

        /// <inheritdoc />
        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            // Build the working set of .import sidecars this scan covers: directly-scoped .import files
            // plus sidecars enumerated under any directory subtree the scope names. Deterministic order
            // — process scoped paths in scope order, and enumerated sidecars in resolver order.
            var workingSet = new List<string>();
            foreach (var resPath in scope.Paths)
            {
                if (string.IsNullOrEmpty(resPath)) continue;
                if (IsImportSidecarPath(resPath))
                {
                    workingSet.Add(resPath);
                }
                else if (IsLikelyDirectory(resPath))
                {
                    // A directory subtree (e.g. res://Sprites, res://): enumerate sidecars under it. A
                    // file-shaped path (e.g. res://Main.tscn) is NOT enumerated — it is not a directory
                    // and contributes no sidecars. This keeps a scoped check bounded: a checkpoint over a
                    // single scene does not walk the tree, and the runner-mechanics tests that pass a
                    // dummy .tscn do not reach the resolver's directory walk.
                    foreach (var sidecar in _resolver.EnumerateImportSidecars(resPath))
                        workingSet.Add(sidecar);
                }
                // else: a file-shaped non-.import path (a .tscn, a .gd) — not this rule's input. Skip.
            }

            if (workingSet.Count == 0) return;

            // Parse every sidecar once; reuse the parsed declarations for both checks. De-dupe by path so
            // a scope that names a directory AND its child .import does not double-count.
            var parsed = new List<ImportFileDecl>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sidecar in workingSet)
            {
                if (!seen.Add(sidecar)) continue;
                var decl = TryReadAndParse(sidecar);
                if (decl != null) parsed.Add(decl);
            }

            // (a) Orphan check: sidecar whose source= no longer exists. Runs in every mode — cheap, one
            // existence probe per sidecar, and it is the load-bearing "deleted source" signal.
            foreach (var decl in parsed)
                ScanOrphan(decl, sink);

            // (b) Duplicate-uid check: two+ sidecars claiming the same uid. Validate/Full only — it needs
            // a complete subtree enumeration to be correct, which a header-only checkpoint should not pay
            // for. The exception is when the scope already lists .import paths directly: in that case the
            // working set was built without an extra walk, so running the check on checkpoint is free and
            // still bounded. We approximate this by running the duplicate check whenever the scope
            // contains at least one directly-scoped .import path OR mode != Checkpoint.
            if (mode != VerifyRunMode.Checkpoint || ScopeHasDirectSidecar(scope))
                ScanDuplicateUids(parsed, sink);
        }

        /// <summary>
        /// Whether the scope lists any <c>.import</c> path directly. Used to decide whether the
        /// duplicate-uid check runs on a checkpoint: when the scope names sidecars explicitly, the
        /// working set was assembled without a tree walk, so the check is free and still bounded.
        /// </summary>
        private static bool ScopeHasDirectSidecar(VerifyScope scope)
        {
            foreach (var p in scope.Paths)
                if (!string.IsNullOrEmpty(p) && IsImportSidecarPath(p)) return true;
            return false;
        }

        private ImportFileDecl? TryReadAndParse(string sidecarPath)
        {
            string text;
            try
            {
                var raw = _readFileText(sidecarPath);
                if (raw == null) return null;
                text = raw;
            }
            catch
            {
                // File vanished between checkpoint and validate, or unreadable. Contribute no issues
                // rather than throw — the "must not throw on ordinary malformed assets" contract.
                return null;
            }

            try
            {
                return ImportFileParser.Parse(sidecarPath, text);
            }
            catch
            {
                // The parser is defensive, but guard the call so a future parser change can never crash a
                // scoped gate check.
                return null;
            }
        }

        private void ScanOrphan(ImportFileDecl decl, List<VerifyIssue> sink)
        {
            // No source= means the sidecar is structurally malformed (Godot always writes one). Not this
            // rule's domain — leave it to a future structural rule rather than emit noise.
            if (string.IsNullOrEmpty(decl.Source)) return;

            if (_resolver.FileExists(decl.Source)) return;

            // Orphan: the source is gone. Warning, not error — an orphan sidecar does not break scene
            // load, but it is project cruft a clean project should not accumulate.
            var description = $"import sidecar \"{decl.SidecarPath}\" references source \"{decl.Source}\" " +
                              "which no longer exists on disk";
            sink.Add(MakeOrphanIssue(decl, description));
        }

        private static void ScanDuplicateUids(List<ImportFileDecl> parsed, List<VerifyIssue> sink)
        {
            // Index sidecars by uid. Skip sidecars with no uid (older Godot, or types that get no uid).
            // A uid claimed by two or more sidecars is the collision.
            var byUid = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var decl in parsed)
            {
                if (string.IsNullOrEmpty(decl.Uid)) continue;
                if (!byUid.TryGetValue(decl.Uid!, out var list))
                {
                    list = new List<string>();
                    byUid[decl.Uid!] = list;
                }
                list.Add(decl.SidecarPath);
            }

            foreach (var kv in byUid)
            {
                if (kv.Value.Count < 2) continue;

                var uid = kv.Key;
                // Sort the conflicting paths deterministically (path-ascending) so the same collision
                // always produces the same evidence ordering — gate-delta stability.
                var conflicting = kv.Value.OrderBy(p => p, StringComparer.Ordinal).ToList();

                // Emit one issue per conflicting sidecar, anchored on that sidecar's asset path. The
                // IssueKey is ruleId|severity|assetPath|issueCode, so anchoring on each sidecar makes
                // each a distinct gate-delta key — a fix that removes one orphaned duplicate resolves
                // exactly that key. The full conflicting set is in evidence.
                foreach (var sidecar in conflicting)
                {
                    // The description lists the OTHER claimants (not this one) so an agent reading a
                    // single issue knows who else claims the uid.
                    var others = conflicting.Where(p => p != sidecar).ToList();
                    var description = $"import sidecar \"{sidecar}\" declares uid \"{uid}\" which is also " +
                                      $"declared by {others.Count} other sidecar(s)";
                    sink.Add(MakeDuplicateUidIssue(sidecar, uid, conflicting, description));
                }
            }
        }

        private static VerifyIssue MakeOrphanIssue(ImportFileDecl decl, string description)
        {
            return new VerifyIssue(RuleId, VerifySeverity.Warning, decl.SidecarPath,
                IssueCodes.OrphanImport, description, BuildOrphanEvidence(decl));
        }

        private static VerifyIssue MakeDuplicateUidIssue(
            string sidecar, string uid, IReadOnlyList<string> conflicting, string description)
        {
            return new VerifyIssue(RuleId, VerifySeverity.Error, sidecar,
                IssueCodes.DuplicateUid, description, BuildDuplicateEvidence(uid, conflicting));
        }

        private static IReadOnlyDictionary<string, string> BuildOrphanEvidence(ImportFileDecl decl)
        {
            // Flat, small, additive — matches P3.2/P3.3's evidence builders. Carries the importer and the
            // cache path so an agent/human knows what kind of import went stale and what Godot cached.
            var ev = new Dictionary<string, string>
            {
                ["kind"] = EvidenceKinds.OrphanImport,
                ["source"] = decl.Source!,
            };
            if (!string.IsNullOrEmpty(decl.Importer)) ev["importer"] = decl.Importer!;
            if (!string.IsNullOrEmpty(decl.Path)) ev["path"] = decl.Path!;
            return ev;
        }

        private static IReadOnlyDictionary<string, string> BuildDuplicateEvidence(
            string uid, IReadOnlyList<string> conflicting)
        {
            var ev = new Dictionary<string, string>
            {
                ["kind"] = EvidenceKinds.DuplicateUid,
                ["uid"] = uid,
                // Comma-joined for a compact, single-line evidence value. The IssueKey already
                // disambiguates per-sidecar; this is the full set for context.
                ["conflictingPaths"] = string.Join(",", conflicting),
            };
            return ev;
        }

        private static bool IsImportSidecarPath(string resPath)
            => resPath.EndsWith(".import", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a <c>res://</c> path looks like a directory rather than a file. A path is a directory
        /// when its final segment (after the last <c>/</c>) has no <c>.</c> extension. <c>res://</c>,
        /// <c>res://Sprites</c>, and <c>res://Sprites/</c> are directories; <c>res://Main.tscn</c> and
        /// <c>res://Player.png</c> are files. This is a heuristic — the resolver's directory walk is the
        /// authoritative check, but this avoids an unnecessary (and, in the binary-less host, native)
        /// enumeration call for paths that are obviously files.
        /// </summary>
        private static bool IsLikelyDirectory(string resPath)
        {
            // Strip a trailing slash so "res://Sprites/" and "res://Sprites" are treated the same.
            var trimmed = resPath.TrimEnd('/');
            // The project root itself: "res://" trims to "res:" → no slash after → directory.
            var lastSlash = trimmed.LastIndexOf('/');
            var leaf = lastSlash >= 0 ? trimmed.Substring(lastSlash + 1) : trimmed;
            // A leaf with no '.' is a directory name; a leaf with a '.' is a file. The scheme "res:" has
            // no '.', so "res://" is correctly classified as a directory.
            return !leaf.Contains('.');
        }

        private static IImportHealthResolver GetLiveResolver()
        {
            // Single #if TOOLS boundary, identical pattern to P3.2/P3.3. Without TOOLS (the binary-less
            // test host links the rule directly), LiveImportHealthResolver does not exist and we fall back
            // to a resolver that flags nothing — the production path is never taken from tests; tests
            // always use the internal constructor.
#if TOOLS
            return LiveImportHealthResolver.Instance;
#else
            return new NullResolver();
#endif
        }

#if !TOOLS
        /// <summary>
        /// Fallback resolver for the non-TOOLS compile path (binary-less test host). Tests never reach it
        /// — they inject through the internal constructor — but the parameterless constructor must still
        /// compile. Treats everything as existing and enumerates no sidecars so the production-less host
        /// never emits spurious issues if it is ever accidentally exercised. Mirrors P3.2/P3.3's
        /// NullResolver.
        /// </summary>
        private sealed class NullResolver : IImportHealthResolver
        {
            public bool FileExists(string? resPath) => true;
            public bool UidExists(string? uid) => true;
            public IReadOnlyList<string> EnumerateImportSidecars(string? resRoot)
                => System.Array.Empty<string>();
        }
#endif
    }

    /// <summary>
    /// Values placed in <c>Evidence["kind"]</c> to distinguish import-health failure modes. Kept internal
    /// because the stable surface is the issue codes in <see cref="IssueCodes"/>. Agent-facing
    /// diagnostics can branch on this; fix providers (P3.7) can use it to pick a strategy.
    /// </summary>
    internal static class EvidenceKinds
    {
        /// <summary>A <c>.import</c> sidecar whose <c>source=</c> file no longer exists on disk.</summary>
        public const string OrphanImport = "orphan_import";

        /// <summary>A <c>uid://</c> declared by two or more <c>.import</c> sidecars in the scope.</summary>
        public const string DuplicateUid = "duplicate_uid";
    }
}
