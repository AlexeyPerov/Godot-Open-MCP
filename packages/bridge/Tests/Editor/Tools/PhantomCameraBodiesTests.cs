#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// PhantomCamera pack unit tests for the pure-managed body parsers. The
    /// editor-only handlers (<see cref="PhantomCameraTools.Create"/> / SetTarget
    /// / …) are <c>#if TOOLS</c> and coupled to <c>EditorInterface.Singleton</c>
    /// + <c>ClassDB</c> + live addon nodes, none of which the binary-less xUnit
    /// host can construct — those paths are exercised by the headless Godot
    /// smoke / live call path (with the addon enabled), not here.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <c>TilemapBodiesTests</c> (copy
    /// fidelity for the parser test shape), with cases specific to the
    /// PhantomCamera field set (dimension normalization, the look_at_mode
    /// absent-vs-zero distinction that <see cref="JsonScalar.ExtractIntOrNull"/>
    /// provides, the optional follow target).
    /// </para>
    /// </summary>
    public class PhantomCameraBodiesTests
    {
        // --- PhantomCameraCreateBody -------------------------------------------

        [Fact]
        public void CreateBody_empty_body_defaults_to_3d()
        {
            var b = PhantomCameraCreateBody.Parse(null);
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
            Assert.Null(b.Dimension);
            Assert.Equal("3d", b.EffectiveDimension);
        }

        [Fact]
        public void CreateBody_reads_all_fields_and_dimension_3d()
        {
            var b = PhantomCameraCreateBody.Parse(
                "{\"name\":\"Cam\",\"parent_node_path\":\"Main\",\"position\":\"1,2,3\",\"dimension\":\"3d\"}");
            Assert.Equal("Cam", b.Name);
            Assert.Equal("Main", b.ParentNodePath);
            Assert.Equal("1,2,3", b.Position);
            Assert.Equal("3d", b.Dimension);
            Assert.Equal("3d", b.EffectiveDimension);
        }

        [Fact]
        public void CreateBody_dimension_2d_normalizes()
        {
            var b = PhantomCameraCreateBody.Parse("{\"dimension\":\"2d\"}");
            Assert.Equal("2d", b.EffectiveDimension);
        }

        [Fact]
        public void CreateBody_dimension_is_case_insensitive()
        {
            var b = PhantomCameraCreateBody.Parse("{\"dimension\":\"2D\"}");
            Assert.Equal("2d", b.EffectiveDimension);
        }

        [Fact]
        public void CreateBody_unrecognized_dimension_falls_back_to_3d()
        {
            var b = PhantomCameraCreateBody.Parse("{\"dimension\":\"4d\"}");
            Assert.Equal("3d", b.EffectiveDimension);
        }

        [Fact]
        public void CreateBody_treats_explicit_null_dimension_as_absent()
        {
            var b = PhantomCameraCreateBody.Parse("{\"dimension\":null}");
            Assert.Equal("3d", b.EffectiveDimension);
        }

        // --- PhantomCameraSetTargetBody ----------------------------------------

        [Fact]
        public void SetTargetBody_empty_body_has_no_paths()
        {
            var b = PhantomCameraSetTargetBody.Parse("");
            Assert.False(b.HasNodePath);
            Assert.False(b.HasTargetNodePath);
        }

        [Fact]
        public void SetTargetBody_reads_paths()
        {
            var b = PhantomCameraSetTargetBody.Parse(
                "{\"node_path\":\"Main/Cam\",\"target_node_path\":\"Main/Player\"}");
            Assert.True(b.HasNodePath);
            Assert.True(b.HasTargetNodePath);
            Assert.Equal("Main/Cam", b.NodePath);
            Assert.Equal("Main/Player", b.TargetNodePath);
        }

        // --- PhantomCameraSetPriorityBody -------------------------------------

        [Fact]
        public void SetPriorityBody_empty_body_defaults_priority_zero()
        {
            var b = PhantomCameraSetPriorityBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.Equal(0, b.Priority);
        }

        [Fact]
        public void SetPriorityBody_reads_node_path_and_priority()
        {
            var b = PhantomCameraSetPriorityBody.Parse(
                "{\"node_path\":\"Main/Cam\",\"priority\":5}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Main/Cam", b.NodePath);
            Assert.Equal(5, b.Priority);
        }

        // --- PhantomCameraSetFollowBody ---------------------------------------

        [Fact]
        public void SetFollowBody_empty_body_defaults_follow_mode_zero()
        {
            var b = PhantomCameraSetFollowBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.Equal(0, b.FollowMode);
            Assert.False(b.HasTargetNodePath);
        }

        [Fact]
        public void SetFollowBody_reads_mode_and_optional_target()
        {
            var b = PhantomCameraSetFollowBody.Parse(
                "{\"node_path\":\"Cam\",\"follow_mode\":1,\"target_node_path\":\"Player\"}");
            Assert.True(b.HasNodePath);
            Assert.Equal(1, b.FollowMode);
            Assert.True(b.HasTargetNodePath);
            Assert.Equal("Player", b.TargetNodePath);
        }

        [Fact]
        public void SetFollowBody_target_is_optional()
        {
            var b = PhantomCameraSetFollowBody.Parse(
                "{\"node_path\":\"Cam\",\"follow_mode\":2}");
            Assert.False(b.HasTargetNodePath);
            Assert.Equal(2, b.FollowMode);
        }

        // --- PhantomCameraSetLookAtBody ---------------------------------------

        [Fact]
        public void SetLookAtBody_empty_body_has_no_target_and_null_mode()
        {
            var b = PhantomCameraSetLookAtBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.False(b.HasTargetNodePath);
            Assert.Null(b.LookAtMode);
        }

        [Fact]
        public void SetLookAtBody_look_at_mode_absent_is_null()
        {
            var b = PhantomCameraSetLookAtBody.Parse(
                "{\"node_path\":\"Cam\",\"target_node_path\":\"Enemy\"}");
            Assert.True(b.HasTargetNodePath);
            Assert.Null(b.LookAtMode);
        }

        [Fact]
        public void SetLookAtBody_look_at_mode_explicit_zero_is_zero_not_null()
        {
            // The key property of ExtractIntOrNull: an explicit 0 must be distinguishable from
            // an absent key (the handler skips setting the mode only when null).
            var b = PhantomCameraSetLookAtBody.Parse(
                "{\"node_path\":\"Cam\",\"target_node_path\":\"Enemy\",\"look_at_mode\":0}");
            Assert.NotNull(b.LookAtMode);
            Assert.Equal(0, b.LookAtMode!.Value);
        }

        [Fact]
        public void SetLookAtBody_reads_full_set()
        {
            var b = PhantomCameraSetLookAtBody.Parse(
                "{\"node_path\":\"Cam\",\"target_node_path\":\"Enemy\",\"look_at_mode\":3}");
            Assert.Equal("Cam", b.NodePath);
            Assert.Equal("Enemy", b.TargetNodePath);
            Assert.Equal(3, b.LookAtMode);
        }

        // --- PhantomCameraGetBody ---------------------------------------------

        [Fact]
        public void GetBody_empty_body_has_no_node_path()
        {
            var b = PhantomCameraGetBody.Parse("");
            Assert.False(b.HasNodePath);
        }

        [Fact]
        public void GetBody_reads_node_path()
        {
            var b = PhantomCameraGetBody.Parse("{\"node_path\":\"Main/Cam\"}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Main/Cam", b.NodePath);
        }
    }
}
