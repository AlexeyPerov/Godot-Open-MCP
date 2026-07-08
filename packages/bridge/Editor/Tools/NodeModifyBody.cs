#nullable enable
using System;
using System.Collections.Generic;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_node_modify</c> (P2.4). Extracts the scalar fields and
    /// the <c>properties</c> map the modify handler needs straight off the raw JSON body using the same
    /// hand-rolled <c>IndexOf</c>-substring style as <see cref="NodeCreateBody"/> and
    /// <see cref="NodeFindBody"/> — the bridge deliberately carries no typed JSON DOM dependency on the
    /// hot path (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is unit-testable in
    /// the binary-less xUnit host. The editor-only <see cref="NodeTools"/> handler constructs this from
    /// the raw body string and then branches on the parsed fields.
    /// </para>
    ///
    /// <para>
    /// Two target shapes, mirroring Unity Open MCP's <c>gameobject_modify</c> single/batch contract
    /// adapted to Godot node semantics:
    /// <list type="bullet">
    /// <item><description>Single target — <see cref="NodePath"/> set; applies to one node.</description></item>
    /// <item><description>Batch target — <see cref="NodePaths"/> set (array of paths); applies the same
    /// <see cref="Properties"/> + transform fields to each.</description></item>
    /// </list>
    /// At least one of <see cref="NodePath"/> / <see cref="NodePaths"/> is required; the handler fails
    /// with <c>missing_parameter</c> when neither is present.
    /// </para>
    ///
    /// <para>
    /// Property application surface: <see cref="Properties"/> (a free-form string→string map) carries
    /// scalar/transform convenience fields an agent wants applied to each target. The editor-only handler
    /// (<see cref="NodeTools.ApplyProperties"/>) coerces each value to the right Godot type and collects
    /// <c>unsupported_property</c> warnings for keys it does not recognize — P2.4 supports
    /// <c>visible</c>, <c>modulate</c>, transform shortcuts (<c>position</c>/<c>rotation</c>/<c>scale</c>
    /// on Node2D/Node3D), and <c>name</c> (single-target only).
    /// </para>
    /// </summary>
    internal sealed class NodeModifyBody
    {
        /// <summary>
        /// Single-target scene-tree path (same resolver vocabulary as <c>node_find</c>:
        /// <c>Main/Player</c>, <c>/root/Main/Player</c>, <c>.</c> for the root). Empty when unset. When
        /// both <see cref="NodePath"/> and <see cref="NodePaths"/> are set, the handler treats the union
        /// of targets (de-duplicated in resolution order).
        /// </summary>
        internal string NodePath { get; private set; } = string.Empty;

        /// <summary>
        /// Batch-target scene-tree paths. Empty when unset. When non-empty, the handler applies the same
        /// <see cref="Properties"/> + transform fields to each resolved node.
        /// </summary>
        internal List<string> NodePaths { get; private set; } = new();

        /// <summary>
        /// Free-form <c>string→string</c> property map applied to each target. Keys are property names
        /// (<c>visible</c>, <c>modulate</c>, <c>position</c>, etc.); values are the raw string the
        /// handler coerces (e.g. <c>"false"</c>, <c>"1,0,0,1"</c>). Empty when unset. Never null.
        /// </summary>
        internal Dictionary<string, string> Properties { get; private set; } = new();

        /// <summary>
        /// Transform convenience field: position as <c>"x,y,z"</c> (3D) or <c>"x,y"</c> (2D). Applied
        /// only when the target is a <c>Node3D</c> / <c>Node2D</c>. Null when unset. Mirrors
        /// <see cref="NodeCreateBody.Position"/>, surfaced at the top level (same as Unity's
        /// <c>gameobject_modify</c> flat transform fields) so an agent can write
        /// <c>{"node_path":"X","position":"1,2,3"}</c> without nesting under <c>properties</c>.
        /// </summary>
        internal string? Position { get; private set; }

        /// <summary>Transform convenience field: rotation in degrees. Null when unset.</summary>
        internal string? Rotation { get; private set; }

        /// <summary>Transform convenience field: scale. Null when unset.</summary>
        internal string? Scale { get; private set; }

        /// <summary>
        /// Rename convenience field (single-target only). When the handler sees <c>name</c> in a batch
        /// context it still applies it to each target (last-writer semantics on collisions are the
        /// agent's responsibility) — but the schema documents it as single-target.
        /// </summary>
        internal string? Name { get; private set; }

        /// <summary>True when at least one target resolver (<see cref="NodePath"/> or
        /// <see cref="NodePaths"/>) is set.</summary>
        internal bool HasTarget =>
            !string.IsNullOrEmpty(NodePath) || (NodePaths.Count > 0);

        /// <summary>
        /// Enumerate every target path in priority order (single <see cref="NodePath"/> first, then each
        /// <see cref="NodePaths"/> entry). De-duplicates so a caller that sets both fields with an
        /// overlapping path does not mutate the same node twice.
        /// </summary>
        internal IEnumerable<string> TargetPaths()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(NodePath) && seen.Add(NodePath))
                yield return NodePath;
            foreach (var p in NodePaths)
            {
                if (!string.IsNullOrEmpty(p) && seen.Add(p))
                    yield return p;
            }
        }

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="NodeModifyBody"/>. Never throws — a missing
        /// or malformed field falls back to its default (empty / null / empty map). Empty/null body
        /// returns an all-default instance (no targets — the handler fails with
        /// <c>missing_parameter</c>).
        /// </summary>
        internal static NodeModifyBody Parse(string? body)
        {
            var parsed = new NodeModifyBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.NodePath = ExtractStringValue(body, "node_path");
            parsed.NodePaths = ExtractStringArray(body, "node_paths");
            parsed.Properties = ExtractStringMap(body, "properties");
            parsed.Position = ExtractNullableStringValue(body, "position");
            parsed.Rotation = ExtractNullableStringValue(body, "rotation");
            parsed.Scale = ExtractNullableStringValue(body, "scale");
            parsed.Name = ExtractNullableStringValue(body, "name");
            return parsed;
        }

        NodeModifyBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Mirrors NodeCreateBody.Extract*: locate `"key"`, walk past the colon, read the scalar. Strings
        // are unwrapped from their quotes and unescaped for the small set of JSON string escapes
        // (\" \\ \/ \n \r \t \uXXXX); a field that is present-but-null (e.g. `"name":null`) is treated
        // as absent. The array/map extractors (node_paths / properties) walk a balanced bracket/brace
        // region with the same escape-aware string slicing. The extraction logic is intentionally
        // independent per tool body (see NodeCreateBody's note on why a shared helper is deferred).

        static string ExtractStringValue(string body, string key)
        {
            var raw = ExtractRawValue(body, key);
            return Unquote(raw) ?? string.Empty;
        }

        static string? ExtractNullableStringValue(string body, string key)
        {
            var raw = ExtractRawValue(body, key);
            return Unquote(raw);
        }

        /// <summary>
        /// Extract a JSON string array (<c>"node_paths":["a","b"]</c>) into a <see cref="List{T}"/> of
        /// unquoted strings. Returns an empty list when the key is absent or the value is null. A
        /// malformed array (missing closing bracket) yields whatever entries were parsed before the
        /// truncation — the handler still resolves each and reports per-target misses.
        /// </summary>
        static List<string> ExtractStringArray(string body, string key)
        {
            var result = new List<string>();
            var region = ExtractRegion(body, key, '[', ']');
            if (region == null) return result;
            CollectStringEntries(region, result);
            return result;
        }

        /// <summary>
        /// Extract a JSON object map (<c>"properties":{"visible":"false",...}</c>) into a string→string
        /// dictionary. Values are read as raw JSON tokens (strings are unescaped; bare tokens like
        /// <c>true</c> / <c>false</c> / numbers are kept verbatim so the handler can coerce them).
        /// Returns an empty dictionary when the key is absent or the value is null.
        /// </summary>
        static Dictionary<string, string> ExtractStringMap(string body, string key)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var region = ExtractRegion(body, key, '{', '}');
            if (region == null) return result;
            CollectMapEntries(region, result);
            return result;
        }

        /// <summary>
        /// Slice the region between a matching open/close bracket pair for <paramref name="key"/>.
        /// Returns null when the key is absent or its value is null. Honors string escaping so a brace
        /// inside a string value does not close the region early.
        /// </summary>
        static string? ExtractRegion(string body, string key, char open, char close)
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
            if (body[start] != open) return null;

            // Walk forward honoring strings until the matching close bracket at depth 0.
            int depth = 0;
            int i = start;
            bool inString = false;
            while (i < body.Length)
            {
                var c = body[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < body.Length) { i += 2; continue; }
                    if (c == '"') inString = false;
                    i++;
                    continue;
                }
                if (c == '"') { inString = true; i++; continue; }
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0)
                        return body.Substring(start, i - start + 1);
                }
                i++;
            }
            // Truncated region — return what we have up to the end so the collector can still parse
            // complete entries. This is defensive; well-formed JSON closes the bracket.
            return body.Substring(start);
        }

        /// <summary>
        /// Walk an array region (<c>[...]</c>) and append every string element to
        /// <paramref name="result"/>. Non-string elements are skipped (node_paths is a string array in
        /// the schema; a stray number is a schema violation we tolerate rather than fault on).
        /// </summary>
        static void CollectStringEntries(string arrayRegion, List<string> result)
        {
            int i = 1; // skip '['
            int n = arrayRegion.Length;
            while (i < n)
            {
                // Skip whitespace and commas.
                while (i < n && (arrayRegion[i] == ',' || char.IsWhiteSpace(arrayRegion[i]))) i++;
                if (i >= n || arrayRegion[i] == ']') break;
                if (arrayRegion[i] == '"')
                {
                    var s = SliceQuotedString(arrayRegion, i, out int next);
                    result.Add(s);
                    i = next;
                }
                else
                {
                    // Skip a non-string element up to the next comma/closing bracket.
                    while (i < n && arrayRegion[i] != ',' && arrayRegion[i] != ']') i++;
                }
            }
        }

        /// <summary>
        /// Walk an object region (<c>{...}</c>) and collect <c>"key":value</c> pairs into
        /// <paramref name="result"/>. Values are stored verbatim: quoted strings are unescaped, bare
        /// tokens (true/false/numbers) are kept as their raw text. Nested objects/arrays are skipped
        /// (P2.4 does not support nested property values; the handler emits an
        /// <c>unsupported_property</c> warning for keys whose value it cannot coerce).
        /// </summary>
        static void CollectMapEntries(string objectRegion, Dictionary<string, string> result)
        {
            int i = 1; // skip '{'
            int n = objectRegion.Length;
            while (i < n)
            {
                // Skip whitespace and commas.
                while (i < n && (objectRegion[i] == ',' || char.IsWhiteSpace(objectRegion[i]))) i++;
                if (i >= n || objectRegion[i] == '}') break;
                if (objectRegion[i] != '"') break; // malformed key — stop.

                var key = SliceQuotedString(objectRegion, i, out int afterKey);
                i = afterKey;
                // Skip whitespace then expect a colon.
                while (i < n && char.IsWhiteSpace(objectRegion[i])) i++;
                if (i >= n || objectRegion[i] != ':') break;
                i++;
                while (i < n && char.IsWhiteSpace(objectRegion[i])) i++;
                if (i >= n) break;

                // Skip nested objects/arrays entirely — record a placeholder so the handler can warn.
                if (objectRegion[i] == '{' || objectRegion[i] == '[')
                {
                    char open = objectRegion[i];
                    char close = open == '{' ? '}' : ']';
                    int depth = 0;
                    int j = i;
                    bool inStr = false;
                    while (j < n)
                    {
                        var c = objectRegion[j];
                        if (inStr)
                        {
                            if (c == '\\' && j + 1 < n) { j += 2; continue; }
                            if (c == '"') inStr = false;
                            j++;
                            continue;
                        }
                        if (c == '"') { inStr = true; j++; continue; }
                        if (c == open) depth++;
                        else if (c == close) { depth--; if (depth == 0) { j++; break; } }
                        j++;
                    }
                    result[key] = objectRegion.Substring(i, j - i);
                    i = j;
                    continue;
                }

                if (objectRegion[i] == '"')
                {
                    var val = SliceQuotedString(objectRegion, i, out int afterVal);
                    result[key] = val;
                    i = afterVal;
                }
                else
                {
                    int end = i;
                    while (end < n && objectRegion[end] != ',' && objectRegion[end] != '}') end++;
                    result[key] = objectRegion.Substring(i, end - i).Trim();
                    i = end;
                }
            }
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
                return SliceQuotedString(body, start, out _);

            // Number / boolean / etc.: slice to the next comma or closing brace.
            return SliceBareToken(body, start);
        }

        static string SliceQuotedString(string body, int start, out int nextIndex)
        {
            // start points at the opening quote.
            var sb = new System.Text.StringBuilder(body.Length - start);
            int i = start + 1;
            while (i < body.Length)
            {
                var c = body[i];
                if (c == '\\' && i + 1 < body.Length)
                {
                    var nxt = body[i + 1];
                    switch (nxt)
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
                            sb.Append(nxt); i += 2; continue;
                    }
                }
                if (c == '"')
                {
                    nextIndex = i + 1;
                    return sb.ToString();
                }
                sb.Append(c);
                i++;
            }
            nextIndex = body.Length;
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
        /// Unquote a raw value into a non-empty string, or null when the raw is null/empty/already the
        /// literal null marker. <see cref="ExtractRawValue"/> already unwraps quotes, so this mostly
        /// filters absent values.
        /// </summary>
        static string? Unquote(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            return raw;
        }
    }
}
