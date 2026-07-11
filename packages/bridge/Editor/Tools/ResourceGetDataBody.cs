#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_resource_get_data</c> (P4.1). Extracts the scalar
    /// fields the get-data handler needs straight off the raw JSON body using the same hand-rolled
    /// <c>IndexOf</c>-substring style as <see cref="NodeFindBody"/> and <see cref="SceneGetDataBody"/>
    /// — the bridge deliberately carries no typed JSON DOM dependency on the hot path
    /// (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// The <c>profile</c> axis (compact / balanced / full) maps to a recursion depth and property
    /// breadth, mirroring Unity Open MCP's <c>read-asset</c> drill-down concept. Compact (default) is
    /// a shallow top-level property list; balanced adds one level of nesting; full walks the whole
    /// bounded tree.
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is
    /// unit-testable in the binary-less xUnit host.
    /// </para>
    /// </summary>
    internal sealed class ResourceGetDataBody
    {
        /// <summary>Required: canonical <c>res://</c> path or <c>uid://</c> of the resource to read.
        /// The handler normalizes and resolves it.</summary>
        internal string? ResourcePath { get; private set; }

        /// <summary>Profile name controlling recursion depth + property breadth. Defaults to
        /// <c>compact</c>. See <see cref="Profiles"/>.</summary>
        internal string Profile { get; private set; } = DefaultProfile;

        /// <summary>Optional drill-down property path. When set, the handler navigates the property
        /// tree to this path and serializes that subtree instead of the whole resource. Null = whole
        /// resource.</summary>
        internal string? PropertyPath { get; private set; }

        /// <summary>Bounded depth override. Null = use the profile default. Clamped to
        /// [<see cref="MinDepth"/>, <see cref="HardMaxDepth"/>].</summary>
        internal int? MaxDepth { get; private set; }

        /// <summary>Bounded page size for large child collections. Defaults to
        /// <see cref="DefaultCollectionPageSize"/>; clamped to [&lt;1, <see cref="MaxCollectionPageSize"/>].</summary>
        internal int CollectionPageSize { get; private set; } = DefaultCollectionPageSize;

        /// <summary>Opaque continuation cursor for paging large child collections. Null = first
        /// page.</summary>
        internal string? Cursor { get; private set; }

        // --- profile constants -------------------------------------------------------

        internal const string DefaultProfile = "compact";
        internal const string CompactProfile = "compact";
        internal const string BalancedProfile = "balanced";
        internal const string FullProfile = "full";

        /// <summary>Per-profile recursion depth. Compact = top-level properties only (depth 0);
        /// balanced = one level of nesting; full = walk to the hard max depth.</summary>
        internal static readonly System.Collections.Generic.Dictionary<string, int> Profiles =
            new(System.StringComparer.OrdinalIgnoreCase)
            {
                [CompactProfile] = 0,
                [BalancedProfile] = 2,
                [FullProfile] = HardMaxDepth,
            };

        internal const int MinDepth = 0;
        internal const int HardMaxDepth = 6;

        internal const int DefaultCollectionPageSize = 50;
        internal const int MaxCollectionPageSize = 200;

        /// <summary>Effective recursion depth: explicit <see cref="MaxDepth"/> (clamped) &gt; profile
        /// default. Used by the serializer.</summary>
        internal int EffectiveDepth
        {
            get
            {
                if (MaxDepth.HasValue)
                    return Math.Max(MinDepth, Math.Min(HardMaxDepth, MaxDepth.Value));
                if (Profiles.TryGetValue(Profile, out var d))
                    return d;
                // Unknown profile string → treat as compact.
                return Profiles[CompactProfile];
            }
        }

        /// <summary>True when <see cref="ResourcePath"/> is present and non-empty.</summary>
        internal bool HasResourcePath => !string.IsNullOrWhiteSpace(ResourcePath);

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="ResourceGetDataBody"/>. Never throws — a
        /// missing or malformed field falls back to its default. Empty/null body returns an
        /// all-default instance (the handler rejects a missing resource_path with
        /// <c>missing_parameter</c>).
        /// </summary>
        internal static ResourceGetDataBody Parse(string? body)
        {
            var parsed = new ResourceGetDataBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.ResourcePath = ExtractNullableStringValue(body, "resource_path");
            if (string.IsNullOrWhiteSpace(parsed.ResourcePath))
                parsed.ResourcePath = ExtractNullableStringValue(body, "resourcePath");

            var profile = ExtractNullableStringValue(body, "profile");
            if (!string.IsNullOrWhiteSpace(profile))
                parsed.Profile = profile!;

            parsed.PropertyPath = ExtractNullableStringValue(body, "property_path");
            if (string.IsNullOrWhiteSpace(parsed.PropertyPath))
                parsed.PropertyPath = ExtractNullableStringValue(body, "propertyPath");

            // max_depth: only set when the key is actually present, so the profile default applies
            // when omitted.
            if (KeyPresent(body, "max_depth") || KeyPresent(body, "maxDepth"))
            {
                var raw = ExtractIntValue(body, "max_depth", int.MinValue);
                if (raw == int.MinValue) raw = ExtractIntValue(body, "maxDepth", int.MinValue);
                if (raw != int.MinValue)
                    parsed.MaxDepth = raw;
            }

            var cps = ExtractIntValue(body, "collection_page_size", DefaultCollectionPageSize);
            if (cps == DefaultCollectionPageSize && !KeyPresent(body, "collection_page_size"))
                cps = ExtractIntValue(body, "collectionPageSize", DefaultCollectionPageSize);
            parsed.CollectionPageSize = Math.Max(1, Math.Min(MaxCollectionPageSize, cps));

            parsed.Cursor = ExtractNullableStringValue(body, "cursor");

            return parsed;
        }

        ResourceGetDataBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------

        static bool KeyPresent(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            return body.IndexOf(quotedKey, StringComparison.Ordinal) >= 0;
        }

        static string? ExtractNullableStringValue(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return null;
            if (start + 4 <= body.Length && body.Substring(start, 4) == "null") return null;
            if (body[start] == '"')
                return SliceQuotedString(body, start);
            return null;
        }

        static int ExtractIntValue(string body, string key, int defaultValue)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return defaultValue;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return defaultValue;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return defaultValue;
            int end = start;
            if (end < body.Length && (body[end] == '-' || body[end] == '+')) end++;
            while (end < body.Length && char.IsDigit(body[end])) end++;
            var token = body.AsSpan(start, end - start).Trim();
            if (token.Length == 0) return defaultValue;
            return int.TryParse(token, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
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
