#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P2.7 <c>godot_open_mcp_scene_get_data</c> / <c>scene_create</c> unit tests for the
    /// pure-managed, off-editor-testable pieces of the scene data/create tool family. The editor-only
    /// handlers (<see cref="SceneTools.GetData"/> / <see cref="SceneTools.Create"/>) are
    /// <c>#if TOOLS</c> and coupled to <c>EditorInterface.Singleton</c>, <c>ClassDB</c>, and
    /// <c>ResourceSaver</c>, none of which the binary-less xUnit host can construct — those paths are
    /// exercised by the headless Godot smoke / live call path, not here. The units covered here are the
    /// request-body parsers and the <see cref="SceneGetDataBody.EffectiveDepth"/> /
    /// <see cref="SceneCreateBody.EffectiveRootType"/> resolvers, exactly the pieces most prone to
    /// default-value / clamping / escaping bugs.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <see cref="SceneOpenBodyTests"/> (copy fidelity for the
    /// parser shape) and <see cref="NodeCreateBodyTests"/> (copy fidelity for the effective-field
    /// resolver pattern), with cases specific to the scene-get-data depth-clamp contract and the
    /// scene-create root-type / overwrite / open field set. Lives in the same xUnit collection-free
    /// zone as the other pure-managed suites. The editor-only behaviors (live hierarchy walk, PackedScene
    /// pack + ResourceSaver.Save, root-name derivation) are ported conceptually from Unity Open MCP's
    /// <c>ScenesToolsTests.cs</c> but cannot run in this host — they are covered by the live smoke once
    /// the gate-less P2.7 handlers are reachable end-to-end.
    /// </para>
    /// </summary>
    public class SceneGetDataBodyTests
    {
        [Fact]
        public void Parse_empty_body_returns_defaults()
        {
            var b = SceneGetDataBody.Parse(null);
            Assert.Null(b.Path);
            Assert.Equal(SceneGetDataBody.DefaultHierarchyDepth, b.HierarchyDepth);
        }

        [Fact]
        public void Parse_reads_path()
        {
            var b = SceneGetDataBody.Parse("{\"path\":\"res://levels/level_1.tscn\"}");
            Assert.Equal("res://levels/level_1.tscn", b.Path);
            Assert.Equal(SceneGetDataBody.DefaultHierarchyDepth, b.HierarchyDepth);
        }

        [Fact]
        public void Parse_reads_hierarchy_depth_zero()
        {
            var b = SceneGetDataBody.Parse("{\"hierarchy_depth\":0}");
            Assert.Equal(0, b.HierarchyDepth);
        }

        [Fact]
        public void Parse_reads_hierarchy_depth_positive()
        {
            var b = SceneGetDataBody.Parse("{\"hierarchy_depth\":3}");
            Assert.Equal(3, b.HierarchyDepth);
        }

        [Fact]
        public void Parse_negative_one_means_unlimited()
        {
            // -1 is the documented "walk the whole tree" sentinel. Preserved verbatim (not clamped to
            // the max) so EffectiveDepth can translate it to int.MaxValue.
            var b = SceneGetDataBody.Parse("{\"hierarchy_depth\":-1}");
            Assert.Equal(-1, b.HierarchyDepth);
            Assert.Equal(int.MaxValue, b.EffectiveDepth);
        }

        [Fact]
        public void Parse_clamps_positive_depth_to_max()
        {
            // Deep trees blow the response token budget; positive depths above MaxHierarchyDepth are
            // clamped down. -1 bypasses the clamp (unlimited), per the schema's documented contract.
            var b = SceneGetDataBody.Parse("{\"hierarchy_depth\":50}");
            Assert.Equal(SceneGetDataBody.MaxHierarchyDepth, b.HierarchyDepth);
            Assert.Equal(SceneGetDataBody.MaxHierarchyDepth, b.EffectiveDepth);
        }

        [Fact]
        public void Parse_depth_just_at_max_is_preserved()
        {
            var b = SceneGetDataBody.Parse(
                "{\"hierarchy_depth\":" + SceneGetDataBody.MaxHierarchyDepth + "}");
            Assert.Equal(SceneGetDataBody.MaxHierarchyDepth, b.HierarchyDepth);
        }

        [Fact]
        public void Parse_unparseable_int_falls_back_to_default()
        {
            var b = SceneGetDataBody.Parse("{\"hierarchy_depth\":\"deep\"}");
            Assert.Equal(SceneGetDataBody.DefaultHierarchyDepth, b.HierarchyDepth);
        }

        [Fact]
        public void Parse_treats_explicit_null_path_as_absent()
        {
            var b = SceneGetDataBody.Parse("{\"path\":null,\"hierarchy_depth\":2}");
            Assert.Null(b.Path);
            Assert.Equal(2, b.HierarchyDepth);
        }

        [Fact]
        public void Parse_unquotes_escaped_string_values()
        {
            var b = SceneGetDataBody.Parse("{\"path\":\"res:\\/\\/a\\\"b.tscn\"}");
            Assert.Equal("res://a\"b.tscn", b.Path);
        }

        [Fact]
        public void Parse_unquotes_unicode_escape()
        {
            // \u0041 == 'A'. Proves the \uXXXX branch decodes for scene paths too.
            var b = SceneGetDataBody.Parse("{\"path\":\"res:\\/\\/\\u0041.tscn\"}");
            Assert.Equal("res://A.tscn", b.Path);
        }

        [Fact]
        public void Parse_does_not_confuse_key_substring_inside_string_value()
        {
            // A path containing the literal substring '"hierarchy_depth":' must not re-trigger the key
            // finder.
            var b = SceneGetDataBody.Parse("{\"path\":\"res:\\/\\\"hierarchy_depth\\\":x.tscn\"}");
            Assert.Equal("res:/\"hierarchy_depth\":x.tscn", b.Path);
            Assert.Equal(SceneGetDataBody.DefaultHierarchyDepth, b.HierarchyDepth);
        }

        [Fact]
        public void Parse_handles_surrounding_whitespace_and_braces()
        {
            var b = SceneGetDataBody.Parse("  {  \"path\" : \"res://x.tscn\" , \"hierarchy_depth\" : 2 }  ");
            Assert.Equal("res://x.tscn", b.Path);
            Assert.Equal(2, b.HierarchyDepth);
        }

        [Fact]
        public void EffectiveDepth_translates_negative_to_maxvalue()
        {
            // The depth-counted walker in NodeTools.ToNodeData treats any large positive depth as
            // "visit everything". EffectiveDepth is the bridge between the -1 sentinel and that walker.
            var b = SceneGetDataBody.Parse("{\"hierarchy_depth\":-5}");
            Assert.Equal(int.MaxValue, b.EffectiveDepth);
        }

        [Fact]
        public void EffectiveDepth_passes_through_nonnegative()
        {
            var b = SceneGetDataBody.Parse("{\"hierarchy_depth\":4}");
            Assert.Equal(4, b.EffectiveDepth);
        }
    }

    public class SceneCreateBodyTests
    {
        [Fact]
        public void Parse_empty_body_returns_defaults()
        {
            var b = SceneCreateBody.Parse(null);
            Assert.Null(b.Path);
            Assert.Null(b.RootType);
            Assert.Null(b.RootName);
            Assert.False(b.Overwrite);
            Assert.True(b.Open);
            Assert.Equal(SceneCreateBody.DefaultRootType, b.EffectiveRootType);
        }

        [Fact]
        public void Parse_reads_path_and_root_type()
        {
            var b = SceneCreateBody.Parse(
                "{\"path\":\"res://levels/level_2.tscn\",\"root_type\":\"Node3D\"}");
            Assert.Equal("res://levels/level_2.tscn", b.Path);
            Assert.Equal("Node3D", b.RootType);
            Assert.Equal("Node3D", b.EffectiveRootType);
        }

        [Fact]
        public void Parse_reads_root_name()
        {
            var b = SceneCreateBody.Parse(
                "{\"path\":\"res://x.tscn\",\"root_name\":\"MainLevel\"}");
            Assert.Equal("MainLevel", b.RootName);
        }

        [Fact]
        public void Parse_reads_overwrite_true()
        {
            var b = SceneCreateBody.Parse("{\"path\":\"res://x.tscn\",\"overwrite\":true}");
            Assert.True(b.Overwrite);
        }

        [Fact]
        public void Parse_overwrite_defaults_false_when_absent()
        {
            var b = SceneCreateBody.Parse("{\"path\":\"res://x.tscn\"}");
            Assert.False(b.Overwrite);
        }

        [Fact]
        public void Parse_reads_open_false()
        {
            var b = SceneCreateBody.Parse("{\"path\":\"res://x.tscn\",\"open\":false}");
            Assert.False(b.Open);
        }

        [Fact]
        public void Parse_open_defaults_true_when_absent()
        {
            // Default true so the typical create-then-edit workflow makes the new scene active.
            var b = SceneCreateBody.Parse("{\"path\":\"res://x.tscn\"}");
            Assert.True(b.Open);
        }

        [Fact]
        public void Parse_effective_root_type_falls_back_to_default()
        {
            // EffectiveRootType is what the editor-only handler instantiates. It resolves to the
            // Node2D default when the caller omitted root_type entirely.
            var b = SceneCreateBody.Parse("{\"path\":\"res://x.tscn\"}");
            Assert.Equal(SceneCreateBody.DefaultRootType, b.EffectiveRootType);
        }

        [Fact]
        public void Parse_treats_explicit_null_as_absent()
        {
            var b = SceneCreateBody.Parse(
                "{\"path\":null,\"root_type\":null,\"root_name\":null}");
            Assert.Null(b.Path);
            Assert.Null(b.RootType);
            Assert.Null(b.RootName);
            Assert.Equal(SceneCreateBody.DefaultRootType, b.EffectiveRootType);
        }

        [Fact]
        public void Parse_unquotes_escaped_string_values()
        {
            var b = SceneCreateBody.Parse(
                "{\"path\":\"res:\\/\\/a\\\\b.tscn\",\"root_name\":\"a\\\"b\"}");
            Assert.Equal("res://a\\b.tscn", b.Path);
            Assert.Equal("a\"b", b.RootName);
        }

        [Fact]
        public void Parse_unquotes_solidus_escape_in_path()
        {
            // \/ is a valid (if unusual) JSON escape for /. A path with an escaped slash must still
            // resolve as a normal res:// path.
            var b = SceneCreateBody.Parse("{\"path\":\"res:\\/\\/levels\\/x.tscn\"}");
            Assert.Equal("res://levels/x.tscn", b.Path);
        }

        [Fact]
        public void Parse_unparseable_bool_falls_back_to_default()
        {
            // overwrite:1 / open:1 are not bare-token booleans → defaults preserved.
            var b = SceneCreateBody.Parse("{\"path\":\"res://x.tscn\",\"overwrite\":1,\"open\":0}");
            Assert.False(b.Overwrite);
            Assert.True(b.Open);
        }

        [Fact]
        public void Parse_does_not_confuse_key_substring_inside_string_value()
        {
            var b = SceneCreateBody.Parse(
                "{\"path\":\"res:\\/\\\"root_type\\\":x.tscn\",\"root_type\":\"Node2D\"}");
            Assert.Equal("res:/\"root_type\":x.tscn", b.Path);
            Assert.Equal("Node2D", b.RootType);
        }

        [Fact]
        public void Parse_handles_surrounding_whitespace_and_braces()
        {
            var b = SceneCreateBody.Parse(
                "  {  \"path\" : \"res://x.tscn\" , \"root_type\" : \"Node3D\" , \"overwrite\" : true }  ");
            Assert.Equal("res://x.tscn", b.Path);
            Assert.Equal("Node3D", b.RootType);
            Assert.True(b.Overwrite);
        }
    }
}
