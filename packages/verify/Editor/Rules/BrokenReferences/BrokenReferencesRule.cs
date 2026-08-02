#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Verify.Rules.BrokenReferences
{
    /// <summary>
    /// Detects broken external and sub-resource references in Godot <c>.tscn</c>/<c>.tres</c> files.
    /// Emits one <see cref="VerifyIssue"/> per broken reference with
    /// <see cref="IssueCodes.BrokenSceneReference"/>. Greenfield for Godot — Unity's
    /// <c>MissingReferencesRule</c> resolves YAML <c>guid:</c>/<c>fileID:</c> through
    /// <c>AssetDatabase</c>; Godot's text format uses <c>[ext_resource]</c> headers with
    /// <c>uid=</c>/<c>path=</c> plus <c>ExtResource("id")</c>/<c>SubResource("id")</c> call sites.
    ///
    /// <para>
    /// <b>Three failure modes, one issue code:</b> a missing <c>path=</c> target, a missing
    /// <c>uid=</c> target, or a usage whose string id was never declared. All three surface as
    /// <c>broken_scene_reference</c> errors (the code the fix surface in P3.7 keys on); the failure mode
    /// is carried in <c>Evidence["kind"]</c> so diagnostics and fixes can branch without multiplying the
    /// stable code surface.
    /// </para>
    ///
    /// <para>
    /// <b>Run-mode behavior</b> (mirrors Unity's <c>fullScan = mode != Checkpoint</c>): every mode
    /// resolves declared <c>[ext_resource]</c> targets via <see cref="IResourceResolver"/> — that is the
    /// load-bearing check and must run on every mutation. The dangling-usage check (matching
    /// <c>ExtResource</c>/<c>SubResource</c> call sites to declared ids) is a <c>Validate</c>/<c>Full</c>
    /// -only pass because it walks the whole file body and is the more expensive half; a checkpoint that
    /// only touched a header should not pay for it. See <c>VerifyRunner.CheckpointBudgetMs</c>.
    /// </para>
    ///
    /// <para>
    /// <b>File reading:</b> the rule reads file text through a seam (<see cref="ReadFileText"/>) so tests
    /// can inject fixture content without touching disk. Production reads via <c>File.ReadAllText</c>;
    /// the verify gate runs in-editor against the real project tree, and the rule is only ever handed
    /// <c>res://</c>-rooted paths by the bridge scope. A read failure (file gone between checkpoint and
    /// validate) is swallowed — the file contributes no issues rather than crashing the scan, matching
    /// the "must not throw" contract.
    /// </para>
    /// </summary>
    public sealed class BrokenReferencesRule : IVerifyRule
    {
        /// <summary>The stable rule id surfaced in MCP responses, the capability catalog, and the gate delta.</summary>
        public const string RuleId = "broken_references";

        /// <inheritdoc />
        public string Id => RuleId;

        private readonly IResourceResolver _resolver;
        private readonly Func<string, string?> _readFileText;

        /// <summary>
        /// Production constructor: resolves references through <see cref="LiveResourceResolver"/> and
        /// reads files from disk via <see cref="File.ReadAllText"/>. Used by
        /// <see cref="VerifyRunner.RegisterDefaults"/> (P3.2 wiring).
        /// </summary>
        public BrokenReferencesRule() : this(GetLiveResolver(), File.ReadAllText) { }

        /// <summary>
        /// Testable constructor: inject the resolver and file reader. Both are pure seams — no Godot API
        /// surface — so the rule compiles and runs in the binary-less xUnit host.
        /// </summary>
        internal BrokenReferencesRule(IResourceResolver resolver, Func<string, string?> readFileText)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _readFileText = readFileText ?? throw new ArgumentNullException(nameof(readFileText));
        }

        /// <inheritdoc />
        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            // fullScan mirrors Unity's MissingReferencesRule: the dangling-usage walk is the more
            // expensive half and is skipped on checkpoint (the gate runs a checkpoint on every mutation).
            var fullScan = mode != VerifyRunMode.Checkpoint;

            foreach (var resPath in scope.Paths)
            {
                if (string.IsNullOrEmpty(resPath)) continue;
                // Only .tscn/.tres/.scn/.res carry the ext_resource model the parser understands. Other
                // asset kinds are silently skipped — they are not this rule's domain.
                if (!IsSceneOrResourcePath(resPath)) continue;

                ScanFile(resPath, fullScan, sink);
            }
        }

        private void ScanFile(string resPath, bool fullScan, List<VerifyIssue> sink)
        {
            string text;
            try
            {
                var raw = _readFileText(resPath);
                if (string.IsNullOrEmpty(raw)) return;
                text = raw!;
            }
            catch
            {
                // File vanished between checkpoint and validate, or is unreadable. Contribute no issues
                // rather than throw — the "must not throw on ordinary malformed assets" contract.
                return;
            }

            SceneRefParseResult parsed;
            try
            {
                parsed = SceneRefParser.Parse(text);
            }
            catch
            {
                // The parser is defensive, but guard the call so a future parser change can never crash
                // a scoped gate check.
                return;
            }

            // (a) Validate every declared ext_resource against the resolver. This runs in every mode —
            // it is the load-bearing broken-ref check and stays cheap (one resolver call per header).
            foreach (var ext in parsed.ExtResources)
            {
                ScanExtResource(resPath, ext, sink);
            }

            // (b) Match usages to declarations. Validate/Full only — walks the whole file body.
            if (fullScan)
            {
                ScanUsages(resPath, parsed, sink);
            }
        }

        private void ScanExtResource(string resPath, ExtResourceDecl ext, List<VerifyIssue> sink)
        {
            // Godot writes at least one of uid/path; modern files prefer uid. For each PRESENT
            // identifier, ask the resolver whether it resolves. A missing identifier is simply not
            // checked. We flag only when every present identifier fails — that is the false-positive
            // guard: Godot relocates by uid, so a stale path with a live uid is normal after a move,
            // and a stale uid with a live path is normal after an engine reimport.
            var uidPresent = !string.IsNullOrEmpty(ext.Uid);
            var pathPresent = !string.IsNullOrEmpty(ext.Path);
            if (!uidPresent && !pathPresent)
            {
                // Neither identifier present — the header is malformed (Godot always writes at least one).
                // There is nothing to resolve; leave it to a future structural rule.
                return;
            }

            // An identifier is "ok" when it is present and the resolver confirms it. An absent
            // identifier contributes nothing — it is neither a pass nor a fail. We flag only when every
            // present identifier fails. (An earlier draft treated absent-as-resolves, which made a
            // path-only header with a broken path slip through as "uid resolves" — wrong.)
            var uidOk = uidPresent && _resolver.UidExists(ext.Uid);
            var pathOk = pathPresent && _resolver.PathExists(ext.Path);
            // At least one present identifier must resolve for the header to be healthy. If none of the
            // present identifiers resolve, every load path Godot could try is dead.
            if (uidOk || pathOk)
            {
                return;
            }

            // Every present identifier failed. Prefer the uid failure mode in the evidence when both are
            // present and broken — it is the stronger deletion signal. Otherwise report whichever
            // identifier was actually carried.
            string kind;
            string target;
            if (uidPresent)
            {
                kind = EvidenceKinds.ExtResourceUidMissing;
                target = ext.Uid!;
            }
            else
            {
                kind = EvidenceKinds.ExtResourcePathMissing;
                target = ext.Path!;
            }

            // Description is single-line and agent-facing. Include the id (so an agent can locate the
            // header) and the broken target. Line is in evidence for the gate delta / IDE jump.
            var description = $"ext_resource \"{ext.Id}\" references {target} which could not be resolved";
            sink.Add(MakeIssue(resPath, description, BuildEvidence(kind, ext, target)));
        }

        private void ScanUsages(string resPath, SceneRefParseResult parsed, List<VerifyIssue> sink)
        {
            // Index declared ext_resource ids once per file so the per-usage lookup is O(1).
            var declaredExt = new HashSet<string>(parsed.ExtResources.Count);
            foreach (var ext in parsed.ExtResources) declaredExt.Add(ext.Id);

            foreach (var usage in parsed.Usages)
            {
                var declared = usage.Kind == "ExtResource"
                    ? declaredExt.Contains(usage.Id)
                    : parsed.SubResourceIds.Contains(usage.Id);

                if (declared) continue;

                var kind = usage.Kind == "ExtResource"
                    ? EvidenceKinds.DanglingExtResource
                    : EvidenceKinds.DanglingSubResource;

                var description = $"{usage.Kind}(\"{usage.Id}\") refers to an id that was never declared in this file";
                sink.Add(MakeIssue(resPath, description, BuildUsageEvidence(kind, usage)));
            }
        }

        private static VerifyIssue MakeIssue(string assetPath, string description, IReadOnlyDictionary<string, string> evidence)
        {
            // Severity is Error per the P3.2 acceptance criteria ("surfaced as broken_scene_reference
            // errors") — a broken reference means the scene will fail to load or load with a null, which
            // is a gate failure, not a warning.
            //
            // P14.5: materialize the explainability taxonomy (rootCause + remediation) onto the issue.
            IssueExplainability.TryGet(RuleId, IssueCodes.BrokenSceneReference, out var ex);
            return new VerifyIssue(RuleId, VerifySeverity.Error, assetPath,
                IssueCodes.BrokenSceneReference, description, evidence, ex?.RootCause, ex?.Remediation);
        }

        private static IReadOnlyDictionary<string, string> BuildEvidence(string kind, ExtResourceDecl ext, string target)
        {
            // Flat, small, additive — matches the Evidence() builder in Unity's IssueMapper. Each issue
            // gets a fresh dict; never share mutable state across issues.
            return new Dictionary<string, string>
            {
                ["kind"] = kind,
                ["extResourceId"] = ext.Id,
                ["target"] = target,
                ["line"] = ext.Line.ToString(),
            };
        }

        private static IReadOnlyDictionary<string, string> BuildUsageEvidence(string kind, ResourceUsage usage)
        {
            return new Dictionary<string, string>
            {
                ["kind"] = kind,
                ["refKind"] = usage.Kind,
                ["refId"] = usage.Id,
                ["line"] = usage.Line.ToString(),
            };
        }

        private static bool IsSceneOrResourcePath(string resPath)
        {
            // .tscn/.tres are the text formats the parser handles. .scn/.res are the binary equivalents
            // and are intentionally excluded — the text parser would misread them. Binary scene/resource
            // integrity is a separate concern (P3.4 import health territory).
            return resPath.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
                   || resPath.EndsWith(".tres", StringComparison.OrdinalIgnoreCase);
        }

        private static IResourceResolver GetLiveResolver()
        {
            // Indirected through a method (not a field) so the #if TOOLS boundary lives in exactly one
            // place. When compiled without TOOLS (the binary-less test host links the rule directly),
            // LiveResourceResolver does not exist and we fall back to a resolver that flags nothing —
            // the production path is never taken from tests; tests always use the internal constructor.
#if TOOLS
            return LiveResourceResolver.Instance;
#else
            return new NullResolver();
#endif
        }

#if !TOOLS
        /// <summary>
        /// Fallback resolver for the non-TOOLS compile path (binary-less test host). Tests never reach
        /// it — they inject through the internal constructor — but the parameterless constructor must
        /// still compile. Treats everything as resolvable so the production-less host never emits
        /// spurious issues if it is ever accidentally exercised.
        /// </summary>
        private sealed class NullResolver : IResourceResolver
        {
            public bool PathExists(string? resPath) => true;
            public bool UidExists(string? uid) => true;
        }
#endif
    }
}
