#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace GodotOpenMcp.Bridge.Editor
{
    // ===========================================================================
    // P16.6 spatial-query pack request body.
    //
    // One tool — godot_open_mcp_spatial_query — dispatches three query kinds
    // (ray / shape / point) across two dimensions (2d / 3d). The body parser
    // records every field the handler needs; the handler resolves the active
    // physics space, builds the Godot query parameters, and serializes hits.
    //
    // Pure-managed (no Godot API surface, no `#if TOOLS`) so the parsing logic is
    // unit-testable in the binary-less xUnit host, mirroring the P12.1–P16.5
    // `<Pack>Bodies.cs` convention. The shared extraction primitives live in the
    // P12.1 `JsonScalar` static class (sibling Tilemap file). P16.6 reuses
    // ExtractString / ExtractFloat / ExtractIntOrNull / ExtractBool and the
    // ExtractRawValue entry point (for the mask uint + the exclude string array).
    //
    // Query kind / dimension / shape handling: the body parser records the raw
    // tokens via the *Parser helpers; Unknown covers both "absent" and "not a
    // valid token", surfaced as invalid_parameter by the handler. The shape↔
    // dimension compatibility table (circle/rectangle → 2d, sphere/box → 3d,
    // capsule → both) is centralized in SpatialShapeCompat so it is unit-testable
    // without the editor.
    // ===========================================================================

    /// <summary>
    /// Normalized query-kind token. <see cref="Unknown"/> covers both "absent" and
    /// "not a valid token"; the handler turns that into <c>invalid_parameter</c>.
    /// </summary>
    internal enum SpatialQueryType
    {
        Unknown = 0,
        Ray = 1,
        Shape = 2,
        Point = 3,
    }

    /// <summary>
    /// Map a raw <c>query_type</c> string ("ray" | "shape" | "point") to a
    /// <see cref="SpatialQueryType"/>. Case-insensitive to tolerate an agent
    /// sending "Ray". Returns <see cref="SpatialQueryType.Unknown"/> for null /
    /// empty / unrecognized tokens.
    /// </summary>
    internal static class SpatialQueryTypeParser
    {
        internal static SpatialQueryType Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return SpatialQueryType.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "ray": return SpatialQueryType.Ray;
                case "shape": return SpatialQueryType.Shape;
                case "point": return SpatialQueryType.Point;
                default: return SpatialQueryType.Unknown;
            }
        }
    }

    /// <summary>
    /// Normalized dimension token. Separate from <c>ParticlesDimension</c> /
    /// <c>NavigationDimension</c> so each pack owns its enum without cross-pack
    /// coupling; the underlying values are irrelevant (the handler switches on the
    /// enum, not the int).
    /// </summary>
    internal enum SpatialDimension
    {
        Unknown = 0,
        TwoD = 2,
        ThreeD = 3,
    }

    /// <summary>
    /// Map a raw <c>dimension</c> string ("2d" | "3d") to a
    /// <see cref="SpatialDimension"/>. Case-insensitive. Returns
    /// <see cref="SpatialDimension.Unknown"/> for null / empty / unrecognized
    /// tokens.
    /// </summary>
    internal static class SpatialDimensionParser
    {
        internal static SpatialDimension Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return SpatialDimension.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "2d": return SpatialDimension.TwoD;
                case "3d": return SpatialDimension.ThreeD;
                default: return SpatialDimension.Unknown;
            }
        }
    }

    /// <summary>
    /// Normalized overlap-shape token for <c>query_type:"shape"</c>. circle /
    /// rectangle are 2D-only; sphere / box are 3D-only; capsule works in both.
    /// <see cref="Unknown"/> covers both "absent" and "not a valid token".
    /// </summary>
    internal enum SpatialShape
    {
        Unknown = 0,
        Circle = 1,
        Sphere = 2,
        Rectangle = 3,
        Box = 4,
        Capsule = 5,
    }

    /// <summary>
    /// Map a raw <c>shape</c> string to a <see cref="SpatialShape"/>. Returns
    /// <see cref="SpatialShape.Unknown"/> for null / empty / unrecognized tokens.
    /// </summary>
    internal static class SpatialShapeParser
    {
        internal static SpatialShape Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return SpatialShape.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "circle": return SpatialShape.Circle;
                case "sphere": return SpatialShape.Sphere;
                case "rectangle": return SpatialShape.Rectangle;
                case "box": return SpatialShape.Box;
                case "capsule": return SpatialShape.Capsule;
                default: return SpatialShape.Unknown;
            }
        }

        /// <summary>Round-trip the enum back to its schema token (for result JSON).</summary>
        internal static string ToSchemaString(SpatialShape shape)
        {
            switch (shape)
            {
                case SpatialShape.Circle: return "circle";
                case SpatialShape.Sphere: return "sphere";
                case SpatialShape.Rectangle: return "rectangle";
                case SpatialShape.Box: return "box";
                case SpatialShape.Capsule: return "capsule";
                default: return "unknown";
            }
        }
    }

    /// <summary>
    /// Shape↔dimension compatibility table. circle / rectangle are 2D-only;
    /// sphere / box are 3D-only; capsule works in both. Used by the handler to
    /// reject a mismatched shape+dimension with <c>invalid_parameter</c>, and
    /// unit-tested here without the editor.
    /// </summary>
    internal static class SpatialShapeCompat
    {
        internal static bool IsCompatible(SpatialShape shape, SpatialDimension dimension)
        {
            switch (shape)
            {
                case SpatialShape.Circle:
                case SpatialShape.Rectangle:
                    return dimension == SpatialDimension.TwoD;
                case SpatialShape.Sphere:
                case SpatialShape.Box:
                    return dimension == SpatialDimension.ThreeD;
                case SpatialShape.Capsule:
                    return dimension == SpatialDimension.TwoD || dimension == SpatialDimension.ThreeD;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_spatial_query</c> (P16.6). Carries
    /// the query kind + dimension + (for shape) the shape kind, plus every scalar
    /// and vector string the handler needs to build the Godot query parameters.
    /// Vectors stay as raw strings ("x,y" / "x,y,z") — the handler parses them on
    /// the main thread against the right Godot Vector type.
    ///
    /// <para>
    /// Nullable extractors (<see cref="JsonScalar.ExtractFloat"/> / ExtractIntOrNull
    /// / ExtractBool) return null when the key is absent or non-parseable; the
    /// handler treats null as "default" (mask → all layers; radius/size → required
    /// for the shape; collide_with_bodies → true; collide_with_areas → false;
    /// max_results → 32). The <c>exclude</c> node-path array is parsed into a
    /// <see cref="List{String}"/> (empty when absent) via the shared array scanner.
    /// </para>
    /// </summary>
    internal sealed class SpatialQueryBody
    {
        internal SpatialQueryType QueryType { get; private set; }
        internal SpatialDimension Dimension { get; private set; }
        internal SpatialShape Shape { get; private set; }

        internal string? From { get; private set; }
        internal string? To { get; private set; }
        internal string? Position { get; private set; }
        internal string? Size { get; private set; }
        internal string? Rotation { get; private set; }

        internal float? Radius { get; private set; }
        internal float? Height { get; private set; }

        /// <summary>
        /// Collision bitmask, or null when absent / non-parseable. Stored as uint
        /// because a full 32-bit mask (0xFFFFFFFF) overflows int; the handler
        /// treats null as "all layers".
        /// </summary>
        internal uint? Mask { get; private set; }

        /// <summary>Collider node paths to exclude (resolved to RIDs by the handler). Empty when absent.</summary>
        internal List<string> Exclude { get; private set; } = new();

        internal bool CollideWithBodies { get; private set; }
        internal bool CollideWithAreas { get; private set; }
        internal int MaxResults { get; private set; }

        internal bool HasFrom => !string.IsNullOrWhiteSpace(From);
        internal bool HasTo => !string.IsNullOrWhiteSpace(To);
        internal bool HasPosition => !string.IsNullOrWhiteSpace(Position);

        internal static SpatialQueryBody Parse(string? body)
        {
            // Defaults that differ from the C# defaults: collide_with_bodies=true,
            // collide_with_areas=false, max_results=32. A present key overrides.
            var parsed = new SpatialQueryBody
            {
                CollideWithBodies = true,
                CollideWithAreas = false,
                MaxResults = 32,
            };
            if (string.IsNullOrEmpty(body)) return parsed;

            // NOTE: every field is read through SliceFromKey (a key-aware lookup)
            // rather than the raw JsonScalar.Extract* entry points. The generic
            // scanner finds the FIRST `"key"` substring anywhere in the body, which
            // collides here because the `query_type` enum value `"shape"` appears
            // before the `"shape"` key — a raw scan would resolve the `shape` field
            // to the value `"3d"` (the next colon after the value token). SliceFromKey
            // requires `"key"` to be immediately followed by `:` (modulo whitespace),
            // so only a real key matches. See SliceFromKey below.
            parsed.QueryType = SpatialQueryTypeParser.Parse(KeyedString(body, "query_type"));
            parsed.Dimension = SpatialDimensionParser.Parse(KeyedString(body, "dimension"));
            parsed.Shape = SpatialShapeParser.Parse(KeyedString(body, "shape"));

            parsed.From = KeyedString(body, "from");
            parsed.To = KeyedString(body, "to");
            parsed.Position = KeyedString(body, "position");
            parsed.Size = KeyedString(body, "size");
            parsed.Rotation = KeyedString(body, "rotation");

            parsed.Radius = KeyedFloat(body, "radius");
            parsed.Height = KeyedFloat(body, "height");

            parsed.Mask = ExtractUInt(body, "mask");
            parsed.Exclude = ExtractStringArray(body, "exclude");

            // bool defaults: present value overrides the C#-default set above; a
            // present-but-non-bool literal degrades to null → keep the default.
            var bodies = KeyedBool(body, "collide_with_bodies");
            if (bodies.HasValue) parsed.CollideWithBodies = bodies.Value;
            var areas = KeyedBool(body, "collide_with_areas");
            if (areas.HasValue) parsed.CollideWithAreas = areas.Value;

            var max = KeyedIntOrNull(body, "max_results");
            if (max.HasValue && max.Value >= 1) parsed.MaxResults = max.Value;

            return parsed;
        }

        SpatialQueryBody() { }

        // ---------------------------------------------------------------------
        // Key-aware extraction.
        //
        // The shared JsonScalar.Extract* scanners resolve a key by finding the
        // first `"key"` substring anywhere in the body. That is ambiguous when a
        // VALUE token equals a later KEY name — which happens in this pack because
        // query_type's enum value "shape" collides with the "shape" field name.
        // SliceFromKey scans for the quoted key and accepts it only when the next
        // non-whitespace char is ':' (a real key, never a value), then delegates
        // to the existing typed extractors over the tail starting at that key.
        // ---------------------------------------------------------------------

        /// <summary>
        /// Return the substring of <paramref name="body"/> starting at the real
        /// <c>"key":</c> occurrence (key immediately followed by a colon, modulo
        /// whitespace), or null when no such occurrence exists. The returned tail
        /// begins with the quoted key so <see cref="JsonScalar"/> extractors find
        /// it at index 0 and slice the correct value.
        /// </summary>
        internal static string? SliceFromKey(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            int searchFrom = 0;
            while (true)
            {
                int idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
                if (idx < 0) return null;
                int j = idx + quotedKey.Length;
                while (j < body.Length && char.IsWhiteSpace(body[j])) j++;
                if (j < body.Length && body[j] == ':')
                    return body.Substring(idx);
                // Not a real key (it was a value token) — resume scanning just
                // past this occurrence.
                searchFrom = idx + quotedKey.Length;
                if (searchFrom >= body.Length) return null;
                body = body.Substring(searchFrom);
                // Note: re-slicing body each loop is fine — bodies are tiny.
            }
        }

        internal static string? KeyedString(string body, string key)
            => SliceFromKey(body, key) is { } s ? JsonScalar.ExtractString(s, key) : null;

        internal static float? KeyedFloat(string body, string key)
            => SliceFromKey(body, key) is { } s ? JsonScalar.ExtractFloat(s, key) : null;

        internal static bool? KeyedBool(string body, string key)
            => SliceFromKey(body, key) is { } s ? JsonScalar.ExtractBool(s, key) : null;

        internal static int? KeyedIntOrNull(string body, string key)
            => SliceFromKey(body, key) is { } s ? JsonScalar.ExtractIntOrNull(s, key) : null;

        // ---------------------------------------------------------------------
        // mask + exclude array helpers. mask is a uint bitmask (a full 32-bit
        // mask overflows int), so it is parsed off the raw token rather than via
        // ExtractIntOrNull. exclude is a JSON string array; the key-aware slice
        // hands the balanced [...] value to ExtractStringArray, which scans it for
        // quoted elements (reusing the same escape handling as JsonScalar).
        // ---------------------------------------------------------------------

        /// <summary>
        /// Extract a uint scalar. Returns null when the key is absent, explicitly
        /// null, or not a parseable non-negative integer. A negative value parses
        /// to null (a collision mask is never negative). Parses as long first so a
        /// value outside the uint range degrades to null instead of throwing.
        /// </summary>
        internal static uint? ExtractUInt(string body, string key)
        {
            var slice = SliceFromKey(body, key);
            if (slice == null) return null;
            var raw = JsonScalar.ExtractRawValue(slice, key);
            if (raw == null) return null;
            var trimmed = raw.AsSpan().Trim();
            if (trimmed.Length == 0) return null;
            // Reject a leading sign — a mask is never negative, and a '+' is not a
            // JSON number form an agent would send.
            if (trimmed[0] == '-' || trimmed[0] == '+') return null;
            if (long.TryParse(trimmed, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var v))
            {
                if (v < 0 || v > uint.MaxValue) return null;
                return (uint)v;
            }
            return null;
        }

        /// <summary>
        /// Extract a JSON string array into a <see cref="List{String}"/>. Returns
        /// an empty list when the key is absent / null / not an array. Non-string
        /// elements are skipped (best-effort — a non-string element is a contract
        /// violation; the handler treats exclude as advisory, so dropping a bad
        /// element is safer than failing the whole query). Quote-escape handling
        /// mirrors <c>JsonScalar.SliceQuotedString</c>.
        /// </summary>
        internal static List<string> ExtractStringArray(string body, string key)
        {
            var result = new List<string>();
            var slice = SliceFromKey(body, key);
            if (slice == null) return result;
            var raw = JsonScalar.ExtractRawValue(slice, key);
            if (string.IsNullOrEmpty(raw)) return result;
            // raw is the balanced "[...]" slice (or, for a non-array value, some
            // other token — bail in that case). A non-array exclude is treated as
            // absent (empty list).
            var s = raw!.TrimStart();
            if (s.Length == 0 || s[0] != '[') return result;

            int i = 1;
            while (i < s.Length)
            {
                // Skip whitespace and commas between elements.
                while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
                if (i >= s.Length || s[i] == ']') break;
                // A non-string element is skipped to the next comma / close — the
                // exclude list is advisory, never worth failing a query over.
                if (s[i] != '"')
                {
                    while (i < s.Length && s[i] != ',' && s[i] != ']') i++;
                    continue;
                }
                i++; // consume opening quote
                var element = new System.Text.StringBuilder();
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        var nxt = s[i + 1];
                        switch (nxt)
                        {
                            case '"': element.Append('"'); i += 2; continue;
                            case '\\': element.Append('\\'); i += 2; continue;
                            case '/': element.Append('/'); i += 2; continue;
                            case 'n': element.Append('\n'); i += 2; continue;
                            case 'r': element.Append('\r'); i += 2; continue;
                            case 't': element.Append('\t'); i += 2; continue;
                            case 'b': element.Append('\b'); i += 2; continue;
                            case 'f': element.Append('\f'); i += 2; continue;
                            case 'u' when i + 5 < s.Length:
                                if (int.TryParse(s.Substring(i + 2, 4),
                                        NumberStyles.HexNumber,
                                        CultureInfo.InvariantCulture, out var code))
                                    element.Append((char)code);
                                i += 6;
                                continue;
                            default:
                                element.Append(nxt); i += 2; continue;
                        }
                    }
                    element.Append(s[i]);
                    i++;
                }
                if (i < s.Length && s[i] == '"') i++; // consume closing quote (tolerate unterminated)
                result.Add(element.ToString());
            }
            return result;
        }
    }
}
