#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P2.3 <c>godot_open_mcp_node_create</c> unit tests for the pure-managed, off-editor-testable
    /// pieces of the tool. The editor-only handler (<see cref="NodeTools.Create"/> /
    /// <see cref="NodeTools.SetOwnerRecursive"/> / <see cref="NodeTools.ApplyTransform"/>) is
    /// <c>#if TOOLS</c> and coupled to <c>EditorInterface.Singleton.GetEditedSceneRoot()</c> and
    /// <c>ClassDB</c>, neither of which the binary-less xUnit host can construct — those paths are
    /// exercised by the headless Godot smoke / live call path, not here. The unit covered here is
    /// the request-body parser, exactly the piece most prone to escaping / default-value bugs.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <see cref="NodeFindBodyTests"/> (copy fidelity for the
    /// parser shape), with cases specific to the node_create field set and its scene-vs-class
    /// precedence. Lives in the same xUnit collection-free zone as the other pure-managed suites
    /// (no HTTP listener, no shared static state), so no <c>[Collection]</c> attribute is needed.
    /// The editor-only create behaviors (Owner assignment, PackedScene instancing, ClassDB
    /// validation) are ported conceptually from Unity Open MCP's
    /// <c>GameObjectsToolsTests.cs</c> create-at-root / create-under-parent cases but cannot run
    /// in this host — they are covered by the P2.11 live smoke once the gate-less P2.3 handler is
    /// reachable end-to-end.
    /// </para>
    /// </summary>
    public class NodeCreateBodyTests
    {
        [Fact]
        public void Parse_empty_body_returns_typed_node_defaults()
        {
            // An empty body must not be a hard error — it creates a plain Node at the scene root,
            // matching Godot-MCP's Tool_Node.Create default (Unity's gameobject_create requires a
            // name; Godot does not, so we diverge and accept an all-default body).
            var b = NodeCreateBody.Parse(null);
            Assert.Null(b.Name);
            Assert.Null(b.TypeClassName);
            Assert.Null(b.InstanceScenePath);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
            Assert.Null(b.Rotation);
            Assert.Null(b.Scale);
            Assert.False(b.IsInstanceScene);
            Assert.Equal("Node", b.EffectiveTypeClassName);
        }

        [Fact]
        public void Parse_reads_typed_mode_fields()
        {
            var b = NodeCreateBody.Parse(
                "{\"name\":\"Enemy\",\"type_class_name\":\"CharacterBody2D\",\"parent_node_path\":\"Main\"}");
            Assert.Equal("Enemy", b.Name);
            Assert.Equal("CharacterBody2D", b.TypeClassName);
            Assert.Equal("CharacterBody2D", b.EffectiveTypeClassName);
            Assert.Equal("Main", b.ParentNodePath);
            Assert.False(b.IsInstanceScene);
        }

        [Fact]
        public void Parse_reads_scene_instancing_mode()
        {
            var b = NodeCreateBody.Parse(
                "{\"name\":\"Pickup\",\"instance_scene_path\":\"res://pickups/coin.tscn\"}");
            Assert.Equal("Pickup", b.Name);
            Assert.Equal("res://pickups/coin.tscn", b.InstanceScenePath);
            Assert.True(b.IsInstanceScene);
        }

        [Fact]
        public void Parse_scene_path_takes_precedence_in_isInstanceScene()
        {
            // When both are supplied, instance_scene_path wins (IsInstanceScene true) — the handler
            // branches on IsInstanceScene, so type_class_name is ignored in that mode. The parser
            // still surfaces type_class_name for diagnostics, but the flag is what drives creation.
            var b = NodeCreateBody.Parse(
                "{\"type_class_name\":\"Node3D\",\"instance_scene_path\":\"res://x.tscn\"}");
            Assert.True(b.IsInstanceScene);
            Assert.Equal("Node3D", b.TypeClassName);
        }

        [Fact]
        public void Parse_effective_type_class_falls_back_to_default()
        {
            // EffectiveTypeClassName is what the editor-only handler instantiates when not instancing
            // a scene. It resolves to "Node" when the caller omitted type_class_name entirely.
            var b = NodeCreateBody.Parse("{\"name\":\"X\"}");
            Assert.Equal("Node", b.EffectiveTypeClassName);
        }

        [Fact]
        public void Parse_reads_transform_fields_as_strings()
        {
            // Transform strings are parsed by the editor-only ApplyTransform (Node3D/Node2D), not
            // here — the body parser only ferries them through verbatim so it stays pure-managed.
            var b = NodeCreateBody.Parse(
                "{\"position\":\"1,2,3\",\"rotation\":\"0,90,0\",\"scale\":\"2,2,2\"}");
            Assert.Equal("1,2,3", b.Position);
            Assert.Equal("0,90,0", b.Rotation);
            Assert.Equal("2,2,2", b.Scale);
        }

        [Fact]
        public void Parse_unquotes_escaped_string_values()
        {
            // A node name with a quote / backslash must not fault the parser — same escape contract
            // as NodeFindBody (\" \\ \/ \n \r \t \uXXXX).
            var b = NodeCreateBody.Parse(
                "{\"name\":\"a\\\"b\\\\c\",\"type_class_name\":\"Node3D\"}");
            Assert.Equal("a\"b\\c", b.Name);
            Assert.Equal("Node3D", b.TypeClassName);
        }

        [Fact]
        public void Parse_unquotes_unicode_escape()
        {
            // \u0041 == 'A'. Proves the \uXXXX branch decodes for create args too.
            var b = NodeCreateBody.Parse("{\"name\":\"\\u0041\\u0042\"}");
            Assert.Equal("AB", b.Name);
        }

        [Fact]
        public void Parse_unquotes_solidus_escape_in_scene_path()
        {
            // \/ is a valid (if unusual) JSON escape for /. A path with an escaped slash must still
            // resolve as a normal res:// path.
            var b = NodeCreateBody.Parse("{\"instance_scene_path\":\"res:\\/\\/x.tscn\"}");
            Assert.Equal("res://x.tscn", b.InstanceScenePath);
        }

        [Fact]
        public void Parse_treats_explicit_null_as_absent()
        {
            // A null value for every field should leave the parsed body at its all-default state —
            // the agent explicitly opted out of each optional field.
            var b = NodeCreateBody.Parse(
                "{\"name\":null,\"type_class_name\":null,\"instance_scene_path\":null," +
                "\"parent_node_path\":null,\"position\":null,\"rotation\":null,\"scale\":null}");
            Assert.Null(b.Name);
            Assert.Null(b.TypeClassName);
            Assert.Null(b.InstanceScenePath);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
            Assert.Null(b.Rotation);
            Assert.Null(b.Scale);
            Assert.False(b.IsInstanceScene);
            Assert.Equal("Node", b.EffectiveTypeClassName);
        }

        [Fact]
        public void Parse_does_not_confuse_key_substring_inside_string_value()
        {
            // A node whose name contains the literal substring '"type_class_name":' must not
            // re-trigger the key finder for the type field. The parser locates each key independently
            // and reads the quoted value to its closing quote.
            var b = NodeCreateBody.Parse(
                "{\"name\":\"a\\\"type_class_name\\\":b\",\"type_class_name\":\"Node2D\"}");
            Assert.Equal("a\"type_class_name\":b", b.Name);
            Assert.Equal("Node2D", b.TypeClassName);
        }

        [Fact]
        public void Parse_handles_surrounding_whitespace_and_braces()
        {
            var b = NodeCreateBody.Parse("  {  \"name\" : \"Player\" , \"type_class_name\" : \"Node3D\" }  ");
            Assert.Equal("Player", b.Name);
            Assert.Equal("Node3D", b.TypeClassName);
        }
    }
}
