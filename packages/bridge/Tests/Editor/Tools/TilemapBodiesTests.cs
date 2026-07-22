#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P12.1 tilemap pack unit tests for the pure-managed, off-editor-testable pieces: the six
    /// request-body parsers and the <see cref="TilemapCellData"/> JSON serializer. The editor-only
    /// handlers (<see cref="TilemapTools.Create"/> / SetTileset / SetCell / …) are <c>#if TOOLS</c>
    /// and coupled to <c>EditorInterface.Singleton</c> and <c>TileMapLayer</c>, neither of which
    /// the binary-less xUnit host can construct — those paths are exercised by the headless Godot
    /// smoke / live call path, not here.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <c>NodeCreateBodyTests</c> / <c>NodeDataJsonTests</c>
    /// (copy fidelity for the parser + DTO test shape), with cases specific to the tilemap field
    /// set (atlas addressing quadruple, max_results clamping, used-cell envelope). Lives in the
    /// same xUnit collection-free zone as the other pure-managed suites (no HTTP listener, no
    /// shared static state), so no <c>[Collection]</c> attribute is needed.
    /// </para>
    /// </summary>
    public class TilemapBodiesTests
    {
        // --- TilemapCreateBody ---------------------------------------------------

        [Fact]
        public void TilemapCreateBody_empty_body_returns_defaults()
        {
            var b = TilemapCreateBody.Parse(null);
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
        }

        [Fact]
        public void TilemapCreateBody_reads_all_fields()
        {
            var b = TilemapCreateBody.Parse(
                "{\"name\":\"Ground\",\"parent_node_path\":\"Main\",\"position\":\"10,20\"}");
            Assert.Equal("Ground", b.Name);
            Assert.Equal("Main", b.ParentNodePath);
            Assert.Equal("10,20", b.Position);
        }

        [Fact]
        public void TilemapCreateBody_unquotes_escaped_string_values()
        {
            var b = TilemapCreateBody.Parse(
                "{\"name\":\"a\\\"b\\\\c\",\"parent_node_path\":\"Main/Player\"}");
            Assert.Equal("a\"b\\c", b.Name);
            Assert.Equal("Main/Player", b.ParentNodePath);
        }

        [Fact]
        public void TilemapCreateBody_unquotes_unicode_escape()
        {
            var b = TilemapCreateBody.Parse("{\"name\":\"\\u0041\\u0042\"}");
            Assert.Equal("AB", b.Name);
        }

        [Fact]
        public void TilemapCreateBody_treats_explicit_null_as_absent()
        {
            var b = TilemapCreateBody.Parse(
                "{\"name\":null,\"parent_node_path\":null,\"position\":null}");
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
        }

        // --- TilemapSetTilesetBody ----------------------------------------------

        [Fact]
        public void TilemapSetTilesetBody_empty_body_has_no_paths()
        {
            var b = TilemapSetTilesetBody.Parse("");
            Assert.False(b.HasNodePath);
            Assert.False(b.HasTilesetPath);
        }

        [Fact]
        public void TilemapSetTilesetBody_reads_paths()
        {
            var b = TilemapSetTilesetBody.Parse(
                "{\"node_path\":\"Main/Ground\",\"tileset_path\":\"res://tiles/dungeon.tres\"}");
            Assert.True(b.HasNodePath);
            Assert.True(b.HasTilesetPath);
            Assert.Equal("Main/Ground", b.NodePath);
            Assert.Equal("res://tiles/dungeon.tres", b.TilesetPath);
        }

        // --- TilemapSetCellBody --------------------------------------------------

        [Fact]
        public void TilemapSetCellBody_empty_body_defaults_to_zero_quadruple()
        {
            var b = TilemapSetCellBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.Equal(0, b.X);
            Assert.Equal(0, b.Y);
            Assert.Equal(0, b.SourceId);
            Assert.Equal(0, b.AtlasX);
            Assert.Equal(0, b.AtlasY);
            Assert.Equal(0, b.AlternativeTile);
        }

        [Fact]
        public void TilemapSetCellBody_reads_full_atlas_quadruple()
        {
            var b = TilemapSetCellBody.Parse(
                "{\"node_path\":\"Ground\",\"x\":5,\"y\":-3,\"source_id\":2," +
                "\"atlas_x\":1,\"atlas_y\":4,\"alternative_tile\":7}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Ground", b.NodePath);
            Assert.Equal(5, b.X);
            Assert.Equal(-3, b.Y);
            Assert.Equal(2, b.SourceId);
            Assert.Equal(1, b.AtlasX);
            Assert.Equal(4, b.AtlasY);
            Assert.Equal(7, b.AlternativeTile);
        }

        [Fact]
        public void TilemapSetCellBody_defaults_optional_atlas_fields_to_zero()
        {
            var b = TilemapSetCellBody.Parse("{\"node_path\":\"Ground\",\"x\":1,\"y\":2}");
            Assert.Equal(0, b.SourceId);
            Assert.Equal(0, b.AtlasX);
            Assert.Equal(0, b.AtlasY);
            Assert.Equal(0, b.AlternativeTile);
        }

        [Fact]
        public void TilemapSetCellBody_out_of_range_int_degrades_to_default()
        {
            // A value outside the int range must degrade to the default rather than throwing.
            var b = TilemapSetCellBody.Parse(
                "{\"node_path\":\"G\",\"x\":99999999999999999999,\"y\":0}");
            Assert.Equal(0, b.X);
            Assert.Equal(0, b.Y);
        }

        [Fact]
        public void TilemapSetCellBody_unquotes_node_path()
        {
            var b = TilemapSetCellBody.Parse(
                "{\"node_path\":\"Layer \\\"A\\\"\",\"x\":0,\"y\":0}");
            Assert.Equal("Layer \"A\"", b.NodePath);
        }

        // --- TilemapEraseCellBody ------------------------------------------------

        [Fact]
        public void TilemapEraseCellBody_empty_body_has_no_node_path()
        {
            var b = TilemapEraseCellBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.Equal(0, b.X);
            Assert.Equal(0, b.Y);
        }

        [Fact]
        public void TilemapEraseCellBody_reads_coords()
        {
            var b = TilemapEraseCellBody.Parse(
                "{\"node_path\":\"Ground\",\"x\":-1,\"y\":-2}");
            Assert.True(b.HasNodePath);
            Assert.Equal(-1, b.X);
            Assert.Equal(-2, b.Y);
        }

        // --- TilemapGetUsedCellsBody --------------------------------------------

        [Fact]
        public void TilemapGetUsedCellsBody_empty_body_uses_default_max()
        {
            var b = TilemapGetUsedCellsBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.Equal(TilemapGetUsedCellsBody.DefaultMaxResults, b.EffectiveMaxResults);
        }

        [Fact]
        public void TilemapGetUsedCellsBody_clamps_to_hard_max()
        {
            var b = TilemapGetUsedCellsBody.Parse(
                "{\"node_path\":\"G\",\"max_results\":99999}");
            Assert.Equal(TilemapGetUsedCellsBody.HardMaxResults, b.EffectiveMaxResults);
        }

        [Fact]
        public void TilemapGetUsedCellsBody_non_positive_falls_back_to_default()
        {
            var b = TilemapGetUsedCellsBody.Parse(
                "{\"node_path\":\"G\",\"max_results\":0}");
            Assert.Equal(TilemapGetUsedCellsBody.DefaultMaxResults, b.EffectiveMaxResults);
        }

        [Fact]
        public void TilemapGetUsedCellsBody_negative_falls_back_to_default()
        {
            var b = TilemapGetUsedCellsBody.Parse(
                "{\"node_path\":\"G\",\"max_results\":-5}");
            Assert.Equal(TilemapGetUsedCellsBody.DefaultMaxResults, b.EffectiveMaxResults);
        }

        [Fact]
        public void TilemapGetUsedCellsBody_respects_in_range_max()
        {
            var b = TilemapGetUsedCellsBody.Parse(
                "{\"node_path\":\"G\",\"max_results\":100}");
            Assert.Equal(100, b.EffectiveMaxResults);
        }

        // --- TilemapClearBody ----------------------------------------------------

        [Fact]
        public void TilemapClearBody_empty_body_has_no_node_path()
        {
            var b = TilemapClearBody.Parse("");
            Assert.False(b.HasNodePath);
        }

        [Fact]
        public void TilemapClearBody_reads_node_path()
        {
            var b = TilemapClearBody.Parse("{\"node_path\":\"Main/Ground\"}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Main/Ground", b.NodePath);
        }

        // --- TilemapCellData JSON ------------------------------------------------

        [Fact]
        public void TilemapCellData_ToJsonString_produces_canonical_shape_with_fixed_field_order()
        {
            var data = new TilemapCellData
            {
                X = 5,
                Y = -3,
                SourceId = 2,
                AtlasX = 1,
                AtlasY = 4,
                AlternativeTile = 7,
            };
            var json = data.ToJsonString();

            // Round-trips through System.Text.Json (the test host can use it freely — the bridge
            // itself does not, but the OUTPUT must be consumable by it).
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(5, root.GetProperty("x").GetInt32());
            Assert.Equal(-3, root.GetProperty("y").GetInt32());
            Assert.Equal(2, root.GetProperty("sourceId").GetInt32());
            Assert.Equal(1, root.GetProperty("atlasX").GetInt32());
            Assert.Equal(4, root.GetProperty("atlasY").GetInt32());
            Assert.Equal(7, root.GetProperty("alternativeTile").GetInt32());

            // Field order is fixed so a diffing client does not flap on reordering.
            var order = string.Join(',', EnumeratePropertyNames(root));
            Assert.Equal("x,y,sourceId,atlasX,atlasY,alternativeTile", order);
        }

        [Fact]
        public void TilemapCellData_ToJsonString_defaults_render_zero()
        {
            var data = new TilemapCellData();
            var json = data.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(0, root.GetProperty("x").GetInt32());
            Assert.Equal(0, root.GetProperty("y").GetInt32());
            Assert.Equal(0, root.GetProperty("sourceId").GetInt32());
            Assert.Equal(0, root.GetProperty("atlasX").GetInt32());
            Assert.Equal(0, root.GetProperty("atlasY").GetInt32());
            Assert.Equal(0, root.GetProperty("alternativeTile").GetInt32());
        }

        [Fact]
        public void TilemapCellData_ToJsonString_renders_large_negative_coords()
        {
            var data = new TilemapCellData { X = -2000, Y = 2000 };
            var json = data.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(-2000, doc.RootElement.GetProperty("x").GetInt32());
            Assert.Equal(2000, doc.RootElement.GetProperty("y").GetInt32());
        }

        static IEnumerable<string> EnumeratePropertyNames(JsonElement obj)
        {
            foreach (var p in obj.EnumerateObject())
                yield return p.Name;
        }
    }
}
