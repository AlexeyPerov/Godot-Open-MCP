#nullable enable
using System.Collections.Generic;
using GodotOpenMcp.Verify.Rules.BrokenReferences;

namespace GodotOpenMcp.Verify.Rules.MissingScripts
{
    /// <summary>
    /// A node with a <c>script = ExtResource("id")</c> attachment, as parsed from a <c>.tscn</c>/<c>.tres</c>.
    /// <see cref="ScriptExtId"/> is the in-file string id the node links through; <see cref="NodeName"/> and
    /// <see cref="NodePath"/> carry node-level evidence the generic broken-references rule does not track,
    /// so the P3.7 <c>remove_missing_script</c> fix and agent diagnostics can point at the exact node.
    /// </summary>
    internal sealed class NodeScriptAttachment
    {
        /// <summary>The <c>name=</c> attribute of the <c>[node ...]</c> header.</summary>
        public string NodeName { get; }

        /// <summary>
        /// The slash-delimited scene-tree path to the node (e.g. <c>Player/Body/Arm</c>), reconstructed
        /// from <c>parent=</c> attributes. Matches the path Godot itself reports for a node and what an
        /// agent would pass to a node tool.
        /// </summary>
        public string NodePath { get; }

        /// <summary>
        /// The in-file <c>[ext_resource]</c> id the node's <c>script = ExtResource("id")</c> links to. May
        /// be a dangling id (no declaration in the file) — the rule resolves that separately.
        /// </summary>
        public string ScriptExtId { get; }

        /// <summary>1-based line of the <c>script = ExtResource("id")</c> line.</summary>
        public int ScriptLine { get; }

        public NodeScriptAttachment(string nodeName, string nodePath, string scriptExtId, int scriptLine)
        {
            NodeName = nodeName;
            NodePath = nodePath;
            ScriptExtId = scriptExtId;
            ScriptLine = scriptLine;
        }
    }

    /// <summary>
    /// Result of parsing the node/script attachments in one scene/resource file. The scanner walks this to
    /// resolve each node's script against the file's <c>[ext_resource type="Script"]</c> declarations.
    /// </summary>
    internal sealed class NodeScriptScanResult
    {
        public List<NodeScriptAttachment> Attachments { get; } = new();
    }

    /// <summary>
    /// Line-oriented text parser for Godot <c>.tscn</c>/<c>.tres</c> node → script attachments. Pure-managed,
    /// no Godot API surface — same binary-less-test discipline as <see cref="SceneRefParser"/>. Greenfield
    /// for Godot: Unity's scanner walks prefab/scene YAML and reads <c>m_Script: {fileID}</c> +
    /// <c>m_Name</c> fields off each MonoBehaviour/GameObject; Godot's text format writes a
    /// <c>[node name="..." parent="..."]</c> header followed by a <c>script = ExtResource("id")</c> body
    /// line.
    ///
    /// <para>
    /// <b>What this parser does and does not do</b>
    /// <list type="bullet">
    ///   <item><b>Does:</b> track the current <c>[node]</c> header (name + parent), reconstruct the
    ///     node's scene-tree path, and capture the first <c>script = ExtResource("id")</c> attachment on
    ///     that node's body. A node carries at most one script in Godot, so one attachment per node.</item>
    ///   <item><b>Does not:</b> parse <c>[ext_resource]</c> declarations (the rule reuses
    ///     <see cref="SceneRefParser"/> for that, via <see cref="SceneRefParseResult.ExtResources"/>), nor
    ///     validate that the ext_resource's <c>type=</c> is "Script". Type filtering happens in the rule
    ///     so a <c>type=</c>-less header still resolves by path/uid.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Node-path reconstruction:</b> Godot writes a node's path as a <c>parent=</c> attribute
    /// (<c>parent="Player/Body"</c>) relative to the scene root; the root node has no <c>parent=</c>. The
    /// full path is <c>parent/name</c>, or just <c>name</c> for a root. This matches what Godot reports
    /// for a node's <c>get_path()</c> (sans the leading <c>/root/SceneName</c> the editor adds at play
    /// time — the verify gate runs edit-time against the serialized tree, so the serialized form is the
    /// right reference).
    /// </para>
    ///
    /// <para>
    /// <b>Robustness contract (<c>IVerifyRule</c>):</b> the parser never throws on ordinary malformed
    /// input. A node header without a name, a stray <c>script =</c> with no <c>ExtResource</c>, or a
    /// truncated body yields whatever it can — never an exception. A throw would silently drop this
    /// file's issues from a scoped gate check.
    /// </para>
    /// </summary>
    internal static class NodeScriptScanner
    {
        /// <summary>
        /// Parse the given file text into node → script attachments. Never throws. Null/empty input
        /// yields an empty result.
        /// </summary>
        public static NodeScriptScanResult Parse(string? text)
        {
            var result = new NodeScriptScanResult();
            if (string.IsNullOrEmpty(text)) return result;

            var lines = text!.Split('\n');
            // Track the node currently being read so a body `script =` line can be attributed to it. Godot
            // writes a node header, then its properties (indented), then the next node header — so the
            // "current node" is simply the most recent [node ...] header seen.
            string? currentNodeName = null;
            string currentNodePath = "";
            // A node has at most one script in Godot; once captured we stop attributing further script
            // lines to it (a second `script =` would be malformed anyway).
            bool currentNodeHasScript = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var lineNo = i + 1;

                // [node ...] headers start at column 0. Opening a new node closes the previous one.
                if (raw.Length > 0 && raw[0] == '[' && raw.StartsWith("[node"))
                {
                    (currentNodeName, currentNodePath) = ParseNodeHeader(raw);
                    currentNodeHasScript = false;
                    continue;
                }

                // Any other bracketed header ([ext_resource], [sub_resource], [gd_scene]) also closes the
                // current node — Godot never interleaves node bodies with other section types.
                if (raw.Length > 0 && raw[0] == '[')
                {
                    currentNodeName = null;
                    continue;
                }

                // Only attribute a `script =` line when we are inside a node body and haven't already
                // captured a script for it.
                if (currentNodeName != null && !currentNodeHasScript)
                {
                    var extId = TryParseScriptAttachment(raw);
                    if (extId != null)
                    {
                        result.Attachments.Add(new NodeScriptAttachment(
                            currentNodeName, currentNodePath, extId, lineNo));
                        currentNodeHasScript = true;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Extract <c>(name, fullPath)</c> from a <c>[node name="..." parent="..." type="..."]</c> header.
        /// Godot root nodes omit <c>parent=</c>; children carry it. Full path is <c>parent/name</c>. A
        /// header without a name yields <c>(null, "")</c> so the caller treats the section as non-node.
        /// </summary>
        private static (string? name, string path) ParseNodeHeader(string line)
        {
            var name = SceneRefParser_ExtractAttribute(line, "name");
            if (string.IsNullOrEmpty(name)) return (null, "");

            var parent = SceneRefParser_ExtractAttribute(line, "parent");
            // parent may be ".", which means "this node's parent is the scene root sibling" → the path is
            // still just the name (Godot writes parent="." for first-level children). Any non-empty
            // parent other than "." is a slash path to the parent.
            var fullPath = string.IsNullOrEmpty(parent) || parent == "."
                ? name!
                : parent + "/" + name!;
            return (name!, fullPath);
        }

        /// <summary>
        /// If <paramref name="line"/> is a <c>script = ExtResource("id")</c> body line, return the id;
        /// otherwise null. Matches the exact Godot serialization shape. Leading whitespace (Godot indents
        /// node properties) is tolerated.
        /// </summary>
        private static string? TryParseScriptAttachment(string line)
        {
            // `script` must be the property key. `Contains` first for the cheap reject, then confirm the
            // line is `script = ExtResource("...")` and not e.g. `my_script = ...` by requiring `script`
            // to appear at the start (after trim) followed by whitespace and `=`.
            if (!line.Contains("ExtResource(")) return null;

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("script")) return null;
            // Advance past "script", skip whitespace, require '='. This rejects `my_script =` and
            // `script_name =` (those start with "script" as a substring but the char after is not a
            // space/tab/'=' boundary).
            var idx = "script".Length;
            while (idx < trimmed.Length && (trimmed[idx] == ' ' || trimmed[idx] == '\t')) idx++;
            if (idx >= trimmed.Length || trimmed[idx] != '=') return null;

            // Find ExtResource("id") anywhere after the '='. The id is between the first pair of double
            // quotes after the opening paren.
            var paren = trimmed.IndexOf("ExtResource(", idx, System.StringComparison.Ordinal);
            if (paren < 0) return null;
            var openQuote = trimmed.IndexOf('"', paren + "ExtResource(".Length);
            if (openQuote < 0) return null;
            var closeQuote = trimmed.IndexOf('"', openQuote + 1);
            if (closeQuote < 0) return null;

            var id = trimmed.Substring(openQuote + 1, closeQuote - openQuote - 1);
            return id.Length > 0 ? id : null;
        }

        // SceneRefParser.ExtractAttribute is internal to the BrokenReferences namespace. We mirror its
        // ` key="value"` extraction here (rather than widen its visibility) so this scanner stays
        // self-contained for node header attributes. The logic is identical: probe with a leading space
        // so "name" does not match inside "node_name" or "parent".
        private static string? SceneRefParser_ExtractAttribute(string line, string key)
        {
            var probe = " " + key + "=\"";
            var idx = line.IndexOf(probe, System.StringComparison.Ordinal);
            if (idx < 0) return null;
            var valueStart = idx + probe.Length;
            var valueEnd = line.IndexOf('"', valueStart);
            if (valueEnd < 0) return null;
            return line.Substring(valueStart, valueEnd - valueStart);
        }
    }
}
