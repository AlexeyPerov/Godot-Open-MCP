#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// Pure bind-address decision tests. P1.3 covered loopback-only; P5.2 widens the surface to
    /// accept remote (<c>0.0.0.0</c>) but only when <c>authMode:"required"</c> is set, copying
    /// Unity's <c>BridgeBindAddress.Decide(address, authMode)</c> pattern. <see cref="BridgeHttpServer.Start"/>
    /// delegates the verdict to <see cref="BridgeBindAddress.Decide(string?, string?)"/> so the
    /// start-vs-refuse logic is covered without a live <see cref="System.Net.HttpListener"/>.
    /// Adapted from Unity's <c>BridgeBindAddressTests</c>.
    /// </summary>
    public class BridgeBindAddressTests
    {
        // ----- constants -----

        [Fact]
        public void Default_IsLoopback()
        {
            // The default bind address must be loopback — the bridge is local-first.
            Assert.Equal(BridgeBindAddress.Loopback, BridgeBindAddress.Default);
        }

        // ----- IsValid -----

        [Fact]
        public void IsValid_TrueForLoopbackAndRemote()
        {
            // P5.2 widened the valid set to include 0.0.0.0 (remote). Anything else is invalid and
            // coerces to loopback in Decide.
            Assert.True(BridgeBindAddress.IsValid("127.0.0.1"));
            Assert.True(BridgeBindAddress.IsValid("0.0.0.0"));
        }

        [Fact]
        public void IsValid_FalseForUnknown()
        {
            Assert.False(BridgeBindAddress.IsValid(null));
            Assert.False(BridgeBindAddress.IsValid(""));
            Assert.False(BridgeBindAddress.IsValid("localhost"));
            Assert.False(BridgeBindAddress.IsValid("192.168.1.1"));
            Assert.False(BridgeBindAddress.IsValid("example.com"));
        }

        // ----- IsRemote -----

        [Fact]
        public void IsRemote_TrueOnlyForWildcard()
        {
            // Only the canonical remote wildcard string would expose the bridge beyond loopback.
            Assert.True(BridgeBindAddress.IsRemote("0.0.0.0"));
            Assert.False(BridgeBindAddress.IsRemote("127.0.0.1"));
            Assert.False(BridgeBindAddress.IsRemote("192.168.1.1"));
            Assert.False(BridgeBindAddress.IsRemote(null));
        }

        // ----- Decide(address, authMode) — loopback -----

        [Fact]
        public void Decide_Loopback_AllowedUnderAnyAuthMode()
        {
            foreach (var mode in new[] { BridgeAuthPolicy.None, BridgeAuthPolicy.Required, null, "bogus" })
            {
                var d = BridgeBindAddress.Decide(BridgeBindAddress.Loopback, mode);
                Assert.True(d.Allowed, $"loopback should be allowed under authMode={mode}");
                Assert.Equal(BridgeBindAddress.Loopback, d.ResolvedAddress);
                Assert.Null(d.RefusalReason);
            }
        }

        // ----- Decide(address, authMode) — remote -----

        [Fact]
        public void Decide_Remote_AllowedWhenRequired()
        {
            var d = BridgeBindAddress.Decide(BridgeBindAddress.Remote, BridgeAuthPolicy.Required);
            Assert.True(d.Allowed);
            Assert.Equal(BridgeBindAddress.Remote, d.ResolvedAddress);
            Assert.Null(d.RefusalReason);
        }

        [Theory]
        [InlineData(BridgeAuthPolicy.None)]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("bogus")]   // unknown policy → deny (fail closed, same as the auth check)
        public void Decide_Remote_RefusedUnlessRequired(string? authMode)
        {
            var d = BridgeBindAddress.Decide(BridgeBindAddress.Remote, authMode);
            Assert.False(d.Allowed);
            Assert.NotNull(d.RefusalReason);
            // The refusal must name the remediation so an operator can self-correct.
            Assert.Contains("required", d.RefusalReason);
        }

        [Fact]
        public void Decide_Remote_RefusalNamesRemoteBindAndSettingsFile()
        {
            var d = BridgeBindAddress.Decide(BridgeBindAddress.Remote, BridgeAuthPolicy.None);
            Assert.False(d.Allowed);
            // Actionable message: names remote bind, authMode, and the settings file.
            Assert.Contains("0.0.0.0", d.RefusalReason);
            Assert.Contains("authMode", d.RefusalReason);
            Assert.Contains("settings.json", d.RefusalReason);
        }

        // ----- Decide — invalid input coerces to loopback -----

        [Theory]
        [InlineData("192.168.1.1")]
        [InlineData("localhost")]
        [InlineData("example.com")]
        [InlineData(null)]
        [InlineData("")]
        public void Decide_UnknownAddress_CoercesToLoopback_AndAllows(string? address)
        {
            // A bogus address coerces to the loopback default (binding it would throw at listen
            // time; coercing gives a deterministic, safe start). Loopback is always allowed.
            var d = BridgeBindAddress.Decide(address, BridgeAuthPolicy.None);
            Assert.True(d.Allowed);
            Assert.Equal(BridgeBindAddress.Loopback, d.ResolvedAddress);
        }

        // ----- single-arg Decide (legacy / loopback-only) -----

        [Fact]
        public void Decide_SingleArg_Loopback_Allowed()
        {
            var d = BridgeBindAddress.Decide(BridgeBindAddress.Loopback);
            Assert.True(d.Allowed);
            Assert.Equal(BridgeBindAddress.Loopback, d.ResolvedAddress);
        }

        [Fact]
        public void Decide_SingleArg_Remote_Refused()
        {
            // The single-arg overload treats authMode as "none", so remote is always refused on
            // this path. A caller that wants remote bind MUST use the two-arg overload.
            var d = BridgeBindAddress.Decide(BridgeBindAddress.Remote);
            Assert.False(d.Allowed);
        }
    }
}
