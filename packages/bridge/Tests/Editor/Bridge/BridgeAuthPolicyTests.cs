#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P5.2 — <see cref="BridgeAuthPolicy"/> constant + validation tests. Pins the canonical mode
    /// strings, the default, and the valid set so a typo in the constants is caught immediately.
    /// Adapted from Unity's <c>BridgeAuthPolicyTests</c>.
    /// </summary>
    public class BridgeAuthPolicyTests
    {
        [Fact]
        public void Constants_AreCanonicalStrings()
        {
            Assert.Equal("none", BridgeAuthPolicy.None);
            Assert.Equal("required", BridgeAuthPolicy.Required);
        }

        [Fact]
        public void Default_IsNone()
        {
            // The default preserves the localhost-trust behavior the bridge shipped with before P5.2.
            Assert.Equal(BridgeAuthPolicy.None, BridgeAuthPolicy.Default);
        }

        [Fact]
        public void ValidModes_ContainsBothCanonicalModes()
        {
            Assert.Equal(2, BridgeAuthPolicy.ValidModes.Length);
            Assert.Contains(BridgeAuthPolicy.None, BridgeAuthPolicy.ValidModes);
            Assert.Contains(BridgeAuthPolicy.Required, BridgeAuthPolicy.ValidModes);
        }

        [Theory]
        [InlineData("none")]
        [InlineData("required")]
        public void IsValid_TrueForCanonicalModes(string mode)
        {
            Assert.True(BridgeAuthPolicy.IsValid(mode));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("NONE")]        // case-sensitive
        [InlineData("Optional")]
        [InlineData("auto")]
        [InlineData(" disabled ")]  // whitespace is not trimmed
        public void IsValid_FalseForUnknownModes(string? mode)
        {
            Assert.False(BridgeAuthPolicy.IsValid(mode));
        }

        [Fact]
        public void GetDefault_ReadsFromProjectSettings()
        {
            // GetDefault routes through BridgeProjectSettings.AuthMode. Pin the default surface:
            // when no settings file is loaded, the effective mode is "none" (the bridge stays
            // open by default). The settings-file coercion cases are covered by
            // BridgeProjectSettingsTests.
            try
            {
                BridgeProjectSettings.ResetForTests();
                Assert.Equal(BridgeAuthPolicy.None, BridgeAuthPolicy.GetDefault());
            }
            finally
            {
                BridgeProjectSettings.ResetForTests();
            }
        }
    }
}
