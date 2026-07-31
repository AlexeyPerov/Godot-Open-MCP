#nullable enable
using System;
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Rules.SceneStructureHealth
{
    /// <summary>
    /// One node parsed from a <c>.tscn</c>, with its reconstructed scene-tree path and the facts the
    /// structure analysis needs (name, type, depth, parent key, whether the node carries content). The
    /// <c>parent</c>/<c>type</c>/<c>script</c>/<c>instance</c> attributes are read verbatim from the
    /// <c>[node ...]</c> header and body, mirroring how <c>scene-parser.ts</c> (P7.2) models a
    /// <c>ParsedSceneNode</c> and how <c>MissingScripts.NodeScriptScanner</c> reconstructs the path.
    /// </summary>
    internal sealed class SceneNode
    {
        /// <summary>The <c>name=</c> attribute of the <c>[node ...]</c> header (never null for a kept node).</summary>
        public string Name { get; }

        /// <summary>
        /// The slash-delimited scene-tree path reconstructed from <c>parent=</c> attributes
        /// (e.g. <c>Main/Player/Body</c>); the root carries its own name. Matches what Godot reports for a
        /// node's edit-time path.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// The <c>type=</c> attribute, or <c>null</c> when omitted (an instanced or inherited node — Godot
        /// writes no <c>type=</c> for nodes whose type comes from the instanced sub-scene).
        /// </summary>
        public string? Type { get; }

        /// <summary>
        /// The canonical key this node is known by — the verbatim <c>parent=</c> value its OWN children
        /// would write (<c>"."</c> for the root; a root-child's key is its name; deeper nodes' keys are
        /// slash paths like <c>"Player/Body"</c>). A child links to its parent by matching the parent's
        /// declared <c>parent=</c> value against the parent's key. Matches the TS <c>buildHierarchy</c>
        /// canonical-key scheme exactly.
        /// </summary>
        public string Key { get; internal set; } = ".";

        /// <summary>Depth from the scene root (root = 0). Assigned during the tree-build pass.</summary>
        public int Depth { get; internal set; }

        /// <summary>
        /// The canonical parent key this node links through (the verbatim <c>parent=</c> value, or
        /// <c>null</c> for the root). Used to build parent → children links in the second pass.
        /// </summary>
        public string? Parent { get; }

        /// <summary>True when the body carried a <c>script = ExtResource("...")</c> attachment.</summary>
        public bool HasScript { get; internal set; }

        /// <summary>True when the header carried an <c>instance=ExtResource("...")</c> (a sub-scene instance).</summary>
        public bool IsInstance { get; }

        /// <summary>Children linked in the second pass, in file declaration order (sibling order).</summary>
        public List<SceneNode> Children { get; } = new();

        public SceneNode(string name, string path, string? type, string? parent, bool isInstance)
        {
            Name = name;
            Path = path;
            Type = type;
            Parent = parent;
            IsInstance = isInstance;
        }
    }

    /// <summary>
    /// Threshold-free structural facts extracted from one <c>.tscn</c>. The rule applies the thresholds
    /// (depth &gt; 10, count &gt; 1000, siblings &gt; 100) so the tuning surface lives in the rule, not the
    /// parser — same separation <c>ProjectAssetParser</c> uses (it returns <c>NodeCount</c>; the rule
    /// decides whether that count is "empty" or "broken").
    ///
    /// <para>
    /// <b>Duplicate-name collisions</b> are reported per <c>(parentKey, name)</c> with the count of nodes
    /// sharing that sibling name. The parser deliberately does NOT collapse duplicates (unlike the TS
    /// <c>buildHierarchy</c>, which throws <c>scene_hierarchy_invalid</c> on a duplicate sibling path):
    /// the rule surfaces <c>duplicate_node_name</c> as a recoverable Warning, so the builder tolerates
    /// collisions and records them as facts instead of aborting. All but the first node with a colliding
    /// path are kept as detached siblings so they still count toward <c>NodeCount</c> and sibling width.
    /// </para>
    /// </summary>
    internal sealed class SceneStructureAnalysis
    {
        /// <summary>Total <c>[node ...]</c> declarations parsed (every node, attached or detached).</summary>
        public int NodeCount { get; }

        /// <summary>The scene root (depth 0), or <c>null</c> when the scene had no recognizable root.</summary>
        public SceneNode? Root { get; }

        /// <summary>All nodes in file order — the root plus every attached/detached node.</summary>
        public IReadOnlyList<SceneNode> Nodes { get; }

        /// <summary>
        /// Sibling-name collisions: each entry is <c>(parentKey, name)</c> with the number of siblings
        /// sharing that name (≥ 2). <c>parentKey</c> is the canonical key (<c>"."</c> for the root's
        /// children, else the verbatim <c>parent=</c> value) so the rule can anchor the issue on the
        /// parent's path.
        /// </summary>
        public IReadOnlyList<(string ParentKey, string Name, int Count)> DuplicateNames { get; }

        /// <summary>
        /// The parse failed to build a usable tree (no header, or no <c>[node]</c> at all). The rule
        /// treats a skipped scene as "not this rule's input" — broken-asset detection is
        /// <c>project_health</c>'s domain — so a skip emits no issues here.
        /// </summary>
        public bool Skipped { get; }

        /// <summary>Human-readable reason when <see cref="Skipped"/> is true; otherwise <c>null</c>.</summary>
        public string? SkipReason { get; }

        private SceneStructureAnalysis(
            int nodeCount,
            SceneNode? root,
            IReadOnlyList<SceneNode> nodes,
            IReadOnlyList<(string, string, int)> duplicateNames,
            bool skipped,
            string? skipReason)
        {
            NodeCount = nodeCount;
            Root = root;
            Nodes = nodes;
            DuplicateNames = duplicateNames;
            Skipped = skipped;
            SkipReason = skipReason;
        }

        /// <summary>Build an analysis for a parsed scene (root may be null when no root was recognized).</summary>
        internal static SceneStructureAnalysis Ok(
            int nodeCount,
            SceneNode? root,
            IReadOnlyList<SceneNode> nodes,
            IReadOnlyList<(string, string, int)> duplicateNames)
            => new SceneStructureAnalysis(nodeCount, root, nodes, duplicateNames, skipped: false, skipReason: null);

        /// <summary>Build a skip result — the scene could not be analyzed (no header / no nodes).</summary>
        internal static SceneStructureAnalysis Skip(string reason)
            => new SceneStructureAnalysis(0, null, Array.Empty<SceneNode>(),
                Array.Empty<(string, string, int)>(), skipped: true, skipReason: reason);
    }

    /// <summary>
    /// Line-oriented <c>.tscn</c> node-tree parser for the <c>scene_structure_health</c> rule. Pure-managed,
    /// no Godot API surface — same binary-less-test discipline as <see cref="ProjectHealth.ProjectAssetParser"/>
    /// and <see cref="MissingScripts.NodeScriptScanner"/>. The algorithm is adapted from the TS
    /// <c>scene-parser.ts</c> + <c>scene-hierarchy.ts</c> (P7.2): a flat <c>[node name= parent= type=]</c>
    /// scan followed by a two-pass tree build (assign each node a canonical path, then link children and
    /// assign depth, root = 0).
    ///
    /// <para>
    /// <b>What this parser does and does not do</b>
    /// <list type="bullet">
    ///   <item><b>Does:</b> extract every <c>[node]</c>'s name/type/parent + whether its body carries a
    ///     <c>script = ExtResource("...")</c> or the header carries an <c>instance=ExtResource("...")</c>,
    ///     reconstruct each node's scene-tree path, build the parent → children tree (assigning depth),
    ///     and record sibling-name collisions. These are exactly the facts the five structure signals
    ///     need.</item>
    ///   <item><b>Does not:</b> validate <c>[ext_resource]</c> references (that is the
    ///     <c>broken_references</c> rule's domain), interpret property values beyond the
    ///     script/instance probes, or detect parse failures (a header-less or node-less file is a broken
    ///     asset — <c>project_health</c>'s domain). It returns <c>Skip</c> for those so this rule emits
    ///     nothing rather than duplicating project_health's findings.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Tolerance vs the TS hierarchy layer.</b> The TS <c>buildHierarchy</c> throws
    /// <c>scene_hierarchy_invalid</c> on a duplicate sibling path, an orphan parent, or multiple roots.
    /// This parser must surface <c>duplicate_node_name</c> as a recoverable Warning, so it tolerates all
    /// three: a duplicate path keeps all but the first node detached (still counted); an orphan parent
    /// keeps the node detached (a malformed file the editor would also reject, but flagging it as a
    /// structure issue adds no value over project_health's broken-asset check); multiple roots keep the
    /// first as the tree root and the rest detached.
    /// </para>
    ///
    /// <para>
    /// <b>Robustness contract (<c>IVerifyRule</c>):</b> the parser never throws on ordinary malformed
    /// input. A node header without a name, a stray bracket, or a truncated body yields whatever it can
    /// — never an exception. A throw would silently drop this scene's issues from a scoped gate check.
    /// </para>
    /// </summary>
    internal static class SceneStructureParser
    {
        /// <summary>
        /// Parse the given scene text into threshold-free structural facts. Never throws. Null/empty
        /// input, a file with no <c>[gd_scene]</c> header, or a scene with no <c>[node]</c> declarations
        /// yields a <see cref="SceneStructureAnalysis.Skip"/> (the rule emits nothing — those are
        /// project_health's broken-asset cases, not structure-complexity cases).
        /// </summary>
        public static SceneStructureAnalysis Parse(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return SceneStructureAnalysis.Skip("scene text is empty");

            var lines = text!.Split('\n');

            // The first non-blank, non-comment line MUST be a [gd_scene] header. A .tres ([gd_resource])
            // has no node tree — skip it (project_health may flag it as broken; this rule has nothing to
            // say about a resource). Anything else is a broken asset — also skip.
            if (!TryFindHeader(lines, out var isScene, out var headerReason))
                return SceneStructureAnalysis.Skip(headerReason);

            if (!isScene)
                return SceneStructureAnalysis.Skip("not a scene (.tres has no node tree)");

            // Pass 1: collect every [node] declaration with its raw attributes (name/type/parent/instance),
            // and scan the node body for a script attachment.
            var raw = CollectNodes(lines);
            if (raw.Count == 0)
                return SceneStructureAnalysis.Skip("scene has no [node] declarations");

            // Pass 2: build the parent → children tree, assign canonical paths + depth (root = 0), and
            // record sibling-name collisions. Tolerates duplicate paths / orphan parents / extra roots.
            var (root, allNodes, duplicateNames) = BuildTree(raw);

            return SceneStructureAnalysis.Ok(raw.Count, root, allNodes, duplicateNames);
        }

        // ---- Pass 0: header detection -----------------------------------------

        private static bool TryFindHeader(string[] lines, out bool isScene, out string reason)
        {
            isScene = false;
            reason = "";
            foreach (var rawLine in lines)
            {
                var trimmed = rawLine.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith(";")) continue; // Godot writes leading comments in some exports
                if (trimmed.StartsWith("[gd_scene"))
                {
                    isScene = true;
                    return true;
                }
                if (trimmed.StartsWith("[gd_resource"))
                {
                    isScene = false;
                    return true;
                }
                reason = $"first non-comment line is not a [gd_scene]/[gd_resource header: \"{Truncate(trimmed, 60)}\"";
                return false;
            }
            reason = "file has no non-blank, non-comment content";
            return false;
        }

        // ---- Pass 1: collect [node] declarations ------------------------------

        private sealed class RawNode
        {
            public string Name { get; }
            public string? Type { get; }
            public string? Parent { get; }     // verbatim parent= value; null = root
            public bool IsInstance { get; }
            public bool HasScript { get; set; }

            public RawNode(string name, string? type, string? parent, bool isInstance)
            {
                Name = name;
                Type = type;
                Parent = parent;
                IsInstance = isInstance;
            }
        }

        private static List<RawNode> CollectNodes(string[] lines)
        {
            var nodes = new List<RawNode>();
            RawNode? current = null;
            var currentHasScript = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];

                // [node ...] headers start at column 0. Opening a new node closes the previous one.
                if (raw.Length > 0 && raw[0] == '[' && raw.StartsWith("[node"))
                {
                    // Close the previous node: capture whether its body carried a script attachment.
                    if (current != null) current.HasScript = currentHasScript;

                    var name = ExtractAttribute(raw, "name");
                    if (!string.IsNullOrEmpty(name))
                    {
                        var type = ExtractAttribute(raw, "type");
                        var parent = ExtractAttribute(raw, "parent");
                        var isInstance = raw.Contains("instance=", StringComparison.Ordinal);
                        current = new RawNode(name!, type, parent, isInstance);
                        currentHasScript = false;
                        nodes.Add(current);
                    }
                    else
                    {
                        // A [node] header without a name is malformed — skip it without aborting.
                        current = null;
                    }
                    continue;
                }

                // Any other bracketed header closes the current node body.
                if (raw.Length > 0 && raw[0] == '[')
                {
                    if (current != null) current.HasScript = currentHasScript;
                    current = null;
                    currentHasScript = false;
                    continue;
                }

                // Only probe for a script attachment while inside a node body.
                if (current != null && !currentHasScript && HasScriptAttachment(raw))
                    currentHasScript = true;
            }

            // Close the final node (its body ran to end-of-file).
            if (current != null) current.HasScript = currentHasScript;

            return nodes;
        }

        /// <summary>
        /// Whether a body line is a <c>script = ExtResource("...")</c> attachment. Mirrors the probe in
        /// <c>MissingScripts.NodeScriptScanner.TryParseScriptAttachment</c> (cheap <c>Contains</c> reject,
        /// then a <c>script</c>-key boundary check so <c>my_script =</c> does not match).
        /// </summary>
        private static bool HasScriptAttachment(string line)
        {
            if (!line.Contains("ExtResource(", StringComparison.Ordinal)) return false;
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("script", StringComparison.Ordinal)) return false;
            var idx = "script".Length;
            while (idx < trimmed.Length && (trimmed[idx] == ' ' || trimmed[idx] == '\t')) idx++;
            return idx < trimmed.Length && trimmed[idx] == '=';
        }

        // ---- Pass 2: build the tree -------------------------------------------

        /// <summary>
        /// Build the parent → children tree, assign canonical keys + paths + depth (root = 0), and record
        /// sibling-name collisions. Tolerates duplicate sibling paths (records them, keeps later nodes
        /// detached), orphan parents (keeps the node detached), and multiple roots (first wins).
        /// Returns (root, allNodes, duplicateNames).
        ///
        /// <para>
        /// <b>Canonical-key scheme</b> (matches the TS <c>buildHierarchy</c>): a node is known by a
        /// <c>Key</c> equal to the verbatim <c>parent=</c> value its OWN children would write. The root's
        /// key is <c>"."</c>; a root-child's key is its own name; a deeper node's key is its path relative
        /// to the root (e.g. <c>"Player/Body"</c>). A child links to its parent by matching the parent's
        /// declared <c>parent=</c> value against the parent's stored <c>Key</c>. Keys are unique by
        /// construction (a second node claiming the same key is a duplicate sibling name), so this scheme
        /// cannot form a link cycle.
        /// </para>
        /// </summary>
        private static (SceneNode? Root, List<SceneNode> All, List<(string, string, int)> Duplicates)
            BuildTree(List<RawNode> raw)
        {
            var all = new List<SceneNode>(raw.Count);
            // Key → node that claimed it first. A second node claiming the same key is a duplicate sibling
            // name: recorded as a collision, kept detached (still counted in NodeCount).
            var byKey = new Dictionary<string, SceneNode>(StringComparer.Ordinal);
            SceneNode? root = null;

            // First: create every node, compute its key + path + root flag, and register it by key.
            foreach (var r in raw)
            {
                var (key, path, isRoot) = CanonicalIdentity(r, root?.Name);
                var node = new SceneNode(r.Name, path, r.Type, r.Parent, r.IsInstance)
                {
                    HasScript = r.HasScript,
                    Key = key,
                };
                all.Add(node);

                if (isRoot)
                {
                    root ??= node; // first root wins; later "roots" stay detached (malformed file)
                    continue;
                }

                // Register the first claimant of a key. A duplicate key → collision; keep detached.
                byKey.TryAdd(key, node);
            }

            // Second: link each non-root, non-duplicate node to its parent (looked up by the parent's key
            // = the node's verbatim parent= value). Orphan parents and detached duplicate-key nodes are
            // left unlinked. `linked` is the set of nodes that successfully claimed a unique key (the
            // first claimant per key) — only those participate in the tree.
            var linked = new HashSet<SceneNode>();
            foreach (var kv in byKey) linked.Add(kv.Value);

            foreach (var node in all)
            {
                if (node == root) continue;
                if (!linked.Contains(node)) continue; // a detached duplicate — not in the tree
                var parentKey = node.Parent ?? ".";
                if (parentKey == ".")
                {
                    // Direct child of the root.
                    root?.Children.Add(node);
                }
                else if (byKey.TryGetValue(parentKey, out var parent))
                {
                    parent.Children.Add(node);
                }
                // else: orphan parent — leave detached.
            }

            // Third: assign depth top-down from the root (root = 0). Detached subtrees stay at depth 0.
            if (root != null)
                AssignDepths(root, 0);

            var duplicates = CollectDuplicateNames(raw);

            return (root, all, duplicates);
        }

        private static void AssignDepths(SceneNode node, int depth)
        {
            node.Depth = depth;
            foreach (var child in node.Children)
                AssignDepths(child, depth + 1);
        }

        /// <summary>
        /// Compute the canonical (key, path, isRoot) identity for a raw node. Root nodes (no
        /// <c>parent=</c>) get key <c>"."</c> and path = their name. <c>parent="."</c> (a direct child of
        /// the root) gets key = name and path = <c>"{rootName}/{name}"</c>. Any other parent value gets
        /// key = <c>"{parent}/{name}"</c> and path = <c>"{rootName}/{parent}/{name}"</c> (with the root
        /// prefix stripped when the parent already starts with the root). Mirrors the TS
        /// <c>buildHierarchy</c> canonical-key logic and <c>MissingScripts.NodeScriptScanner.ParseNodeHeader</c>.
        /// </summary>
        private static (string Key, string Path, bool IsRoot) CanonicalIdentity(RawNode r, string? rootName)
        {
            if (string.IsNullOrEmpty(r.Parent))
                return (Key: ".", Path: r.Name, IsRoot: true);

            if (r.Parent == ".")
                return (Key: r.Name, Path: JoinPath(rootName, r.Name), IsRoot: false);

            return (Key: r.Parent + "/" + r.Name, Path: JoinPath(rootName, r.Parent + "/" + r.Name), IsRoot: false);
        }

        /// <summary>Join a root name (nullable during the pre-root scan) with a relative path.</summary>
        private static string JoinPath(string? rootName, string relative)
            => string.IsNullOrEmpty(rootName) ? relative : rootName + "/" + relative;

        /// <summary>
        /// Collect sibling-name collisions across every parent group. A group with ≥ 2 nodes sharing a
        /// name is a collision. Collisions are recorded once per <c>(parentKey, name)</c> with the count,
        /// so the rule can emit one <c>duplicate_node_name</c> finding per collided name. The count is
        /// derived from the raw declarations (not the deduped tree) so it reflects how many siblings
        /// actually share the name, including the detached duplicates.
        /// </summary>
        private static List<(string, string, int)> CollectDuplicateNames(List<RawNode> raw)
        {
            var counts = new Dictionary<(string ParentKey, string Name), int>();
            foreach (var r in raw)
            {
                if (string.IsNullOrEmpty(r.Parent)) continue; // root — no siblings to collide with
                var key = (r.Parent, r.Name);
                counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
            }

            var duplicates = new List<(string, string, int)>();
            // Sort by parent key then name for deterministic emission (gate-delta stability).
            var sortedKeys = new List<(string ParentKey, string Name)>(counts.Keys);
            sortedKeys.Sort((a, b) =>
            {
                var cmp = string.CompareOrdinal(a.ParentKey, b.ParentKey);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Name, b.Name);
            });
            foreach (var key in sortedKeys)
            {
                if (counts[key] >= 2)
                    duplicates.Add((key.ParentKey, key.Name, counts[key]));
            }
            return duplicates;
        }

        // ---- Attribute extraction (mirrors NodeScriptScanner.SceneRefParser_ExtractAttribute) ----

        /// <summary>
        /// Extract a <c>key="value"</c> attribute from a header line. The leading-space probe
        /// (<c>" key=\""</c>) prevents <c>name</c> matching inside <c>node_name</c> or <c>uid</c> — same
        /// trick <c>SceneRefParser.ExtractAttribute</c> and <c>NodeScriptScanner</c> use.
        /// </summary>
        private static string? ExtractAttribute(string line, string key)
        {
            var probe = " " + key + "=\"";
            var idx = line.IndexOf(probe, StringComparison.Ordinal);
            if (idx < 0) return null;
            var valueStart = idx + probe.Length;
            var valueEnd = line.IndexOf('"', valueStart);
            if (valueEnd < 0) return null;
            return line.Substring(valueStart, valueEnd - valueStart);
        }

        private static string Truncate(string s, int n)
            => s.Length > n ? s.Substring(0, n) + "…" : s;
    }
}
