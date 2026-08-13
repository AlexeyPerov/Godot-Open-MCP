#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// GridMap pack unit tests for the pure-managed, off-editor-testable pieces:
    /// the six request-body parsers and the <see cref="GridMapCellData"/> JSON
    /// serializer. The editor-only handlers (<see cref="GridMapTools.Create"/>
    /// / SetMeshLibrary / SetCell / …) are <c>#if TOOLS</c> and coupled to
    /// <c>EditorInterface.Singleton</c> and <c>GridMap</c>, neither of which the
    /// binary-less xUnit host can construct — those paths are exercised by the
    /// headless Godot smoke / live call path, not here.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <c>TilemapBodiesTests</c> (copy
    /// fidelity for the parser + DTO test shape), with cases specific to the
    /// GridMap field set (3D cell coordinate + item + orientation,
    /// max_results clamping, used-cell envelope).
    /// </para>
    /// </summary>
    public class GridMapBodiesTests
    {
        // --- GridMapCreateBody ---------------------------------------------------

        [Fact]
        public void GridMapCreateBody_empty_body_returns_defaults()
        {
            var b = GridMapCreateBody.Parse(null);
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
        }

        [Fact]
        public void GridMapCreateBody_reads_all_fields()
        {
            var b = GridMapCreateBody.Parse(
                "{\"name\":\"Level\",\"parent_node_path\":\"Main\",\"position\":\"1,2,3\"}");
            Assert.Equal("Level", b.Name);
            Assert.Equal("Main", b.ParentNodePath);
            Assert.Equal("1,2,3", b.Position);
        }

        [Fact]
        public void GridMapCreateBody_unquotes_escaped_string_values()
        {
            var b = GridMapCreateBody.Parse(
                "{\"name\":\"a\\\"b\\\\c\",\"parent_node_path\":\"Main/Level\"}");
            Assert.Equal("a\"b\\c", b.Name);
            Assert.Equal("Main/Level", b.ParentNodePath);
        }

        [Fact]
        public void GridMapCreateBody_unquotes_unicode_escape()
        {
            var b = GridMapCreateBody.Parse("{\"name\":\"\\u0041\\u0042\"}");
            Assert.Equal("AB", b.Name);
        }

        [Fact]
        public void GridMapCreateBody_treats_explicit_null_as_absent()
        {
            var b = GridMapCreateBody.Parse(
                "{\"name\":null,\"parent_node_path\":null,\"position\":null}");
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
        }

        // --- GridMapSetMeshLibraryBody -----------------------------------------

        [Fact]
        public void GridMapSetMeshLibraryBody_empty_body_has_no_paths()
        {
            var b = GridMapSetMeshLibraryBody.Parse("");
            Assert.False(b.HasNodePath);
            Assert.False(b.HasMeshLibraryPath);
        }

        [Fact]
        public void GridMapSetMeshLibraryBody_reads_paths()
        {
            var b = GridMapSetMeshLibraryBody.Parse(
                "{\"node_path\":\"Main/Grid\",\"mesh_library_path\":\"res://tiles/blocks.meshlib\"}");
            Assert.True(b.HasNodePath);
            Assert.True(b.HasMeshLibraryPath);
            Assert.Equal("Main/Grid", b.NodePath);
            Assert.Equal("res://tiles/blocks.meshlib", b.MeshLibraryPath);
        }

        // --- GridMapSetCellBody -------------------------------------------------

        [Fact]
        public void GridMapSetCellBody_empty_body_defaults_to_zero_set()
        {
            var b = GridMapSetCellBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.Equal(0, b.X);
            Assert.Equal(0, b.Y);
            Assert.Equal(0, b.Z);
            Assert.Equal(0, b.Item);
            Assert.Equal(0, b.Orientation);
        }

        [Fact]
        public void GridMapSetCellBody_reads_full_cell_tuple()
        {
            var b = GridMapSetCellBody.Parse(
                "{\"node_path\":\"Grid\",\"x\":5,\"y\":-3,\"z\":2,\"item\":4,\"orientation\":10}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Grid", b.NodePath);
            Assert.Equal(5, b.X);
            Assert.Equal(-3, b.Y);
            Assert.Equal(2, b.Z);
            Assert.Equal(4, b.Item);
            Assert.Equal(10, b.Orientation);
        }

        [Fact]
        public void GridMapSetCellBody_defaults_optional_orientation_to_zero()
        {
            var b = GridMapSetCellBody.Parse(
                "{\"node_path\":\"Grid\",\"x\":1,\"y\":2,\"z\":3,\"item\":1}");
            Assert.Equal(0, b.Orientation);
        }

        [Fact]
        public void GridMapSetCellBody_out_of_range_int_degrades_to_default()
        {
            // A value outside the int range must degrade to the default rather than throwing.
            var b = GridMapSetCellBody.Parse(
                "{\"node_path\":\"G\",\"x\":99999999999999999999,\"y\":0,\"z\":0,\"item\":0}");
            Assert.Equal(0, b.X);
            Assert.Equal(0, b.Y);
        }

        [Fact]
        public void GridMapSetCellBody_unquotes_node_path()
        {
            var b = GridMapSetCellBody.Parse(
                "{\"node_path\":\"Layer \\\"A\\\"\",\"x\":0,\"y\":0,\"z\":0,\"item\":0}");
            Assert.Equal("Layer \"A\"", b.NodePath);
        }

        // --- GridMapEraseCellBody -----------------------------------------------

        [Fact]
        public void GridMapEraseCellBody_empty_body_has_no_node_path()
        {
            var b = GridMapEraseCellBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.Equal(0, b.X);
            Assert.Equal(0, b.Y);
            Assert.Equal(0, b.Z);
        }

        [Fact]
        public void GridMapEraseCellBody_reads_coords()
        {
            var b = GridMapEraseCellBody.Parse(
                "{\"node_path\":\"Grid\",\"x\":-1,\"y\":-2,\"z\":-3}");
            Assert.True(b.HasNodePath);
            Assert.Equal(-1, b.X);
            Assert.Equal(-2, b.Y);
            Assert.Equal(-3, b.Z);
        }

        // --- GridMapGetUsedCellsBody -------------------------------------------

        [Fact]
        public void GridMapGetUsedCellsBody_empty_body_uses_default_max()
        {
            var b = GridMapGetUsedCellsBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.Equal(GridMapGetUsedCellsBody.DefaultMaxResults, b.EffectiveMaxResults);
        }

        [Fact]
        public void GridMapGetUsedCellsBody_clamps_to_hard_max()
        {
            var b = GridMapGetUsedCellsBody.Parse(
                "{\"node_path\":\"G\",\"max_results\":99999}");
            Assert.Equal(GridMapGetUsedCellsBody.HardMaxResults, b.EffectiveMaxResults);
        }

        [Fact]
        public void GridMapGetUsedCellsBody_non_positive_falls_back_to_default()
        {
            var b = GridMapGetUsedCellsBody.Parse(
                "{\"node_path\":\"G\",\"max_results\":0}");
            Assert.Equal(GridMapGetUsedCellsBody.DefaultMaxResults, b.EffectiveMaxResults);
        }

        [Fact]
        public void GridMapGetUsedCellsBody_negative_falls_back_to_default()
        {
            var b = GridMapGetUsedCellsBody.Parse(
                "{\"node_path\":\"G\",\"max_results\":-5}");
            Assert.Equal(GridMapGetUsedCellsBody.DefaultMaxResults, b.EffectiveMaxResults);
        }

        [Fact]
        public void GridMapGetUsedCellsBody_respects_in_range_max()
        {
            var b = GridMapGetUsedCellsBody.Parse(
                "{\"node_path\":\"G\",\"max_results\":100}");
            Assert.Equal(100, b.EffectiveMaxResults);
        }

        // --- GridMapClearBody ---------------------------------------------------

        [Fact]
        public void GridMapClearBody_empty_body_has_no_node_path()
        {
            var b = GridMapClearBody.Parse("");
            Assert.False(b.HasNodePath);
        }

        [Fact]
        public void GridMapClearBody_reads_node_path()
        {
            var b = GridMapClearBody.Parse("{\"node_path\":\"Main/Grid\"}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Main/Grid", b.NodePath);
        }

        // --- GridMapCellData JSON ----------------------------------------------

        [Fact]
        public void GridMapCellData_ToJsonString_produces_canonical_shape_with_fixed_field_order()
        {
            var data = new GridMapCellData
            {
                X = 5,
                Y = -3,
                Z = 2,
                Item = 4,
                Orientation = 10,
            };
            var json = data.ToJsonString();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(5, root.GetProperty("x").GetInt32());
            Assert.Equal(-3, root.GetProperty("y").GetInt32());
            Assert.Equal(2, root.GetProperty("z").GetInt32());
            Assert.Equal(4, root.GetProperty("item").GetInt32());
            Assert.Equal(10, root.GetProperty("orientation").GetInt32());

            // Field order is fixed so a diffing client does not flap on reordering.
            var order = string.Join(',', EnumeratePropertyNames(root));
            Assert.Equal("x,y,z,item,orientation", order);
        }

        [Fact]
        public void GridMapCellData_ToJsonString_defaults_render_zero()
        {
            var data = new GridMapCellData();
            var json = data.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(0, root.GetProperty("x").GetInt32());
            Assert.Equal(0, root.GetProperty("y").GetInt32());
            Assert.Equal(0, root.GetProperty("z").GetInt32());
            Assert.Equal(0, root.GetProperty("item").GetInt32());
            Assert.Equal(0, root.GetProperty("orientation").GetInt32());
        }

        [Fact]
        public void GridMapCellData_ToJsonString_renders_large_negative_coords()
        {
            var data = new GridMapCellData { X = -2000, Y = 2000, Z = -1 };
            var json = data.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(-2000, doc.RootElement.GetProperty("x").GetInt32());
            Assert.Equal(2000, doc.RootElement.GetProperty("y").GetInt32());
            Assert.Equal(-1, doc.RootElement.GetProperty("z").GetInt32());
        }

        static IEnumerable<string> EnumeratePropertyNames(JsonElement obj)
        {
            foreach (var p in obj.EnumerateObject())
                yield return p.Name;
        }
    }
}
