#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P4.6 unit tests for the pure-managed editor selection pieces: the
    /// <see cref="EditorSelectionSetBody"/> body parser (the <c>select</c> array-of-objects scanner),
    /// the <see cref="EditorSelectionSetBody.NodeRef"/> precedence rules, and the
    /// <see cref="EditorSelectionData"/> DTO serialization.
    ///
    /// <para>
    /// These mirror the P4.4/P4.5 suites: the units covered here are exactly the ones most prone to
    /// off-by-one / escaping / precedence bugs, and they are pure-managed so they run in the
    /// binary-less xUnit host (no Godot editor needed). The <c>#if TOOLS</c> handlers
    /// (<see cref="EditorSelectionTools.GetSelection"/>/<see cref="EditorSelectionTools.SetSelection"/>)
    /// and their <c>EditorInterface</c>/<c>EditorSelection</c> interactions are exercised by the
    /// headless Godot smoke and the gate integration tests
    /// (<see cref="EditorSelectionGateTests"/>), not here.
    /// </para>
    /// </summary>
    public class EditorSelectionSetBodyTests
    {
        // --- select array parsing --------------------------------------------------

        [Fact]
        public void Parse_EmptyBody_ReturnsEmptySelect()
        {
            var body = EditorSelectionSetBody.Parse(null);
            Assert.Empty(body.Select);
            Assert.False(body.HasSelectField);
        }

        [Fact]
        public void Parse_EmptySelectArray_HasSelectFieldTrue()
        {
            var body = EditorSelectionSetBody.Parse("{\"select\":[]}");
            Assert.Empty(body.Select);
            Assert.True(body.HasSelectField);
        }

        [Fact]
        public void Parse_NodePathSnakeCase_RoundTrips()
        {
            var body = EditorSelectionSetBody.Parse("{\"select\":[{\"node_path\":\"Main/Player\"}]}");
            Assert.Single(body.Select);
            Assert.Equal("Main/Player", body.Select[0].NodePath);
            Assert.True(body.Select[0].HasNodePath);
            Assert.False(body.Select[0].HasInstanceId);
            Assert.False(body.Select[0].IsEmpty);
        }

        [Fact]
        public void Parse_NodePathCamelCase_RoundTrips()
        {
            var body = EditorSelectionSetBody.Parse("{\"select\":[{\"nodePath\":\"Main/Player\"}]}");
            Assert.Single(body.Select);
            Assert.Equal("Main/Player", body.Select[0].NodePath);
        }

        [Fact]
        public void Parse_InstanceIdSnakeCase_RoundTrips()
        {
            var body = EditorSelectionSetBody.Parse("{\"select\":[{\"instance_id\":12345}]}");
            Assert.Single(body.Select);
            Assert.Equal(12345UL, body.Select[0].InstanceId);
            Assert.True(body.Select[0].HasInstanceId);
            Assert.False(body.Select[0].HasNodePath);
            Assert.False(body.Select[0].IsEmpty);
        }

        [Fact]
        public void Parse_InstanceIdCamelCase_RoundTrips()
        {
            var body = EditorSelectionSetBody.Parse("{\"select\":[{\"instanceId\":99}]}");
            Assert.Single(body.Select);
            Assert.Equal(99UL, body.Select[0].InstanceId);
        }

        [Fact]
        public void Parse_BothFields_BothPopulated()
        {
            // Both fields parse; precedence (instance_id wins) is the handler's job, not the parser's.
            var body = EditorSelectionSetBody.Parse(
                "{\"select\":[{\"node_path\":\"Main/Player\",\"instance_id\":42}]}");
            Assert.Single(body.Select);
            Assert.Equal("Main/Player", body.Select[0].NodePath);
            Assert.Equal(42UL, body.Select[0].InstanceId);
        }

        [Fact]
        public void Parse_MultipleRefs_PreserveOrder()
        {
            var body = EditorSelectionSetBody.Parse(
                "{\"select\":[" +
                "{\"node_path\":\"A\"}," +
                "{\"instance_id\":2}," +
                "{\"node_path\":\"C\"}" +
                "]}");
            Assert.Equal(3, body.Select.Count);
            Assert.Equal("A", body.Select[0].NodePath);
            Assert.Equal(2UL, body.Select[1].InstanceId);
            Assert.Equal("C", body.Select[2].NodePath);
        }

        [Fact]
        public void Parse_NullValues_TreatedAsAbsent()
        {
            var body = EditorSelectionSetBody.Parse(
                "{\"select\":[{\"node_path\":null,\"instance_id\":null}]}");
            Assert.Single(body.Select);
            Assert.True(body.Select[0].IsEmpty);
        }

        [Fact]
        public void Parse_EmptyRefObject_IsEmpty()
        {
            var body = EditorSelectionSetBody.Parse("{\"select\":[{}]}");
            Assert.Single(body.Select);
            Assert.True(body.Select[0].IsEmpty);
        }

        [Fact]
        public void Parse_EscapedNodePath_Unescaped()
        {
            // A node path with an escaped quote (contrived but pins the escape decoder).
            var body = EditorSelectionSetBody.Parse("{\"select\":[{\"node_path\":\"a\\\"b\"}]}");
            Assert.Equal("a\"b", body.Select[0].NodePath);
        }

        [Fact]
        public void Parse_WhitespaceBetweenElements_Tolerated()
        {
            var body = EditorSelectionSetBody.Parse(
                "{ \"select\" : [ { \"node_path\" : \"A\" } , { \"instance_id\" : 7 } ] }");
            Assert.Equal(2, body.Select.Count);
            Assert.Equal("A", body.Select[0].NodePath);
            Assert.Equal(7UL, body.Select[1].InstanceId);
        }

        [Fact]
        public void Parse_MissingSelectKey_ReturnsEmpty()
        {
            var body = EditorSelectionSetBody.Parse("{\"paths_hint\":[\"res://main.tscn\"]}");
            Assert.Empty(body.Select);
            Assert.False(body.HasSelectField);
        }

        [Fact]
        public void Parse_NonArraySelect_ReturnsEmpty()
        {
            // A non-array select value yields an empty list (the handler treats empty as clear, but
            // HasSelectField will be true — an agent who passed a wrong-typed field sees a clear rather
            // than a parse fault).
            var body = EditorSelectionSetBody.Parse("{\"select\":\"not an array\"}");
            Assert.Empty(body.Select);
        }

        [Fact]
        public void NodeRef_ToString_DescribesFields()
        {
            var withId = new EditorSelectionSetBody.NodeRef { InstanceId = 42 };
            Assert.Contains("42", withId.ToString());
            var withPath = new EditorSelectionSetBody.NodeRef { NodePath = "Main" };
            Assert.Contains("Main", withPath.ToString());
            var empty = new EditorSelectionSetBody.NodeRef();
            Assert.Contains("empty", empty.ToString());
        }
    }

    /// <summary>
    /// DTO serialization tests for <see cref="EditorSelectionData"/> — pins the fixed field order
    /// (nodes, activeNode, count, scenePath, [cleared]) and the Godot node-only shape.
    /// </summary>
    public class EditorSelectionDataTests
    {
        [Fact]
        public void ToJsonString_EmptySelection_ProducesExpectedShape()
        {
            var data = new EditorSelectionData
            {
                Count = 0,
                ScenePath = "res://main.tscn",
            };
            var json = data.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(0, root.GetProperty("count").GetInt32());
            Assert.Equal("res://main.tscn", root.GetProperty("scenePath").GetString());
            Assert.True(root.GetProperty("activeNode").ValueKind == JsonValueKind.Null);
            Assert.Equal(0, root.GetProperty("nodes").GetArrayLength());
            // cleared is omitted when null.
            Assert.False(root.TryGetProperty("cleared", out _));
        }

        [Fact]
        public void ToJsonString_WithNodes_SerializesNodeDataArray()
        {
            var data = new EditorSelectionData
            {
                Count = 1,
                ScenePath = null,
                Nodes = new List<NodeData>
                {
                    new NodeData { InstanceId = 1, Name = "Player", Path = "/root/Main/Player", Type = "Node3D", ChildCount = 0 },
                },
            };
            data.ActiveNode = data.Nodes[0];
            var json = data.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            var nodes = doc.RootElement.GetProperty("nodes");
            Assert.Equal(1, nodes.GetArrayLength());
            var first = nodes[0];
            Assert.Equal("Player", first.GetProperty("name").GetString());
            Assert.Equal("Node3D", first.GetProperty("type").GetString());
            var active = doc.RootElement.GetProperty("activeNode");
            Assert.Equal("Player", active.GetProperty("name").GetString());
        }

        [Fact]
        public void ToJsonString_ClearedTrue_EmitsClearedField()
        {
            var data = new EditorSelectionData { Cleared = true, Count = 0 };
            var json = data.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("cleared").GetBoolean());
        }

        [Fact]
        public void ToJsonString_DoesNotExposeUnityOnlyFields()
        {
            // Guards against an accidental copy-paste from Unity's selection DTO.
            var data = new EditorSelectionData();
            var json = data.ToJsonString();
            Assert.DoesNotContain("assetGuid", json);
            Assert.DoesNotContain("component", json);
            Assert.DoesNotContain("instanceId", json); // selection-level instanceId; NodeData has its own
            Assert.DoesNotContain("globalObjectId", json);
        }

        [Fact]
        public void ToJsonString_FieldOrderIsStable()
        {
            // Pin the fixed field order so diffing clients don't flap on reordering.
            var data = new EditorSelectionData
            {
                Count = 2,
                ScenePath = "res://main.tscn",
                Cleared = true,
                Nodes = new List<NodeData>
                {
                    new NodeData { InstanceId = 1, Name = "A", Path = "/root/Main/A", Type = "Node", ChildCount = 0 },
                    new NodeData { InstanceId = 2, Name = "B", Path = "/root/Main/B", Type = "Node", ChildCount = 0 },
                },
            };
            var json = data.ToJsonString();
            int nodesIdx = json.IndexOf("\"nodes\"");
            int activeIdx = json.IndexOf("\"activeNode\"");
            int countIdx = json.IndexOf("\"count\"");
            int sceneIdx = json.IndexOf("\"scenePath\"");
            int clearedIdx = json.IndexOf("\"cleared\"");
            Assert.True(nodesIdx < activeIdx, "nodes must precede activeNode");
            Assert.True(activeIdx < countIdx, "activeNode must precede count");
            Assert.True(countIdx < sceneIdx, "count must precede scenePath");
            Assert.True(sceneIdx < clearedIdx, "scenePath must precede cleared");
        }
    }
}
