#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P5.2 — auth decision matrix tests for <see cref="BridgeAuthCheck.IsAuthorized"/>. Pins the
    /// full policy table from <c>specs/execution/P5/P5.2.md</c>:
    ///
    /// <list type="bullet">
    ///   <item><c>none</c> → allow unconditionally (any/missing token).</item>
    ///   <item><c>required</c> + correct Bearer → allow.</item>
    ///   <item><c>required</c> + missing/wrong/non-Bearer → deny.</item>
    ///   <item><c>required</c> + empty expected token (bug) → deny (fail closed).</item>
    ///   <item>unknown/invalid policy → deny (fail closed — never coerce to allow).</item>
    /// </list>
    ///
    /// Adapted from Unity's <c>BridgeAuthCheckTests</c> (copy fidelity — the policy is
    /// byte-for-byte identical). The pure function takes the resolved policy + header + expected
    /// token, so no HTTP listener or settings reader is needed.
    /// </summary>
    public class BridgeAuthCheckTests
    {
        // 64-hex-char tokens (256-bit). Two distinct values so a constant-time compare between them
        // returns false deterministically.
        const string GoodToken = "deadbeef00000000000000000000000000000000000000000000000000000000";
        const string WrongToken = "cafebabe00000000000000000000000000000000000000000000000000000000";

        // ----- authMode: none (allow) -----

        [Fact]
        public void NonePolicy_AllowsMissingHeader()
        {
            Assert.True(BridgeAuthCheck.IsAuthorized(
                BridgeAuthPolicy.None, null, GoodToken));
        }

        [Fact]
        public void NonePolicy_AllowsWrongToken()
        {
            // Even a wrong header is allowed under "none" — enforcement is off.
            Assert.True(BridgeAuthCheck.IsAuthorized(
                BridgeAuthPolicy.None, $"Bearer {WrongToken}", GoodToken));
        }

        [Fact]
        public void NonePolicy_AllowsEmptyExpectedToken()
        {
            // "none" ignores the token entirely, so an empty expected token is fine.
            Assert.True(BridgeAuthCheck.IsAuthorized(
                BridgeAuthPolicy.None, null, null));
        }

        // ----- authMode: required (enforce) -----

        [Fact]
        public void RequiredPolicy_CorrectBearer_Allows()
        {
            Assert.True(BridgeAuthCheck.IsAuthorized(
                BridgeAuthPolicy.Required, $"Bearer {GoodToken}", GoodToken));
        }

        [Fact]
        public void RequiredPolicy_MissingHeader_Denies()
        {
            Assert.False(BridgeAuthCheck.IsAuthorized(
                BridgeAuthPolicy.Required, null, GoodToken));
        }

        [Fact]
        public void RequiredPolicy_EmptyHeader_Denies()
        {
            Assert.False(BridgeAuthCheck.IsAuthorized(
                BridgeAuthPolicy.Required, "", GoodToken));
        }

        [Fact]
        public void RequiredPolicy_WrongBearer_Denies()
        {
            Assert.False(BridgeAuthCheck.IsAuthorized(
                BridgeAuthPolicy.Required, $"Bearer {WrongToken}", GoodToken));
        }

        [Fact]
        public void RequiredPolicy_NonBearerScheme_Denies()
        {
            Assert.False(BridgeAuthCheck.IsAuthorized(
                BridgeAuthPolicy.Required, $"Basic {GoodToken}", GoodToken));
        }

        [Fact]
        public void RequiredPolicy_MalformedBearer_Denies()
        {
            // "Bearer" with no token after it.
            Assert.False(BridgeAuthCheck.IsAuthorized(
                BridgeAuthPolicy.Required, "Bearer ", GoodToken));
        }

        [Fact]
        public void RequiredPolicy_EmptyExpectedToken_Denies_FailClosed()
        {
            // A bug that left the expected token empty must NOT allow a request through even if the
            // header happens to match — fail closed.
            Assert.False(BridgeAuthCheck.IsAuthorized(
                BridgeAuthPolicy.Required, $"Bearer ", null));
            Assert.False(BridgeAuthCheck.IsAuthorized(
                BridgeAuthPolicy.Required, $"Bearer {GoodToken}", ""));
        }

        // ----- unknown / invalid policy (fail closed) -----

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("optional")]     // a plausible-but-invalid name an operator might type
        [InlineData("auto")]
        [InlineData("REQUIRED")]     // case-sensitive — the canonical string is lowercase
        [InlineData("loose")]
        public void UnknownPolicy_FailsClosed(string? policy)
        {
            // Whatever the request carries, an unrecognized policy never allows. This is the
            // fail-closed guard against a corrupt settings file or a typo (the Godot delta from
            // Unity: the reader does NOT coerce invalid → none; the raw value flows here).
            Assert.False(BridgeAuthCheck.IsAuthorized(policy, $"Bearer {GoodToken}", GoodToken));
            Assert.False(BridgeAuthCheck.IsAuthorized(policy, null, GoodToken));
        }
    }
}
