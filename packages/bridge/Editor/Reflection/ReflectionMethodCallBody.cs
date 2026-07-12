#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_reflection_method_call</c> (P5.1). Extracts the
    /// fields the invoke handler needs straight off the raw JSON body using the same hand-rolled
    /// <c>IndexOf</c>-substring style as <see cref="NodeFindBody"/> — the bridge carries no typed
    /// JSON DOM dependency on the hot path (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// Adapted (adapt fidelity) from Unity Open MCP's <c>invoke_method</c> request shape:
    /// <c>type_name</c> / <c>method_name</c> / <c>args</c> / <c>arg_type_names</c> /
    /// <c>generic_arg_types</c> / <c>is_static</c> / <c>assembly_name</c> /
    /// <c>max_depth</c> / <c>max_items</c> map directly. Godot-specific targeting deltas
    /// (documented in <c>ReflectionTools.Call</c>):
    /// <list type="bullet">
    /// <item><description><c>node_path</c> is the primary instance target — resolves a node from the
    /// edited scene (Godot-native). Replaces Unity's <c>object_id</c>-first targeting.</description></item>
    /// <item><description><c>object_id</c> is accepted but only honored against a stable handle
    /// registry (not present in v1); without one it surfaces <c>unsupported_target</c> rather than
    /// silently fabricating an instance.</description></item>
    /// <item><description><c>execute_in_main_thread</c> (default <c>true</c>) — the Godot main-thread
    /// marshalling opt-out for thread-safe pure logic (mirrors Godot-MCP's
    /// <c>executeInMainThread</c>).</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is unit-testable
    /// in the binary-less xUnit host. The <see cref="Args"/> list holds raw JSON values
    /// (<c>string</c>, <c>long</c>, <c>double</c>, <c>bool</c>, <c>null</c>, nested
    /// <c>Dictionary</c>/<c>List</c>) — the editor-only handler coerces them to the resolved
    /// parameter types.
    /// </para>
    /// </summary>
    internal sealed class ReflectionMethodCallBody
    {
        /// <summary>Required: the declaring type full or simple name to resolve the method on.</summary>
        internal string TypeName { get; private set; } = string.Empty;

        /// <summary>Required: the method name to invoke.</summary>
        internal string MethodName { get; private set; } = string.Empty;

        /// <summary>Optional positional arguments (raw JSON values). Null when unset; empty when an
        /// explicit <c>[]</c> is sent.</summary>
        internal List<object?>? Args { get; private set; }

        /// <summary>Optional explicit parameter type names used to disambiguate overloads. Length must
        /// match the chosen overload's parameter count. Null when unset.</summary>
        internal string[]? ArgTypeNames { get; private set; }

        /// <summary>Optional generic type-argument names for binding a generic method. Length must
        /// match the method's generic parameter count. Null when unset.</summary>
        internal string[]? GenericArgTypes { get; private set; }

        /// <summary>True to resolve a static method (no instance). Defaults to <c>false</c>.</summary>
        internal bool IsStatic { get; private set; } = false;

        /// <summary>Optional assembly simple-name to disambiguate an ambiguous type.</summary>
        internal string? AssemblyName { get; private set; }

        /// <summary>
        /// Primary instance target (Godot-native): scene-tree path relative to the edited scene root
        /// (<c>Main/Player</c>, <c>/root/Main/Player</c>, or <c>.</c>). Empty when unset.
        /// </summary>
        internal string NodePath { get; private set; } = string.Empty;

        /// <summary>
        /// Secondary instance target: a stable handle registry id from a prior tool. Only honored when
        /// a handle registry exists; otherwise the handler rejects with <c>unsupported_target</c>.
        /// Zero when unset.
        /// </summary>
        internal long ObjectId { get; private set; } = 0;

        /// <summary>
        /// Run the call on the editor main thread. Defaults to <c>true</c> — keep true for any
        /// Godot-API-touching method; set <c>false</c> only for thread-safe pure logic. The handler
        /// additionally forces main-thread execution when the target is a GodotObject regardless of
        /// this flag.
        /// </summary>
        internal bool ExecuteInMainThread { get; private set; } = true;

        /// <summary>Max recursion depth when serializing the returned object graph. Defaults to 4.</summary>
        internal int MaxDepth { get; private set; } = 4;

        /// <summary>Max items emitted per list/enumerable in the returned object graph. Defaults to
        /// 100.</summary>
        internal int MaxItems { get; private set; } = 100;

        /// <summary>True when <see cref="Args"/> was explicitly provided (even as an empty array),
        /// distinguishing "no args field" from "zero args".</summary>
        internal bool HasArgs => Args != null;

        /// <summary>True when any instance target field (<see cref="NodePath"/> non-empty, or
        /// <see cref="ObjectId"/> non-zero) is set.</summary>
        internal bool HasInstanceTarget => !string.IsNullOrEmpty(NodePath) || ObjectId != 0;

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="ReflectionMethodCallBody"/>. Never throws —
        /// a missing or malformed field falls back to its default. Empty/null body returns an
        /// all-default instance (the handler's required-field guard then surfaces
        /// <c>validation_error</c>).
        /// </summary>
        internal static ReflectionMethodCallBody Parse(string? body)
        {
            var parsed = new ReflectionMethodCallBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.TypeName = ExtractStringValue(body, "type_name");
            parsed.MethodName = ExtractStringValue(body, "method_name");
            parsed.Args = ExtractArgsArray(body, "args");
            parsed.ArgTypeNames = ExtractStringArray(body, "arg_type_names");
            parsed.GenericArgTypes = ExtractStringArray(body, "generic_arg_types");
            parsed.IsStatic = ExtractBoolValue(body, "is_static", false);
            parsed.AssemblyName = ExtractNullableStringValue(body, "assembly_name");
            parsed.NodePath = ExtractStringValue(body, "node_path");
            parsed.ObjectId = ExtractLongValue(body, "object_id", 0);
            parsed.ExecuteInMainThread = ExtractBoolValue(body, "execute_in_main_thread", true);
            parsed.MaxDepth = ClampPositive(ExtractIntValue(body, "max_depth", 4), 4);
            parsed.MaxItems = ClampNonNegative(ExtractIntValue(body, "max_items", 100), 100);
            return parsed;
        }

        ReflectionMethodCallBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------

        static int ClampPositive(int value, int fallback)
        {
            if (value <= 0) return fallback;
            return value;
        }

        static int ClampNonNegative(int value, int fallback)
        {
            if (value < 0) return fallback;
            return value;
        }

        static string ExtractStringValue(string body, string key)
        {
            return ExtractRawValue(body, key) ?? string.Empty;
        }

        static string? ExtractNullableStringValue(string body, string key)
        {
            return ExtractRawValue(body, key);
        }

        static bool ExtractBoolValue(string body, string key, bool defaultValue)
        {
            var raw = ExtractRawValue(body, key);
            if (raw == null) return defaultValue;
            var trimmed = raw.AsSpan().Trim();
            if (trimmed.Length == 0) return defaultValue;
            if (trimmed[0] == 't') return true;
            if (trimmed[0] == 'f') return false;
            return defaultValue;
        }

        static int ExtractIntValue(string body, string key, int defaultValue)
        {
            var raw = ExtractRawValue(body, key);
            if (raw == null) return defaultValue;
            return ParseInt(raw, defaultValue);
        }

        static long ExtractLongValue(string body, string key, long defaultValue)
        {
            var raw = ExtractRawValue(body, key);
            if (raw == null) return defaultValue;
            var trimmed = raw.AsSpan().Trim();
            int end = 0;
            if (trimmed.Length > 0 && (trimmed[0] == '-' || trimmed[0] == '+')) end = 1;
            while (end < trimmed.Length && char.IsDigit(trimmed[end])) end++;
            if (end == 0 || (end == 1 && trimmed.Length > 0 && (trimmed[0] == '-' || trimmed[0] == '+')))
                return defaultValue;
            return long.TryParse(trimmed.Slice(0, end), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
        }

        static int ParseInt(string raw, int defaultValue)
        {
            var trimmed = raw.AsSpan().Trim();
            int end = 0;
            if (trimmed.Length > 0 && (trimmed[0] == '-' || trimmed[0] == '+')) end = 1;
            while (end < trimmed.Length && char.IsDigit(trimmed[end])) end++;
            if (end == 0 || (end == 1 && trimmed.Length > 0 && (trimmed[0] == '-' || trimmed[0] == '+')))
                return defaultValue;
            return int.TryParse(trimmed.Slice(0, end), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
        }

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
            if (start + 4 <= body.Length && body.Substring(start, 4) == "null")
                return null;
            if (body[start] == '"')
                return SliceQuotedString(body, start);
            // Balanced array/object: slice to the matching close so the whole token survives
            // (mirrors JsonBody.GetRawValue). Required for args / arg_type_names / generic_arg_types.
            if (body[start] == '[' || body[start] == '{')
                return SliceBalanced(body, start);
            return SliceBareToken(body, start);
        }

        static string SliceBalanced(string body, int start)
        {
            var open = body[start];
            var close = open == '[' ? ']' : '}';
            int depth = 1;
            int i = start + 1;
            while (i < body.Length && depth > 0)
            {
                if (body[i] == '"')
                {
                    i++;
                    while (i < body.Length)
                    {
                        if (body[i] == '\\') { i += 2; continue; }
                        if (body[i] == '"') { i++; break; }
                        i++;
                    }
                    continue;
                }
                if (body[i] == open) depth++;
                else if (body[i] == close) depth--;
                i++;
            }
            return body.Substring(start, i - start);
        }

        static string[]? ExtractStringArray(string body, string key)
        {
            var raw = ExtractRawValue(body, key);
            if (raw == null) return null;
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed == "null") return null;
            if (!trimmed.StartsWith("[", StringComparison.Ordinal)) return null;
            var items = new List<string>();
            int i = 1;
            while (i < trimmed.Length)
            {
                while (i < trimmed.Length && char.IsWhiteSpace(trimmed[i])) i++;
                if (i >= trimmed.Length || trimmed[i] == ']') break;
                if (trimmed[i] == '"')
                {
                    i++;
                    items.Add(ReadQuotedStringInline(trimmed, ref i));
                }
                else
                {
                    // Skip a non-string element to the next comma.
                    while (i < trimmed.Length && trimmed[i] != ',' && trimmed[i] != ']') i++;
                }
                while (i < trimmed.Length && (trimmed[i] == ',' || char.IsWhiteSpace(trimmed[i]))) i++;
            }
            // The signature uses nullable-string-array internally so callers can distinguish
            // absent from empty; we strip the per-element nullability here since string elements are
            // never null in a well-formed request.
            var arr = new string[items.Count];
            items.CopyTo(arr);
            return arr;
        }

        /// <summary>
        /// Parse a JSON array of mixed values (string / number / bool / null / object / nested array)
        /// into a list of raw CLR values. Mirrors Unity's <c>JsonBody.ParseArgsArray</c>. Returns null
        /// when the key is absent or the value is the literal <c>null</c>; returns the (possibly empty)
        /// list when present.
        /// </summary>
        static List<object?>? ExtractArgsArray(string body, string key)
        {
            var raw = ExtractRawValue(body, key);
            if (raw == null) return null;
            var trimmed = raw.Trim();
            if (trimmed == "null") return null;
            if (!trimmed.StartsWith("[", StringComparison.Ordinal)) return null;
            return ParseJsonValues(trimmed);
        }

        static List<object?> ParseJsonValues(string jsonArray)
        {
            var result = new List<object?>();
            int i = 1;
            while (i < jsonArray.Length)
            {
                while (i < jsonArray.Length && char.IsWhiteSpace(jsonArray[i])) i++;
                if (i >= jsonArray.Length || jsonArray[i] == ']') break;
                var (val, next) = ReadJsonValue(jsonArray, i);
                result.Add(val);
                i = next;
                while (i < jsonArray.Length && (jsonArray[i] == ',' || char.IsWhiteSpace(jsonArray[i]))) i++;
            }
            return result;
        }

        static (object?, int) ReadJsonValue(string json, int start)
        {
            int i = start;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return (null, i);

            if (json[i] == '"')
            {
                i++;
                var s = ReadQuotedStringInline(json, ref i);
                return (s, i);
            }
            if (json[i] == 't') return (true, Math.Min(i + 4, json.Length));
            if (json[i] == 'f') return (false, Math.Min(i + 5, json.Length));
            if (json[i] == 'n') return (null, Math.Min(i + 4, json.Length));

            if (json[i] == '-' || char.IsDigit(json[i]))
            {
                int end = i;
                while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' ||
                    json[end] == '-' || json[end] == 'e' || json[end] == 'E' || json[end] == '+'))
                    end++;
                var numStr = json.Substring(i, end - i);
                if (numStr.Contains('.') || numStr.Contains('e') || numStr.Contains('E'))
                {
                    if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        return (d, end);
                }
                else
                {
                    if (long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                        return (l, end);
                }
                return (numStr, end);
            }

            if (json[i] == '{' || json[i] == '[')
            {
                var open = json[i];
                var close = open == '{' ? '}' : ']';
                int depth = 1;
                int end = i + 1;
                while (end < json.Length && depth > 0)
                {
                    if (json[end] == '"')
                    {
                        end++;
                        while (end < json.Length)
                        {
                            if (json[end] == '\\') { end += 2; continue; }
                            if (json[end] == '"') { end++; break; }
                            end++;
                        }
                        continue;
                    }
                    if (json[end] == open) depth++;
                    else if (json[end] == close) depth--;
                    end++;
                }
                // Nested objects/arrays are preserved as their raw JSON substring — the invoke handler
                // coerces primitives only in v1 and surfaces structured args as raw strings.
                return (json.Substring(i, end - i), end);
            }

            // Bare token (unexpected in a well-formed args array) — slice to the next delimiter.
            int bEnd = i;
            while (bEnd < json.Length && json[bEnd] != ',' && json[bEnd] != ']' && json[bEnd] != '}')
                bEnd++;
            return (json.Substring(i, bEnd - i).Trim(), bEnd);
        }

        static string SliceQuotedString(string body, int start)
        {
            int i = start + 1;
            return ReadQuotedStringInline(body, ref i);
        }

        static string ReadQuotedStringInline(string s, ref int i)
        {
            // `i` is positioned just after the opening quote on entry; on return it sits just after
            // the closing quote.
            var sb = new System.Text.StringBuilder(64);
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    var next = s[i + 1];
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
                        case 'u' when i + 5 < s.Length:
                            if (int.TryParse(s.Substring(i + 2, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out var code))
                                sb.Append((char)code);
                            i += 6;
                            continue;
                        default:
                            sb.Append(next); i += 2; continue;
                    }
                }
                if (c == '"') { i++; return sb.ToString(); }
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
    }
}
