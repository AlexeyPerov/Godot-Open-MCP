#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Raw-JSON scalar extraction for tool handlers. The bridge deliberately carries no
    /// System.Text.Json / Newtonsoft dependency (<c>packages/bridge/AGENTS.md</c> §Transport): every
    /// handler reads the fields it needs straight off the body string with hand-rolled
    /// <c>IndexOf</c> scans. <see cref="BridgeRequestBody"/> covers the dispatcher's hot-path
    /// scalars (<c>timeout_ms</c> / <c>gate</c> / <c>paths_hint</c>); <c>JsonBody</c> covers the
    /// handler-facing field reads that are not on the dispatch hot path.
    ///
    /// <para>
    /// Ported (copy) from Unity Open MCP's <c>JsonBody</c>, narrowed to the <c>GetString</c> /
    /// <c>GetStringArray</c> / <c>GetBool</c> surface the gate meta-tools (<c>validate_edit</c>,
    /// <c>checkpoint_create</c>, <c>delta</c>, P3.7 <c>apply_fix</c>) need.
    /// <see cref="BridgeRequestBody.ExtractPathsHint"/> already implements a near-identical array scan;
    /// it is not folded in here because the dispatcher path owns its parser and the contract differs
    /// (absent-vs-empty semantics). Future handlers that need <c>GetInt</c> / <c>GetObjectArray</c>
    /// will widen this type from the Unity original as needed.
    /// </para>
    ///
    /// <para>
    /// Editor-only (<c>#if TOOLS</c>): it is only referenced by tool handlers, which are themselves
    /// editor-only. The string parsing is pure-managed and is unit-tested directly.
    /// </para>
    /// </summary>
    internal static class JsonBody
    {
        /// <summary>
        /// Read a quoted-string JSON field. Returns null when the key is absent, when the value is
        /// the literal <c>null</c>, or when the value is not a string. Never throws — a malformed
        /// value yields null so the caller's emptiness/required guard fires with a structured error
        /// rather than a parse fault. Mirrors Unity's <c>JsonBody.GetString</c>.
        /// </summary>
        internal static string? GetString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;
            var start = colonIdx + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length) return null;
            // Literal null → treat as absent so a caller's "absent or present" contract is uniform.
            if (start + 3 < json.Length
                && json[start] == 'n' && json[start + 1] == 'u'
                && json[start + 2] == 'l' && json[start + 3] == 'l')
                return null;
            if (json[start] != '"') return null;
            start++;
            return ReadQuotedString(json, ref start);
        }

        /// <summary>
        /// Read a JSON boolean field. Returns <paramref name="defaultValue"/> when the key is absent, when
        /// the value is the literal <c>null</c>, or when the value is not a boolean. Never throws — a
        /// malformed value yields the default so the caller's contract holds without a parse fault. Used by
        /// <c>apply_fix</c> (<c>dry_run</c>, default true). Mirrors Unity's <c>JsonBody.GetBool</c>.
        /// </summary>
        internal static bool GetBool(string json, string key, bool defaultValue)
        {
            if (string.IsNullOrEmpty(json)) return defaultValue;
            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return defaultValue;
            var colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return defaultValue;
            var start = colonIdx + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length) return defaultValue;
            // Literal null → treat as absent (default).
            if (start + 3 < json.Length
                && json[start] == 'n' && json[start + 1] == 'u'
                && json[start + 2] == 'l' && json[start + 3] == 'l')
                return defaultValue;
            if (json[start] == 't')
            {
                // Accept "true" (a stricter check would verify the remaining chars; the value is either a
                // well-formed JSON bool from the MCP client or absent, so a truncated "tru" is not a real
                // input).
                return true;
            }
            if (json[start] == 'f') return false;
            return defaultValue;
        }

        /// <summary>
        /// Read a JSON string-array field. Returns null when the key is absent or the value is the
        /// literal <c>null</c>; returns the array (possibly empty) when present. Non-string elements
        /// are skipped (a contract violation, but not worth a hard fault here). Never throws — a
        /// malformed array yields null so the caller surfaces a structured <c>missing_parameter</c>
        /// rather than a parse fault. Mirrors Unity's <c>JsonBody.GetStringArray</c>.
        /// </summary>
        internal static string[]? GetStringArray(string json, string key)
        {
            var raw = GetRawValue(json, key);
            if (raw == null) return null;
            raw = raw.Trim();
            if (raw == "null") return null;
            if (!raw.StartsWith("[", StringComparison.Ordinal)) return null;

            var items = new List<string>();
            var i = 1;
            while (i < raw.Length)
            {
                while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
                if (i >= raw.Length || raw[i] == ']') break;
                if (raw[i] == '"')
                {
                    i++;
                    items.Add(ReadQuotedString(raw, ref i));
                }
                else
                {
                    // Skip a non-string element to the next comma (a contract violation, but keep
                    // parsing the rest of the array — the caller's required guard handles emptiness).
                    while (i < raw.Length && raw[i] != ',' && raw[i] != ']') i++;
                }
                while (i < raw.Length && (raw[i] == ',' || char.IsWhiteSpace(raw[i]))) i++;
            }
            return items.ToArray();
        }

        /// <summary>
        /// Read the raw (unparsed) JSON token for <paramref name="key"/> — a quoted string, a
        /// balanced array/object body, or a bare scalar. Returns null when the key is absent.
        /// Mirrors Unity's <c>JsonBody.GetRawValue</c>; only the array form is used by
        /// <see cref="GetStringArray"/> today, but the string/object forms are kept so widening this
        /// type later does not require re-porting the scanner.
        /// </summary>
        internal static string? GetRawValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;
            var start = colonIdx + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length) return null;

            if (json[start] == '"')
            {
                var i = start + 1;
                while (i < json.Length)
                {
                    if (json[i] == '\\') { i += 2; continue; }
                    if (json[i] == '"') { i++; break; }
                    i++;
                }
                return json.Substring(start, i - start);
            }

            if (json[start] == '[' || json[start] == '{')
            {
                var open = json[start];
                var close = open == '[' ? ']' : '}';
                var depth = 1;
                var i = start + 1;
                while (i < json.Length && depth > 0)
                {
                    if (json[i] == '"')
                    {
                        i++;
                        while (i < json.Length)
                        {
                            if (json[i] == '\\') { i += 2; continue; }
                            if (json[i] == '"') { i++; break; }
                            i++;
                        }
                        continue;
                    }
                    if (json[i] == open) depth++;
                    else if (json[i] == close) depth--;
                    i++;
                }
                return json.Substring(start, i - start);
            }

            var end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']')
                end++;
            return json.Substring(start, end - start);
        }

        /// <summary>
        /// Read a JSON string literal starting at <paramref name="i"/> (positioned just after the
        /// opening quote), advancing <paramref name="i"/> past the closing quote. Honors the standard
        /// escape set (<c>\" \\ \/ \b \f \n \r \t \uXXXX</c>). Mirrors Unity's
        /// <c>JsonBody.ReadQuotedString</c>.
        /// </summary>
        static string ReadQuotedString(string json, ref int i)
        {
            var sb = new StringBuilder(64);
            while (i < json.Length)
            {
                var c = json[i++];
                if (c == '\\')
                {
                    if (i >= json.Length) break;
                    var e = json[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 3 < json.Length)
                            {
                                sb.Append((char)Convert.ToUInt16(
                                    json.Substring(i, 4), 16));
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else if (c == '"')
                {
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
#endif
