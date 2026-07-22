#nullable enable
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// One cell of a <c>TileMapLayer</c> as returned by the tilemap pack's read/list tools
    /// (P12.1). Holds the map coordinate plus the addressing quadruple Godot uses to identify a
    /// tile inside a <c>TileSet</c> (source id + atlas coords + alternative tile). Pure data — it
    /// is built on the main thread from a resolved <c>TileMapLayer</c> and serialized off the main
    /// thread, touching no Godot native object once constructed.
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so it is unit-testable in the
    /// binary-less xUnit host. Field order is fixed (x, y, sourceId, atlasX, atlasY,
    /// alternativeTile) so diffing clients do not flap on reordering; every numeric is rendered
    /// with <see cref="System.Globalization.CultureInfo.InvariantCulture"/> so a comma-decimal
    /// locale cannot corrupt the JSON. The serializer mirrors the camelCase style already used by
    /// <see cref="NodeData"/> (the canonical bridge serializer contract) — a tilemap cell is a
    /// leaf in the same scene tree, so it matches rather than inventing a third casing style.
    /// </para>
    /// </summary>
    internal sealed class TilemapCellData
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int SourceId { get; set; } = 0;
        public int AtlasX { get; set; } = 0;
        public int AtlasY { get; set; } = 0;
        public int AlternativeTile { get; set; } = 0;

        /// <summary>
        /// Append this cell as a JSON object to <paramref name="sb"/>. Field order is fixed so a
        /// diffing client does not flap on reordering.
        /// </summary>
        internal void AppendJsonTo(StringBuilder sb)
        {
            sb.Append('{');
            sb.Append("\"x\":").Append(X.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"y\":").Append(Y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"sourceId\":").Append(SourceId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"atlasX\":").Append(AtlasX.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"atlasY\":").Append(AtlasY.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"alternativeTile\":").Append(AlternativeTile.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('}');
        }

        /// <summary>Serialize this cell as a standalone JSON object string (test helper).</summary>
        public string ToJsonString()
        {
            var sb = new StringBuilder(80);
            AppendJsonTo(sb);
            return sb.ToString();
        }
    }
}
