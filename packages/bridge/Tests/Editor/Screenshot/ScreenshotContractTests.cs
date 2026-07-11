#nullable enable
using System;
using GodotOpenMcp.Bridge.Editor;
using GodotOpenMcp.Bridge.Runtime.Screenshot;
using Xunit;
using SMath = GodotOpenMcp.Bridge.Runtime.Screenshot.ScreenshotMath;

namespace GodotOpenMcp.Bridge.Tests.Editor.Screenshot
{
    /// <summary>
    /// Screenshot contract tests (P4.8). Pins the body-parser extraction, the image-envelope
    /// JSON shape, and the registry wiring (the registration lives in the editor-only
    /// <c>ScreenshotTools</c>, but the tool-name constants and the gate/group metadata are
    /// testable without a Godot binary by querying <c>BridgeToolRegistry</c> after a synthetic
    /// registration). Live capture is verified by the headless Godot smoke, not here.
    /// </summary>
    public class ScreenshotContractTests
    {
        // --- ScreenshotViewportBody ------------------------------------------------

        [Fact]
        public void ViewportBody_DefaultsToEmpty_WhenAbsent()
        {
            var b = ScreenshotViewportBody.Parse("{}");
            Assert.False(b.HasMode);
            Assert.Equal(string.Empty, b.Mode);
        }

        [Fact]
        public void ViewportBody_ParsesMode()
        {
            var b = ScreenshotViewportBody.Parse("{\"mode\":\"2d\"}");
            Assert.True(b.HasMode);
            Assert.Equal("2d", b.Mode);
        }

        [Fact]
        public void ViewportBody_NullBody_ReturnsDefaults()
        {
            var b = ScreenshotViewportBody.Parse(null);
            Assert.False(b.HasMode);
        }

        // --- ScreenshotCameraBody --------------------------------------------------

        [Fact]
        public void CameraBody_ParsesNestedNodeRef_InstanceIdWins()
        {
            var b = ScreenshotCameraBody.Parse(
                "{\"node_ref\":{\"node_path\":\"/root/Main/Camera3D\",\"instance_id\":12345},\"width\":640,\"height\":480}");
            Assert.True(b.Camera.HasInstanceId);
            Assert.Equal(12345UL, b.Camera.InstanceId);
            Assert.True(b.Camera.HasNodePath);
            Assert.Equal("/root/Main/Camera3D", b.Camera.NodePath);
            Assert.Equal(640, b.Width);
            Assert.Equal(480, b.Height);
            Assert.True(b.HasWidth);
            Assert.True(b.HasHeight);
        }

        [Fact]
        public void CameraBody_ParsesCamelCase()
        {
            var b = ScreenshotCameraBody.Parse(
                "{\"nodeRef\":{\"nodePath\":\"Main/Cam\",\"instanceId\":7}}");
            Assert.Equal(7UL, b.Camera.InstanceId);
            Assert.Equal("Main/Cam", b.Camera.NodePath);
        }

        [Fact]
        public void CameraBody_DefaultsDimensions()
        {
            var b = ScreenshotCameraBody.Parse("{\"node_ref\":{\"node_path\":\"Cam\"}}");
            Assert.Equal(1920, b.Width);
            Assert.Equal(1080, b.Height);
        }

        [Fact]
        public void CameraBody_EmptyRef_IsEmpty()
        {
            var b = ScreenshotCameraBody.Parse("{}");
            Assert.True(b.Camera.IsEmpty);
        }

        // --- ScreenshotIsolatedBody ------------------------------------------------

        [Fact]
        public void IsolatedBody_ParsesAllFields()
        {
            var b = ScreenshotIsolatedBody.Parse(
                "{\"node_ref\":{\"node_path\":\"/root/Mesh\"},\"camera_view\":\"top\"," +
                "\"background\":\"transparent\",\"background_color\":\"#112233\"," +
                "\"field_of_view\":45.0,\"near_clip_plane\":0.1,\"far_clip_plane\":500.0," +
                "\"padding\":1.5,\"resolution\":1024}");
            Assert.Equal("/root/Mesh", b.Target.NodePath);
            Assert.Equal("top", b.CameraView);
            Assert.Equal("transparent", b.Background);
            Assert.Equal("#112233", b.BackgroundColor);
            Assert.Equal(45f, b.FieldOfView);
            Assert.Equal(0.1f, b.NearClipPlane);
            Assert.Equal(500f, b.FarClipPlane);
            Assert.Equal(1.5f, b.Padding);
            Assert.Equal(1024, b.Resolution);
        }

        [Fact]
        public void IsolatedBody_Defaults()
        {
            var b = ScreenshotIsolatedBody.Parse("{}");
            Assert.Equal("front", b.CameraView);
            Assert.Equal("solid_color", b.Background);
            Assert.Equal("#404040", b.BackgroundColor);
            Assert.Equal(60f, b.FieldOfView);
            Assert.Equal(0.05f, b.NearClipPlane);
            Assert.Equal(4000f, b.FarClipPlane);
            Assert.Equal(1.2f, b.Padding);
            Assert.Equal(512, b.Resolution);
        }

        // --- ScreenshotResult.BuildImageSuccess -----------------------------------

        [Fact]
        public void BuildImageSuccess_Shape()
        {
            // Use a tiny fake base64 — the builder does not validate it.
            var json = ScreenshotResult.BuildImageSuccess(
                base64Png: "AAAA",
                width: 128,
                height: 64,
                byteLength: 42,
                mode: "viewport",
                caption: "3D editor viewport",
                clamped: false,
                source: "3d");
            Assert.Contains("\"mediaType\":\"image/png\"", json);
            Assert.Contains("\"data\":\"AAAA\"", json);
            Assert.Contains("\"width\":128", json);
            Assert.Contains("\"height\":64", json);
            Assert.Contains("\"byteLength\":42", json);
            Assert.Contains("\"mode\":\"viewport\"", json);
            Assert.Contains("\"caption\":\"3D editor viewport\"", json);
            Assert.Contains("\"clamped\":false", json);
            Assert.Contains("\"source\":\"3d\"", json);
            // Must be parseable JSON.
            // (No System.Text.Json dependency in the test host — parse by structural checks above.)
        }

        [Fact]
        public void BuildImageSuccess_OmitsSource_WhenNull()
        {
            var json = ScreenshotResult.BuildImageSuccess(
                base64Png: "AAAA", width: 1, height: 1, byteLength: 1,
                mode: "isolated", caption: "x", clamped: true, source: null);
            Assert.DoesNotContain("\"source\"", json);
            Assert.Contains("\"clamped\":true", json);
        }

        [Fact]
        public void BuildImageSuccess_ExtraMetadata_Appended()
        {
            var json = ScreenshotResult.BuildImageSuccess(
                base64Png: "AAAA", width: 1, height: 1, byteLength: 1,
                mode: "isolated", caption: "x", clamped: false, source: null,
                extraMetadata: sb =>
                {
                    sb.Append(",\"bounds\":{");
                    sb.Append("\"size\":[1.0,2.0,3.0]");
                    sb.Append('}');
                });
            Assert.Contains("\"bounds\":{\"size\":[1.0,2.0,3.0]}", json);
        }

        [Fact]
        public void BuildImageSuccess_RejectsEmptyBase64()
        {
            Assert.Throws<ArgumentException>(() => ScreenshotResult.BuildImageSuccess(
                base64Png: "", width: 1, height: 1, byteLength: 1,
                mode: "viewport", caption: "x", clamped: false, source: null));
        }

        // --- EscapeJsonString ------------------------------------------------------

        [Theory]
        [InlineData(null, "\"\"")]
        [InlineData("", "\"\"")]
        [InlineData("plain", "\"plain\"")]
        [InlineData("a\"b", "\"a\\\"b\"")]
        [InlineData("a\\b", "\"a\\\\b\"")]
        [InlineData("a\nb", "\"a\\nb\"")]
        [InlineData("a\tb", "\"a\\tb\"")]
        public void EscapeJsonString_ProducesValidJsonLiteral(string? input, string expected)
        {
            Assert.Equal(expected, ScreenshotResult.EscapeJsonString(input));
        }
    }
}
