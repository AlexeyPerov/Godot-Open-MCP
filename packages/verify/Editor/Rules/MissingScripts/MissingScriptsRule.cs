#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Rules.BrokenReferences;

namespace GodotOpenMcp.Verify.Rules.MissingScripts
{
    /// <summary>
    /// Detects nodes in Godot <c>.tscn</c>/<c>.tres</c> files whose <c>script = ExtResource("id")</c>
    /// attachment can no longer be resolved — the Godot analog of Unity's MonoBehaviour with a missing
    /// script GUID. Emits one <see cref="VerifyIssue"/> per broken script attachment with
    /// <see cref="IssueCodes.MissingScript"/>. Adapted from Unity's <c>MissingReferencesRule</c>
    /// (<c>missing_script</c> code): Unity keys its <c>remove_missing_script</c> fix off
    /// <c>missing_references|missing_script</c>; this rule keys the P3.7 Godot fix off
    /// <c>missing_scripts|missing_script</c>.
    ///
    /// <para>
    /// <b>Relationship to P3.2 <c>broken_references</c>:</b> the broken-references rule already flags any
    /// unresolved <c>[ext_resource]</c> generically, and a deleted script's ext_resource would surface
    /// there as <c>broken_scene_reference</c>. This rule is the <b>script-specialized</b> view the fix
    /// surface needs: it emits the distinct <c>missing_script</c> code (so P3.7's
    /// <c>remove_missing_script</c> can route on the <c>ruleId|issueCode</c> tuple without catching every
    /// broken reference) and carries <b>node-level evidence</b> — <c>nodeName</c>, <c>nodePath</c> — that
    /// the generic rule does not track. An agent reading the issue can go straight to the node.
    /// </para>
    ///
    /// <para>
    /// <b>Two failure modes, one issue code:</b> a node's script is "missing" when either (a) the
    /// <c>[ext_resource type="Script"]</c> it links to has a <c>path=</c>/<c>uid=</c> that fails to resolve
    /// (the script file was deleted/moved), or (b) the <c>script = ExtResource("id")</c> uses an id no
    /// header declares (dangling, a partial edit). Both surface as <c>missing_script</c>; the mode is in
    /// <c>Evidence["kind"]</c> so diagnostics and fixes can branch without multiplying the stable code.
    /// </para>
    ///
    /// <para>
    /// <b>Resolution model:</b> the rule resolves a node's script the same way the engine does at load —
    /// map the usage id to its <c>[ext_resource]</c> declaration, then ask the resolver whether that
    /// declaration's <c>uid=</c>/<c>path=</c> resolves. It reuses <see cref="IResourceResolver"/> (the P3.2
    /// seam) and <see cref="SceneRefParser"/> (the P3.2 ext_resource parser) rather than duplicating them,
    /// so both rules agree on what "resolves" means. The false-positive guard is identical to P3.2: a
    /// present identifier that resolves is enough; flag only when every present identifier fails.
    /// </para>
    ///
    /// <para>
    /// <b>Run-mode behavior</b> (mirrors P3.2 / Unity's <c>fullScan = mode != Checkpoint</c>): the
    /// dangling-id check (a usage whose id was never declared) is a <c>Validate</c>/<c>Full</c>-only pass
    /// — it walks node bodies and is the more expensive half, so a header-only checkpoint mutation does
    /// not pay for it. The resource-resolution check (the script's <c>ext_resource</c> target is gone)
    /// runs in every mode — that is the load-bearing signal and stays cheap (one resolver call per node
    /// with a script).
    /// </para>
    /// </summary>
    public sealed class MissingScriptsRule : IVerifyRule
    {
        /// <summary>The stable rule id surfaced in MCP responses, the capability catalog, and the gate delta.</summary>
        public const string RuleId = "missing_scripts";

        /// <inheritdoc />
        public string Id => RuleId;

        private readonly IResourceResolver _resolver;
        private readonly Func<string, string?> _readFileText;

        /// <summary>
        /// Production constructor: resolves script targets through <see cref="LiveResourceResolver"/> and
        /// reads files from disk via <see cref="File.ReadAllText"/>. Used by
        /// <see cref="Core.VerifyRunner.RegisterDefaults"/> (P3.3 wiring).
        /// </summary>
        public MissingScriptsRule() : this(GetLiveResolver(), File.ReadAllText) { }

        /// <summary>
        /// Testable constructor: inject the resolver and file reader. Both are pure seams (no Godot API
        /// surface) so the rule compiles and runs in the binary-less xUnit host. The resolver type is
        /// <see cref="IResourceResolver"/> from the P3.2 package — the rules share one resolution model.
        /// </summary>
        internal MissingScriptsRule(IResourceResolver resolver, Func<string, string?> readFileText)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _readFileText = readFileText ?? throw new ArgumentNullException(nameof(readFileText));
        }

        /// <inheritdoc />
        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            // fullScan mirrors P3.2: the dangling-id walk reads node bodies and is skipped on checkpoint.
            var fullScan = mode != VerifyRunMode.Checkpoint;

            foreach (var resPath in scope.Paths)
            {
                if (string.IsNullOrEmpty(resPath)) continue;
                // Reuse P3.2's scope filter: only .tscn/.tres carry the node + ext_resource model. Other
                // asset kinds are not this rule's domain.
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
                // File vanished between checkpoint and validate, or unreadable. Contribute no issues rather
                // than throw — the "must not throw on ordinary malformed assets" contract.
                return;
            }

            SceneRefParseResult refs;
            NodeScriptScanResult nodes;
            try
            {
                // Parse both views of the file: the ext_resource declarations (shared with P3.2) and the
                // node → script attachments (this rule's specialization). Both parsers are defensive.
                refs = SceneRefParser.Parse(text);
                nodes = NodeScriptScanner.Parse(text);
            }
            catch
            {
                // Guard the calls so a future parser change can never crash a scoped gate check.
                return;
            }

            // Index ext_resource declarations by id so a node's script usage maps to its target in O(1).
            // Built with an indexer loop rather than ToDictionary: SceneRefParser does not de-duplicate
            // ids, and a file carrying two [ext_resource] headers with the same id= (a botched merge, a
            // hand edit) is ordinary malformed input. ToDictionary throws ArgumentException on it, and
            // that throw would escape Scan — abandoning this rule for every remaining path in the scope
            // while the run still reports success, so the gate would compare against an artificially
            // clean fingerprint. Godot's own loader keeps the first declaration wins/last wins
            // distinction loose here; either is safe for a resolution lookup.
            var byId = new Dictionary<string, ExtResourceDecl>();
            foreach (var decl in refs.ExtResources) byId[decl.Id] = decl;

            foreach (var node in nodes.Attachments)
            {
                if (byId.TryGetValue(node.ScriptExtId, out var ext))
                {
                    // The usage maps to a declared ext_resource. Resolve it the same way P3.2 does: flag
                    // only when every present identifier fails. This runs in every mode — it is the
                    // load-bearing "script deleted" signal.
                    if (!IsExtResourceMissing(ext)) continue;

                    var description =
                        $"node \"{node.NodeName}\" script attachment references \"{node.ScriptExtId}\" " +
                        $"({FormatTarget(ext)}) which could not be resolved";
                    sink.Add(MakeIssue(resPath, description,
                        BuildEvidence(EvidenceKinds.ScriptResourceMissing, node, ext)));
                }
                else if (fullScan)
                {
                    // The usage id was never declared — a dangling id from a partial edit. Validate/Full
                    // only: it requires walking node bodies, the more expensive half.
                    var description =
                        $"node \"{node.NodeName}\" references script id \"{node.ScriptExtId}\" " +
                        $"that was never declared in this file";
                    sink.Add(MakeIssue(resPath, description,
                        BuildDanglingEvidence(EvidenceKinds.ScriptIdDangling, node)));
                }
            }
        }

        /// <summary>
        /// The P3.2 false-positive guard, applied to a node's script ext_resource: an identifier is "ok"
        /// when it is present and resolves; flag only when every present identifier fails. A stale path
        /// with a live uid (the common relocation case) must NOT flag, and vice versa.
        /// </summary>
        private bool IsExtResourceMissing(ExtResourceDecl ext)
        {
            var uidPresent = !string.IsNullOrEmpty(ext.Uid);
            var pathPresent = !string.IsNullOrEmpty(ext.Path);
            if (!uidPresent && !pathPresent) return false; // malformed header; not this rule's concern

            var uidOk = uidPresent && _resolver.UidExists(ext.Uid);
            var pathOk = pathPresent && _resolver.PathExists(ext.Path);
            return !uidOk && !pathOk;
        }

        private static string FormatTarget(ExtResourceDecl ext)
        {
            // Prefer uid in the message (stronger deletion signal) when both are present, matching P3.2's
            // evidence-target choice.
            if (!string.IsNullOrEmpty(ext.Uid)) return ext.Uid!;
            return ext.Path ?? "";
        }

        private static VerifyIssue MakeIssue(string assetPath, string description, IReadOnlyDictionary<string, string> evidence)
        {
            // Severity is Error: a missing script means the node runs without its intended behavior (or
            // fails to load in strict modes) — a gate failure, not a warning. Matches Unity's
            // missing_script severity and the P3.2 precedent.
            return new VerifyIssue(RuleId, VerifySeverity.Error, assetPath,
                IssueCodes.MissingScript, description, evidence);
        }

        private static IReadOnlyDictionary<string, string> BuildEvidence(
            string kind, NodeScriptAttachment node, ExtResourceDecl ext)
        {
            // Flat, small, additive — matches P3.2's Evidence builder. Node-level fields (nodeName,
            // nodePath) are what distinguish this rule's evidence from P3.2's and what the P3.7 fix and
            // agent diagnostics key on to locate the broken attachment.
            return new Dictionary<string, string>
            {
                ["kind"] = kind,
                ["nodeName"] = node.NodeName,
                ["nodePath"] = node.NodePath,
                ["scriptExtId"] = node.ScriptExtId,
                ["target"] = FormatTarget(ext),
                ["line"] = node.ScriptLine.ToString(),
            };
        }

        private static IReadOnlyDictionary<string, string> BuildDanglingEvidence(
            string kind, NodeScriptAttachment node)
        {
            return new Dictionary<string, string>
            {
                ["kind"] = kind,
                ["nodeName"] = node.NodeName,
                ["nodePath"] = node.NodePath,
                ["scriptExtId"] = node.ScriptExtId,
                ["line"] = node.ScriptLine.ToString(),
            };
        }

        private static bool IsSceneOrResourcePath(string resPath)
        {
            // Same scope filter as P3.2: .tscn/.tres text formats only. Binary .scn/.res are excluded.
            return resPath.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
                   || resPath.EndsWith(".tres", StringComparison.OrdinalIgnoreCase);
        }

        private static IResourceResolver GetLiveResolver()
        {
            // Single #if TOOLS boundary, identical pattern to P3.2. Without TOOLS (the binary-less test
            // host links the rule directly), LiveResourceResolver does not exist and we fall back to a
            // resolver that flags nothing — the production path is never taken from tests; tests always
            // use the internal constructor.
#if TOOLS
            return LiveResourceResolver.Instance;
#else
            return new NullResolver();
#endif
        }

#if !TOOLS
        /// <summary>
        /// Fallback resolver for the non-TOOLS compile path (binary-less test host). Tests never reach it
        /// — they inject through the internal constructor — but the parameterless constructor must still
        /// compile. Mirrors P3.2's NullResolver.
        /// </summary>
        private sealed class NullResolver : IResourceResolver
        {
            public bool PathExists(string? resPath) => true;
            public bool UidExists(string? uid) => true;
        }
#endif
    }
}
