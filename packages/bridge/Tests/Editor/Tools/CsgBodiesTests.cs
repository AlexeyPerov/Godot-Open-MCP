#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P12.5 CSG pack unit tests for the pure-managed, off-editor-testable pieces: the
    /// kind parser, the operation parser (+ its schema-string round-trip), the
    /// centralized clamp table (<see cref="CsgPropertyClamp"/> — mirrors the P12.3
    /// particles pack's design decision §2), and the four request-body parsers. The
    /// editor-only handlers (<see cref="CsgTools.Defaults"/> / BoxCreate / SphereCreate /
    /// CylinderCreate / CombinerCreate / SetOperation / Get) are <c>#if TOOLS</c> and
    /// coupled to <c>EditorInterface.Singleton</c> and the Csg*3D node classes, neither
    /// of which the binary-less xUnit host can construct — those paths are exercised by
    /// the headless Godot smoke / live call path, not here.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <c>ParticlesBodiesTests</c> (copy fidelity
    /// for the parser + clamp-table test shape), with cases specific to the CSG field
    /// set (kind enum, operation enum + round-trip, int/float/bool/string nullable
    /// extraction, the clamp-table ranges). The clamp table is the highest-value unit
    /// test surface in this pack — it pins the allow-list ranges without requiring the
    /// editor. Lives in the same xUnit collection-free zone as the other pure-managed
    /// suites (no HTTP listener, no shared static state), so no <c>[Collection]</c>
    /// attribute is needed.
    /// </para>
    /// </summary>
    public class CsgBodiesTests
    {
        // --- CsgKindParser -----------------------------------------------------
        // CsgKind is internal, so the [Theory] passes the expected token as its
        // underlying int and casts back inside (xUnit's InlineData serializer requires a
        // public-parameter-accessible type, which an internal enum is not).

        [Theory]
        [InlineData("box", 1)]
        [InlineData("sphere", 2)]
        [InlineData("cylinder", 3)]
        [InlineData("combiner", 4)]
        [InlineData("Box", 1)]            // case-insensitive
        [InlineData("SPHERE", 2)]
        [InlineData(" combiner ", 4)]     // tolerates whitespace
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("torus", 0)]          // not in the v1 catalog
        [InlineData("polygon", 0)]
        public void KindParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((CsgKind)expected, CsgKindParser.Parse(raw));
        }

        // --- CsgOperationParser (parse direction) -------------------------------

        [Theory]
        [InlineData("union", 1)]
        [InlineData("intersection", 2)]
        [InlineData("subtraction", 3)]
        [InlineData("Union", 1)]          // case-insensitive
        [InlineData("SUBTRACTION", 3)]
        [InlineData(" union ", 1)]        // tolerates whitespace
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("difference", 0)]     // NOT a valid token (common synonym — must reject)
        [InlineData("subtract", 0)]       // NOT a valid token (must be the full "subtraction")
        public void OperationParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((CsgOperation)expected, CsgOperationParser.Parse(raw));
        }

        // --- CsgOperationParser (schema-string round-trip) ----------------------
        // The read paths (defaults / get) render the operation back as the schema string
        // so an agent can re-feed it into set_operation / create operation. Pin both the
        // known mappings and the Unknown fallback (the engine default Operation is Union,
        // so Unknown renders as "union" — a never-set shape still reports a valid default).
        // CsgOperation is internal, so the [Theory] passes the enum as its underlying int
        // and casts back inside (same xUnit-InlineData workaround the particles tests use).

        [Theory]
        [InlineData(1, "union")]
        [InlineData(2, "intersection")]
        [InlineData(3, "subtraction")]
        [InlineData(0, "union")]   // Unknown fallback — engine default is Union
        public void OperationParser_ToSchemaString_round_trips(int opInt, string expected)
        {
            Assert.Equal(expected, CsgOperationParser.ToSchemaString((CsgOperation)opInt));
        }

        [Fact]
        public void OperationParser_parse_then_render_round_trips_for_known_tokens()
        {
            // The full round-trip: schema string → enum → schema string. A known token
            // must survive the round-trip unchanged.
            foreach (var token in new[] { "union", "intersection", "subtraction" })
            {
                var op = CsgOperationParser.Parse(token);
                Assert.Equal(token, CsgOperationParser.ToSchemaString(op));
            }
        }

        // --- CsgPropertyClamp (the clamp table — highest-value unit surface) ----
        // These pin the allow-list ranges without requiring the editor. The handler
        // echoes the clamped result, so an agent sees what landed.

        [Theory]
        [InlineData(0.5f, 0.5f)]
        [InlineData(1.0f, 1.0f)]
        [InlineData(2.5f, 2.5f)]
        [InlineData(0.0001f, 0.0001f)]   // the floor
        [InlineData(0f, 0.0001f)]         // zero → clamped up to the floor
        [InlineData(-1f, 0.0001f)]        // negative → clamped up to the floor
        public void ClampPositive_is_strictly_positive(float input, float expected)
        {
            Assert.Equal(expected, CsgPropertyClamp.ClampPositive(input));
        }

        [Theory]
        [InlineData(3, 3)]
        [InlineData(8, 8)]
        [InlineData(12, 12)]
        [InlineData(1000, 1000)]          // the ceiling
        [InlineData(1001, 1000)]          // above the ceiling → clamped down
        [InlineData(100000, 1000)]        // a typo that would tank perf → clamped down
        [InlineData(2, 3)]                // below the floor → clamped up to 3
        [InlineData(1, 3)]                // Godot rejects < 3 → clamped up
        [InlineData(0, 3)]
        [InlineData(-5, 3)]
        public void ClampSegmentCount_enforces_three_to_max(int input, int expected)
        {
            Assert.Equal(expected, CsgPropertyClamp.ClampSegmentCount(input));
        }

        // --- CsgDefaultsBody ---------------------------------------------------

        [Fact]
        public void CsgDefaultsBody_empty_body_is_unknown()
        {
            var b = CsgDefaultsBody.Parse(null);
            Assert.Equal(CsgKind.Unknown, b.Kind);
        }

        [Fact]
        public void CsgDefaultsBody_reads_kind()
        {
            var b = CsgDefaultsBody.Parse("{\"kind\":\"box\"}");
            Assert.Equal(CsgKind.Box, b.Kind);
        }

        [Fact]
        public void CsgDefaultsBody_reads_each_valid_kind()
        {
            Assert.Equal(CsgKind.Sphere, CsgDefaultsBody.Parse("{\"kind\":\"sphere\"}").Kind);
            Assert.Equal(CsgKind.Cylinder, CsgDefaultsBody.Parse("{\"kind\":\"cylinder\"}").Kind);
            Assert.Equal(CsgKind.Combiner, CsgDefaultsBody.Parse("{\"kind\":\"combiner\"}").Kind);
        }

        [Fact]
        public void CsgDefaultsBody_unrecognized_kind_is_unknown()
        {
            var b = CsgDefaultsBody.Parse("{\"kind\":\"torus\"}");
            Assert.Equal(CsgKind.Unknown, b.Kind);
        }

        // --- CsgCreateBody (shared by all four create tools) -------------------

        [Fact]
        public void CsgCreateBody_empty_body_returns_defaults()
        {
            var b = CsgCreateBody.Parse(null);
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
            Assert.Equal(CsgOperation.Unknown, b.Operation);
            Assert.False(b.HasOperation);
            Assert.Null(b.Size);
            Assert.Null(b.Radius);
            Assert.Null(b.Height);
            Assert.Null(b.RadialSegments);
            Assert.Null(b.Rings);
            Assert.Null(b.Sides);
            Assert.Null(b.SmoothFaces);
            Assert.Null(b.Cone);
        }

        [Fact]
        public void CsgCreateBody_reads_all_common_fields()
        {
            var b = CsgCreateBody.Parse(
                "{\"name\":\"Body\",\"parent_node_path\":\"Main/Geometry\",\"position\":\"1,2,3\"," +
                "\"operation\":\"subtraction\"}");
            Assert.Equal("Body", b.Name);
            Assert.Equal("Main/Geometry", b.ParentNodePath);
            Assert.Equal("1,2,3", b.Position);
            Assert.Equal(CsgOperation.Subtraction, b.Operation);
            Assert.True(b.HasOperation);
        }

        [Fact]
        public void CsgCreateBody_reads_box_specific_scalars()
        {
            var b = CsgCreateBody.Parse("{\"size\":\"2,3,4\"}");
            Assert.Equal("2,3,4", b.Size);
        }

        [Fact]
        public void CsgCreateBody_reads_sphere_specific_scalars()
        {
            var b = CsgCreateBody.Parse(
                "{\"radius\":1.5,\"radial_segments\":16,\"rings\":8,\"smooth_faces\":false}");
            Assert.Equal(1.5f, b.Radius);
            Assert.Equal(16, b.RadialSegments);
            Assert.Equal(8, b.Rings);
            Assert.False(b.SmoothFaces);
        }

        [Fact]
        public void CsgCreateBody_reads_cylinder_specific_scalars()
        {
            var b = CsgCreateBody.Parse(
                "{\"radius\":0.75,\"height\":3.0,\"sides\":6,\"cone\":true,\"smooth_faces\":false}");
            Assert.Equal(0.75f, b.Radius);
            Assert.Equal(3.0f, b.Height);
            Assert.Equal(6, b.Sides);
            Assert.True(b.Cone);
            Assert.False(b.SmoothFaces);
        }

        [Fact]
        public void CsgCreateBody_treats_explicit_null_as_absent()
        {
            // Mirrors the particles / navigation nullable-extractor contract — a JSON null
            // degrades to null/Unknown so the handler treats it as "leave the engine default".
            var b = CsgCreateBody.Parse(
                "{\"name\":null,\"parent_node_path\":null,\"position\":null,\"operation\":null," +
                "\"size\":null,\"radius\":null,\"height\":null,\"radial_segments\":null," +
                "\"rings\":null,\"sides\":null,\"smooth_faces\":null,\"cone\":null}");
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
            Assert.Equal(CsgOperation.Unknown, b.Operation);
            Assert.False(b.HasOperation);
            Assert.Null(b.Size);
            Assert.Null(b.Radius);
            Assert.Null(b.Height);
            Assert.Null(b.RadialSegments);
            Assert.Null(b.Rings);
            Assert.Null(b.Sides);
            Assert.Null(b.SmoothFaces);
            Assert.Null(b.Cone);
        }

        [Fact]
        public void CsgCreateBody_absent_scalars_leave_null()
        {
            // Only name sent — the rest must stay null/Unknown (the handler treats null as
            // "leave the engine default").
            var b = CsgCreateBody.Parse("{\"name\":\"Solo\"}");
            Assert.Equal("Solo", b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
            Assert.False(b.HasOperation);
            Assert.Null(b.Size);
            Assert.Null(b.Radius);
            Assert.Null(b.Height);
            Assert.Null(b.RadialSegments);
            Assert.Null(b.Rings);
            Assert.Null(b.Sides);
            Assert.Null(b.SmoothFaces);
            Assert.Null(b.Cone);
        }

        [Fact]
        public void CsgCreateBody_non_numeric_scalar_is_null()
        {
            // A present-but-non-numeric value degrades to null (the handler skips it
            // silently, matching node_modify's non-aborting contract).
            var b = CsgCreateBody.Parse(
                "{\"radius\":\"oops\",\"height\":\"tall\",\"sides\":\"many\"}");
            Assert.Null(b.Radius);
            Assert.Null(b.Height);
            Assert.Null(b.Sides);
        }

        [Fact]
        public void CsgCreateBody_out_of_range_scalar_is_read_as_is()
        {
            // The body parser does NOT clamp — it records the raw value. Clamping is the
            // handler's job (so the clamped result can be echoed). Pin the parser's
            // no-clamp contract.
            var b = CsgCreateBody.Parse(
                "{\"radius\":-1.0,\"height\":0.0,\"sides\":2,\"radial_segments\":99999}");
            Assert.Equal(-1.0f, b.Radius);
            Assert.Equal(0.0f, b.Height);
            Assert.Equal(2, b.Sides);
            Assert.Equal(99999, b.RadialSegments);
        }

        [Fact]
        public void CsgCreateBody_bool_literals_parse()
        {
            var b = CsgCreateBody.Parse(
                "{\"smooth_faces\":false,\"cone\":true}");
            Assert.False(b.SmoothFaces);
            Assert.True(b.Cone);
        }

        [Fact]
        public void CsgCreateBody_invalid_bool_is_null()
        {
            // "True" (capitalized) is not a JSON bool literal — degrades to null.
            var b = CsgCreateBody.Parse("{\"smooth_faces\":True,\"cone\":1}");
            Assert.Null(b.SmoothFaces);
            Assert.Null(b.Cone);
        }

        [Fact]
        public void CsgCreateBody_invalid_operation_is_unknown()
        {
            // An unrecognized operation token degrades to Unknown → HasOperation is false →
            // the handler leaves the engine default Operation (Union) untouched.
            var b = CsgCreateBody.Parse("{\"operation\":\"difference\"}");
            Assert.Equal(CsgOperation.Unknown, b.Operation);
            Assert.False(b.HasOperation);
        }

        [Fact]
        public void CsgCreateBody_unquotes_string_fields()
        {
            // The shared JsonScalar extractor unwraps quoted strings, honoring backslash
            // escapes — pin that an embedded quote in name / parent_node_path survives.
            var b = CsgCreateBody.Parse(
                "{\"name\":\"Box \\\"A\\\"\",\"parent_node_path\":\"Main/Group\",\"size\":\"1,2,3\"}");
            Assert.Equal("Box \"A\"", b.Name);
            Assert.Equal("Main/Group", b.ParentNodePath);
            Assert.Equal("1,2,3", b.Size);
        }

        // --- CsgSetOperationBody ----------------------------------------------

        [Fact]
        public void CsgSetOperationBody_empty_body_has_no_node_path_or_operation()
        {
            var b = CsgSetOperationBody.Parse("");
            Assert.False(b.HasNodePath);
            Assert.False(b.HasOperation);
            Assert.Equal(CsgOperation.Unknown, b.Operation);
        }

        [Fact]
        public void CsgSetOperationBody_reads_node_path_and_operation()
        {
            var b = CsgSetOperationBody.Parse(
                "{\"node_path\":\"Main/Geometry/Cutter\",\"operation\":\"subtraction\"}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Main/Geometry/Cutter", b.NodePath);
            Assert.True(b.HasOperation);
            Assert.Equal(CsgOperation.Subtraction, b.Operation);
        }

        [Fact]
        public void CsgSetOperationBody_unrecognized_operation_is_not_set()
        {
            // "difference" is a common synonym but NOT a valid token — the handler must
            // reject it with missing_parameter, not silently coerce.
            var b = CsgSetOperationBody.Parse(
                "{\"node_path\":\"A\",\"operation\":\"difference\"}");
            Assert.True(b.HasNodePath);
            Assert.False(b.HasOperation);
        }

        [Fact]
        public void CsgSetOperationBody_absent_node_path_is_not_set()
        {
            var b = CsgSetOperationBody.Parse("{\"operation\":\"union\"}");
            Assert.False(b.HasNodePath);
            Assert.True(b.HasOperation);
        }

        // --- CsgGetBody --------------------------------------------------------

        [Fact]
        public void CsgGetBody_empty_body_has_no_node_path()
        {
            var b = CsgGetBody.Parse("");
            Assert.False(b.HasNodePath);
        }

        [Fact]
        public void CsgGetBody_reads_node_path()
        {
            var b = CsgGetBody.Parse("{\"node_path\":\"Main/Geometry/Box\"}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Main/Geometry/Box", b.NodePath);
        }
    }
}
