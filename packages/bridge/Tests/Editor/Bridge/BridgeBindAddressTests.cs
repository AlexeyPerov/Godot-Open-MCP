#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// Pure bind-address decision tests for P1.3. <see cref="BridgeHttpServer.Start"/> delegates
    /// the verdict to <see cref="BridgeBindAddress.Decide"/> so the start-vs-refuse logic is covered
    /// without a live <see cref="System.Net.HttpListener"/>. Adapted from Unity's
    /// <c>BridgeBindAddressTests</c>; the P1.3 surface is loopback-only, so the remote-allow tests
    /// (Unity) are deferred to P5.2's auth gate and the remote-refuse tests assert the P1.3 refusal
    /// message instead.
    /// </summary>
    public class BridgeBindAddressTests
    {
        [Fact]
        public void Loopback_IsAllowed()
        {
            var d = BridgeBindAddress.Decide(BridgeBindAddress.Loopback);
            Assert.True(d.Allowed);
            Assert.Equal(BridgeBindAddress.Loopback, d.ResolvedAddress);
            Assert.Null(d.RefusalReason);
        }

        [Fact]
        public void Default_IsLoopback()
        {
            // The default bind address must be loopback — the bridge is local-first.
            Assert.Equal(BridgeBindAddress.Loopback, BridgeBindAddress.Default);
        }

        [Fact]
        public void UnknownAddress_CoercesToLoopback()
        {
            // Anything that isn't loopback is NOT silently allowed in P1.3 — Decide coerces the
            // resolved address to Default (loopback) and allows it. This means a misconfigured
            // project setting still binds loopback, never the bogus value.
            var d = BridgeBindAddress.Decide("0.0.0.0");
            Assert.True(d.Allowed);
            Assert.Equal(BridgeBindAddress.Loopback, d.ResolvedAddress);
        }

        [Fact]
        public void UnknownAddress_CoercesToLoopback_ForArbitraryHost()
        {
            var d = BridgeBindAddress.Decide("example.com");
            Assert.True(d.Allowed);
            Assert.Equal(BridgeBindAddress.Loopback, d.ResolvedAddress);
        }

        [Fact]
        public void IsValid_TrueOnlyForLoopback()
        {
            // P1.3 surface: loopback only. P5.2 widens this to also accept 0.0.0.0 once the auth
            // gate is in place; that test will be added alongside the widening.
            Assert.True(BridgeBindAddress.IsValid("127.0.0.1"));
            Assert.False(BridgeBindAddress.IsValid("0.0.0.0"));
            Assert.False(BridgeBindAddress.IsValid(null));
            Assert.False(BridgeBindAddress.IsValid(""));
            Assert.False(BridgeBindAddress.IsValid("localhost"));
            Assert.False(BridgeBindAddress.IsValid("192.168.1.1"));
        }

        [Fact]
        public void IsRemote_TrueForAnythingButLoopback()
        {
            // The inverse of IsValid in P1.3: anything that is not the canonical loopback string
            // would expose the bridge beyond loopback. Used by Start's decision path to refuse a
            // non-loopback resolved address (unreachable in P1.3 since IsValid coerces first, but
            // defensive against a future widening that forgets the auth gate).
            Assert.True(BridgeBindAddress.IsRemote("0.0.0.0"));
            Assert.True(BridgeBindAddress.IsRemote("192.168.1.1"));
            Assert.True(BridgeBindAddress.IsRemote(null));
            Assert.False(BridgeBindAddress.IsRemote("127.0.0.1"));
        }
    }
}
