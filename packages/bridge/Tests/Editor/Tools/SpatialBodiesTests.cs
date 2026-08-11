#nullable enable
using System.Collections.Generic;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P16.6 spatial-query pack unit tests for the pure-managed, off-editor-
    /// testable pieces: the query-type / dimension / shape parsers, the shape↔
    /// dimension compatibility table, the request-body parser (every field, the
    /// bool/int defaults, the mask uint extractor, the exclude string-array
    /// extractor). The editor-only handler (<see cref="SpatialTools.Query"/>) is
    /// <c>#if TOOLS</c> and coupled to <c>EditorInterface.Singleton</c> and the
    /// PhysicsDirectSpaceState2D/3D classes, neither of which the binary-less xUnit
    /// host can construct — those paths are exercised by the headless Godot smoke
    /// / live call path, not here.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <c>ParticlesBodiesTests</c> (copy
    /// fidelity for the parser + extractor test shape), with cases specific to the
    /// spatial pack's enum surface (query_type / dimension / shape) and the mask /
    /// exclude extractors. Lives in the same xUnit collection-free zone as the
    /// other pure-managed suites (no HTTP listener, no shared static state), so no
    /// <c>[Collection]</c> attribute is needed.
    /// </para>
    /// </summary>
    public class SpatialBodiesTests
    {
        // --- SpatialQueryTypeParser -------------------------------------------
        // SpatialQueryType is internal, so the [Theory] passes the expected token
        // as its underlying int and casts back inside (xUnit's InlineData
        // serializer requires a public-parameter-accessible type).

        [Theory]
        [InlineData("ray", 1)]
        [InlineData("shape", 2)]
        [InlineData("point", 3)]
        [InlineData("Ray", 1)]
        [InlineData(" POINT ", 3)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("sweep", 0)]
        public void QueryTypeParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((SpatialQueryType)expected, SpatialQueryTypeParser.Parse(raw));
        }

        // --- SpatialDimensionParser -------------------------------------------

        [Theory]
        [InlineData("2d", 2)]
        [InlineData("3d", 3)]
        [InlineData("2D", 2)]
        [InlineData(" 3d ", 3)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("4d", 0)]
        public void DimensionParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((SpatialDimension)expected, SpatialDimensionParser.Parse(raw));
        }

        // --- SpatialShapeParser -----------------------------------------------

        [Theory]
        [InlineData("circle", 1)]
        [InlineData("sphere", 2)]
        [InlineData("rectangle", 3)]
        [InlineData("box", 4)]
        [InlineData("capsule", 5)]
        [InlineData("Box", 4)]
        [InlineData(" capsule ", 5)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("cylinder", 0)]
        public void ShapeParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((SpatialShape)expected, SpatialShapeParser.Parse(raw));
        }

        [Theory]
        // SpatialShape is internal, so the [Theory] passes the expected token as its
        // underlying int and casts back inside (xUnit's InlineData serializer requires a
        // public-parameter-accessible type, which an internal enum is not).
        [InlineData(1, "circle")]
        [InlineData(2, "sphere")]
        [InlineData(3, "rectangle")]
        [InlineData(4, "box")]
        [InlineData(5, "capsule")]
        [InlineData(0, "unknown")]
        public void ShapeParser_round_trips_to_schema_string(int shape, string expected)
        {
            Assert.Equal(expected, SpatialShapeParser.ToSchemaString((SpatialShape)shape));
        }

        // --- SpatialShapeCompat (shape↔dimension table) ----------------------
        // The handler rejects a mismatched shape+dimension with invalid_parameter;
        // this pins the table the handler consults. SpatialShape / SpatialDimension
        // are internal, so the [Theory] passes both as their underlying ints.

        [Theory]
        // 2D-only shapes: circle(1) / rectangle(3)
        [InlineData(1, 2, true)]
        [InlineData(1, 3, false)]
        [InlineData(3, 2, true)]
        [InlineData(3, 3, false)]
        // 3D-only shapes: sphere(2) / box(4)
        [InlineData(2, 3, true)]
        [InlineData(2, 2, false)]
        [InlineData(4, 3, true)]
        [InlineData(4, 2, false)]
        // both dimensions: capsule(5)
        [InlineData(5, 2, true)]
        [InlineData(5, 3, true)]
        // unknown shape (0) is never compatible
        [InlineData(0, 2, false)]
        [InlineData(0, 3, false)]
        public void ShapeCompat_pins_the_shape_dimension_table(int shape, int dim, bool expected)
        {
            Assert.Equal(expected,
                SpatialShapeCompat.IsCompatible((SpatialShape)shape, (SpatialDimension)dim));
        }

        // --- SpatialQueryBody — empty + defaults -----------------------------

        [Fact]
        public void SpatialQueryBody_empty_body_is_unknown_with_defaults()
        {
            var b = SpatialQueryBody.Parse(null);
            Assert.Equal(SpatialQueryType.Unknown, b.QueryType);
            Assert.Equal(SpatialDimension.Unknown, b.Dimension);
            Assert.Equal(SpatialShape.Unknown, b.Shape);
            Assert.Empty(b.Exclude);
            Assert.Null(b.Mask);
            // bool defaults (a present key overrides; absent keeps the default).
            Assert.True(b.CollideWithBodies);
            Assert.False(b.CollideWithAreas);
            Assert.Equal(32, b.MaxResults);
        }

        [Fact]
        public void SpatialQueryBody_reads_all_fields()
        {
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"shape\",\"dimension\":\"3d\",\"shape\":\"sphere\"," +
                "\"from\":\"0,0,0\",\"to\":\"1,1,1\",\"position\":\"5,6,7\",\"size\":\"2,2,2\"," +
                "\"radius\":1.5,\"height\":4.0,\"rotation\":\"0,90,0\",\"mask\":6," +
                "\"exclude\":[\"Root/Wall\",\"Root/Floor\"]," +
                "\"collide_with_bodies\":false,\"collide_with_areas\":true,\"max_results\":8}");
            Assert.Equal(SpatialQueryType.Shape, b.QueryType);
            Assert.Equal(SpatialDimension.ThreeD, b.Dimension);
            Assert.Equal(SpatialShape.Sphere, b.Shape);
            Assert.Equal("0,0,0", b.From);
            Assert.Equal("1,1,1", b.To);
            Assert.Equal("5,6,7", b.Position);
            Assert.Equal("2,2,2", b.Size);
            Assert.Equal(1.5f, b.Radius);
            Assert.Equal(4.0f, b.Height);
            Assert.Equal("0,90,0", b.Rotation);
            Assert.Equal(6u, b.Mask);
            Assert.Equal(new List<string> { "Root/Wall", "Root/Floor" }, b.Exclude);
            Assert.False(b.CollideWithBodies);
            Assert.True(b.CollideWithAreas);
            Assert.Equal(8, b.MaxResults);
        }

        [Fact]
        public void SpatialQueryBody_query_type_shape_value_does_not_shadow_the_shape_key()
        {
            // Regression: the query_type enum value "shape" is the same string as the
            // "shape" field name. A naive first-occurrence key scan would resolve the
            // shape field to the value that follows the value token "shape" (here
            // "3d" / dimension). The key-aware lookup must read shape = "sphere".
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"shape\",\"dimension\":\"3d\",\"shape\":\"sphere\",\"position\":\"0,0,0\",\"radius\":1}");
            Assert.Equal(SpatialQueryType.Shape, b.QueryType);
            Assert.Equal(SpatialDimension.ThreeD, b.Dimension);
            Assert.Equal(SpatialShape.Sphere, b.Shape);
            Assert.Equal("0,0,0", b.Position);
            Assert.Equal(1f, b.Radius);
        }

        [Fact]
        public void SpatialQueryBody_treats_explicit_null_as_absent()
        {
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":null,\"dimension\":null,\"shape\":null,\"from\":null,\"to\":null," +
                "\"position\":null,\"size\":null,\"radius\":null,\"height\":null,\"rotation\":null," +
                "\"mask\":null,\"exclude\":null,\"collide_with_bodies\":null,\"collide_with_areas\":null," +
                "\"max_results\":null}");
            Assert.Equal(SpatialQueryType.Unknown, b.QueryType);
            Assert.Equal(SpatialDimension.Unknown, b.Dimension);
            Assert.Equal(SpatialShape.Unknown, b.Shape);
            Assert.False(b.HasFrom);
            Assert.False(b.HasTo);
            Assert.False(b.HasPosition);
            Assert.Null(b.Radius);
            Assert.Null(b.Height);
            Assert.Null(b.Size);
            Assert.Null(b.Rotation);
            Assert.Null(b.Mask);
            Assert.Empty(b.Exclude);
            // null bools keep the defaults.
            Assert.True(b.CollideWithBodies);
            Assert.False(b.CollideWithAreas);
            // null max_results keeps the default (not 0).
            Assert.Equal(32, b.MaxResults);
        }

        // --- bool + max_results overrides ------------------------------------

        [Fact]
        public void SpatialQueryBody_present_bool_overrides_default()
        {
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"ray\",\"dimension\":\"2d\",\"collide_with_bodies\":false," +
                "\"collide_with_areas\":true}");
            Assert.False(b.CollideWithBodies);
            Assert.True(b.CollideWithAreas);
        }

        [Fact]
        public void SpatialQueryBody_present_but_invalid_bool_keeps_default()
        {
            // "1" / "True" are not JSON bool literals — degrade to null → default kept.
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"ray\",\"dimension\":\"2d\",\"collide_with_bodies\":1," +
                "\"collide_with_areas\":True}");
            Assert.True(b.CollideWithBodies);
            Assert.False(b.CollideWithAreas);
        }

        [Fact]
        public void SpatialQueryBody_max_results_below_one_keeps_default()
        {
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"ray\",\"dimension\":\"2d\",\"max_results\":0}");
            Assert.Equal(32, b.MaxResults);
        }

        [Fact]
        public void SpatialQueryBody_max_results_negative_keeps_default()
        {
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"ray\",\"dimension\":\"2d\",\"max_results\":-5}");
            Assert.Equal(32, b.MaxResults);
        }

        // --- mask uint extractor ---------------------------------------------

        [Fact]
        public void SpatialQueryBody_mask_absent_is_null()
        {
            var b = SpatialQueryBody.Parse("{\"query_type\":\"ray\",\"dimension\":\"2d\"}");
            Assert.Null(b.Mask);
        }

        [Fact]
        public void SpatialQueryBody_mask_full_32bit_value_parses()
        {
            // 0xFFFFFFFF overflows int — must still parse as uint.
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"ray\",\"dimension\":\"2d\",\"mask\":4294967295}");
            Assert.Equal(4294967295u, b.Mask);
        }

        [Fact]
        public void SpatialQueryBody_mask_zero_parses()
        {
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"ray\",\"dimension\":\"2d\",\"mask\":0}");
            Assert.Equal(0u, b.Mask);
        }

        [Fact]
        public void SpatialQueryBody_mask_negative_is_null()
        {
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"ray\",\"dimension\":\"2d\",\"mask\":-1}");
            Assert.Null(b.Mask);
        }

        [Fact]
        public void SpatialQueryBody_mask_out_of_range_is_null()
        {
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"ray\",\"dimension\":\"2d\",\"mask\":4294967296}");
            Assert.Null(b.Mask);
        }

        [Fact]
        public void SpatialQueryBody_mask_non_numeric_is_null()
        {
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"ray\",\"dimension\":\"2d\",\"mask\":\"layers\"}");
            Assert.Null(b.Mask);
        }

        // --- exclude string-array extractor ----------------------------------

        [Fact]
        public void SpatialQueryBody_exclude_absent_is_empty()
        {
            var b = SpatialQueryBody.Parse("{\"query_type\":\"ray\",\"dimension\":\"2d\"}");
            Assert.Empty(b.Exclude);
        }

        [Fact]
        public void SpatialQueryBody_exclude_reads_quoted_elements()
        {
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"ray\",\"dimension\":\"2d\",\"exclude\":[\"A\",\"B/C\"]}");
            Assert.Equal(new List<string> { "A", "B/C" }, b.Exclude);
        }

        [Fact]
        public void SpatialQueryBody_exclude_empty_array_is_empty()
        {
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"ray\",\"dimension\":\"2d\",\"exclude\":[]}");
            Assert.Empty(b.Exclude);
        }

        [Fact]
        public void SpatialQueryBody_exclude_skips_non_string_elements()
        {
            // A non-string element is a contract violation; it is skipped rather
            // than failing the whole query (exclude is advisory).
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"ray\",\"dimension\":\"2d\",\"exclude\":[\"A\",42,\"B\"]}");
            Assert.Equal(new List<string> { "A", "B" }, b.Exclude);
        }

        [Fact]
        public void SpatialQueryBody_exclude_honors_escape_sequences()
        {
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"ray\",\"dimension\":\"2d\",\"exclude\":[\"A\\\\B\",\"C\\\"D\"]}");
            Assert.Equal(new List<string> { "A\\B", "C\"D" }, b.Exclude);
        }

        [Fact]
        public void SpatialQueryBody_exclude_non_array_is_empty()
        {
            // A non-array exclude value is treated as absent (empty list).
            var b = SpatialQueryBody.Parse(
                "{\"query_type\":\"ray\",\"dimension\":\"2d\",\"exclude\":\"Root/Wall\"}");
            Assert.Empty(b.Exclude);
        }
    }
}
