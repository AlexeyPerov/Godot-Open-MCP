#nullable enable
using System.Text.Json;
using System.Text;
using GodotOpenMcp.Bridge.Runtime.Logging;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P18.3 unit tests for the pure-managed pieces of the full console-log hook:
    /// <list type="bullet">
    /// <item><description><see cref="GodotVersionInfo"/> — the version gate that decides whether the
    /// global <c>Logger</c> hook can be armed (Godot 4.5+).</description></item>
    /// <item><description><see cref="LogCaptureMode"/> — the active capture-mode state + the
    /// <c>capture</c> capability JSON object rendered into the <c>console_get_logs</c>
    /// response.</description></item>
    /// </list>
    ///
    /// These run in the binary-less xUnit host because both types are pure-managed (no Godot API,
    /// no <c>#if TOOLS</c>). The editor-only arming path (<c>GlobalLogHook</c> reflection installer,
    /// <c>EngineLogSink</c> RefCounted, <c>engine_log_sink.gd</c>) is verified by the headless Godot
    /// smoke on 4.5+ / manual check per the P18.3 verification plan.
    /// </summary>
    public class GodotVersionInfoTests
    {
        [Theory]
        [InlineData("4.5.stable.mono", 4, 5, 0)]
        [InlineData("4.5.1.stable.mono", 4, 5, 1)]
        [InlineData("4.6.2.stable", 4, 6, 2)]
        [InlineData("5.0.stable.mono", 5, 0, 0)]
        [InlineData("4.3.1.stable.mono", 4, 3, 1)]
        [InlineData("4.4.stable.mono", 4, 4, 0)]
        public void Parse_ExtractsMajorMinorPatch(string raw, int major, int minor, int patch)
        {
            var v = GodotVersionInfo.Parse(raw);
            Assert.True(v.IsValid);
            Assert.Equal(major, v.Major);
            Assert.Equal(minor, v.Minor);
            Assert.Equal(patch, v.Patch);
            Assert.Equal(raw, v.Raw);
        }

        [Theory]
        // 4.5+ supports the managed Logger class (PR #91006 shipped with 4.5).
        [InlineData("4.5.stable.mono", true)]
        [InlineData("4.5.1.stable.mono", true)]
        [InlineData("4.6.stable.mono", true)]
        [InlineData("5.0.stable.mono", true)]
        // Below 4.5 (the addon floor 4.3, and 4.4) the API does not exist.
        [InlineData("4.4.stable.mono", false)]
        [InlineData("4.3.1.stable.mono", false)]
        [InlineData("4.3.stable.mono", false)]
        public void SupportsGlobalLogHook_GatesAt45(string raw, bool expected)
        {
            var v = GodotVersionInfo.Parse(raw);
            Assert.True(v.IsValid);
            Assert.Equal(expected, v.SupportsGlobalLogHook);
        }

        [Fact]
        public void Parse_NullOrEmpty_IsInvalid()
        {
            Assert.False(GodotVersionInfo.Parse(null).IsValid);
            Assert.False(GodotVersionInfo.Parse("").IsValid);
            Assert.False(GodotVersionInfo.Parse("   ").IsValid);
        }

        [Fact]
        public void Parse_Garbage_IsInvalid()
        {
            // A non-numeric leading token yields no major.minor → invalid.
            var v = GodotVersionInfo.Parse("not-a-version");
            Assert.False(v.IsValid);
            Assert.False(v.SupportsGlobalLogHook);
        }

        [Fact]
        public void Parse_MajorOnly_IsInvalid()
        {
            // Need at least major.minor to be meaningful.
            Assert.False(GodotVersionInfo.Parse("4").IsValid);
        }

        [Fact]
        public void Parse_IgnoresQualifierAfterPatch()
        {
            // "4.5.1.stable.mono" — the ".stable.mono" tail is not a number and stops the collector.
            var v = GodotVersionInfo.Parse("4.5.1.stable.mono");
            Assert.Equal(4, v.Major);
            Assert.Equal(5, v.Minor);
            Assert.Equal(1, v.Patch);
        }

        [Fact]
        public void Parse_MissingPatch_DefaultsToZero()
        {
            var v = GodotVersionInfo.Parse("4.5.dev");
            Assert.True(v.IsValid);
            Assert.Equal(4, v.Major);
            Assert.Equal(5, v.Minor);
            Assert.Equal(0, v.Patch);
            Assert.True(v.SupportsGlobalLogHook);
        }
    }

    /// <summary>
    /// Capture-mode state + JSON render tests. <see cref="LogCaptureMode"/> holds process-wide static
    /// state, so each test resets it before/after to stay isolated.
    /// </summary>
    public class LogCaptureModeTests
    {
        string RenderCapture()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            LogCaptureMode.AppendCaptureJsonTo(sb);
            sb.Append('}');
            return sb.ToString();
        }

        void AssertCapture(LogCaptureModeKind kind, bool native, bool sink, string modeToken)
        {
            LogCaptureMode.ResetForTests();
            try
            {
                LogCaptureMode.Set(kind);
                Assert.Equal(kind, LogCaptureMode.Kind);
                Assert.Equal(native, LogCaptureMode.NativeOutputComplete);
                Assert.Equal(sink, LogCaptureMode.EngineErrorSinkActive);
                Assert.Equal(modeToken, LogCaptureMode.ModeToken);

                // Render and validate the JSON shape end-to-end.
                var json = RenderCapture();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                Assert.Equal(native, root.GetProperty("nativeOutputComplete").GetBoolean());
                Assert.Equal(sink, root.GetProperty("engineErrorSinkActive").GetBoolean());
                Assert.Equal(modeToken, root.GetProperty("mode").GetString());

                // Field order is fixed: nativeOutputComplete, engineErrorSinkActive, mode.
                int nativeIdx = json.IndexOf("\"nativeOutputComplete\"");
                int sinkIdx = json.IndexOf("\"engineErrorSinkActive\"");
                int modeIdx = json.IndexOf("\"mode\"");
                Assert.True(nativeIdx < sinkIdx);
                Assert.True(sinkIdx < modeIdx);
            }
            finally
            {
                LogCaptureMode.ResetForTests();
            }
        }

        [Fact]
        public void Default_IsAddonOnly()
        {
            LogCaptureMode.ResetForTests();
            Assert.Equal(LogCaptureModeKind.AddonOnly, LogCaptureMode.Kind);
            Assert.False(LogCaptureMode.EngineErrorSinkActive);
            Assert.False(LogCaptureMode.NativeOutputComplete);
            Assert.Equal("addon_only", LogCaptureMode.ModeToken);
        }

        [Fact]
        public void AddonOnly_RendersPreP183Shape()
        {
            // The pre-P18.3 capture metadata (addon-only: no native output, no engine sink).
            AssertCapture(LogCaptureModeKind.AddonOnly,
                native: false, sink: false, modeToken: "addon_only");
        }

        [Fact]
        public void NativeOutput_RendersArmedShape()
        {
            // The P18.3 armed metadata (4.5+ Logger hook active: native output tapped).
            AssertCapture(LogCaptureModeKind.NativeOutput,
                native: true, sink: true, modeToken: "native_output");
        }

        [Fact]
        public void Set_CanFlipBetweenModes()
        {
            LogCaptureMode.ResetForTests();
            try
            {
                LogCaptureMode.Set(LogCaptureModeKind.NativeOutput);
                Assert.True(LogCaptureMode.EngineErrorSinkActive);
                LogCaptureMode.Set(LogCaptureModeKind.AddonOnly);
                Assert.False(LogCaptureMode.EngineErrorSinkActive);
                Assert.Equal("addon_only", LogCaptureMode.ModeToken);
            }
            finally
            {
                LogCaptureMode.ResetForTests();
            }
        }
    }
}
