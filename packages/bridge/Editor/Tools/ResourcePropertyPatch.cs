#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// One segment of a parsed property-path (P4.2). A path is slash-separated
    /// (<c>property/nested/[0]/[key]</c>); each segment is classified as a property name, an array
    /// index, or a dictionary key. The grammar is the documented Godot-oriented v1 mutation contract
    /// — explicit, auditable assignments rather than arbitrary JSON merge patch.
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the grammar and the patch-list
    /// extraction are unit-testable in the binary-less xUnit host.
    /// </para>
    /// </summary>
    internal enum SegmentKind
    {
        /// <summary>A bare property name segment (e.g. <c>albedo_color</c>).</summary>
        PropertyName,

        /// <summary>A bracketed integer index segment (e.g. <c>[0]</c>).</summary>
        ArrayIndex,

        /// <summary>A bracketed string key segment (e.g. <c>[player]</c>).</summary>
        DictionaryKey,
    }

    /// <summary>
    /// One classified segment of a property-path. The <see cref="Kind"/> determines which fields are
    /// meaningful: <see cref="PropertyName"/> for <see cref="SegmentKind.PropertyName"/>,
    /// <see cref="ArrayIndex"/> for <see cref="SegmentKind.ArrayIndex"/>, and
    /// <see cref="DictionaryKey"/> for <see cref="SegmentKind.DictionaryKey"/>.
    /// </summary>
    internal readonly struct PatchPathSegment
    {
        public readonly SegmentKind Kind;
        public readonly string? PropertyName;
        public readonly int ArrayIndex;
        public readonly string? DictionaryKey;

        PatchPathSegment(SegmentKind kind, string? propertyName, int arrayIndex, string? dictionaryKey)
        {
            Kind = kind;
            PropertyName = propertyName;
            ArrayIndex = arrayIndex;
            DictionaryKey = dictionaryKey;
        }

        public static PatchPathSegment ForProperty(string name)
            => new PatchPathSegment(SegmentKind.PropertyName, name, 0, null);

        public static PatchPathSegment ForIndex(int index)
            => new PatchPathSegment(SegmentKind.ArrayIndex, null, index, null);

        public static PatchPathSegment ForKey(string key)
            => new PatchPathSegment(SegmentKind.DictionaryKey, null, 0, key);

        public override string ToString()
        {
            switch (Kind)
            {
                case SegmentKind.ArrayIndex: return $"[{ArrayIndex}]";
                case SegmentKind.DictionaryKey: return $"[{DictionaryKey}]";
                default: return PropertyName ?? "";
            }
        }
    }

    /// <summary>
    /// One parsed patch from a <c>resource_create</c> <c>properties</c> array or a
    /// <c>resource_modify</c> <c>patches</c> array. Carries the raw path/value strings plus the
    /// classified <see cref="Segments"/> (or a <see cref="ParseError"/> when the path grammar is
    /// invalid). The value is kept as a raw JSON token so the <c>#if TOOLS</c> converter can
    /// coerce it to the target Godot Variant type later.
    /// </summary>
    internal sealed class ResourcePropertyPatch
    {
        /// <summary>The raw path string exactly as supplied (e.g. <c>albedo_color</c> or
        /// <c>metadata/[player]</c>). Null when the <c>path</c> key was absent or empty.</summary>
        internal string? RawPath { get; }

        /// <summary>The raw JSON value token exactly as supplied (e.g. <c>"Wood"</c>, <c>42</c>,
        /// <c>[1,0,0,1]</c>, <c>null</c>). Null when the <c>value</c> key was absent.</summary>
        internal string? RawValue { get; }

        /// <summary>Classified segments when <see cref="RawPath"/> parsed cleanly; empty list
        /// otherwise.</summary>
        internal List<PatchPathSegment> Segments { get; } = new List<PatchPathSegment>();

        /// <summary>Non-null when the path grammar is invalid (empty, unbalanced bracket, negative
        /// index, leading index/key). Null when the path parsed cleanly or was absent.</summary>
        internal string? ParseError { get; }

        /// <summary>True when <see cref="RawPath"/> is present and parsed without error.</summary>
        internal bool IsValid => !string.IsNullOrWhiteSpace(RawPath) && ParseError == null;

        /// <summary>True when the path string was present (regardless of whether it parsed).</summary>
        internal bool HasPath => !string.IsNullOrWhiteSpace(RawPath);

        ResourcePropertyPatch(string? rawPath, string? rawValue, List<PatchPathSegment> segments, string? parseError)
        {
            RawPath = rawPath;
            RawValue = rawValue;
            Segments = segments;
            ParseError = parseError;
        }

        /// <summary>
        /// Parse a path string into <see cref="PatchPathSegment"/>s. Returns the segments on success
        /// or a non-null error message on failure. Never throws. Grammar rules:
        /// <list type="bullet">
        /// <item><description>Segments are separated by <c>/</c>.</description></item>
        /// <item><description>The first segment MUST be a property name (not an index/key).</description></item>
        /// <item><description>A bracketed all-digits segment <c>[n]</c> is an array index (n ≥ 0).</description></item>
        /// <item><description>A bracketed non-digits segment <c>[key]</c> is a dictionary key.</description></item>
        /// <item><description>A bare segment is a property name.</description></item>
        /// <item><description>Brackets must be balanced and closed.</description></item>
        /// </list>
        /// Slashes inside brackets are part of the key/index text, not separators
        /// (e.g. <c>dict/[a/b]</c> is two segments: property <c>dict</c>, key <c>a/b</c>).
        /// </summary>
        internal static (List<PatchPathSegment> segments, string? error) ParsePath(string? rawPath)
        {
            var trimmed = (rawPath ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                return (new List<PatchPathSegment>(), "path is empty");

            var segments = new List<PatchPathSegment>();
            int i = 0;
            bool first = true;

            while (i < trimmed.Length)
            {
                // Read one segment up to the next top-level '/'.
                var sb = new StringBuilder();
                while (i < trimmed.Length && trimmed[i] != '/')
                {
                    if (trimmed[i] == '[')
                    {
                        // Read the entire bracketed token including any '/' inside it.
                        sb.Append('[');
                        i++;
                        bool closed = false;
                        while (i < trimmed.Length)
                        {
                            if (trimmed[i] == ']') { sb.Append(']'); i++; closed = true; break; }
                            sb.Append(trimmed[i]);
                            i++;
                        }
                        if (!closed)
                            return (new List<PatchPathSegment>(), $"path segment '{sb}' has an unclosed '['");
                    }
                    else
                    {
                        sb.Append(trimmed[i]);
                        i++;
                    }
                }

                var token = sb.ToString();
                if (token.Length == 0)
                    return (new List<PatchPathSegment>(), "path has an empty segment");

                var seg = ClassifySegment(token);
                if (seg == null)
                    return (new List<PatchPathSegment>(), $"path segment '{token}' is malformed");

                // The first segment MUST be a property name — you cannot index/key into nothing.
                if (first && seg.Value.Kind != SegmentKind.PropertyName)
                    return (new List<PatchPathSegment>(),
                        "path must start with a property name; an index or key cannot be the first segment");
                first = false;

                segments.Add(seg.Value);

                // Skip the separator.
                if (i < trimmed.Length && trimmed[i] == '/')
                {
                    i++;
                    // A trailing slash leaves an empty final segment — reject.
                    if (i >= trimmed.Length)
                        return (new List<PatchPathSegment>(), "path ends with a trailing '/' (empty segment)");
                }
            }

            return (segments, null);
        }

        /// <summary>
        /// Classify a single segment token into a <see cref="PatchPathSegment"/>. Returns null when
        /// the token is malformed (unbalanced bracket, empty name). A bracketed token whose inner
        /// text is all digits is an index; any other bracketed token is a dictionary key. A bare
        /// token is a property name.
        /// </summary>
        static PatchPathSegment? ClassifySegment(string token)
        {
            if (token.Length == 0) return null;

            // Bracketed: [digits] → index, [text] → key.
            if (token[0] == '[')
            {
                if (!token.EndsWith("]", StringComparison.Ordinal)) return null;
                var inner = token.Substring(1, token.Length - 2);
                if (inner.Length == 0) return null;
                // All-digits (and not a leading-zero multi-digit or sign) → array index.
                if (int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var idx) && idx >= 0)
                    return PatchPathSegment.ForIndex(idx);
                return PatchPathSegment.ForKey(inner);
            }

            // Bare token — property name. Must not contain a stray bracket.
            if (token.IndexOf(']') >= 0) return null;
            return PatchPathSegment.ForProperty(token);
        }

        /// <summary>
        /// Build a <see cref="ResourcePropertyPatch"/> from a raw path/value pair, parsing the path
        /// and recording any grammar error. Never throws.
        /// </summary>
        internal static ResourcePropertyPatch FromRaw(string? rawPath, string? rawValue)
        {
            var (segments, error) = ParsePath(rawPath);
            return new ResourcePropertyPatch(rawPath, rawValue, segments, error);
        }

        // --- shared patch-list extraction -------------------------------------------
        //
        // Extracts a `[{path, value}, ...]` array from a raw JSON body. Used by both
        // ResourceCreateBody ("properties") and ResourceModifyBody ("patches"). The extraction is a
        // balanced-bracket walk — mirrors NodeModifyBody.ExtractRegion / CollectMapEntries but
        // specialized for the {path, value} entry shape so path grammar errors can be surfaced per
        // entry without aborting the whole parse.

        /// <summary>
        /// Parse the <c>patches</c>/<c>properties</c> JSON array from <paramref name="body"/> into a
        /// list of <see cref="ResourcePropertyPatch"/>. Never throws — a missing/malformed array
        /// returns an empty list. Each entry's <c>path</c> is parsed into segments; a grammar error
        /// is recorded on the patch (<see cref="ParseError"/>) so the handler can reject the batch
        /// before any mutation.
        /// </summary>
        /// <param name="body">The raw JSON request body.</param>
        /// <param name="arrayKey">The JSON key under which the array lives
        /// (<c>patches</c> or <c>properties</c>).</param>
        internal static List<ResourcePropertyPatch> ParseList(string? body, string arrayKey)
        {
            var result = new List<ResourcePropertyPatch>();
            if (string.IsNullOrEmpty(body)) return result;

            var quotedKey = "\"" + arrayKey + "\"";
            var keyIdx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (keyIdx < 0) return result;

            var colonIdx = body.IndexOf(':', keyIdx + quotedKey.Length);
            if (colonIdx < 0) return result;

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length || body[start] != '[') return result;

            // Walk the array entries.
            int i = start + 1;
            while (i < body.Length)
            {
                // Skip whitespace and commas between entries.
                while (i < body.Length && (body[i] == ',' || char.IsWhiteSpace(body[i]))) i++;
                if (i >= body.Length) break;
                if (body[i] == ']') break;

                // Each entry is an object {...}. Slice it with a balanced-brace walk.
                if (body[i] != '{')
                {
                    // Malformed — skip to the next comma at the top level.
                    i = SkipToEndOfEntry(body, i);
                    continue;
                }

                var entryEnd = FindBalancedEnd(body, i, '{', '}');
                if (entryEnd < 0) break;
                var entry = body.Substring(i, entryEnd - i + 1);

                var path = ExtractStringValue(entry, "path");
                var value = ExtractRawValue(entry, "value");
                result.Add(FromRaw(path, value));

                i = entryEnd + 1;
            }

            return result;
        }

        /// <summary>Find the index of the matching closing brace for the opener at
        /// <paramref name="openIdx"/>, honoring string escapes and nesting. Returns -1 when
        /// unbalanced.</summary>
        static int FindBalancedEnd(string body, int openIdx, char open, char close)
        {
            int depth = 0;
            bool inString = false;
            bool escape = false;
            for (int i = openIdx; i < body.Length; i++)
            {
                var c = body[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>Skip from <paramref name="idx"/> to the next top-level comma or closing
        /// bracket. Used to recover from a malformed entry without aborting the whole array.</summary>
        static int SkipToEndOfEntry(string body, int idx)
        {
            int depth = 0;
            bool inString = false;
            bool escape = false;
            for (int i = idx; i < body.Length; i++)
            {
                var c = body[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']')
                {
                    if (depth == 0) return i;
                    depth--;
                }
                else if (c == ',' && depth == 0) return i;
            }
            return body.Length;
        }

        /// <summary>
        /// Extract a quoted string value for <paramref name="key"/> from a JSON object substring.
        /// Returns null when the key is absent or the value is a <c>null</c> literal. Honors the
        /// JSON escape set (<c>\" \\ \/ \n \r \t \b \f \uXXXX</c>).
        /// </summary>
        static string? ExtractStringValue(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return null;
            // null literal → null.
            if (start + 4 <= body.Length && body.Substring(start, 4) == "null") return null;
            if (body[start] != '"') return null;
            return SliceQuotedString(body, start);
        }

        /// <summary>
        /// Extract the raw JSON token (string-unquoted, number, bool, null, array, object) for
        /// <paramref name="key"/> from a JSON object substring. For string values the quotes are
        /// stripped and escapes unescaped; for everything else the raw token is returned verbatim.
        /// Returns null when the key is absent or the value is a <c>null</c> literal.
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

            // null literal → null (caller distinguishes absent vs explicit-null via RawValue).
            if (start + 4 <= body.Length && body.Substring(start, 4) == "null") return null;

            var c = body[start];
            if (c == '"') return SliceQuotedString(body, start);
            if (c == '{' || c == '[')
            {
                var end = FindBalancedEnd(body, start, c, c == '{' ? '}' : ']');
                if (end < 0) return body.Substring(start);
                return body.Substring(start, end - start + 1);
            }

            // Bare token (number / true / false) — read to the next comma/brace/whitespace.
            int tokEnd = start;
            while (tokEnd < body.Length
                   && body[tokEnd] != ','
                   && body[tokEnd] != '}'
                   && body[tokEnd] != ']'
                   && !char.IsWhiteSpace(body[tokEnd]))
                tokEnd++;
            return body.Substring(start, tokEnd - start);
        }

        static string SliceQuotedString(string body, int start)
        {
            var sb = new StringBuilder(body.Length - start);
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
                                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
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
