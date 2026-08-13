#nullable enable
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// One used cell of a <c>GridMap</c> as returned by the gridmap pack's
    /// read/list tool (the read-only <c>gridmap_get_used_cells</c>). Holds the
    /// 3D map coordinate plus the item id and orientation Godot uses to
    /// identify a placed mesh inside a <c>MeshLibrary</c>. Pure data — it is
    /// built on the main thread from a resolved <c>GridMap</c> and serialized
    /// off the main thread, touching no Godot native object once constructed.
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so it is
    /// unit-testable in the binary-less xUnit host. Field order is fixed
    /// (x, y, z, item, orientation) so a diffing client does not flap on
    /// reordering; every numeric is rendered with
    /// <see cref="System.Globalization.CultureInfo.InvariantCulture"/> so a
    /// comma-decimal locale cannot corrupt the JSON. The serializer mirrors the
    /// camelCase style already used by <see cref="NodeData"/> (the canonical
    /// bridge serializer contract) — a gridmap cell is a leaf in the same scene
    /// tree, so it matches rather than inventing a fourth casing style.
    /// </para>
    /// </summary>
    internal sealed class GridMapCellData
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public int Item { get; set; } = 0;
        public int Orientation { get; set; } = 0;

        /// <summary>
        /// Append this cell as a JSON object to <paramref name="sb"/>. Field
        /// order is fixed so a diffing client does not flap on reordering.
        /// </summary>
        internal void AppendJsonTo(StringBuilder sb)
        {
            sb.Append('{');
            sb.Append("\"x\":").Append(X.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"y\":").Append(Y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"z\":").Append(Z.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"item\":").Append(Item.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"orientation\":").Append(Orientation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('}');
        }

        /// <summary>Serialize this cell as a standalone JSON object string (test helper).</summary>
        public string ToJsonString()
        {
            var sb = new StringBuilder(96);
            AppendJsonTo(sb);
            return sb.ToString();
        }
    }
}
