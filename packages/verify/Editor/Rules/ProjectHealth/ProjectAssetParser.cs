#nullable enable

namespace GodotOpenMcp.Verify.Rules.ProjectHealth
{
    /// <summary>
    /// Result of structurally validating a <c>.tres</c>/<c>.tscn</c> text resource for the
    /// <c>project_health</c> rule. Carries whether the file parsed as a recognizable Godot resource
    /// (<see cref="IsValid"/>) and, for scenes, how many <c>[node]</c> declarations the body carries
    /// (<see cref="NodeCount"/>) — the empty-scene check keys off the latter. Pure data; the parser
    /// never attaches to <see cref="VerifyIssue"/> directly (the rule owns issue construction so severity
    /// and evidence stay in one place).
    /// </summary>
    internal sealed class ProjectAssetParseResult
    {
        /// <summary>
        /// True when the file opened with a recognizable Godot resource header
        /// (<c>[gd_resource</c> or <c>[gd_scene</c>) AND, for scenes, contained at least one node.
        /// A file with no header, or a scene with zero nodes, is structurally broken/empty and sets this
        /// <c>false</c> with a <see cref="FailureDetail"/> explaining why.
        /// </summary>
        public bool IsValid { get; }

        /// <summary>
        /// True when the file opened with <c>[gd_scene</c> (a scene). Used by the rule to decide whether
        /// to run the empty-scene check (scenes only) — a <c>.tres</c> is never an empty-scene candidate.
        /// </summary>
        public bool IsScene { get; }

        /// <summary>
        /// Count of <c>[node ...]</c> headers in the body. Godot scenes always have exactly one root
        /// node, so <c>NodeCount == 1</c> means a root-only (empty) scene. <c>0</c> for non-scenes and
        /// for scenes that failed to parse.
        /// </summary>
        public int NodeCount { get; }

        /// <summary>
        /// Human-readable reason when <see cref="IsValid"/> is <c>false</c> (the parse failure detail
        /// surfaced in <c>Evidence["detail"]</c>); <c>null</c> for valid assets.
        /// </summary>
        public string? FailureDetail { get; }

        private ProjectAssetParseResult(bool isValid, bool isScene, int nodeCount, string? failureDetail)
        {
            IsValid = isValid;
            IsScene = isScene;
            NodeCount = nodeCount;
            FailureDetail = failureDetail;
        }

        /// <summary>Build a result for a valid resource (header present; for scenes, ≥1 node).</summary>
        internal static ProjectAssetParseResult Ok(bool isScene, int nodeCount)
            => new ProjectAssetParseResult(isValid: true, isScene, nodeCount, failureDetail: null);

        /// <summary>Build a result for a structurally broken file (no/invalid header, or a scene with no nodes).</summary>
        internal static ProjectAssetParseResult Fail(string failureDetail)
            => new ProjectAssetParseResult(isValid: false, isScene: false, nodeCount: 0, failureDetail);
    }

    /// <summary>
    /// Line-oriented structural validator for Godot <c>.tscn</c>/<c>.tres</c> text resources. Pure-managed,
    /// no Godot API surface — same binary-less-test discipline as <see cref="BrokenReferences.SceneRefParser"/>.
    /// Adapted from Unity's broken-asset load probe (Unity tries <c>AssetDatabase.LoadMainAssetAtPath</c>
    /// and flags a null/throwing load as broken); Godot's text formats are parseable offline, so the
    /// Godot analogue is a header + node-count check against the serialized text — no engine load needed.
    ///
    /// <para>
    /// <b>What this parser does and does not do</b>
    /// <list type="bullet">
    ///   <item><b>Does:</b> confirm the file opens with a <c>[gd_resource</c> or <c>[gd_scene</c> header
    ///     (the first non-blank, non-comment line), and for scenes count the <c>[node</c> headers. This
    ///     is exactly what the broken-asset and empty-scene checks need.</item>
    ///   <item><b>Does not:</b> validate <c>[ext_resource]</c>/<c>[sub_resource]</c> references (that is
    ///     the <c>broken_references</c> rule's domain), resolve property values, or interpret the node
    ///     tree. Keeping the parser narrowly scoped to header + node-count is what makes the
    ///     whole-project scan cheap enough to run on every Full-mode pass.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Robustness contract (<c>IVerifyRule</c>):</b> the parser never throws on ordinary malformed
    /// input. A truncated body, a stray bracket, or an unparseable header yields a <c>Fail</c> result
    /// with a detail string — never an exception. A throw would silently drop this file's issues from a
    /// scoped gate check.
    /// </para>
    /// </summary>
    internal static class ProjectAssetParser
    {
        /// <summary>
        /// Structurally validate the given file text. Never throws. Null/empty input yields a
        /// <c>Fail</c> (an empty file is not a valid Godot resource). A file whose first non-blank,
        /// non-comment line is not a <c>[gd_resource</c>/<c>[gd_scene</c> header yields a <c>Fail</c>.
        /// A scene whose body contains zero <c>[node</c> headers yields a <c>Fail</c>.
        /// </summary>
        public static ProjectAssetParseResult Parse(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return ProjectAssetParseResult.Fail("file is empty");

            var lines = text!.Split('\n');

            // The first non-blank, non-comment line MUST be a Godot resource header. Godot always writes
            // [gd_scene ...] or [gd_resource ...] as the first line; anything else (a stray BOM-stripped
            // fragment, a hand-edited file missing its header, a binary .scn/.res mislabeled .tscn) is
            // structurally broken from this rule's point of view.
            string? headerLine = null;
            foreach (var raw in lines)
            {
                var trimmed = raw.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith(";")) continue; // Godot writes leading comments in some exports
                headerLine = trimmed;
                break;
            }

            if (headerLine == null)
                return ProjectAssetParseResult.Fail("file has no non-blank, non-comment content");

            var isScene = headerLine.StartsWith("[gd_scene");
            var isResource = headerLine.StartsWith("[gd_resource");
            if (!isScene && !isResource)
                return ProjectAssetParseResult.Fail(
                    $"first non-comment line is not a [gd_scene]/[gd_resource header: \"{Truncate(headerLine, 60)}\"");

            // For a .tres ([gd_resource]) the header presence is enough — resources have no node tree.
            if (!isScene)
                return ProjectAssetParseResult.Ok(isScene: false, nodeCount: 0);

            // For a scene, count [node headers. Godot always writes exactly one root node; zero nodes
            // means the scene is structurally broken (a hand-truncated file), one node means a root-only
            // (empty) scene. The empty-scene finding keys off NodeCount == 1; a zero-node scene is a
            // broken-asset (the stronger signal wins).
            var nodeCount = 0;
            foreach (var raw in lines)
            {
                // [node headers start at column 0 (no indent) — same convention as SceneRefParser and
                // NodeScriptScanner. StartsWith("[node") avoids matching a hypothetical "[node_like]" tag.
                if (raw.Length > 0 && raw[0] == '[' && raw.StartsWith("[node"))
                    nodeCount++;
            }

            if (nodeCount == 0)
                return ProjectAssetParseResult.Fail("scene has no [node] declarations");

            return ProjectAssetParseResult.Ok(isScene: true, nodeCount);
        }

        private static string Truncate(string s, int n)
            => s.Length > n ? s.Substring(0, n) + "…" : s;
    }
}
