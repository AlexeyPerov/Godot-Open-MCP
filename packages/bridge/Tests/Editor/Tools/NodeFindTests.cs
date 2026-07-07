#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P2.2 <c>godot_open_mcp_node_find</c> unit tests for the pure-managed, off-editor-testable
    /// pieces of the tool. The editor-only handler (<see cref="NodeTools.Find"/> /
    /// <see cref="NodeTools.ResolveNode"/> / <see cref="NodeTools.ToNodeData"/>) is
    /// <c>#if TOOLS</c> and coupled to <c>EditorInterface.Singleton.GetEditedSceneRoot()</c>, which
    /// the binary-less xUnit host cannot construct — those paths are exercised by the headless Godot
    /// smoke / live call path, not here. The three units covered here are exactly the ones most
    /// prone to off-by-one / escaping bugs and the ones Godot-MCP's reference unit-tests separately
    /// (path normalization, request parsing, NodeData serialization).
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from Godot-MCP's <c>NodePathNormalizer</c> unit tests and
    /// the implicit shape contract in <c>NodeData.AppendJsonTo</c>; the body-parser cases mirror
    /// the field set Unity Open MCP's <c>gameobject_find</c> exercises via reflection. Lives in the
    /// same xUnit collection-free zone as the other pure-managed suites (no HTTP listener, no shared
    /// static <c>BridgeSession</c> state), so no <c>[Collection]</c> attribute is needed.
    /// </para>
    /// </summary>
    public class NodePathNormalizerTests
    {
        [Theory]
        [InlineData("Main", "Main", ".", "bare root name resolves to root itself")]
        [InlineData("/root/Main", "Main", ".", "/root/<rootName> resolves to the root")]
        [InlineData("Main/Player", "Main", "Player", "root-prefixed path strips the root segment, leaves child")]
        [InlineData("/root/Main/Player", "Main", "Player", "/root/ prefix stripped, then root segment removed")]
        [InlineData("/root/Main/Player/Body", "Main", "Player/Body", "multi-segment path under /root/<rootName>")]
        [InlineData("Player", "Main", "Player", "relative child path (no root prefix) is unchanged")]
        [InlineData("/Player", "Main", "Player", "leading slash without /root/ is stripped")]
        [InlineData("Main/Player/Body", "Main", "Player/Body", "multi-segment path under edited root")]
        [InlineData("/", "Main", "", "bare slash normalizes to empty (treated as root by ResolveNode)")]
        [InlineData("/root/", "Main", "", "bare /root/ normalizes to empty")]
        [InlineData("", "Main", "", "empty input stays empty")]
        [InlineData("  Main/Player  ", "Main", "Player", "surrounding whitespace trimmed")]
        public void Normalize_handles_godot_path_forms(string raw, string rootName, string expected, string reason)
        {
            var actual = NodePathNormalizer.Normalize(raw, rootName);
            Assert.Equal(expected, actual);
            // `reason` is the human-readable intent of each InlineData row — folding it into the
            // assertion context means a failure points at which path form regressed, not just the
            // raw/expected pair.
            Assert.True(actual == expected, $"path form regression: {reason} (raw={raw}, root={rootName})");
        }

        [Fact]
        public void Normalize_accepts_root_name_with_trailing_slash_path()
        {
            // A path that is exactly editedRoot + "/" — degenerate but should not throw; the
            // normalizer strips the root segment and leaves an empty remainder (root-relative
            // nothing), which ResolveNode treats as the root.
            var actual = NodePathNormalizer.Normalize("Main/", "Main");
            Assert.Equal("", actual);
        }
    }

    /// <summary>
    /// Request-body parsing for <c>godot_open_mcp_node_find</c>. Asserts every scalar field round
    /// trips, defaults apply on absence, and the JSON string-escape set
    /// (<c>\"</c>, <c>\\</c>, <c>\n</c>, <c>\t</c>, <c>\/</c>, <c>\uXXXX</c>) is honored so an
    /// agent sending a node name with a quote does not fault the parser.
    /// </summary>
    public class NodeFindBodyTests
    {
        [Fact]
        public void Parse_empty_body_returns_list_mode_defaults()
        {
            var b = NodeFindBody.Parse(null);
            Assert.False(b.IsTargeted);
            Assert.Equal("", b.NodePath);
            Assert.Equal("", b.Name);
            Assert.Null(b.NameContains);
            Assert.Null(b.Type);
            Assert.Equal(0, b.HierarchyDepth);
            Assert.Equal(NodeFindBody.DefaultMaxResults, b.MaxResults);
        }

        [Fact]
        public void Parse_node_path_targets_mode()
        {
            var b = NodeFindBody.Parse("{\"node_path\":\"Main/Player\"}");
            Assert.True(b.IsTargeted);
            Assert.Equal("Main/Player", b.NodePath);
            Assert.Equal("", b.Name);
        }

        [Fact]
        public void Parse_path_falls_back_to_alternate_key()
        {
            // Some clients send "path" instead of "node_path"; the parser accepts both.
            var b = NodeFindBody.Parse("{\"path\":\"Main/Player\"}");
            Assert.Equal("Main/Player", b.NodePath);
            Assert.True(b.IsTargeted);
        }

        [Fact]
        public void Parse_node_path_takes_priority_over_path()
        {
            // When both are present, node_path wins (it is the canonical key).
            var b = NodeFindBody.Parse("{\"node_path\":\"A\",\"path\":\"B\"}");
            Assert.Equal("A", b.NodePath);
        }

        [Fact]
        public void Parse_name_targets_mode()
        {
            var b = NodeFindBody.Parse("{\"name\":\"Player\"}");
            Assert.True(b.IsTargeted);
            Assert.Equal("Player", b.Name);
        }

        [Fact]
        public void Parse_list_filters()
        {
            var b = NodeFindBody.Parse(
                "{\"type\":\"Node3D\",\"name_contains\":\"play\",\"max_results\":5,\"hierarchy_depth\":2}");
            Assert.False(b.IsTargeted);
            Assert.Equal("Node3D", b.Type);
            Assert.Equal("play", b.NameContains);
            Assert.Equal(5, b.MaxResults);
            Assert.Equal(2, b.HierarchyDepth);
        }

        [Fact]
        public void Parse_clamps_negative_hierarchy_depth_to_zero()
        {
            var b = NodeFindBody.Parse("{\"hierarchy_depth\":-3}");
            Assert.Equal(0, b.HierarchyDepth);
        }

        [Fact]
        public void Parse_clamps_zero_max_results_to_one()
        {
            // max_results must be at least 1 so a list scan always returns something; a caller
            // passing 0 is corrected rather than getting an empty page with no signal.
            var b = NodeFindBody.Parse("{\"max_results\":0}");
            Assert.Equal(1, b.MaxResults);
        }

        [Fact]
        public void Parse_negative_max_results_becomes_one()
        {
            var b = NodeFindBody.Parse("{\"max_results\":-10}");
            Assert.Equal(1, b.MaxResults);
        }

        [Fact]
        public void Parse_unquotes_escaped_string_values()
        {
            var b = NodeFindBody.Parse(
                "{\"node_path\":\"Main/\\\"Quoted\\\"\",\"name\":\"a\\\\b\"}");
            Assert.Equal("Main/\"Quoted\"", b.NodePath);
            Assert.Equal("a\\b", b.Name);
        }

        [Fact]
        public void Parse_unquotes_unicode_escape()
        {
            // \u0041 == 'A'. Proves the \uXXXX branch decodes.
            var b = NodeFindBody.Parse("{\"name\":\"\\u0041\\u0042\"}");
            Assert.Equal("AB", b.Name);
        }

        [Fact]
        public void Parse_unquotes_solidus_escape()
        {
            // \/ is a valid (if unusual) JSON escape for /.
            var b = NodeFindBody.Parse("{\"node_path\":\"Main\\/Player\"}");
            Assert.Equal("Main/Player", b.NodePath);
        }

        [Fact]
        public void Parse_treats_explicit_null_as_absent()
        {
            // A null value for a targeted key should NOT trip IsTargeted — the agent explicitly
            // opted out of that resolver.
            var b = NodeFindBody.Parse("{\"node_path\":null,\"name\":null}");
            Assert.False(b.IsTargeted);
            Assert.Equal("", b.NodePath);
            Assert.Equal("", b.Name);
        }

        [Fact]
        public void Parse_unparseable_int_falls_back_to_default()
        {
            var b = NodeFindBody.Parse("{\"max_results\":\"not-a-number\"}");
            // A non-numeric max_results does not fault the parser — it falls back to the default
            // rather than dropping the whole request.
            Assert.Equal(NodeFindBody.DefaultMaxResults, b.MaxResults);
        }

        [Fact]
        public void Parse_handles_surrounding_whitespace_and_braces()
        {
            var b = NodeFindBody.Parse("  {  \"name\" : \"Player\" , \"max_results\" : 7 }  ");
            Assert.Equal("Player", b.Name);
            Assert.Equal(7, b.MaxResults);
        }

        [Fact]
        public void Parse_does_not_confuse_key_substring_inside_string_value()
        {
            // A node whose name contains the literal substring '"name":' must not re-trigger the
            // key finder. The parser locates the first "name" key, then reads the quoted value to
            // its closing quote — an embedded key-like token inside the value is part of the value.
            var b = NodeFindBody.Parse("{\"name\":\"a\\\"name\\\":b\"}");
            Assert.Equal("a\"name\":b", b.Name);
        }
    }

    /// <summary>
    /// NodeData JSON serialization. Asserts the fixed field order, that strings flow through
    /// <see cref="BridgeJson"/> for escaping, that null scriptResourcePath renders as
    /// <c>"null"</c>, and that children recurse correctly. The handler relies on this output being
    /// valid JSON (the dispatcher splices it verbatim into the result envelope), so a serialization
    /// bug would produce <c>bridge_response_unparsable</c> on the MCP side — these tests pin the
    /// contract before that round-trip.
    /// </summary>
    public class NodeDataJsonTests
    {
        [Fact]
        public void ToJsonString_produces_canonical_shape_with_fixed_field_order()
        {
            var data = new NodeData
            {
                InstanceId = 1234UL,
                Name = "Player",
                Path = "/root/Main/Player",
                Type = "Node3D",
                ScriptResourcePath = null,
                ChildCount = 0,
                Children = null,
            };
            var json = data.ToJsonString();

            // Round-trips through System.Text.Json (the test host can use it freely — the bridge
            // itself does not, but the OUTPUT must be consumable by it).
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(1234UL, root.GetProperty("instanceId").GetUInt64());
            Assert.Equal("Player", root.GetProperty("name").GetString());
            Assert.Equal("/root/Main/Player", root.GetProperty("path").GetString());
            Assert.Equal("Node3D", root.GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("scriptResourcePath").ValueKind);
            Assert.Equal(0, root.GetProperty("childCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("children").ValueKind);

            // Field order is fixed: instanceId, name, path, type, scriptResourcePath, childCount,
            // children. A diffing client should not flap on reordering.
            var order = string.Join(',', EnumeratePropertyNames(root));
            Assert.Equal("instanceId,name,path,type,scriptResourcePath,childCount,children", order);
        }

        static IEnumerable<string> EnumeratePropertyNames(JsonElement obj)
        {
            foreach (var p in obj.EnumerateObject())
                yield return p.Name;
        }

        [Fact]
        public void ToJsonString_escapes_special_characters_in_name()
        {
            var data = new NodeData { Name = "a\"b\\c\nd" };
            var json = data.ToJsonString();
            // Must be valid JSON and round-trip back to the original name.
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("a\"b\\c\nd", doc.RootElement.GetProperty("name").GetString());
        }

        [Fact]
        public void ToJsonString_renders_children_recursively()
        {
            var data = new NodeData
            {
                InstanceId = 1UL,
                Name = "Main",
                Path = "/root/Main",
                Type = "Node3D",
                ChildCount = 2,
                Children = new List<NodeData>
                {
                    new NodeData { InstanceId = 2UL, Name = "A", Path = "/root/Main/A", Type = "Node" },
                    new NodeData { InstanceId = 3UL, Name = "B", Path = "/root/Main/B", Type = "Node" },
                },
            };
            var json = data.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            var children = doc.RootElement.GetProperty("children");
            Assert.Equal(JsonValueKind.Array, children.ValueKind);
            var arr = children.EnumerateArray();
            Assert.Collection(arr,
                c => Assert.Equal("A", c.GetProperty("name").GetString()),
                c => Assert.Equal("B", c.GetProperty("name").GetString()));
            // Each child has children:null (no further depth requested).
            foreach (var child in children.EnumerateArray())
                Assert.Equal(JsonValueKind.Null, child.GetProperty("children").ValueKind);
        }

        [Fact]
        public void ToJsonString_renders_script_resource_path_when_set()
        {
            var data = new NodeData { ScriptResourcePath = "res://player.gd" };
            var json = data.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("res://player.gd", doc.RootElement.GetProperty("scriptResourcePath").GetString());
        }

        [Fact]
        public void ToJsonString_renders_empty_children_array_not_null_when_list_is_empty()
        {
            // A node with ChildCount 0 and hierarchy_depth > 0 still produces an empty array — the
            // handler distinguishes "no depth requested" (null) from "depth requested, no children"
            // ([]) so an agent can tell the two apart.
            var data = new NodeData
            {
                ChildCount = 0,
                Children = new List<NodeData>(),
            };
            var json = data.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("children").ValueKind);
            Assert.Empty(doc.RootElement.GetProperty("children").EnumerateArray());
        }
    }
}
