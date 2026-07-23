#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P12.2 navigation pack unit tests for the pure-managed, off-editor-testable pieces: the
    /// dimension parser, the seven request-body parsers, and the JsonScalar float/bool extractors
    /// added in P12.2. The editor-only handlers
    /// (<see cref="NavigationTools.RegionCreate"/> / RegionSetMesh / AgentCreate / AgentConfigure /
    /// LinkCreate / Get) are <c>#if TOOLS</c> and coupled to <c>EditorInterface.Singleton</c> and
    /// the navigation node classes, neither of which the binary-less xUnit host can construct —
    /// those paths are exercised by the headless Godot smoke / live call path, not here.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <c>TilemapBodiesTests</c> (copy fidelity for the
    /// parser test shape), with cases specific to the navigation field set (dimension enum,
    /// float/bool extraction, link start/end strings). Lives in the same xUnit collection-free
    /// zone as the other pure-managed suites (no HTTP listener, no shared static state), so no
    /// <c>[Collection]</c> attribute is needed.
    /// </para>
    /// </summary>
    public class NavigationBodiesTests
    {
        // --- NavigationDimensionParser -------------------------------------------
        // NavDimension is internal, so the [Theory] passes the expected token as its underlying
        // int and casts back inside (xUnit's InlineData serializer requires a public-parameter-
        // accessible type, which an internal enum is not).

        [Theory]
        [InlineData("2d", 2)]
        [InlineData("3d", 3)]
        [InlineData("2D", 2)]
        [InlineData("3D", 3)]
        [InlineData(" 2d ", 2)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("4d", 0)]
        [InlineData("two", 0)]
        public void DimensionParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((NavDimension)expected, NavigationDimensionParser.Parse(raw));
        }

        // --- NavigationDefaultsBody ---------------------------------------------

        [Fact]
        public void NavigationDefaultsBody_empty_body_is_unknown()
        {
            var b = NavigationDefaultsBody.Parse(null);
            Assert.Equal(NavDimension.Unknown, b.Dimension);
        }

        [Fact]
        public void NavigationDefaultsBody_reads_dimension()
        {
            var b = NavigationDefaultsBody.Parse("{\"dimension\":\"3d\"}");
            Assert.Equal(NavDimension.ThreeD, b.Dimension);
        }

        [Fact]
        public void NavigationDefaultsBody_unrecognized_dimension_is_unknown()
        {
            var b = NavigationDefaultsBody.Parse("{\"dimension\":\"4d\"}");
            Assert.Equal(NavDimension.Unknown, b.Dimension);
        }

        // --- NavigationRegionCreateBody -----------------------------------------

        [Fact]
        public void NavigationRegionCreateBody_empty_body_returns_defaults()
        {
            var b = NavigationRegionCreateBody.Parse(null);
            Assert.Equal(NavDimension.Unknown, b.Dimension);
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
        }

        [Fact]
        public void NavigationRegionCreateBody_reads_all_fields()
        {
            var b = NavigationRegionCreateBody.Parse(
                "{\"dimension\":\"2d\",\"name\":\"NavRegion\",\"parent_node_path\":\"Main\",\"position\":\"10,20\"}");
            Assert.Equal(NavDimension.TwoD, b.Dimension);
            Assert.Equal("NavRegion", b.Name);
            Assert.Equal("Main", b.ParentNodePath);
            Assert.Equal("10,20", b.Position);
        }

        [Fact]
        public void NavigationRegionCreateBody_unquotes_escaped_string_values()
        {
            var b = NavigationRegionCreateBody.Parse(
                "{\"dimension\":\"3d\",\"name\":\"a\\\"b\\\\c\",\"parent_node_path\":\"Main/Player\"}");
            Assert.Equal(NavDimension.ThreeD, b.Dimension);
            Assert.Equal("a\"b\\c", b.Name);
            Assert.Equal("Main/Player", b.ParentNodePath);
        }

        [Fact]
        public void NavigationRegionCreateBody_treats_explicit_null_as_absent()
        {
            var b = NavigationRegionCreateBody.Parse(
                "{\"dimension\":null,\"name\":null,\"parent_node_path\":null,\"position\":null}");
            Assert.Equal(NavDimension.Unknown, b.Dimension);
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
        }

        // --- NavigationRegionSetMeshBody ----------------------------------------

        [Fact]
        public void NavigationRegionSetMeshBody_empty_body_has_no_paths()
        {
            var b = NavigationRegionSetMeshBody.Parse("");
            Assert.False(b.HasNodePath);
            Assert.False(b.HasMeshPath);
        }

        [Fact]
        public void NavigationRegionSetMeshBody_reads_paths()
        {
            var b = NavigationRegionSetMeshBody.Parse(
                "{\"node_path\":\"Main/NavRegion\",\"mesh_path\":\"res://nav/level_polygon.tres\"}");
            Assert.True(b.HasNodePath);
            Assert.True(b.HasMeshPath);
            Assert.Equal("Main/NavRegion", b.NodePath);
            Assert.Equal("res://nav/level_polygon.tres", b.MeshPath);
        }

        // --- NavigationAgentCreateBody ------------------------------------------

        [Fact]
        public void NavigationAgentCreateBody_empty_body_returns_defaults()
        {
            var b = NavigationAgentCreateBody.Parse(null);
            Assert.Equal(NavDimension.Unknown, b.Dimension);
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
        }

        [Fact]
        public void NavigationAgentCreateBody_reads_all_fields()
        {
            var b = NavigationAgentCreateBody.Parse(
                "{\"dimension\":\"3d\",\"name\":\"PlayerAgent\",\"parent_node_path\":\"Main/Player\",\"position\":\"0,1,0\"}");
            Assert.Equal(NavDimension.ThreeD, b.Dimension);
            Assert.Equal("PlayerAgent", b.Name);
            Assert.Equal("Main/Player", b.ParentNodePath);
            Assert.Equal("0,1,0", b.Position);
        }

        // --- NavigationAgentConfigureBody ---------------------------------------

        [Fact]
        public void NavigationAgentConfigureBody_empty_body_has_no_scalars()
        {
            var b = NavigationAgentConfigureBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.Null(b.Radius);
            Assert.Null(b.Height);
            Assert.Null(b.MaxSpeed);
            Assert.Null(b.PathDesiredDistance);
            Assert.Null(b.TargetDesiredDistance);
            Assert.Null(b.AvoidanceEnabled);
        }

        [Fact]
        public void NavigationAgentConfigureBody_reads_all_scalars()
        {
            var b = NavigationAgentConfigureBody.Parse(
                "{\"node_path\":\"Main/Player/Agent\",\"radius\":0.5,\"height\":1.8," +
                "\"max_speed\":5.0,\"path_desired_distance\":1,\"target_desired_distance\":1," +
                "\"avoidance_enabled\":true}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Main/Player/Agent", b.NodePath);
            Assert.Equal(0.5f, b.Radius);
            Assert.Equal(1.8f, b.Height);
            Assert.Equal(5.0f, b.MaxSpeed);
            Assert.Equal(1f, b.PathDesiredDistance);
            Assert.Equal(1f, b.TargetDesiredDistance);
            Assert.True(b.AvoidanceEnabled);
        }

        [Fact]
        public void NavigationAgentConfigureBody_absent_scalars_leave_null()
        {
            // Only radius sent — the rest must stay null (the handler treats null as "unchanged").
            var b = NavigationAgentConfigureBody.Parse(
                "{\"node_path\":\"A\",\"radius\":12}");
            Assert.Equal(12f, b.Radius);
            Assert.Null(b.Height);
            Assert.Null(b.MaxSpeed);
            Assert.Null(b.PathDesiredDistance);
            Assert.Null(b.TargetDesiredDistance);
            Assert.Null(b.AvoidanceEnabled);
        }

        [Fact]
        public void NavigationAgentConfigureBody_non_numeric_scalar_is_null()
        {
            // A present-but-non-numeric value degrades to null (the handler skips it silently,
            // matching node_modify's non-aborting contract).
            var b = NavigationAgentConfigureBody.Parse(
                "{\"node_path\":\"A\",\"radius\":\"oops\"}");
            Assert.Null(b.Radius);
        }

        [Fact]
        public void NavigationAgentConfigureBody_negative_max_speed_is_read_as_is()
        {
            // The body parser does NOT clamp — it records the raw value. Clamping is the handler's
            // job (so the clamped result can be echoed). Pin the parser's no-clamp contract.
            var b = NavigationAgentConfigureBody.Parse(
                "{\"node_path\":\"A\",\"max_speed\":-5}");
            Assert.Equal(-5f, b.MaxSpeed);
        }

        [Fact]
        public void NavigationAgentConfigureBody_bool_literals_parse()
        {
            var b = NavigationAgentConfigureBody.Parse(
                "{\"node_path\":\"A\",\"avoidance_enabled\":false}");
            Assert.False(b.AvoidanceEnabled);
        }

        [Fact]
        public void NavigationAgentConfigureBody_invalid_bool_is_null()
        {
            // "True" (capitalized) is not a JSON bool literal — degrades to null.
            var b = NavigationAgentConfigureBody.Parse(
                "{\"node_path\":\"A\",\"avoidance_enabled\":True}");
            Assert.Null(b.AvoidanceEnabled);
        }

        [Fact]
        public void NavigationAgentConfigureBody_unquotes_node_path()
        {
            var b = NavigationAgentConfigureBody.Parse(
                "{\"node_path\":\"Agent \\\"A\\\"\",\"radius\":1}");
            Assert.Equal("Agent \"A\"", b.NodePath);
        }

        // --- NavigationLinkCreateBody -------------------------------------------

        [Fact]
        public void NavigationLinkCreateBody_empty_body_returns_defaults()
        {
            var b = NavigationLinkCreateBody.Parse(null);
            Assert.Equal(NavDimension.Unknown, b.Dimension);
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
            Assert.Null(b.StartPosition);
            Assert.Null(b.EndPosition);
            Assert.Null(b.Bidirectional);
            Assert.False(b.HasStartPosition);
            Assert.False(b.HasEndPosition);
        }

        [Fact]
        public void NavigationLinkCreateBody_reads_all_fields()
        {
            var b = NavigationLinkCreateBody.Parse(
                "{\"dimension\":\"3d\",\"name\":\"JumpPad\",\"parent_node_path\":\"Main\"," +
                "\"position\":\"0,0,0\",\"start_position\":\"1,2,3\",\"end_position\":\"4,5,6\"," +
                "\"bidirectional\":false}");
            Assert.Equal(NavDimension.ThreeD, b.Dimension);
            Assert.Equal("JumpPad", b.Name);
            Assert.Equal("Main", b.ParentNodePath);
            Assert.Equal("0,0,0", b.Position);
            Assert.Equal("1,2,3", b.StartPosition);
            Assert.Equal("4,5,6", b.EndPosition);
            Assert.False(b.Bidirectional);
            Assert.True(b.HasStartPosition);
            Assert.True(b.HasEndPosition);
        }

        [Fact]
        public void NavigationLinkCreateBody_absent_bidirectional_is_null()
        {
            var b = NavigationLinkCreateBody.Parse(
                "{\"dimension\":\"2d\",\"start_position\":\"0,0\",\"end_position\":\"1,1\"}");
            Assert.Null(b.Bidirectional);
        }

        // --- NavigationGetBody ---------------------------------------------------

        [Fact]
        public void NavigationGetBody_empty_body_has_no_node_path()
        {
            var b = NavigationGetBody.Parse("");
            Assert.False(b.HasNodePath);
        }

        [Fact]
        public void NavigationGetBody_reads_node_path()
        {
            var b = NavigationGetBody.Parse("{\"node_path\":\"Main/Player/Agent\"}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Main/Player/Agent", b.NodePath);
        }

        // --- JsonScalar float/bool extractors (P12.2 additions) -----------------

        [Fact]
        public void JsonScalar_ExtractFloat_absent_key_returns_null()
        {
            Assert.Null(JsonScalar.ExtractFloat("{\"x\":1}", "y"));
        }

        [Fact]
        public void JsonScalar_ExtractFloat_null_literal_returns_null()
        {
            Assert.Null(JsonScalar.ExtractFloat("{\"x\":null}", "x"));
        }

        [Fact]
        public void JsonScalar_ExtractFloat_reads_integer_and_decimal()
        {
            Assert.Equal(5f, JsonScalar.ExtractFloat("{\"x\":5}", "x"));
            Assert.Equal(0.5f, JsonScalar.ExtractFloat("{\"x\":0.5}", "x"));
        }

        [Fact]
        public void JsonScalar_ExtractFloat_reads_negative_and_scientific()
        {
            Assert.Equal(-3.5f, JsonScalar.ExtractFloat("{\"x\":-3.5}", "x"));
            Assert.Equal(1e3f, JsonScalar.ExtractFloat("{\"x\":1e3}", "x"));
        }

        [Fact]
        public void JsonScalar_ExtractFloat_non_numeric_returns_null()
        {
            Assert.Null(JsonScalar.ExtractFloat("{\"x\":\"oops\"}", "x"));
            Assert.Null(JsonScalar.ExtractFloat("{\"x\":true}", "x"));
        }

        [Fact]
        public void JsonScalar_ExtractBool_reads_true_and_false()
        {
            Assert.True(JsonScalar.ExtractBool("{\"x\":true}", "x"));
            Assert.False(JsonScalar.ExtractBool("{\"x\":false}", "x"));
        }

        [Fact]
        public void JsonScalar_ExtractBool_absent_and_null_return_null()
        {
            Assert.Null(JsonScalar.ExtractBool("{\"x\":1}", "y"));
            Assert.Null(JsonScalar.ExtractBool("{\"x\":null}", "x"));
        }

        [Fact]
        public void JsonScalar_ExtractBool_capitalized_is_null()
        {
            // JSON bool literals are lowercase; a capitalized token is not a valid bool.
            Assert.Null(JsonScalar.ExtractBool("{\"x\":True}", "x"));
            Assert.Null(JsonScalar.ExtractBool("{\"x\":FALSE}", "x"));
        }

        [Fact]
        public void JsonScalar_ExtractFloat_is_invariant_under_locale()
        {
            // Pin the invariant-culture parse so a comma-decimal locale cannot corrupt the value.
            // (This test is a no-op on invariant hosts but guards a regression on EU-locale CI.)
            Assert.Equal(0.5f, JsonScalar.ExtractFloat("{\"x\":0.5}", "x"));
            // A comma-decimal ("0,5") must NOT parse as 0.5 — it's either rejected or read as 0.
            var commaValue = JsonScalar.ExtractFloat("{\"x\":0,5}", "x");
            Assert.True(commaValue == null || commaValue.Value == 0f);
        }
    }
}
