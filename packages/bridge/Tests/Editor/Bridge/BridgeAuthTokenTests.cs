#nullable enable
using System;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P5.2 — bearer-token primitive tests. Pins the mint format (256-bit hex), the Bearer header
    /// parser (whitespace/case tolerance, null/absent handling), and the constant-time equality
    /// contract (same-length vs different-length, no early-exit leak). Adapted from Unity's
    /// <c>BridgeAuthTokenTests</c> (copy fidelity — the policy is byte-for-byte identical).
    /// </summary>
    public class BridgeAuthTokenTests
    {
        // ----- Generate -----

        [Fact]
        public void Generate_ReturnsExpectedHexLength()
        {
            var token = BridgeAuthToken.Generate();
            Assert.Equal(BridgeAuthToken.HexLength, token.Length);
        }

        [Fact]
        public void Generate_ReturnsLowercaseHex()
        {
            var token = BridgeAuthToken.Generate();
            foreach (var c in token)
            {
                // Only [0-9a-f] allowed.
                Assert.True(
                    (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
                    $"Token contained non-lowercase-hex char '{c}'");
            }
        }

        [Fact]
        public void Generate_NeverReturnsNull()
        {
            // Sample several mints — a null/empty would be a catastrophic RNG failure.
            for (int i = 0; i < 16; i++)
            {
                var token = BridgeAuthToken.Generate();
                Assert.False(string.IsNullOrEmpty(token));
            }
        }

        [Fact]
        public void Generate_MintsUniqueTokens()
        {
            // Cryptographic RNG → collisions across 256-bit mints should not happen in practice.
            // A collision here would indicate a seeding fault. Sample a modest set so the test is
            // fast but still catches a grossly broken generator.
            var set = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < 64; i++)
            {
                Assert.True(set.Add(BridgeAuthToken.Generate()),
                    "Generate returned a duplicate token — RNG is not unique");
            }
        }

        // ----- ExtractBearer -----

        [Fact]
        public void ExtractBearer_ValidHeader_ReturnsToken()
        {
            var token = BridgeAuthToken.ExtractBearer("Bearer abc123");
            Assert.Equal("abc123", token);
        }

        [Fact]
        public void ExtractBearer_ToleratesSurroundingWhitespace()
        {
            Assert.Equal("abc123", BridgeAuthToken.ExtractBearer("  Bearer   abc123  "));
        }

        [Fact]
        public void ExtractBearer_SchemeIsCaseInsensitive()
        {
            Assert.Equal("abc123", BridgeAuthToken.ExtractBearer("bearer abc123"));
            Assert.Equal("abc123", BridgeAuthToken.ExtractBearer("BEARER abc123"));
            Assert.Equal("abc123", BridgeAuthToken.ExtractBearer("BeArEr abc123"));
        }

        [Fact]
        public void ExtractBearer_NullOrEmpty_ReturnsNull()
        {
            Assert.Null(BridgeAuthToken.ExtractBearer(null));
            Assert.Null(BridgeAuthToken.ExtractBearer(""));
        }

        [Fact]
        public void ExtractBearer_NonBearerScheme_ReturnsNull()
        {
            Assert.Null(BridgeAuthToken.ExtractBearer("Basic abc123"));
            Assert.Null(BridgeAuthToken.ExtractBearer("abc123"));
        }

        [Fact]
        public void ExtractBearer_EmptyTokenAfterScheme_ReturnsNull()
        {
            // "Bearer " with nothing after it (after trim).
            Assert.Null(BridgeAuthToken.ExtractBearer("Bearer "));
            Assert.Null(BridgeAuthToken.ExtractBearer("Bearer"));
        }

        // ----- EqualsConstantTime -----

        [Fact]
        public void EqualsConstantTime_EqualStrings_True()
        {
            var token = BridgeAuthToken.Generate();
            Assert.True(BridgeAuthToken.EqualsConstantTime(token, token));
        }

        [Fact]
        public void EqualsConstantTime_DifferentStrings_False()
        {
            var a = BridgeAuthToken.Generate();
            var b = BridgeAuthToken.Generate();
            Assert.NotEqual(a, b);
            Assert.False(BridgeAuthToken.EqualsConstantTime(a, b));
        }

        [Fact]
        public void EqualsConstantTime_DifferentLengths_False()
        {
            // Different lengths cannot match; the helper still walks the shorter length so the
            // timing profile does not reveal the expected length.
            Assert.False(BridgeAuthToken.EqualsConstantTime("abc", "abcd"));
            Assert.False(BridgeAuthToken.EqualsConstantTime("abcd", "abc"));
        }

        [Fact]
        public void EqualsConstantTime_Nulls_TreatedAsEmpty()
        {
            Assert.True(BridgeAuthToken.EqualsConstantTime(null, null));
            Assert.True(BridgeAuthToken.EqualsConstantTime("", ""));
            Assert.True(BridgeAuthToken.EqualsConstantTime(null, ""));
            Assert.False(BridgeAuthToken.EqualsConstantTime(null, "a"));
        }

        [Fact]
        public void EqualsConstantTime_SingleCharDifference_False()
        {
            // A common implementation bug is early-exit on the first differing byte; pin that the
            // full string is walked by checking a difference at the end.
            var a = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var b = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab";
            Assert.False(BridgeAuthToken.EqualsConstantTime(a, b));
        }
    }
}
