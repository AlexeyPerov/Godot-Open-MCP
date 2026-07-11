#nullable enable
using System;
using System.Collections.Generic;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_editor_selection_set</c> (P4.6). Extracts the
    /// <c>select</c> array — a list of node references, each identified by <c>node_path</c>
    /// (scene-tree path) and/or <c>instance_id</c> (Godot instance id). An empty (or absent) list
    /// means "clear the selection".
    ///
    /// <para>
    /// <b>Resolution precedence.</b> When both fields are present on the same ref, <c>instance_id</c>
    /// wins (priority 1) and <c>node_path</c> is the fallback (priority 2). This matches the NodeRef
    /// precedence in the Godot-MCP behavior reference and the Unity Open MCP selection contract — a
    /// live Node is most reliably identified by its instance id, while a scene-tree path can be
    /// ambiguous or shift as the tree mutates.
    /// </para>
    ///
    /// <para>
    /// The parser owns ONLY the raw field extraction from the <c>select</c> array. The handler reads
    /// <c>paths_hint</c>/<c>gate</c> via <see cref="BridgeRequestBody"/> (the dispatcher-level
    /// scalars) and performs the actual node resolution against the edited scene on the main thread.
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is
    /// unit-testable in the binary-less xUnit host. Uses the same hand-rolled <c>IndexOf</c>-substring
    /// style as <see cref="NodeFindBody"/> / <see cref="EditorApplicationSetStateBody"/> — the bridge
    /// deliberately carries no typed JSON DOM dependency on the hot path
    /// (<c>packages/bridge/AGENTS.md</c> §Transport).
    /// </para>
    /// </summary>
    internal sealed class EditorSelectionSetBody
    {
        /// <summary>
        /// One node reference parsed from the <c>select</c> array. A ref with both fields absent (or
        /// null) is retained as-is so the handler can surface a structured <c>node_not_found</c> error
        /// naming the offending index — the parser does not silently drop entries.
        /// </summary>
        internal sealed class NodeRef
        {
            /// <summary>Godot instance id of the Node. 0 when unset. Priority 1 when non-zero.
            /// Accepts both <c>instance_id</c> (snake_case) and <c>instanceId</c> (camelCase).</summary>
            internal ulong InstanceId { get; set; }

            /// <summary>True when <see cref="InstanceId"/> was explicitly set (non-zero).</summary>
            internal bool HasInstanceId => InstanceId != 0;

            /// <summary>Scene-tree path (<c>Main/Player</c>, <c>/root/Main/Player</c>, or <c>.</c> for
            /// the edited root). Empty when unset. Priority 2. Accepts <c>node_path</c> /
            /// <c>nodePath</c>.</summary>
            internal string NodePath { get; set; } = string.Empty;

            /// <summary>True when <see cref="NodePath"/> was explicitly set (non-empty).</summary>
            internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

            /// <summary>True when neither field is set — the handler rejects this with
            /// <c>node_not_found</c>.</summary>
            internal bool IsEmpty => !HasInstanceId && !HasNodePath;

            /// <summary>Human-readable label for error messages.</summary>
            public override string ToString()
            {
                if (HasInstanceId) return $"instance_id={InstanceId}";
                if (HasNodePath) return $"node_path='{NodePath}'";
                return "(empty ref)";
            }
        }

        /// <summary>The parsed node references, in request order. Empty when the <c>select</c> array
        /// is absent or empty (which the handler treats as "clear").</summary>
        internal List<NodeRef> Select { get; private set; } = new();

        /// <summary>True when the <c>select</c> key was present (even if empty). Distinguishes an
        /// explicit empty list (clear) from a missing field (error). The plan treats absent as clear
        /// too, but this flag lets the handler report a clearer message.</summary>
        internal bool HasSelectField { get; private set; }

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="EditorSelectionSetBody"/>. Never throws —
        /// a missing or malformed field falls back to its default. Empty/null body returns an
        /// all-default instance (empty select list → clear).
        /// </summary>
        internal static EditorSelectionSetBody Parse(string? body)
        {
            var parsed = new EditorSelectionSetBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.Select = ExtractSelectArray(body);
            parsed.HasSelectField = IndexOfKey(body, "select") >= 0;
            return parsed;
        }

        EditorSelectionSetBody() { }

        // --- hand-rolled JSON array-of-objects extraction --------------------------------
        //
        // The `select` array is a list of objects: `[{"node_path":"Main","instance_id":123}, ...]`.
        // We locate the `select` key, verify the value is a JSON array, then walk top-level object
        // boundaries (respecting nested braces so an embedded object doesn't confuse the splitter).
        // Each object slice is then parsed for `instance_id`/`instanceId` (ulong) and
        // `node_path`/`nodePath` (string) using the same IndexOf scalar helpers as the other body
        // parsers.

        static List<NodeRef> ExtractSelectArray(string body)
        {
            var result = new List<NodeRef>();
            var idx = IndexOfKey(body, "select");
            if (idx < 0) return result;

            var colonIdx = body.IndexOf(':', idx + "\"select\"".Length);
            if (colonIdx < 0) return result;

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length || body[start] != '[') return result;

            // Walk the array elements. Each element is a {...} object.
            int i = start + 1;
            while (i < body.Length)
            {
                // Skip whitespace and commas between elements.
                while (i < body.Length && (char.IsWhiteSpace(body[i]) || body[i] == ',')) i++;
                if (i >= body.Length) break;
                if (body[i] == ']') break;

                if (body[i] != '{')
                {
                    // A non-object element is a contract violation; skip to the next comma/top-level
                    // element boundary rather than aborting the whole list — a single malformed entry
                    // should not mask the rest. The handler validates each resolved ref anyway.
                    i = SkipToEndOfElement(body, i);
                    continue;
                }

                // Slice the object from '{' to its matching '}'.
                int objStart = i;
                int depth = 0;
                while (i < body.Length)
                {
                    if (body[i] == '{') depth++;
                    else if (body[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { i++; break; }
                    }
                    i++;
                }
                var objSlice = body.Substring(objStart, i - objStart);
                result.Add(ParseRefObject(objSlice));
            }

            return result;
        }

        static NodeRef ParseRefObject(string obj)
        {
            var r = new NodeRef();

            // instance_id (snake_case) / instanceId (camelCase) — ulong.
            var idSnake = ExtractUlong(obj, "instance_id");
            if (idSnake.HasValue)
                r.InstanceId = idSnake.Value;
            else
            {
                var idCamel = ExtractUlong(obj, "instanceId");
                if (idCamel.HasValue) r.InstanceId = idCamel.Value;
            }

            // node_path (snake_case) / nodePath (camelCase) — string.
            var pathSnake = ExtractStringValue(obj, "node_path");
            if (!string.IsNullOrEmpty(pathSnake))
                r.NodePath = pathSnake;
            else
            {
                var pathCamel = ExtractStringValue(obj, "nodePath");
                if (!string.IsNullOrEmpty(pathCamel)) r.NodePath = pathCamel;
            }

            return r;
        }

        static int IndexOfKey(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            return body.IndexOf(quotedKey, StringComparison.Ordinal);
        }

        static int SkipToEndOfElement(string body, int i)
        {
            // Skip to the next comma at depth 0.
            int depth = 0;
            while (i < body.Length)
            {
                var c = body[i];
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') { if (depth == 0) break; depth--; }
                else if (c == ',' && depth == 0) break;
                i++;
            }
            return i;
        }

        static string ExtractStringValue(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return string.Empty;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return string.Empty;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return string.Empty;
            if (start + 4 <= body.Length && body.Substring(start, 4) == "null") return string.Empty;
            if (body[start] != '"') return string.Empty;
            return SliceQuotedString(body, start);
        }

        static ulong? ExtractUlong(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return null;
            int end = start;
            if (end < body.Length && (body[end] == '-' || body[end] == '+')) end++;
            while (end < body.Length && char.IsDigit(body[end])) end++;
            var token = body.AsSpan(start, end - start).Trim();
            if (token.Length == 0) return null;
            return ulong.TryParse(token, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        static string SliceQuotedString(string body, int start)
        {
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
    }
}
