#nullable enable
using System;
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Rules.ScriptAudit
{
    /// <summary>
    /// The script attachment a <c>.tscn</c>/<c>.tres</c> records, as parsed offline. Mirrors the shape
    /// <see cref="MaterialsShaderHealth.MaterialsShaderParser"/> extracts for a material's shader slot —
    /// Godot writes the recorded class name on the file header and the script reference in the body, so the
    /// parser pulls both, then resolves the body's <c>ExtResource("id")</c> to its declared
    /// <c>[ext_resource path=]</c> so the rule can read the script file.
    /// </summary>
    internal sealed class ScriptAttachment
    {
        /// <summary>
        /// The class name the scene/resource header recorded (<c>script_class="X"</c>), when present.
        /// Godot only writes this attribute when a custom script class is attached; a builtin-typed
        /// resource or a class_name-less script carries none. <c>null</c> means "no recorded class name" —
        /// the rule treats that as "no mismatch to check" (there is nothing to compare against).
        /// </summary>
        public string? RecordedClass { get; }

        /// <summary>
        /// The raw <c>ExtResource</c> id the body's <c>script = ExtResource("id")</c> uses, when a script is
        /// attached. <c>null</c> when no <c>script =</c> line is present (a scene/resource with no script).
        /// Set in two passes (header pass leaves it null; the body pass sets it), so it carries an
        /// <c>internal set</c> like the other body-resolved fields.
        /// </summary>
        public string? ScriptUsageId { get; internal set; }

        /// <summary>
        /// The <c>res://</c> path the script's <c>[ext_resource]</c> declares, when the usage id was
        /// declared. <c>null</c> when the id was dangling (never declared) or the declaration carried no
        /// <c>path=</c>. Kept separate from <see cref="ScriptUid"/> so the rule can read the file by path.
        /// </summary>
        public string? ScriptPath { get; internal set; }

        /// <summary>
        /// The <c>uid://</c> token the script's <c>[ext_resource]</c> declares, when present and distinct
        /// from <see cref="ScriptPath"/>. Carried for evidence/diagnostics; the rule reads the file by path.
        /// </summary>
        public string? ScriptUid { get; internal set; }

        /// <summary>Whether the <c>script =</c> usage id was never declared by any <c>[ext_resource]</c>.</summary>
        public bool ScriptUsageDangling { get; internal set; }

        public ScriptAttachment(string? recordedClass, string? scriptUsageId)
        {
            RecordedClass = recordedClass;
            ScriptUsageId = scriptUsageId;
        }
    }

    /// <summary>
    /// The class declared by a <c>.gd</c> or <c>.cs</c> file, as parsed offline. For a <c>.gd</c> the class
    /// comes from a <c>class_name X</c> statement; for a <c>.cs</c> from a
    /// <c>public [partial] class X</c> declaration, falling back to the file-name stem (Godot convention).
    /// </summary>
    internal sealed class ScriptClass
    {
        /// <summary>
        /// The declared class name, or the file-name-stem fallback for a <c>.cs</c> with no parseable
        /// declaration. <c>null</c> for a <c>.gd</c> that declares no <c>class_name</c>.
        /// </summary>
        public string? ClassName { get; }

        /// <summary>
        /// Whether a <c>.gd</c> file declared a <c>class_name</c>. Always <c>true</c> for <c>.cs</c> (a C#
        /// type always has a name; the fallback guarantees one). Used by the
        /// <c>script_missing_class_name</c> signal, which applies to <c>.gd</c> only.
        /// </summary>
        public bool HasClassName { get; }

        /// <summary>How the class name was resolved — surfaced in evidence so diagnostics can flag heuristic matches.</summary>
        public string Resolution { get; }

        public ScriptClass(string? className, bool hasClassName, string resolution)
        {
            ClassName = className;
            HasClassName = hasClassName;
            Resolution = resolution;
        }
    }

    /// <summary>
    /// Line-oriented text parser for the <c>script_audit</c> rule. Pure-managed, no Godot API surface —
    /// same binary-less-test discipline as <see cref="MaterialsShaderHealth.MaterialsShaderParser"/> and
    /// <see cref="BrokenReferences.SceneRefParser"/>. Greenfield for Godot: Unity's
    /// <c>MissingReferences.Scanner</c> resolves a MonoBehaviour's <c>m_Script</c> GUID to a C# type via
    /// <c>AssetDatabase</c> + reflection; Godot's <c>.tscn</c>/<c>.tres</c>/<c>.gd</c>/<c>.cs</c> are
    /// text-serialized and parseable offline, so no engine load is needed.
    ///
    /// <para>
    /// <b>What this parser does and does not do</b>
    /// <list type="bullet">
    ///   <item><b>Does:</b> from a <c>.tscn</c>/<c>.tres</c> extract the header <c>script_class=</c>
    ///     attribute and the body <c>script = ExtResource("id")</c> slot (with the id resolved to its
    ///     declared <c>[ext_resource]</c> path/uid). From a <c>.gd</c> extract the <c>class_name X</c>
    ///     statement. From a <c>.cs</c> extract the <c>public [partial] class X</c> declaration, falling
    ///     back to the file-name stem. These are exactly the facts the three script-audit signals need.</item>
    ///   <item><b>Does not:</b> resolve references against disk (the rule does that via the file reader),
    ///     validate that the <c>[ext_resource type=]</c> is "Script" (the rule decides), or interpret
    ///     inheritance. It returns whatever it could extract — never throws.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Robustness contract (<c>IVerifyRule</c>):</b> the parser never throws on ordinary malformed
    /// input. A truncated header, a stray bracket, or a body without a <c>[resource]</c> section yields
    /// whatever facts it could extract — never an exception. A throw would silently drop this file's issues
    /// from a scoped gate check.
    /// </para>
    /// </summary>
    internal static class ScriptClassParser
    {
        /// <summary>
        /// Parse a <c>.tscn</c>/<c>.tres</c> into its recorded script attachment. Never throws. Null/empty
        /// input yields an attachment with no recorded class and no script usage. The header
        /// <c>script_class=</c> attribute (only present when a custom script class is attached) is read off
        /// the first <c>[gd_scene]</c>/<c>[gd_resource]</c> header; the body <c>script = ExtResource("id")</c>
        /// slot is the first <c>script =</c> line under a <c>[node]</c>/<c>[resource]</c> body.
        /// </summary>
        public static ScriptAttachment ParseSceneScriptAttachment(string? text)
        {
            var attachment = new ScriptAttachment(recordedClass: null, scriptUsageId: null);
            if (string.IsNullOrEmpty(text)) return attachment;

            var lines = text!.Split('\n');

            // Header script_class= : read it off the first [gd_scene]/[gd_resource] header. Godot writes the
            // attribute only when a custom script class is attached; a builtin-typed resource carries none.
            foreach (var raw in lines)
            {
                var trimmed = raw.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith(";")) continue;
                if (trimmed.StartsWith("[gd_scene") || trimmed.StartsWith("[gd_resource"))
                {
                    attachment = new ScriptAttachment(
                        recordedClass: ExtractAttribute(trimmed, "script_class"),
                        scriptUsageId: attachment.ScriptUsageId);
                    break;
                }
                // The first non-comment line is the header; if it is not a gd_scene/gd_resource, there is no
                // recorded class to read.
                if (trimmed.StartsWith("[")) break;
            }

            // Collect [ext_resource] declarations (id → path + uid) so the script slot's ExtResource id can
            // be resolved to its declared target. Mirrors MaterialsShaderParser.CollectExtResources.
            var extById = CollectExtResources(lines);

            // Body script = ExtResource("id"): the first `script =` line under a [node]/[resource] body.
            // Godot writes a node/resource body with at most one script attachment.
            foreach (var raw in lines)
            {
                if (raw.Length > 0 && raw[0] == '[') continue; // skip headers
                var id = TryParseScriptSlot(raw);
                if (id == null) continue;

                attachment.ScriptUsageId = id;
                if (extById.TryGetValue(id, out var decl))
                {
                    attachment.ScriptPath = decl.Path;
                    attachment.ScriptUid = decl.Uid;
                }
                else
                {
                    attachment.ScriptUsageDangling = true; // id never declared — missing_scripts' domain
                }
                break; // first script = wins
            }

            return attachment;
        }

        /// <summary>
        /// Parse a <c>.gd</c> file's <c>class_name X</c> statement. Never throws. Returns a
        /// <see cref="ScriptClass"/> with <see cref="ScriptClass.HasClassName"/> = false when no
        /// <c>class_name</c> is declared. GDScript writes the statement at the top level (not indented),
        /// typically after <c>extends</c>; <c>class_name X, "res://icon.png"</c> (the optional icon form)
        /// is handled by taking the first token after <c>class_name</c>.
        /// </summary>
        public static ScriptClass ParseGdClass(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return new ScriptClass(className: null, hasClassName: false, resolution: "gd_empty");

            var lines = text!.Split('\n');
            foreach (var raw in lines)
            {
                // A class_name statement is top-level (not indented). Skip indented lines (inner classes use
                // `class X:` which is a different construct, not the global registration this rule tracks).
                if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t')) continue;

                var trimmed = raw.Trim();
                if (trimmed.Length == 0) continue;
                // Skip comments and annotation lines that might contain "class_name" in a string.
                if (trimmed.StartsWith("#")) continue;

                if (TryMatchLeadingKeyword(trimmed, "class_name", out var rest))
                {
                    // The class name is the first token after `class_name`. The optional icon form
                    // `class_name X, "res://icon.png"` is handled by splitting on whitespace/comma.
                    var name = FirstToken(rest);
                    if (!string.IsNullOrEmpty(name))
                        return new ScriptClass(name, hasClassName: true, resolution: "gd_class_name");
                }
            }

            return new ScriptClass(className: null, hasClassName: false, resolution: "gd_no_class_name");
        }

        /// <summary>
        /// Parse a <c>.cs</c> file's class declaration. Never throws. Returns a <see cref="ScriptClass"/>
        /// with the <c>public [partial] class X</c> / <c>internal [partial] class X</c> name when found,
        /// falling back to the file-name stem (Godot convention: <c>Player.cs</c> → <c>Player</c>) so a
        /// <c>.cs</c> always has a candidate class name. The fallback is marked
        /// <see cref="ScriptClass.Resolution"/> = <c>cs_filename</c> so the rule can flag it as heuristic.
        /// The first top-level type declaration wins; nested types are ignored.
        /// </summary>
        public static ScriptClass ParseCsClass(string? text, string fileName)
        {
            if (!string.IsNullOrEmpty(text))
            {
                var lines = text!.Split('\n');
                foreach (var raw in lines)
                {
                    // A top-level type declaration starts at column 0 (no indent); nested types are indented
                    // and ignored. This is a heuristic — a file with unconventional indentation could miss,
                    // which is why the file-name fallback exists.
                    if (raw.Length == 0 || raw[0] == ' ' || raw[0] == '\t') continue;

                    var name = TryMatchClassDeclaration(raw);
                    if (name != null)
                        return new ScriptClass(name, hasClassName: true, resolution: "cs_declaration");
                }
            }

            // Fallback: file-name stem. Godot's convention is Player.cs → class Player. Strip the extension
            // (and any `.g#` variant Godot uses for nested scripts, though those are rare).
            var stem = FileNameStem(fileName);
            return new ScriptClass(stem, hasClassName: true, resolution: "cs_filename");
        }

        /// <summary>
        /// Collect every <c>class_name X</c> declared across a set of <c>.gd</c> files for the cyclic
        /// (duplicate) check. Returns a map from class name → list of <c>res://</c> paths that declare it.
        /// Never throws; a <c>.gd</c> with no <c>class_name</c> contributes nothing. The order of paths per
        /// class follows the iteration order of <paramref name="gdFiles"/> (the rule sorts for deterministic
        /// emission).
        /// </summary>
        public static Dictionary<string, List<string>> CollectGdClassNames(
            IEnumerable<(string ResPath, string Text)> gdFiles)
        {
            var byClass = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (resPath, text) in gdFiles)
            {
                var cls = ParseGdClass(text);
                if (!cls.HasClassName || string.IsNullOrEmpty(cls.ClassName)) continue;
                if (!byClass.TryGetValue(cls.ClassName!, out var list))
                {
                    list = new List<string>();
                    byClass[cls.ClassName!] = list;
                }
                list.Add(resPath);
            }
            return byClass;
        }

        // ---- [ext_resource] collection (mirrors MaterialsShaderParser) --------

        /// <summary>
        /// Build an id → (path, uid) map from every <c>[ext_resource path= uid= id=]</c> header. Both
        /// <c>path</c> and <c>uid</c> are kept so the rule can read the file by path and report the uid.
        /// </summary>
        private static Dictionary<string, (string? Path, string? Uid)> CollectExtResources(string[] lines)
        {
            var map = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
            foreach (var raw in lines)
            {
                if (raw.Length == 0 || raw[0] != '[') continue;
                if (!raw.StartsWith("[ext_resource")) continue;
                var id = ExtractAttribute(raw, "id");
                if (string.IsNullOrEmpty(id)) continue;
                var path = ExtractAttribute(raw, "path");
                var uid = ExtractAttribute(raw, "uid");
                map[id!] = (
                    string.IsNullOrEmpty(path) ? null : path,
                    string.IsNullOrEmpty(uid) ? null : uid);
            }
            return map;
        }

        /// <summary>
        /// If <paramref name="line"/> is a <c>script = ExtResource("id")</c> body line, return the id;
        /// otherwise null. Matches the exact Godot serialization shape and the
        /// <c>MissingScripts.NodeScriptScanner.TryParseScriptAttachment</c> logic: <c>script</c> must be the
        /// property key (rejecting <c>my_script =</c> / <c>script_name =</c>), then <c>ExtResource("id")</c>
        /// after the <c>=</c>.
        /// </summary>
        private static string? TryParseScriptSlot(string line)
        {
            if (!line.Contains("ExtResource(")) return null;

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("script")) return null;
            var idx = "script".Length;
            while (idx < trimmed.Length && (trimmed[idx] == ' ' || trimmed[idx] == '\t')) idx++;
            if (idx >= trimmed.Length || trimmed[idx] != '=') return null;

            var paren = trimmed.IndexOf("ExtResource(", idx, StringComparison.Ordinal);
            if (paren < 0) return null;
            var openQuote = trimmed.IndexOf('"', paren + "ExtResource(".Length);
            if (openQuote < 0) return null;
            var closeQuote = trimmed.IndexOf('"', openQuote + 1);
            if (closeQuote < 0) return null;

            var id = trimmed.Substring(openQuote + 1, closeQuote - openQuote - 1);
            return id.Length > 0 ? id : null;
        }

        // ---- .cs class declaration -------------------------------------------

        /// <summary>
        /// If <paramref name="line"/> is a top-level C# type declaration
        /// (<c>public [partial|sealed|abstract|static] class X</c>, likewise <c>internal</c>), return the
        /// type name; otherwise null. Handles the optional modifiers between the access modifier and
        /// <c>class</c>. The name runs from after <c>class</c> to the first non-identifier char
        /// (<c>&lt;</c>, <c>:</c>, <c>(</c>, whitespace, <c>{</c>).
        /// </summary>
        private static string? TryMatchClassDeclaration(string line)
        {
            // Reject comment lines and using/namespace statements outright.
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith("/*")) return null;
            if (trimmed.StartsWith("using") || trimmed.StartsWith("namespace")) return null;

            // Require an access modifier at the start. Godot scripts are public partial classes; internal is
            // accepted for completeness.
            const string pub = "public";
            const string Int = "internal";
            int i;
            if (trimmed.StartsWith(pub, StringComparison.Ordinal) && AfterModifier(trimmed, pub.Length, out i)) { }
            else if (trimmed.StartsWith(Int, StringComparison.Ordinal) && AfterModifier(trimmed, Int.Length, out i)) { }
            else return null;

            // Skip optional modifiers (partial, sealed, abstract, static, readonly) between the access
            // modifier and `class`. Loop so any ordering/combination is tolerated.
            while (i < trimmed.Length)
            {
                // Skip whitespace.
                while (i < trimmed.Length && (trimmed[i] == ' ' || trimmed[i] == '\t')) i++;

                const string cls = "class";
                if (i + cls.Length <= trimmed.Length
                    && trimmed.Substring(i, cls.Length) == cls
                    && (i + cls.Length == trimmed.Length || !IsIdentChar(trimmed[i + cls.Length])))
                {
                    // Found `class`. The name is the identifier after it.
                    var nameStart = i + cls.Length;
                    while (nameStart < trimmed.Length && (trimmed[nameStart] == ' ' || trimmed[nameStart] == '\t'))
                        nameStart++;
                    var nameEnd = nameStart;
                    while (nameEnd < trimmed.Length && IsIdentChar(trimmed[nameEnd])) nameEnd++;
                    if (nameEnd > nameStart)
                        return trimmed.Substring(nameStart, nameEnd - nameStart);
                    return null;
                }

                // Consume one modifier token and loop. If the next token is not a known modifier, give up
                // (the line is not a class declaration this heuristic recognizes).
                var tokEnd = i;
                while (tokEnd < trimmed.Length && IsIdentChar(trimmed[tokEnd])) tokEnd++;
                if (tokEnd == i) return null; // no token consumed — bail
                i = tokEnd;
            }
            return null;
        }

        /// <summary>Advance past the access modifier, requiring a whitespace boundary after it.</summary>
        private static bool AfterModifier(string s, int start, out int next)
        {
            next = start;
            if (start >= s.Length) return false;
            // The char right after the modifier must be whitespace (not an identifier char) — otherwise the
            // match was a prefix of a longer identifier (e.g. "publicly").
            if (IsIdentChar(s[start])) return false;
            next = start;
            return true;
        }

        private static bool IsIdentChar(char c)
            => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

        // ---- GDScript keyword + token helpers --------------------------------

        /// <summary>
        /// If <paramref name="line"/> starts with <paramref name="keyword"/> followed by a whitespace
        /// boundary, return the remainder (after the keyword and any trailing whitespace); otherwise false.
        /// </summary>
        private static bool TryMatchLeadingKeyword(string line, string keyword, out string rest)
        {
            rest = "";
            if (!line.StartsWith(keyword, StringComparison.Ordinal)) return false;
            if (line.Length == keyword.Length) return false;
            // The char after the keyword must be whitespace (so `class_nameX` is not matched).
            var after = line[keyword.Length];
            if (after != ' ' && after != '\t') return false;
            var i = keyword.Length;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            rest = line.Substring(i);
            return true;
        }

        /// <summary>The first whitespace/comma-delimited token of <paramref name="s"/> (the class name in a <c>class_name X[, ...]</c> statement).</summary>
        private static string FirstToken(string s)
        {
            var end = 0;
            while (end < s.Length && s[end] != ' ' && s[end] != '\t' && s[end] != ',' && s[end] != '\r')
                end++;
            return s.Substring(0, end);
        }

        // ---- Attribute extraction (mirrors the sibling parsers) --------------

        /// <summary>
        /// Extract a <c>key="value"</c> attribute from a header line. The leading-space probe
        /// (<c>" key=\""</c>) prevents <c>script_class</c> matching inside another attribute or the bracket
        /// tag — same trick <see cref="BrokenReferences.SceneRefParser"/> /
        /// <see cref="MissingScripts.NodeScriptScanner"/> use.
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

        /// <summary>The file-name stem (no extension) of a script file name, or the whole name when there is no extension.</summary>
        private static string FileNameStem(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "";
            var slash = fileName.LastIndexOfAny(new[] { '/', '\\' });
            var leaf = slash >= 0 ? fileName.Substring(slash + 1) : fileName;
            var dot = leaf.LastIndexOf('.');
            return dot > 0 ? leaf.Substring(0, dot) : leaf;
        }
    }
}
