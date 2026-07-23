#nullable enable
using System;
using System.Globalization;

namespace GodotOpenMcp.Bridge.Editor
{
    // ===========================================================================
    // P12.1 tilemap pack request bodies.
    //
    // Each tool that needs structured extraction gets a tiny sealed body type. They
    // all mirror the hand-rolled `IndexOf`-substring style already used by
    // NodeCreateBody / NodeFindBody (packages/bridge/AGENTS.md §Transport: the bridge
    // deliberately carries no typed JSON DOM dependency on the hot path). Pure-
    // managed (no Godot API surface, no `#if TOOLS`), so the parsing logic is unit-
    // testable in the binary-less xUnit host.
    //
    // The shared extraction primitives (ExtractString / ExtractInt / SliceQuotedString)
    // are ported verbatim from NodeCreateBody so the same JSON escape set and null-
    // literal handling applies. If a fourth family duplicates this, factor a shared
    // JsonScalar reader then.
    // ===========================================================================

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_tilemap_create</c> (P12.1). Mirrors
    /// <c>node_create</c> for the shape an agent already knows (name / parent_node_path /
    /// position), because creating a <c>TileMapLayer</c> is a node creation under the hood — the
    /// only delta is the type is fixed to <c>TileMapLayer</c>.
    /// </summary>
    internal sealed class TilemapCreateBody
    {
        internal string? Name { get; private set; }
        internal string? ParentNodePath { get; private set; }
        internal string? Position { get; private set; }

        internal static TilemapCreateBody Parse(string? body)
        {
            var parsed = new TilemapCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Name = JsonScalar.ExtractString(body, "name");
            parsed.ParentNodePath = JsonScalar.ExtractString(body, "parent_node_path");
            parsed.Position = JsonScalar.ExtractString(body, "position");
            return parsed;
        }

        TilemapCreateBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_tilemap_set_tileset</c> (P12.1). Carries the
    /// layer target (node_path) and the <c>res://</c> path of the <c>TileSet</c> resource to
    /// assign.
    /// </summary>
    internal sealed class TilemapSetTilesetBody
    {
        internal string? NodePath { get; private set; }
        internal string? TilesetPath { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);
        internal bool HasTilesetPath => !string.IsNullOrEmpty(TilesetPath);

        internal static TilemapSetTilesetBody Parse(string? body)
        {
            var parsed = new TilemapSetTilesetBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.TilesetPath = JsonScalar.ExtractString(body, "tileset_path");
            return parsed;
        }

        TilemapSetTilesetBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_tilemap_set_cell</c> (P12.1). Carries the
    /// addressing quadruple Godot uses to paint a tile: map coords (x, y) plus
    /// source_id / atlas coords / alternative_tile. The atlas quadruple defaults to (0, 0, 0, 0)
    /// so an agent painting from a single-source single-tile TileSet can omit every optional
    /// field. Coordinates are extracted as longs and clamped to int range so an absurd value
    /// surfaces as <c>invalid_parameter</c> rather than an <c>OverflowException</c>.
    /// </summary>
    internal sealed class TilemapSetCellBody
    {
        internal string? NodePath { get; private set; }
        internal int X { get; private set; }
        internal int Y { get; private set; }
        internal int SourceId { get; private set; }
        internal int AtlasX { get; private set; }
        internal int AtlasY { get; private set; }
        internal int AlternativeTile { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal static TilemapSetCellBody Parse(string? body)
        {
            var parsed = new TilemapSetCellBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.X = JsonScalar.ExtractInt(body, "x", defaultValue: 0);
            parsed.Y = JsonScalar.ExtractInt(body, "y", defaultValue: 0);
            parsed.SourceId = JsonScalar.ExtractInt(body, "source_id", defaultValue: 0);
            parsed.AtlasX = JsonScalar.ExtractInt(body, "atlas_x", defaultValue: 0);
            parsed.AtlasY = JsonScalar.ExtractInt(body, "atlas_y", defaultValue: 0);
            parsed.AlternativeTile = JsonScalar.ExtractInt(body, "alternative_tile", defaultValue: 0);
            return parsed;
        }

        TilemapSetCellBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_tilemap_erase_cell</c> (P12.1). Only the layer
    /// target (node_path) and the map coordinate (x, y) are needed — erase does not care which
    /// tile occupied the cell.
    /// </summary>
    internal sealed class TilemapEraseCellBody
    {
        internal string? NodePath { get; private set; }
        internal int X { get; private set; }
        internal int Y { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal static TilemapEraseCellBody Parse(string? body)
        {
            var parsed = new TilemapEraseCellBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.X = JsonScalar.ExtractInt(body, "x", defaultValue: 0);
            parsed.Y = JsonScalar.ExtractInt(body, "y", defaultValue: 0);
            return parsed;
        }

        TilemapEraseCellBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_tilemap_get_used_cells</c> (P12.1). Read-only —
    /// carries only the layer target (node_path) and the response bound (max_results).
    /// </summary>
    internal sealed class TilemapGetUsedCellsBody
    {
        internal const int DefaultMaxResults = 256;
        internal const int HardMaxResults = 2000;

        internal string? NodePath { get; private set; }
        internal int MaxResults { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        /// <summary>The effective cap, clamped to [1, HardMaxResults]. A non-positive requested
        /// value falls back to <see cref="DefaultMaxResults"/>.</summary>
        internal int EffectiveMaxResults
        {
            get
            {
                if (MaxResults <= 0) return DefaultMaxResults;
                return Math.Min(MaxResults, HardMaxResults);
            }
        }

        internal static TilemapGetUsedCellsBody Parse(string? body)
        {
            var parsed = new TilemapGetUsedCellsBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.MaxResults = JsonScalar.ExtractInt(body, "max_results", defaultValue: DefaultMaxResults);
            return parsed;
        }

        TilemapGetUsedCellsBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_tilemap_clear</c> (P12.1). Only the layer
    /// target (node_path) is needed — clear empties every cell and keeps the TileSet.
    /// </summary>
    internal sealed class TilemapClearBody
    {
        internal string? NodePath { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal static TilemapClearBody Parse(string? body)
        {
            var parsed = new TilemapClearBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            return parsed;
        }

        TilemapClearBody() { }
    }

    // ===========================================================================
    // Shared JSON scalar extraction primitives (P12.1).
    //
    // Verbatim port of the NodeCreateBody / NodeFindBody quartet (ExtractRawValue /
    // SliceQuotedString / SliceBareToken / the full \" \\ \/ \n \r \t \b \f \uXXXX
    // escape table) plus the int extractor from NodeFindBody. Kept as a private static
    // class so every tilemap body type shares one implementation without widening the
    // namespace surface. P12.2 (navigation) is the "third family" the original comment
    // anticipated — it reuses this class rather than duplicating a third copy, and adds
    // ExtractFloat / ExtractBool for the navigation agent scalars. If a fourth family
    // needs a different extractor, add it here.
    // ===========================================================================

    internal static class JsonScalar
    {
        internal static string? ExtractString(string body, string key)
        {
            var raw = ExtractRawValue(body, key);
            // ExtractRawValue already unwraps the quotes via SliceQuotedString; a present-but-null
            // literal ("key":null) returns null. So raw IS the string value (or null).
            return raw;
        }

        internal static int ExtractInt(string body, string key, int defaultValue)
        {
            var raw = ExtractRawValue(body, key);
            if (raw == null) return defaultValue;
            var trimmed = raw.AsSpan().Trim();
            int end = 0;
            if (trimmed.Length > 0 && (trimmed[0] == '-' || trimmed[0] == '+')) end = 1;
            while (end < trimmed.Length && char.IsDigit(trimmed[end])) end++;
            if (end == 0 || (end == 1 && trimmed.Length > 0 && (trimmed[0] == '-' || trimmed[0] == '+')))
                return defaultValue;
            // Parse as long first so a value outside the int range degrades to the default instead
            // of throwing OverflowException; the handler surfaces invalid_parameter in that case.
            if (long.TryParse(trimmed.Slice(0, end), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var v))
            {
                if (v < int.MinValue || v > int.MaxValue) return defaultValue;
                return (int)v;
            }
            return defaultValue;
        }

        /// <summary>
        /// Extract a float scalar. Returns null when the key is absent, explicitly null, or not a
        /// parseable number — the caller treats null as "leave unchanged" (P12.2 navigation agent
        /// configure contract). Added in P12.2 for the navigation pack's clamped agent scalars
        /// (radius / height / max_speed / distances); reused by any future float-extracting body.
        /// Parses with <see cref="NumberStyles.Float"/> + invariant culture so a comma-decimal
        /// locale cannot corrupt the value.
        /// </summary>
        internal static float? ExtractFloat(string body, string key)
        {
            var raw = ExtractRawValue(body, key);
            if (raw == null) return null;
            var trimmed = raw.AsSpan().Trim();
            if (trimmed.Length == 0) return null;
            if (float.TryParse(trimmed, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }
            return null;
        }

        /// <summary>
        /// Extract a bool scalar. Returns null when the key is absent, explicitly null, or not a
        /// recognized bool literal — the caller treats null as "leave unchanged". Recognizes the
        /// JSON bool literals <c>true</c> / <c>false</c> (case-sensitive per the JSON spec; a
        /// stray <c>True</c> degrades to null). Added in P12.2 for the navigation pack's
        /// avoidance_enabled / bidirectional flags.
        /// </summary>
        internal static bool? ExtractBool(string body, string key)
        {
            var raw = ExtractRawValue(body, key);
            if (raw == null) return null;
            var trimmed = raw.AsSpan().Trim();
            if (trimmed.Length == 4 && trimmed.SequenceEqual("true".AsSpan())) return true;
            if (trimmed.Length == 5 && trimmed.SequenceEqual("false".AsSpan())) return false;
            return null;
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
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out var code))
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
    }
}
