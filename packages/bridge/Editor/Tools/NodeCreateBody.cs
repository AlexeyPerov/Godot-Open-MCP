#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_node_create</c> (P2.3). Extracts the scalar fields
    /// the create handler needs straight off the raw JSON body using the same hand-rolled
    /// <c>IndexOf</c>-substring style as <see cref="NodeFindBody"/> and
    /// <see cref="BridgeRequestBody.ExtractTimeoutMs"/> — the bridge deliberately carries no typed
    /// JSON DOM dependency on the hot path (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is
    /// unit-testable in the binary-less xUnit host. The editor-only <see cref="NodeTools"/> handler
    /// constructs this from the raw body string and then branches on the parsed fields.
    /// </para>
    ///
    /// <para>
    /// Two creation modes, mirroring Godot-MCP's <c>Tool_Node.Create</c>:
    /// <list type="bullet">
    /// <item><description><see cref="InstanceScenePath"/> (priority) — instance a <c>PackedScene</c>
    /// from a <c>res://</c> <c>.tscn</c> path.</description></item>
    /// <item><description><see cref="TypeClassName"/> (fallback, default <c>"Node"</c>) — instantiate
    /// a Godot class via <c>ClassDB</c>.</description></item>
    /// </list>
    /// When <see cref="InstanceScenePath"/> is set, it wins over <see cref="TypeClassName"/>.
    /// </para>
    /// </summary>
    internal sealed class NodeCreateBody
    {
        /// <summary>
        /// Name for the new Node. When null/empty the handler leaves Godot's default name for the
        /// type/scene untouched.
        /// </summary>
        internal string? Name { get; private set; }

        /// <summary>
        /// Godot class name to instantiate (e.g. <c>Node3D</c>, <c>Sprite2D</c>, <c>Node</c>).
        /// Used only when <see cref="InstanceScenePath"/> is unset. Defaults to <c>"Node"</c> when
        /// null/empty — adapted from Godot-MCP's <c>Tool_Node.Create</c> default.
        /// </summary>
        internal string? TypeClassName { get; private set; }

        /// <summary>
        /// <c>res://</c> path to a <c>PackedScene</c> (<c>.tscn</c>/<c>.scn</c>) to instance as the
        /// new Node. Takes precedence over <see cref="TypeClassName"/> when set.
        /// </summary>
        internal string? InstanceScenePath { get; private set; }

        /// <summary>
        /// Scene-tree path of the parent Node (relative to the edited scene root, or
        /// <c>/root/&lt;rootName&gt;/...</c>). When null/empty the new Node is parented to the
        /// edited scene root. Reuses <see cref="NodePathNormalizer"/> + <c>GetNodeOrNull</c> on the
        /// edited root — same resolver as <c>node_find</c>.
        /// </summary>
        internal string? ParentNodePath { get; private set; }

        /// <summary>
        /// Optional position as <c>"x,y,z"</c> (3D) or <c>"x,y"</c> (2D). Applied only when the
        /// new Node is a <c>Node3D</c> / <c>Node2D</c>. Null when unset.
        /// </summary>
        internal string? Position { get; private set; }

        /// <summary>
        /// Optional rotation as <c>"x,y,z"</c> degrees (3D) or <c>"x,y"</c> degrees (2D). Applied
        /// only when the new Node is a <c>Node3D</c> / <c>Node2D</c>. Null when unset.
        /// </summary>
        internal string? Rotation { get; private set; }

        /// <summary>
        /// Optional scale as <c>"x,y,z"</c> (3D) or <c>"x,y"</c> (2D). Applied only when the new
        /// Node is a <c>Node3D</c> / <c>Node2D</c>. Null when unset.
        /// </summary>
        internal string? Scale { get; private set; }

        /// <summary>Default <see cref="TypeClassName"/> when the caller omits both creation-mode
        /// fields. Matches Godot-MCP's <c>Tool_Node.Create</c> default so an agent that sends an
        /// empty body gets a plain <c>Node</c>, not a hard error.</summary>
        internal const string DefaultTypeClassName = "Node";

        /// <summary>True when <see cref="InstanceScenePath"/> is set — scene instancing takes
        /// precedence over class instantiation.</summary>
        internal bool IsInstanceScene => !string.IsNullOrEmpty(InstanceScenePath);

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="NodeCreateBody"/>. Never throws — a
        /// missing or malformed field falls back to its default (null / <c>"Node"</c>). Empty/null
        /// body returns an all-default instance (typed <c>Node</c> at the scene root).
        /// </summary>
        internal static NodeCreateBody Parse(string? body)
        {
            var parsed = new NodeCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.Name = ExtractNullableStringValue(body, "name");
            parsed.TypeClassName = ExtractNullableStringValue(body, "type_class_name");
            parsed.InstanceScenePath = ExtractNullableStringValue(body, "instance_scene_path");
            parsed.ParentNodePath = ExtractNullableStringValue(body, "parent_node_path");
            parsed.Position = ExtractNullableStringValue(body, "position");
            parsed.Rotation = ExtractNullableStringValue(body, "rotation");
            parsed.Scale = ExtractNullableStringValue(body, "scale");
            return parsed;
        }

        /// <summary>The effective class to instantiate when not instancing a scene. Falls back to
        /// <see cref="DefaultTypeClassName"/> when the caller omitted <see cref="TypeClassName"/>.
        /// Used by the editor-only handler (which is <c>#if TOOLS</c>); exposed here so a future
        /// pure-managed validation step can read it without re-parsing.</summary>
        internal string EffectiveTypeClassName =>
            string.IsNullOrEmpty(TypeClassName) ? DefaultTypeClassName : TypeClassName!;

        NodeCreateBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Mirrors NodeFindBody.Extract*: locate `"key"`, walk past the colon, read the scalar.
        // Strings are unwrapped from their quotes and unescaped for the small set of JSON string
        // escapes (\" \\ \/ \n \r \t \uXXXX); a field that is present-but-null (e.g.
        // `"name":null`) is treated as absent. The extraction logic is duplicated from
        // NodeFindBody rather than factored into a shared JsonScalar reader because (a) the bridge
        // intentionally avoids a typed JSON dependency, (b) each tool's field set is tiny, and
        // (c) keeping the parsers independent means a bug in one cannot regress the other. If a
        // third tool duplicates this, extract a shared helper then.

        static string? ExtractNullableStringValue(string body, string key)
        {
            var raw = ExtractRawValue(body, key);
            return Unquote(raw);
        }

        /// <summary>
        /// Slice the raw token(s) following <c>"key":</c> up to the next top-level comma or closing
        /// brace. Returns null when the key is absent.
        /// </summary>
        static string? ExtractRawValue(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;

            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return null;

            // null literal → absent.
            if (start + 4 <= body.Length && body.Substring(start, 4) == "null")
                return null;

            // String value: slice from opening quote to the matching closing quote, honoring
            // backslash escapes so an embedded quote doesn't end the value early.
            if (body[start] == '"')
                return SliceQuotedString(body, start);

            // Number / boolean / etc.: slice to the next comma or closing brace.
            return SliceBareToken(body, start);
        }

        static string SliceQuotedString(string body, int start)
        {
            // start points at the opening quote.
            var sb = new System.Text.StringBuilder(body.Length - start);
            int i = start + 1;
            while (i < body.Length)
            {
                var c = body[i];
                if (c == '\\' && i + 1 < body.Length)
                {
                    var next = body[i + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); i += 2; continue;
                        case '\\': sb.Append('\\'); i += 2; continue;
                        case '/': sb.Append('/'); i += 2; continue;
                        case 'n': sb.Append('\n'); i += 2; continue;
                        case 'r': sb.Append('\r'); i += 2; continue;
                        case 't': sb.Append('\t'); i += 2; continue;
                        case 'b': sb.Append('\b'); i += 2; continue;
                        case 'f': sb.Append('\f'); i += 2; continue;
                        case 'u' when i + 5 < body.Length:
                            if (int.TryParse(body.Substring(i + 2, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var code))
                                sb.Append((char)code);
                            i += 6;
                            continue;
                        default:
                            sb.Append(next); i += 2; continue;
                    }
                }
                if (c == '"') return sb.ToString();
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        static string SliceBareToken(string body, int start)
        {
            int end = start;
            while (end < body.Length && body[end] != ',' && body[end] != '}' && body[end] != ']')
                end++;
            return body.Substring(start, end - start).Trim();
        }

        /// <summary>
        /// Unquote a raw value into a non-empty string, or null when the raw is null/empty/already
        /// the literal null marker. <see cref="ExtractRawValue"/> already unwraps quotes, so this
        /// mostly filters absent values.
        /// </summary>
        static string? Unquote(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            return raw;
        }
    }
}
