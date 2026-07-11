#nullable enable
using System;
using GodotOpenMcp.Bridge.Runtime.Screenshot;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests.Runtime.Screenshot
{
    /// <summary>
    /// Pure screenshot-math tests (P4.8). Ports the Godot-MCP reference's
    /// <c>ScreenshotMathTests.cs</c> (the canonical Godot-flavored framing math) and adapts the
    /// dimension/pixel/byte-clamp + color + view-direction coverage to this project's
    /// <see cref="ScreenshotMath"/> API.
    ///
    /// <para>
    /// These tests never touch a Godot API or a GPU — they pin the pure clamp/framing/clip/color
    /// math so the editor-only render handlers can rely on it. Live capture (viewport, camera,
    /// isolated) is verified by the headless Godot smoke, not here.
    /// </para>
    /// </summary>
    public class ScreenshotMathTests
    {
        // --- ValidateDimensions ----------------------------------------------------

        [Theory]
        [InlineData(1, 1, true)]
        [InlineData(1920, 1080, true)]
        [InlineData(ScreenshotMath.MaxDimension, ScreenshotMath.MaxDimension, true)]
        [InlineData(0, 100, false)]
        [InlineData(100, 0, false)]
        [InlineData(-1, 100, false)]
        [InlineData(ScreenshotMath.MaxDimension + 1, 100, false)]
        [InlineData(100, ScreenshotMath.MaxDimension + 1, false)]
        public void ValidateDimensions_EnforcesBounds(int w, int h, bool expectedValid)
        {
            var valid = ScreenshotMath.ValidateDimensions(w, h, out var error);
            Assert.Equal(expectedValid, valid);
            if (expectedValid)
                Assert.Null(error);
            else
                Assert.False(string.IsNullOrEmpty(error));
        }

        // --- ClampToTransportLimit -------------------------------------------------

        [Fact]
        public void ClampToTransportLimit_WithinCap_ReturnsUnchanged()
        {
            var (w, h) = ScreenshotMath.ClampToTransportLimit(1920, 1080);
            Assert.Equal(1920, w);
            Assert.Equal(1080, h);
        }

        [Fact]
        public void ClampToTransportLimit_AtCap_ReturnsUnchanged()
        {
            var (w, h) = ScreenshotMath.ClampToTransportLimit(
                ScreenshotMath.MaxScreenshotDimension, ScreenshotMath.MaxScreenshotDimension);
            Assert.Equal(ScreenshotMath.MaxScreenshotDimension, w);
            Assert.Equal(ScreenshotMath.MaxScreenshotDimension, h);
        }

        [Fact]
        public void ClampToTransportLimit_OversizedLandscape_ScalesLongestEdgeToCap_PreservesAspect()
        {
            var (w, h) = ScreenshotMath.ClampToTransportLimit(8000, 4000);
            Assert.Equal(ScreenshotMath.MaxScreenshotDimension, w);
            // 4000 * (3840/8000) = 1920
            Assert.Equal(1920, h);
        }

        [Fact]
        public void ClampToTransportLimit_OversizedPortrait_ScalesLongestEdgeToCap_PreservesAspect()
        {
            var (w, h) = ScreenshotMath.ClampToTransportLimit(4000, 8000);
            Assert.Equal(1920, w);
            Assert.Equal(ScreenshotMath.MaxScreenshotDimension, h);
        }

        [Fact]
        public void ClampToTransportLimit_ExtremeAspect_FloorsShortEdgeAtOne()
        {
            var (w, h) = ScreenshotMath.ClampToTransportLimit(16000, 1);
            Assert.Equal(ScreenshotMath.MaxScreenshotDimension, w);
            Assert.True(h >= 1);
        }

        // --- ValidateFraming -------------------------------------------------------

        [Theory]
        [InlineData(60f, 0.05f, 1000f, 1.2f, true)]
        [InlineData(ScreenshotMath.MinFieldOfView, 0.05f, 1000f, 1.2f, true)]
        [InlineData(ScreenshotMath.MaxFieldOfView, 0.05f, 1000f, 1.2f, true)]
        [InlineData(0f, 0.05f, 1000f, 1.2f, false)]
        [InlineData(180f, 0.05f, 1000f, 1.2f, false)]
        [InlineData(60f, 0f, 1000f, 1.2f, false)]
        [InlineData(60f, 0.05f, 0f, 1.2f, false)]
        [InlineData(60f, 0.05f, -1f, 1.2f, false)]
        [InlineData(60f, 0.05f, 1000f, 0f, false)]
        [InlineData(60f, 0.05f, 1000f, 1000f, false)]
        [InlineData(60f, 0.05f, 0.05f, 1.2f, false)] // far == near
        [InlineData(60f, 0.1f, 0.05f, 1.2f, false)]  // far < near
        public void ValidateFraming_EnforcesRanges(float fov, float near, float far, float padding, bool expected)
        {
            var valid = ScreenshotMath.ValidateFraming(fov, near, far, padding, out var error);
            Assert.Equal(expected, valid);
            if (expected)
                Assert.Null(error);
            else
                Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void ValidateFraming_RejectsNonFinite()
        {
            Assert.False(ScreenshotMath.ValidateFraming(float.NaN, 0.05f, 1000f, 1.2f, out _));
            Assert.False(ScreenshotMath.ValidateFraming(60f, float.PositiveInfinity, 1000f, 1.2f, out _));
            Assert.False(ScreenshotMath.ValidateFraming(60f, 0.05f, float.NaN, 1.2f, out _));
            Assert.False(ScreenshotMath.ValidateFraming(60f, 0.05f, 1000f, float.PositiveInfinity, out _));
        }

        // --- ComputeCameraDistance -------------------------------------------------

        [Fact]
        public void ComputeCameraDistance_LargerRadius_YieldsLargerDistance()
        {
            var near = ScreenshotMath.ComputeCameraDistance(1f, 60f, 1.0f);
            var far = ScreenshotMath.ComputeCameraDistance(2f, 60f, 1.0f);
            Assert.True(far > near);
            Assert.Equal(near * 2f, far, 3);
        }

        [Fact]
        public void ComputeCameraDistance_MorePadding_YieldsLargerDistance()
        {
            var tight = ScreenshotMath.ComputeCameraDistance(3f, 60f, 1.0f);
            var loose = ScreenshotMath.ComputeCameraDistance(3f, 60f, 1.5f);
            Assert.Equal(tight * 1.5f, loose, 3);
        }

        [Fact]
        public void ComputeCameraDistance_ZeroRadius_StillFinitePositive()
        {
            var d = ScreenshotMath.ComputeCameraDistance(0f, 60f, 1.0f);
            Assert.True(ScreenshotMath.IsFinite(d));
            Assert.True(d > 0f);
        }

        [Fact]
        public void ComputeCameraDistance_KnownFov_MatchesTrig()
        {
            // fov=60°, half-angle=30°, sin(30°)=0.5 ⇒ distance = 2 * radius * padding.
            var d = ScreenshotMath.ComputeCameraDistance(3f, 60f, 1.0f);
            Assert.Equal(6f, d, 3);
        }

        // --- BracketClipPlanes -----------------------------------------------------

        [Fact]
        public void BracketClipPlanes_ObjectAlwaysWithinRange()
        {
            // Camera 100 out, radius 10 → object spans [90, 110] in depth.
            var (near, far) = ScreenshotMath.BracketClipPlanes(100f, 10f, 0.05f, 4000f);
            Assert.True(near <= 90f);
            Assert.True(far >= 110f);
            Assert.True(near > 0f);
            Assert.True(far > near);
        }

        [Fact]
        public void BracketClipPlanes_OnlyLoosensCallerPlanes()
        {
            // Wide user range already brackets → returned unchanged.
            var (near, far) = ScreenshotMath.BracketClipPlanes(100f, 10f, 0.01f, 10000f);
            Assert.Equal(0.01f, near, 5);
            Assert.Equal(10000f, far, 5);
        }

        [Fact]
        public void BracketClipPlanes_LargeObjectSmallFar_GrowsFar()
        {
            // distance 200, radius 100, far 100 → far must grow past ~300.
            var (near, far) = ScreenshotMath.BracketClipPlanes(200f, 100f, 0.05f, 100f);
            Assert.True(far >= 300f);
        }

        [Fact]
        public void BracketClipPlanes_NearStaysPositive_ForCloseLargeObject()
        {
            // distance 5, radius 20 → front face would be negative; near must stay positive.
            var (near, far) = ScreenshotMath.BracketClipPlanes(5f, 20f, 0.05f, 4000f);
            Assert.True(near > 0f);
            Assert.True(far > near);
            Assert.True(ScreenshotMath.IsFinite(near));
            Assert.True(ScreenshotMath.IsFinite(far));
        }

        // --- GetViewDirectionAndUp -------------------------------------------------

        [Theory]
        [InlineData(ScreenshotMath.View.Front, 0f, 0f, -1f)]
        [InlineData(ScreenshotMath.View.Back, 0f, 0f, 1f)]
        [InlineData(ScreenshotMath.View.Left, -1f, 0f, 0f)]
        [InlineData(ScreenshotMath.View.Right, 1f, 0f, 0f)]
        [InlineData(ScreenshotMath.View.Top, 0f, 1f, 0f)]
        [InlineData(ScreenshotMath.View.Bottom, 0f, -1f, 0f)]
        public void GetViewDirectionAndUp_ReturnsExpectedDirection(
            ScreenshotMath.View view, float ex, float ey, float ez)
        {
            ScreenshotMath.GetViewDirectionAndUp(view,
                out var dx, out var dy, out var dz,
                out var ux, out var uy, out var uz);
            Assert.Equal(ex, dx, 5);
            Assert.Equal(ey, dy, 5);
            Assert.Equal(ez, dz, 5);
            // Up must NOT be parallel to direction (keeps LookAt non-degenerate).
            var dot = dx * ux + dy * uy + dz * uz;
            Assert.Equal(0f, dot, 5);
        }

        [Theory]
        [InlineData("front", ScreenshotMath.View.Front, true)]
        [InlineData("FRONT", ScreenshotMath.View.Front, true)]
        [InlineData(" back ", ScreenshotMath.View.Back, true)]
        [InlineData("left", ScreenshotMath.View.Left, true)]
        [InlineData("right", ScreenshotMath.View.Right, true)]
        [InlineData("top", ScreenshotMath.View.Top, true)]
        [InlineData("bottom", ScreenshotMath.View.Bottom, true)]
        [InlineData("sideways", ScreenshotMath.View.Front, false)]
        [InlineData("", ScreenshotMath.View.Front, false)]
        [InlineData(null, ScreenshotMath.View.Front, false)]
        public void TryParseView_NormalizesAndValidates(string? input, ScreenshotMath.View expected, bool shouldParse)
        {
            var ok = ScreenshotMath.TryParseView(input, out var view);
            Assert.Equal(shouldParse, ok);
            Assert.Equal(expected, view);
        }

        // --- TryParseHtmlColor -----------------------------------------------------

        [Fact]
        public void TryParseHtmlColor_SixDigit_Parses()
        {
            Assert.True(ScreenshotMath.TryParseHtmlColor("#404040", out var c));
            Assert.Equal(0x40 / 255f, c.R, 5);
            Assert.Equal(0x40 / 255f, c.G, 5);
            Assert.Equal(0x40 / 255f, c.B, 5);
            Assert.Equal(1f, c.A, 5);
        }

        [Fact]
        public void TryParseHtmlColor_NoHash_Parses()
        {
            Assert.True(ScreenshotMath.TryParseHtmlColor("FF0000", out var c));
            Assert.Equal(1f, c.R, 5);
            Assert.Equal(0f, c.G, 5);
            Assert.Equal(0f, c.B, 5);
            Assert.Equal(1f, c.A, 5);
        }

        [Fact]
        public void TryParseHtmlColor_EightDigit_ParsesAlpha()
        {
            Assert.True(ScreenshotMath.TryParseHtmlColor("#00FF0080", out var c));
            Assert.Equal(0f, c.R, 5);
            Assert.Equal(1f, c.G, 5);
            Assert.Equal(0f, c.B, 5);
            Assert.Equal(0x80 / 255f, c.A, 5);
        }

        [Fact]
        public void TryParseHtmlColor_ThreeDigitShorthand_Parses()
        {
            Assert.True(ScreenshotMath.TryParseHtmlColor("#FFF", out var c));
            Assert.Equal(1f, c.R, 5);
            Assert.Equal(1f, c.G, 5);
            Assert.Equal(1f, c.B, 5);
            Assert.Equal(1f, c.A, 5);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("#GGGGGG")]
        [InlineData("#12345")]
        [InlineData("#1234567")]
        [InlineData("notacolor")]
        public void TryParseHtmlColor_Invalid_ReturnsFalse(string? input)
        {
            Assert.False(ScreenshotMath.TryParseHtmlColor(input, out _));
        }

        // --- HasPngSignature -------------------------------------------------------

        [Fact]
        public void HasPngSignature_AcceptsRealSignature()
        {
            var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0 };
            Assert.True(ScreenshotMath.HasPngSignature(png));
        }

        [Fact]
        public void HasPngSignature_RejectsNonPng()
        {
            Assert.False(ScreenshotMath.HasPngSignature(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
            Assert.False(ScreenshotMath.HasPngSignature(null));
            Assert.False(ScreenshotMath.HasPngSignature(new byte[0]));
            Assert.False(ScreenshotMath.HasPngSignature(new byte[] { 137, 80, 78 })); // too short
        }
    }
}
