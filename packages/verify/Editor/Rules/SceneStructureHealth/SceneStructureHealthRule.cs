#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Verify.Rules.SceneStructureHealth
{
    /// <summary>
    /// Detects scene-tree structural complexity in <c>.tscn</c> files: pathologically deep nesting,
    /// oversized node counts, very wide sibling lists, duplicate sibling names, and empty branches.
    /// Adapted from Unity Open MCP's <c>ScenePrefabHealth</c> rule — but most signals are greenfield for
    /// Godot (see <see cref="IssueCodes"/> for the fidelity breakdown). The node tree is walked offline
    /// from the serialized <c>[node name= parent= type=]</c> declarations — no editor load — reusing the
    /// two-pass build algorithm from <c>scene-hierarchy.ts</c> (P7.2), re-implemented in
    /// <see cref="SceneStructureParser"/>.
    ///
    /// <para>
    /// <b>Relationship to <c>project_health</c> (P14.1):</b> <c>project_health</c> flags structurally
    /// broken or root-only scenes (<c>project_broken_asset</c> / <c>project_empty_scene</c>). This rule
    /// is concerned only with the <i>shape</i> of a parseable scene's node tree. A scene that fails to
    /// parse (no header / no nodes) or is a non-scene <c>.tres</c> is skipped here with no issues —
    /// those are project_health's cases, and surfacing them again would duplicate findings (see
    /// <c>specs/execution/P14/P14.2.md</c>).
    /// </para>
    ///
    /// <para>
    /// <b>Scope handling.</b> The scope paths may be:
    /// <list type="bullet">
    ///   <item><b>A directory</b> (e.g. <c>res://Scenes</c>, <c>res://</c>): the rule walks the subtree
    ///     via <see cref="ISceneStructureResolver.ListDirectory"/> and analyzes every <c>.tscn</c> it
    ///     reaches.</item>
    ///   <item><b>A <c>.tscn</c> file</b> (e.g. <c>res://Main.tscn</c>): the rule analyzes that one
    ///     scene.</item>
    ///   <item><b>A <c>.tres</c> or other file</b>: not this rule's input. Skip.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Run-mode behavior.</b> Same split as <c>project_health</c>: the directory walk is
    /// <see cref="VerifyRunMode.Validate"/>/<see cref="VerifyRunMode.Full"/> only (it needs a subtree
    /// enumeration); but a directly-scoped <c>.tscn</c> is analyzed in every mode including
    /// <see cref="VerifyRunMode.Checkpoint"/> (one cheap parse, no walk) so a gated mutation on a single
    /// scene still catches a structural regression it introduced.
    /// </para>
    ///
    /// <para>
    /// <b>Thresholds.</b> Deep &gt; 10; high node count &gt; 1000; wide sibling list &gt; 100. Constants for
    /// v1 (see <c>specs/execution/P14/P14.2.md</c>); configurable later via
    /// <c>.godot-open-mcp/settings.json</c>. Depth is measured from the scene root (root = 0).
    /// </para>
    ///
    /// <para>
    /// <b>File reading:</b> the rule reads <c>.tscn</c> text through a seam (<see cref="ReadFileText"/>)
    /// so tests inject fixture content without touching disk. Production reads via
    /// <c>File.ReadAllText</c>. A read failure (file gone between checkpoint and validate) is swallowed —
    /// that scene contributes no issues rather than crashing the scan, matching the "must not throw"
    /// contract.
    /// </para>
    /// </summary>
    public sealed class SceneStructureHealthRule : IVerifyRule
    {
        /// <summary>The stable rule id surfaced in MCP responses, the capability catalog, and the gate delta.</summary>
        public const string RuleId = "scene_structure_health";

        /// <inheritdoc />
        public string Id => RuleId;

        // ---- Thresholds (v1 constants) ----------------------------------------

        /// <summary>
        /// Nodes deeper than this from the scene root are flagged <c>deep_nesting</c>. Default 10 —
        /// matches the value in the canonical issue roster and P14.2 design decision.
        /// </summary>
        private const int DeepNestingThreshold = 10;

        /// <summary>
        /// Scenes with more than this many total <c>[node]</c> declarations are flagged
        /// <c>high_node_count</c>. Default 1000.
        /// </summary>
        private const int HighNodeCountThreshold = 1000;

        /// <summary>
        /// Parents with more than this many direct child nodes are flagged <c>wide_sibling_list</c>.
        /// Default 100.
        /// </summary>
        private const int WideSiblingListThreshold = 100;

        private readonly ISceneStructureResolver _resolver;
        private readonly Func<string, string?> _readFileText;

        /// <summary>
        /// Production constructor: lists directories + checks existence through
        /// <see cref="LiveSceneStructureResolver"/> and reads files from disk via
        /// <see cref="File.ReadAllText"/>. Used by <see cref="Core.VerifyRunner.RegisterDefaults"/>.
        /// </summary>
        public SceneStructureHealthRule() : this(GetLiveResolver(), File.ReadAllText) { }

        /// <summary>
        /// Testable constructor: inject the resolver and file reader. Both are pure seams — no Godot API
        /// surface — so the rule compiles and runs in the binary-less xUnit host.
        /// </summary>
        internal SceneStructureHealthRule(ISceneStructureResolver resolver, Func<string, string?> readFileText)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _readFileText = readFileText ?? throw new ArgumentNullException(nameof(readFileText));
        }

        /// <inheritdoc />
        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            // The directory walk is Validate/Full only — it needs a subtree enumeration. A directly-scoped
            // .tscn is analyzed in every mode (cheap, no walk), so we branch per scoped path below rather
            // than early-return wholesale. Identical structure to ProjectHealthRule.Scan.
            var fullScan = mode != VerifyRunMode.Checkpoint;

            // De-dupe scoped directory roots so a scope naming "res://" AND "res://Scenes" does not walk
            // Scenes twice.
            var walkedDirs = new HashSet<string>(StringComparer.Ordinal);

            foreach (var resPath in scope.Paths)
            {
                if (string.IsNullOrEmpty(resPath)) continue;

                if (IsScenePath(resPath))
                {
                    ScanScene(resPath, sink);
                    continue;
                }

                if (!fullScan) continue;

                if (IsLikelyDirectory(resPath))
                {
                    if (!walkedDirs.Add(resPath)) continue;
                    WalkDirectory(resPath, sink, walkedDirs);
                }
                // else: a non-.tscn file (.tres, .gd, .png) — not this rule's input. Skip.
            }
        }

        // ---- Directory walk (reaches every .tscn in a subtree) ----------------

        private void WalkDirectory(string dirRes, List<VerifyIssue> sink, HashSet<string> walkedDirs)
        {
            walkedDirs.Add(dirRes);

            IReadOnlyList<SceneFolderEntry> entries;
            try
            {
                entries = _resolver.ListDirectory(dirRes);
            }
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
                    WalkDirectory(entry.ResPath, sink, walkedDirs);
                }
                else if (IsScenePath(entry.ResPath))
                {
                    ScanScene(entry.ResPath, sink);
                }
            }
        }

        // ---- Scene analysis ---------------------------------------------------

        /// <summary>
        /// Parse one <c>.tscn</c> and emit the five structure findings. Never throws — a read or parse
        /// failure is swallowed and contributes no issues. A scene that the parser skips (no header / no
        /// nodes / a <c>.tres</c>) is not this rule's input; broken-asset detection is project_health's
        /// domain.
        /// </summary>
        private void ScanScene(string resPath, List<VerifyIssue> sink)
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
                // File vanished between checkpoint and validate, or unreadable. Contribute no issues.
                return;
            }

            SceneStructureAnalysis analysis;
            try
            {
                analysis = SceneStructureParser.Parse(text);
            }
            catch
            {
                // The parser is defensive, but guard the call so a future parser change can never crash a
                // scoped gate check.
                return;
            }

            // A skipped scene (broken / non-scene / empty) is project_health's domain, not this rule's.
            if (analysis.Skipped) return;

            Analyze(resPath, analysis, sink);
        }

        /// <summary>
        /// Apply the five structure checks to a parsed scene's facts. Emits issues in a deterministic
        /// order: high-node-count (scene-level) first, then per-node findings in tree order
        /// (deep-nesting, wide-sibling, empty-branch), then duplicate-name collisions sorted by parent
        /// key + name. Two identical scans produce identical issue sequences (gate-delta stability).
        /// </summary>
        private void Analyze(string resPath, SceneStructureAnalysis analysis, List<VerifyIssue> sink)
        {
            // Key → node map: lets the empty-branch de-dup walk climb the parent chain by direct lookup
            // (a node's verbatim parent= value is its parent's key). Built once per scene.
            var byKey = new Dictionary<string, SceneNode>(StringComparer.Ordinal);
            foreach (var n in analysis.Nodes)
            {
                // The first claimant of a key wins (mirrors the parser's link pass); duplicate-key nodes
                // are detached and have no parent to climb to.
                byKey.TryAdd(n.Key, n);
            }

            // high_node_count — scene-level.
            if (analysis.NodeCount > HighNodeCountThreshold)
            {
                sink.Add(MakeIssue(resPath, VerifySeverity.Warning, IssueCodes.HighNodeCount,
                    $"scene \"{resPath}\" has {analysis.NodeCount} nodes — exceeds threshold {HighNodeCountThreshold}",
                    BuildEvidence(EvidenceKinds.HighNodeCount, resPath,
                        nodeCount: analysis.NodeCount.ToString(), threshold: HighNodeCountThreshold.ToString())));
            }

            // Per-node checks in tree order. We walk the analysis.Nodes list (file order) but emit only the
            // applicable findings — file order keeps two scans identical.
            foreach (var node in analysis.Nodes)
            {
                // deep_nesting.
                if (node.Depth > DeepNestingThreshold)
                {
                    sink.Add(MakeIssue(resPath, VerifySeverity.Warning, IssueCodes.DeepNesting,
                        $"node \"{node.Path}\" at depth {node.Depth} exceeds threshold {DeepNestingThreshold}",
                        BuildEvidence(EvidenceKinds.DeepNesting, resPath,
                            nodePath: node.Path, depth: node.Depth.ToString(), threshold: DeepNestingThreshold.ToString())));
                }

                // wide_sibling_list — a parent with too many direct children. Check children attached to
                // THIS node (it is the parent).
                if (node.Children.Count > WideSiblingListThreshold)
                {
                    sink.Add(MakeIssue(resPath, VerifySeverity.Warning, IssueCodes.WideSiblingList,
                        $"node \"{node.Path}\" has {node.Children.Count} children — exceeds threshold {WideSiblingListThreshold}",
                        BuildEvidence(EvidenceKinds.WideSiblingList, resPath,
                            nodePath: node.Path, childCount: node.Children.Count.ToString(),
                            threshold: WideSiblingListThreshold.ToString())));
                }

                // empty_node_branch — a non-root branch with no content in its subtree, de-duped to the
                // topmost reportable empty branch in a chain.
                if (IsReportableEmptyBranch(node, analysis.Root, byKey))
                {
                    sink.Add(MakeIssue(resPath, VerifySeverity.Warning, IssueCodes.EmptyNodeBranch,
                        $"node \"{node.Path}\" heads an empty branch (no script, instance, or concrete leaf in its subtree)",
                        BuildEvidence(EvidenceKinds.EmptyNodeBranch, resPath, nodePath: node.Path)));
                }
            }

            // duplicate_node_name — already sorted by (parentKey, name) by the parser.
            foreach (var collision in analysis.DuplicateNames)
            {
                var parentPath = ParentKeyToPath(collision.ParentKey, analysis.Root);
                sink.Add(MakeIssue(resPath, VerifySeverity.Warning, IssueCodes.DuplicateNodeName,
                    $"node name \"{collision.Name}\" appears {collision.Count} times under \"{parentPath}\"",
                    BuildEvidence(EvidenceKinds.DuplicateNodeName, resPath,
                        nodePath: parentPath, nodeName: collision.Name, count: collision.Count.ToString())));
            }
        }

        /// <summary>
        /// Whether <paramref name="node"/> should be reported as an empty-branch head. A node is an empty
        /// branch when it (a) is not the scene root, (b) has ≥ 1 child, and (c) has no content node
        /// anywhere in its subtree. A content node carries a script, is an instanced sub-scene, or is a
        /// leaf with a concrete type (not bare <c>Node</c>, not type-less) — bare <c>Node</c> containers
        /// are scaffolding. The scene root is excluded (a root-only scene is <c>project_empty_scene</c>'s
        /// domain).
        ///
        /// <para>
        /// <b>De-dup to the topmost reportable branch.</b> A chain of nested empty containers would yield
        /// one finding per level; instead we report only the highest empty branch a detached/orphan or
        /// root descends from. Concretely: report N only if N is empty AND (N's parent is the root, OR N's
        /// parent is detached, OR N's parent is NOT itself empty). Walk up the parent chain a bounded
        /// number of times (depth strictly decreases, so it always terminates).
        /// </para>
        /// </summary>
        private static bool IsReportableEmptyBranch(SceneNode node, SceneNode? root,
            Dictionary<string, SceneNode> byKey)
        {
            if (node == root) return false;          // (a) root excluded
            if (node.Children.Count == 0) return false; // (b) a leaf cannot head a branch
            if (SubtreeHasContent(node)) return false;  // (c) subtree carries content

            // De-dup: climb the parent chain by key lookup (a node's verbatim parent= value is its
            // parent's key). If any non-root ancestor is itself an empty branch, IT is reported instead.
            var parent = FindParentByKey(node, root, byKey);
            while (parent != null && parent != root)
            {
                // An ancestor with content elsewhere breaks the chain — this node is the topmost empty.
                if (SubtreeHasContent(parent)) return true;
                // An ancestor that is itself an empty branch (≥1 child, no content) is reported instead.
                if (parent.Children.Count > 0) return false;
                // An ancestor that is an empty leaf (no children, no content) is scaffolding, not a branch
                // head — keep climbing.
                parent = FindParentByKey(parent, root, byKey);
            }
            // We reached the root or a detached node with no empty-branch ancestor above this node.
            return true;
        }

        /// <summary>
        /// Resolve a node's parent via the key map: the parent's key is the node's verbatim
        /// <c>parent=</c> value (or <c>"."</c> for the root). Returns null for the root or a detached
        /// node (an orphan parent). Climbing by key cannot cycle because keys are unique by construction
        /// and parent keys strictly shorten the path.
        /// </summary>
        private static SceneNode? FindParentByKey(SceneNode node, SceneNode? root,
            Dictionary<string, SceneNode> byKey)
        {
            if (root == null) return null;
            if (node == root) return null;
            var parentKey = node.Parent ?? ".";
            if (parentKey == ".") return root; // a direct child of the root
            return byKey.TryGetValue(parentKey, out var parent) ? parent : null;
        }

        /// <summary>
        /// Whether any node in <paramref name="node"/>'s subtree (including itself) is a content node:
        /// has a script, is an instance, or is a leaf with a concrete type.
        /// </summary>
        private static bool SubtreeHasContent(SceneNode node)
        {
            if (IsContent(node)) return true;
            foreach (var child in node.Children)
                if (SubtreeHasContent(child)) return true;
            return false;
        }

        /// <summary>
        /// Whether a single node is a "content" node: a script attachment, an instanced sub-scene, or a
        /// leaf (no children) with a concrete type (not bare <c>Node</c>, not type-less). Bare
        /// <c>Node</c> containers and type-less interior nodes are scaffolding.
        /// </summary>
        private static bool IsContent(SceneNode node)
        {
            if (node.HasScript) return true;
            if (node.IsInstance) return true;
            if (node.Children.Count == 0 && IsConcreteType(node.Type)) return true;
            return false;
        }

        /// <summary>A type is "concrete" when it is present and not the bare <c>Node</c> container.</summary>
        private static bool IsConcreteType(string? type)
            => !string.IsNullOrEmpty(type) && type != "Node";

        /// <summary>
        /// Render a parent key (the verbatim <c>parent=</c> value, or <c>"."</c>) as a display path. For
        /// <c>"."</c> (the root's children), the parent is the scene root, so we show the root's path;
        /// otherwise the parent key is already a root-relative path (e.g. <c>"Player"</c>).
        /// </summary>
        private static string ParentKeyToPath(string parentKey, SceneNode? root)
        {
            if (parentKey == ".") return root?.Path ?? ".";
            return root != null ? root.Name + "/" + parentKey : parentKey;
        }

        // ---- Issue construction ------------------------------------------------

        // P14.5: materialize the explainability taxonomy (rootCause + remediation) onto the issue. The
        // pair is resolved by issueCode, so each scene_structure_health code gets its own rootCause.
        private static VerifyIssue MakeIssue(
            string assetPath, VerifySeverity severity, string issueCode, string description,
            IReadOnlyDictionary<string, string> evidence)
        {
            IssueExplainability.TryGet(RuleId, issueCode, out var ex);
            return new VerifyIssue(RuleId, severity, assetPath, issueCode, description, evidence, ex?.RootCause, ex?.Remediation);
        }

        private static IReadOnlyDictionary<string, string> BuildEvidence(
            string kind, string assetPath, string? nodePath = null, string? depth = null,
            string? threshold = null, string? nodeCount = null, string? childCount = null,
            string? nodeName = null, string? count = null)
        {
            var ev = new Dictionary<string, string> { ["kind"] = kind, ["assetPath"] = assetPath };
            if (nodePath != null) ev["nodePath"] = nodePath;
            if (depth != null) ev["depth"] = depth;
            if (threshold != null) ev["threshold"] = threshold;
            if (nodeCount != null) ev["nodeCount"] = nodeCount;
            if (childCount != null) ev["childCount"] = childCount;
            if (nodeName != null) ev["nodeName"] = nodeName;
            if (count != null) ev["count"] = count;
            return ev;
        }

        // ---- Path helpers (mirror ProjectHealthRule) --------------------------

        private static bool IsScenePath(string resPath)
            => resPath.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a <c>res://</c> path looks like a directory rather than a file — same heuristic as
        /// <see cref="ProjectHealth.ProjectHealthRule"/>: the leaf segment (after the last <c>/</c>) has no
        /// <c>.</c> extension.
        /// </summary>
        private static bool IsLikelyDirectory(string resPath)
        {
            var trimmed = resPath.TrimEnd('/');
            var lastSlash = trimmed.LastIndexOf('/');
            var leaf = lastSlash >= 0 ? trimmed.Substring(lastSlash + 1) : trimmed;
            return !leaf.Contains('.');
        }

        // ---- Live resolver wiring (mirrors P3.2/P3.3/P3.4/P14.1) --------------

        private static ISceneStructureResolver GetLiveResolver()
        {
#if TOOLS
            return LiveSceneStructureResolver.Instance;
#else
            return new NullResolver();
#endif
        }

#if !TOOLS
        /// <summary>
        /// Fallback resolver for the non-TOOLS compile path (binary-less test host). Tests never reach it
        /// — they inject through the internal constructor — but the parameterless constructor must still
        /// compile. Mirrors ProjectHealth's NullResolver.
        /// </summary>
        private sealed class NullResolver : ISceneStructureResolver
        {
            public IReadOnlyList<SceneFolderEntry> ListDirectory(string? resDir)
                => Array.Empty<SceneFolderEntry>();
            public bool FileExists(string? resPath) => true;
        }
#endif
    }

    /// <summary>
    /// Values placed in <c>Evidence["kind"]</c> to distinguish scene-structure failure modes. Kept
    /// internal because the stable surface is the issue codes in <see cref="IssueCodes"/>.
    /// </summary>
    internal static class EvidenceKinds
    {
        public const string DeepNesting = "deep_nesting";
        public const string HighNodeCount = "high_node_count";
        public const string WideSiblingList = "wide_sibling_list";
        public const string DuplicateNodeName = "duplicate_node_name";
        public const string EmptyNodeBranch = "empty_node_branch";
    }
}
