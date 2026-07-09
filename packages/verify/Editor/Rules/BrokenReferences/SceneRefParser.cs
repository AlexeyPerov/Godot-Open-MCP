#nullable enable
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Rules.BrokenReferences
{
    /// <summary>
    /// A parsed <c>[ext_resource ...]</c> declaration from a <c>.tscn</c>/<c>.tres</c> file. Mirrors the
    /// Godot header shape <c>[ext_resource type="T" uid="uid://..." path="res://..." id="1_abc"]</c> —
    /// any of <c>uid</c>/<c>path</c> may be absent (Godot writes at least one, and prefers uid since
    /// 4.x). <see cref="Id"/> is the in-file string id that <c>ExtResource("id")</c> sites use to link
    /// back here.
    /// </summary>
    internal sealed class ExtResourceDecl
    {
        public string Id { get; }
        public string? Path { get; }
        public string? Uid { get; }
        public int Line { get; }

        public ExtResourceDecl(string id, string? path, string? uid, int line)
        {
            Id = id;
            Path = path;
            Uid = uid;
            Line = line;
        }
    }

    /// <summary>
    /// A usage of <c>ExtResource("id")</c> or <c>SubResource("id")</c> in node/resource property values.
    /// The Godot serializer writes these as e.g. <c>script = ExtResource("1_abc")</c>. A usage is
    /// "dangling" when its id was never declared by any <c>[ext_resource]</c>/<c>[sub_resource]</c>
    /// header in the same file.
    /// </summary>
    internal sealed class ResourceUsage
    {
        /// <summary><c>ExtResource</c> or <c>SubResource</c> — the Godot builtin the usage refers through.</summary>
        public string Kind { get; }

        public string Id { get; }
        public int Line { get; }

        public ResourceUsage(string kind, string id, int line)
        {
            Kind = kind;
            Id = id;
            Line = line;
        }
    }

    /// <summary>
    /// Result of parsing one scene/resource file: the declarations found and the usages seen. The
    /// scanner walks this to (a) validate each <c>[ext_resource]</c> against <see cref="IResourceResolver"/>
    /// and (b) match each usage to a declaration, flagging dangling ids.
    /// </summary>
    internal sealed class SceneRefParseResult
    {
        public List<ExtResourceDecl> ExtResources { get; } = new();
        public HashSet<string> SubResourceIds { get; } = new();
        public List<ResourceUsage> Usages { get; } = new();
    }

    /// <summary>
    /// Line-oriented text parser for Godot <c>.tscn</c>/<c>.tres</c> resource references. Pure-managed,
    /// no Godot API surface — the same binary-less-test discipline as the <c>Core/</c> contract types.
    /// Greenfield for Godot: Unity's scanner parses YAML <c>guid:</c>/<c>fileID:</c> tokens against
    /// <c>AssetDatabase</c>; Godot's text format uses bracketed <c>[ext_resource]</c>/<c>[sub_resource]</c>
    /// headers plus inline <c>ExtResource("id")</c>/<c>SubResource("id")</c> call sites.
    ///
    /// <para>
    /// <b>What this parser does and does not do</b>
    /// <list type="bullet">
    ///   <item><b>Does:</b> extract <c>[ext_resource]</c> id/path/uid tuples, record declared
    ///     <c>[sub_resource]</c> ids, and collect every <c>ExtResource("...")</c>/<c>SubResource("...")</c>
    ///     usage with its line. This is exactly what the broken-references rule needs.</item>
    ///   <item><b>Does not:</b> validate types, resolve inheritance, or interpret node trees. Those are
    ///     later rules' concerns (P3.3 missing scripts, etc.). Keeping the parser narrowly scoped to
    ///     reference resolution is what makes the checkpoint path cheap.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Robustness contract (<c>IVerifyRule</c>):</b> the parser never throws on ordinary malformed
    /// input. A truncated header, a stray bracket, or an unparseable id yields an empty/exact-as-found
    /// result rather than an exception — the rule's caller (<c>VerifyRunner</c>) catches defensively,
    /// but a throw would silently drop this file's issues from a scoped gate check.
    /// </para>
    /// </summary>
    internal static class SceneRefParser
    {
        /// <summary>
        /// Parse the given file text into declarations + usages. Never throws. A null/empty input yields
        /// an empty result.
        /// </summary>
        public static SceneRefParseResult Parse(string? text)
        {
            var result = new SceneRefParseResult();
            if (string.IsNullOrEmpty(text)) return result;

            // Split manually rather than text.Split('\n') to avoid allocating a full string array for
            // large scenes; the checkpoint path runs on every mutation. We track line numbers 1-based
            // to match what a developer sees in their editor and what Evidence["line"] reports.
            var lines = text!.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var lineNo = i + 1;

                // Godot .tscn/.tres headers are bracketed sections like [ext_resource ...]. They start
                // at column 0 (no indent). Property-value lines (script = ExtResource("1_abc")) are
                // indented or appear in the node body; we scan those for usages regardless of indent.
                if (raw.Length > 0 && raw[0] == '[')
                {
                    ParseHeader(raw, lineNo, result);
                }
                ParseUsages(raw, lineNo, result);
            }

            return result;
        }

        private static void ParseHeader(string line, int lineNo, SceneRefParseResult result)
        {
            // Match the opening tag only: [ext_resource ...], [sub_resource ...]. Other headers
            // ([node], [gd_scene], [gd_resource], etc.) are ignored — they carry no external refs.
            // StartsWith is used (not a full regex) because Godot appends attributes after the tag name.
            if (line.StartsWith("[ext_resource"))
            {
                var decl = ParseExtResource(line, lineNo);
                if (decl != null) result.ExtResources.Add(decl);
            }
            else if (line.StartsWith("[sub_resource"))
            {
                // We only need the id of each declared sub_resource to detect dangling SubResource
                // usages. Type/instance_placeholder are irrelevant to reference integrity.
                var id = ExtractAttribute(line, "id");
                if (!string.IsNullOrEmpty(id)) result.SubResourceIds.Add(id!);
            }
        }

        private static ExtResourceDecl? ParseExtResource(string line, int lineNo)
        {
            // Every ext_resource MUST carry an id (Godot always writes one). No id → the header is
            // malformed; treat it as unparseable rather than emitting a phantom declaration.
            var id = ExtractAttribute(line, "id");
            if (string.IsNullOrEmpty(id)) return null;

            var path = ExtractAttribute(line, "path");
            var uid = ExtractAttribute(line, "uid");
            return new ExtResourceDecl(id!, path, uid, lineNo);
        }

        private static void ParseUsages(string line, int lineNo, SceneRefParseResult result)
        {
            // Usages appear as ExtResource("id") or SubResource("id") in property values. We scan the
            // whole line (not just indented body) because Godot also writes them inside [node ...] header
            // instance= attributes: [node name="X" instance=ExtResource("1_abc")].
            //
            // We do a cheap Contains check before the heavier index walk so lines that obviously have no
            // reference (the vast majority) skip the substring search entirely.
            if (!line.Contains("ExtResource(") && !line.Contains("SubResource(")) return;

            CollectUsages(line, "ExtResource(", lineNo, result);
            CollectUsages(line, "SubResource(", lineNo, result);
        }

        private static void CollectUsages(string line, string token, int lineNo, SceneRefParseResult result)
        {
            var kind = token == "ExtResource(" ? "ExtResource" : "SubResource";
            var idx = 0;
            while (idx < line.Length)
            {
                var hit = line.IndexOf(token, idx, System.StringComparison.Ordinal);
                if (hit < 0) break;

                // Godot always quotes the id: ExtResource("1_abc"). Find the opening quote right after
                // the paren, then the closing quote. If the quotes aren't where expected (malformed),
                // advance past the token and continue — never throw.
                var openQuote = line.IndexOf('"', hit + token.Length);
                if (openQuote < 0) break;
                var closeQuote = line.IndexOf('"', openQuote + 1);
                if (closeQuote < 0) break;

                var id = line.Substring(openQuote + 1, closeQuote - openQuote - 1);
                if (id.Length > 0)
                    result.Usages.Add(new ResourceUsage(kind, id, lineNo));

                idx = closeQuote + 1;
            }
        }

        /// <summary>
        /// Extract a <c>key="value"</c> attribute from a header line. Returns null when absent. Handles
        /// the Godot serialization shape <c>key="value"</c> (value always double-quoted). Robust to
        /// attribute order — Godot writes <c>type=</c>, <c>uid=</c>, <c>path=</c>, <c>id=</c> in varying
        /// order across versions.
        /// </summary>
        private static string? ExtractAttribute(string line, string key)
        {
            // Godot separates attributes with spaces: [ext_resource type="..." uid="..." id="..."]. We
            // require a leading space before the key so "id" does not match inside "uid" (the substring
            // `id="` is contained in `uid="`). The leading-space probe also naturally skips the bracket
            // tag name (e.g. `[ext_resource`) since that is followed by a space, not a quote.
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
