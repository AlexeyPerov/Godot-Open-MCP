#nullable enable

namespace GodotOpenMcp.Bridge.Editor
{
    // ===========================================================================
    // GridMap pack request bodies.
    //
    // Each tool that needs structured extraction gets a tiny sealed body type.
    // They mirror the hand-rolled `IndexOf`-substring style already used by
    // the P12 packs (packages/bridge/AGENTS.md §Transport: the bridge carries
    // no typed JSON DOM dependency on the hot path). Pure-managed (no Godot
    // API surface, no `#if TOOLS`), so the parsing logic is unit-testable in
    // the binary-less xUnit host.
    //
    // The shared extraction primitives live on `JsonScalar` (declared once in
    // TilemapBodies.cs and reused by every later pack). This pack adds no new
    // extractor — its ints all have sensible defaults (mirrors the tilemap
    // pack), so `ExtractInt` + `ExtractString` suffice.
    // ===========================================================================

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_gridmap_create</c>. Mirrors
    /// <c>node_create</c> for the shape an agent already knows (name /
    /// parent_node_path / position) — creating a <c>GridMap</c> is a node
    /// creation under the hood; the type is fixed to <c>GridMap</c> (a
    /// <c>Node3D</c> subclass), so <c>position</c> is an "x,y,z" string.
    /// </summary>
    internal sealed class GridMapCreateBody
    {
        internal string? Name { get; private set; }
        internal string? ParentNodePath { get; private set; }
        internal string? Position { get; private set; }

        internal static GridMapCreateBody Parse(string? body)
        {
            var parsed = new GridMapCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Name = JsonScalar.ExtractString(body, "name");
            parsed.ParentNodePath = JsonScalar.ExtractString(body, "parent_node_path");
            parsed.Position = JsonScalar.ExtractString(body, "position");
            return parsed;
        }

        GridMapCreateBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_gridmap_set_mesh_library</c>.
    /// Carries the target (node_path) and the <c>res://</c> path of the
    /// <c>MeshLibrary</c> resource to assign.
    /// </summary>
    internal sealed class GridMapSetMeshLibraryBody
    {
        internal string? NodePath { get; private set; }
        internal string? MeshLibraryPath { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);
        internal bool HasMeshLibraryPath => !string.IsNullOrEmpty(MeshLibraryPath);

        internal static GridMapSetMeshLibraryBody Parse(string? body)
        {
            var parsed = new GridMapSetMeshLibraryBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.MeshLibraryPath = JsonScalar.ExtractString(body, "mesh_library_path");
            return parsed;
        }

        GridMapSetMeshLibraryBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_gridmap_set_cell</c>. Carries
    /// the 3D map coordinate (x, y, z), the <c>MeshLibrary</c> item id, and the
    /// orientation (0–23 Godot orthonormal rotations, default 0). Coordinates
    /// are extracted as longs and clamped to int range so an absurd value
    /// surfaces as <c>invalid_parameter</c> rather than an
    /// <c>OverflowException</c>.
    /// </summary>
    internal sealed class GridMapSetCellBody
    {
        internal string? NodePath { get; private set; }
        internal int X { get; private set; }
        internal int Y { get; private set; }
        internal int Z { get; private set; }
        internal int Item { get; private set; }
        internal int Orientation { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal static GridMapSetCellBody Parse(string? body)
        {
            var parsed = new GridMapSetCellBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.X = JsonScalar.ExtractInt(body, "x", defaultValue: 0);
            parsed.Y = JsonScalar.ExtractInt(body, "y", defaultValue: 0);
            parsed.Z = JsonScalar.ExtractInt(body, "z", defaultValue: 0);
            parsed.Item = JsonScalar.ExtractInt(body, "item", defaultValue: 0);
            parsed.Orientation = JsonScalar.ExtractInt(body, "orientation", defaultValue: 0);
            return parsed;
        }

        GridMapSetCellBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_gridmap_erase_cell</c>. Only
    /// the target (node_path) and the 3D map coordinate (x, y, z) are needed —
    /// erase does not care which item occupied the cell.
    /// </summary>
    internal sealed class GridMapEraseCellBody
    {
        internal string? NodePath { get; private set; }
        internal int X { get; private set; }
        internal int Y { get; private set; }
        internal int Z { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal static GridMapEraseCellBody Parse(string? body)
        {
            var parsed = new GridMapEraseCellBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.X = JsonScalar.ExtractInt(body, "x", defaultValue: 0);
            parsed.Y = JsonScalar.ExtractInt(body, "y", defaultValue: 0);
            parsed.Z = JsonScalar.ExtractInt(body, "z", defaultValue: 0);
            return parsed;
        }

        GridMapEraseCellBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_gridmap_get_used_cells</c>
    /// (read-only). Carries only the target (node_path) and the response bound
    /// (max_results).
    /// </summary>
    internal sealed class GridMapGetUsedCellsBody
    {
        internal const int DefaultMaxResults = 256;
        internal const int HardMaxResults = 2000;

        internal string? NodePath { get; private set; }
        internal int MaxResults { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        /// <summary>The effective cap, clamped to [1, HardMaxResults]. A
        /// non-positive requested value falls back to
        /// <see cref="DefaultMaxResults"/>.</summary>
        internal int EffectiveMaxResults
        {
            get
            {
                if (MaxResults <= 0) return DefaultMaxResults;
                return System.Math.Min(MaxResults, HardMaxResults);
            }
        }

        internal static GridMapGetUsedCellsBody Parse(string? body)
        {
            var parsed = new GridMapGetUsedCellsBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.MaxResults = JsonScalar.ExtractInt(body, "max_results", defaultValue: DefaultMaxResults);
            return parsed;
        }

        GridMapGetUsedCellsBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_gridmap_clear</c>. Only the
    /// target (node_path) is needed — clear empties every cell and keeps the
    /// MeshLibrary.
    /// </summary>
    internal sealed class GridMapClearBody
    {
        internal string? NodePath { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal static GridMapClearBody Parse(string? body)
        {
            var parsed = new GridMapClearBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            return parsed;
        }

        GridMapClearBody() { }
    }
}
