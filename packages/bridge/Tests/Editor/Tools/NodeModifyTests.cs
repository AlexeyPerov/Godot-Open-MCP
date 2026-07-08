#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P2.4 <c>godot_open_mcp_node_modify</c> unit tests for the pure-managed, off-editor-testable
    /// pieces of the tool: the <see cref="NodeModifyBody"/> request parser, including its
    /// string-array (<c>node_paths</c>) and string-map (<c>properties</c>) extractors. The
    /// editor-only handler (<see cref="NodeTools.Modify"/> / <see cref="NodeTools.ApplyProperties"/>
    /// / <see cref="NodeTools.TryParseColor"/>) is <c>#if TOOLS</c> and coupled to
    /// <c>EditorInterface.Singleton.GetEditedSceneRoot()</c> and live <c>Node</c> objects, neither of
    /// which the binary-less xUnit host can construct — those paths are exercised by the headless
    /// Godot smoke / live call path, not here.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <see cref="NodeCreateBodyTests"/> (copy fidelity for the
    /// parser shape), with cases specific to the node_modify field set and its single/batch + flat/
    /// map surfaces. The single-target modify + transform application cases mirror the intent of
    /// Unity Open MCP's <c>GameObjectsToolsTests.cs</c> modify cases, but run against the parser here
    /// (the editor-coupled mutation runs live). The batch-semantics and warnings cases are
    /// greenfield for Godot (Unity's modify is single-target via component paths; Godot's is batch
    /// via the properties map). Lives in the same xUnit collection-free zone as the other
    /// pure-managed suites.
    /// </para>
    /// </summary>
    public class NodeModifyBodyTests
    {
        // --- single-target parsing --------------------------------------------------

        [Fact]
        public void Parse_empty_body_has_no_targets()
        {
            // No targets → the handler will fail with missing_parameter. The parser itself must not
            // throw on an empty body.
            var b = NodeModifyBody.Parse(null);
            Assert.False(b.HasTarget);
            Assert.Empty(b.NodePaths);
            Assert.Empty(b.Properties);
            Assert.Null(b.Position);
            Assert.Null(b.Rotation);
            Assert.Null(b.Scale);
            Assert.Null(b.Name);
        }

        [Fact]
        public void Parse_reads_single_node_path()
        {
            var b = NodeModifyBody.Parse("{\"node_path\":\"Main/Player\"}");
            Assert.Equal("Main/Player", b.NodePath);
            Assert.True(b.HasTarget);
        }

        [Fact]
        public void Parse_reads_transform_convenience_fields()
        {
            var b = NodeModifyBody.Parse(
                "{\"node_path\":\"X\",\"position\":\"1,2,3\",\"rotation\":\"0,90,0\",\"scale\":\"2,2,2\"}");
            Assert.Equal("1,2,3", b.Position);
            Assert.Equal("0,90,0", b.Rotation);
            Assert.Equal("2,2,2", b.Scale);
        }

        [Fact]
        public void Parse_reads_name_field()
        {
            var b = NodeModifyBody.Parse("{\"node_path\":\"X\",\"name\":\"Renamed\"}");
            Assert.Equal("Renamed", b.Name);
        }

        // --- batch target parsing ---------------------------------------------------

        [Fact]
        public void Parse_reads_node_paths_array()
        {
            var b = NodeModifyBody.Parse("{\"node_paths\":[\"Main/A\",\"Main/B\"]}");
            Assert.True(b.HasTarget);
            Assert.Equal(new[] { "Main/A", "Main/B" }, b.NodePaths);
        }

        [Fact]
        public void Parse_node_paths_array_unquotes_escaped_entries()
        {
            // An entry with a quote/backslash must round-trip through the array extractor's escape-aware
            // string slicer, same contract as the scalar string fields.
            var b = NodeModifyBody.Parse("{\"node_paths\":[\"a\\\"b\",\"c\\\\d\"]}");
            Assert.Equal(new[] { "a\"b", "c\\d" }, b.NodePaths);
        }

        [Fact]
        public void Parse_node_paths_empty_array_is_no_target()
        {
            var b = NodeModifyBody.Parse("{\"node_paths\":[]}");
            Assert.False(b.HasTarget);
            Assert.Empty(b.NodePaths);
        }

        [Fact]
        public void Parse_node_paths_null_is_absent()
        {
            var b = NodeModifyBody.Parse("{\"node_paths\":null}");
            Assert.False(b.HasTarget);
            Assert.Empty(b.NodePaths);
        }

        [Fact]
        public void TargetPaths_dedupes_single_and_array_overlap()
        {
            // When the caller sets both node_path and node_paths with an overlapping path, the merged
            // enumeration yields each path exactly once (single first, then array entries). This
            // protects a batch from mutating the same node twice.
            var b = NodeModifyBody.Parse(
                "{\"node_path\":\"Main/A\",\"node_paths\":[\"Main/A\",\"Main/B\"]}");
            var paths = new System.Collections.Generic.List<string>(b.TargetPaths());
            Assert.Equal(new[] { "Main/A", "Main/B" }, paths);
        }

        [Fact]
        public void TargetPaths_skips_empty_array_entries()
        {
            // An empty-string entry in node_paths is dropped — it would resolve to the edited root,
            // which is rarely the agent's intent, and the dedupe guard treats it as absent.
            var b = NodeModifyBody.Parse("{\"node_paths\":[\"Main/A\",\"\",\"Main/B\"]}");
            var paths = new System.Collections.Generic.List<string>(b.TargetPaths());
            Assert.Equal(new[] { "Main/A", "Main/B" }, paths);
        }

        // --- properties map parsing -------------------------------------------------

        [Fact]
        public void Parse_reads_string_properties_map()
        {
            var b = NodeModifyBody.Parse(
                "{\"node_path\":\"X\",\"properties\":{\"visible\":\"false\",\"name\":\"Hidden\"}}");
            Assert.Equal("false", b.Properties["visible"]);
            Assert.Equal("Hidden", b.Properties["name"]);
        }

        [Fact]
        public void Parse_properties_map_preserves_bare_tokens_verbatim()
        {
            // Bare JSON tokens (true / false / numbers) are kept as their raw text so the handler can
            // coerce them (the properties map ferries everything as strings per the schema).
            var b = NodeModifyBody.Parse(
                "{\"node_path\":\"X\",\"properties\":{\"visible\":true,\"opacity\":0.5}}");
            Assert.Equal("true", b.Properties["visible"]);
            Assert.Equal("0.5", b.Properties["opacity"]);
        }

        [Fact]
        public void Parse_properties_map_unquotes_escaped_string_values()
        {
            var b = NodeModifyBody.Parse(
                "{\"node_path\":\"X\",\"properties\":{\"name\":\"a\\\"b\"}}");
            Assert.Equal("a\"b", b.Properties["name"]);
        }

        [Fact]
        public void Parse_properties_map_records_nested_value_as_raw_region()
        {
            // P2.4 does not support nested property values (arrays/resources). The parser captures the
            // raw region so the handler can emit an unsupported_property warning naming the key.
            var b = NodeModifyBody.Parse(
                "{\"node_path\":\"X\",\"properties\":{\"nested\":{\"a\":1}}}");
            Assert.Equal("{\"a\":1}", b.Properties["nested"]);
        }

        [Fact]
        public void Parse_properties_map_empty_object_is_absent()
        {
            var b = NodeModifyBody.Parse("{\"node_path\":\"X\",\"properties\":{}}");
            Assert.Empty(b.Properties);
        }

        [Fact]
        public void Parse_properties_null_is_absent()
        {
            var b = NodeModifyBody.Parse("{\"node_path\":\"X\",\"properties\":null}");
            Assert.Empty(b.Properties);
        }

        [Fact]
        public void Parse_properties_map_brace_inside_string_does_not_close_early()
        {
            // A value string containing a brace must not be mistaken for the close of the object
            // region. The region walker honors string escaping.
            var b = NodeModifyBody.Parse(
                "{\"node_path\":\"X\",\"properties\":{\"label\":\"a}b\",\"visible\":\"true\"}}");
            Assert.Equal("a}b", b.Properties["label"]);
            Assert.Equal("true", b.Properties["visible"]);
        }

        [Fact]
        public void Parse_properties_map_handles_multiple_entries_with_whitespace()
        {
            var b = NodeModifyBody.Parse(
                "{\"node_path\":\"X\",\"properties\":{ \"visible\" : \"false\" , \"name\" : \"Y\" }}");
            Assert.Equal("false", b.Properties["visible"]);
            Assert.Equal("Y", b.Properties["name"]);
        }

        // --- combined / explicit-null cases ----------------------------------------

        [Fact]
        public void Parse_treats_explicit_null_scalars_as_absent()
        {
            var b = NodeModifyBody.Parse(
                "{\"node_path\":null,\"position\":null,\"rotation\":null,\"scale\":null,\"name\":null}");
            Assert.False(b.HasTarget);
            Assert.Null(b.Position);
            Assert.Null(b.Rotation);
            Assert.Null(b.Scale);
            Assert.Null(b.Name);
        }

        [Fact]
        public void Parse_unquotes_solidus_escape_in_node_path()
        {
            // \/ is a valid JSON escape for /. A path with an escaped slash must still read as a normal
            // Godot scene path.
            var b = NodeModifyBody.Parse("{\"node_path\":\"Main\\/Player\"}");
            Assert.Equal("Main/Player", b.NodePath);
        }

        [Fact]
        public void Parse_unquotes_unicode_escape_in_name()
        {
            // \u0041 == 'A'. Proves the \uXXXX branch decodes for modify args too.
            var b = NodeModifyBody.Parse("{\"node_path\":\"X\",\"name\":\"\\u0041\\u0042\"}");
            Assert.Equal("AB", b.Name);
        }

        [Fact]
        public void Parse_does_not_confuse_key_substring_inside_string_value()
        {
            // A node_path whose value contains the literal substring '"position":' must not re-trigger
            // the key finder for the position field.
            var b = NodeModifyBody.Parse(
                "{\"node_path\":\"a\\\"position\\\":b\",\"position\":\"1,2,3\"}");
            Assert.Equal("a\"position\":b", b.NodePath);
            Assert.Equal("1,2,3", b.Position);
        }
    }
}
