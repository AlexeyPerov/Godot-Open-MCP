#nullable enable
using System;
using System.Text.Json;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// Pure <c>/ping</c> payload tests for P1.3. Pins the deterministic JSON shape the readiness
    /// probe relies on: field presence, field order, the bridge version, the live mode, and the
    /// boolean encoding. <see cref="BridgeSession"/>'s state is static and outlives a single test,
    /// so each test resets it via the constructor and <see cref="Dispose"/>. The
    /// <c>[Collection]</c> attribute serializes these against each other (and any future test that
    /// touches the same statics) so a concurrent test never observes mid-mutation state.
    ///
    /// <para>
    /// Adapted from Unity's <c>Ping_ReturnsExpectedShape</c> / <c>Ping_BridgeVersion_IsExpected</c> /
    /// <c>Ping_Mode_IsLive</c> HTTP tests; here they are pure JSON-shape tests (no HttpListener),
    /// because the live listener round-trip is covered by <see cref="BridgeHttpServerTests"/>.
    /// </para>
    /// </summary>
    [Collection(nameof(BridgePingJsonTests))]
    [CollectionDefinition(nameof(BridgePingJsonTests), DisableParallelization = true)]
    public class BridgePingJsonTests : IDisposable
    {
        public BridgePingJsonTests() => BridgeSession.ResetForTests();

        public void Dispose() => BridgeSession.ResetForTests();

        [Fact]
        public void BuildPingJson_HasAllFieldsInOrder()
        {
            BridgeSession.SetProjectPathForTests("/home/user/MyGame");
            BridgeSession.SetGodotVersionForTests("4.3.1.stable.mono");
            BridgeSession.SetConnected(true);
            BridgeSession.SetCompilingForTests(false);
            BridgeSession.SetPlayingForTests(false);

            var json = BridgeJson.BuildPingJson();

            // Valid JSON; field set matches the Unity /ping contract (godotVersion in place of
            // unityVersion).
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);

            Assert.Equal(true, root.GetProperty("connected").GetBoolean());
            Assert.Equal("/home/user/MyGame", root.GetProperty("projectPath").GetString());
            Assert.Equal("4.3.1.stable.mono", root.GetProperty("godotVersion").GetString());
            Assert.Equal(BridgeSession.BridgeVersion, root.GetProperty("bridgeVersion").GetString());
            Assert.Equal("live", root.GetProperty("mode").GetString());
            Assert.Equal(false, root.GetProperty("compiling").GetBoolean());
            Assert.Equal(false, root.GetProperty("isPlaying").GetBoolean());
        }

        [Fact]
        public void BuildPingJson_FieldOrderIsDeterministic()
        {
            // The readiness payload must be byte-stable so a diffing probe (e.g. an MCP client that
            // snapshots /ping to detect bridge restarts) doesn't flap on field reordering. Pin the
            // order here so a later refactor can't silently reorder it.
            BridgeSession.SetProjectPathForTests("/p");
            BridgeSession.SetGodotVersionForTests("4.3");

            var json = BridgeJson.BuildPingJson();

            var expectedOrder = new[]
            {
                "\"connected\"",
                "\"projectPath\"",
                "\"godotVersion\"",
                "\"bridgeVersion\"",
                "\"mode\"",
                "\"compiling\"",
                "\"isPlaying\""
            };
            var prev = -1;
            foreach (var field in expectedOrder)
            {
                var idx = json.IndexOf(field, System.StringComparison.Ordinal);
                Assert.True(idx > prev, $"field {field} out of order or missing in: {json}");
                prev = idx;
            }
        }

        [Fact]
        public void BuildPingJson_BooleansAreLowercase()
        {
            BridgeSession.SetProjectPathForTests(null);
            BridgeSession.SetGodotVersionForTests(null);
            BridgeSession.SetConnected(false);
            BridgeSession.SetCompilingForTests(true);
            BridgeSession.SetPlayingForTests(true);

            var json = BridgeJson.BuildPingJson();

            Assert.Contains("\"connected\":false", json);
            Assert.Contains("\"compiling\":true", json);
            Assert.Contains("\"isPlaying\":true", json);
            // Must NOT contain the C# ToString() form ("True"/"False").
            Assert.DoesNotContain("True", json);
            Assert.DoesNotContain("False", json);
        }

        [Fact]
        public void BuildPingJson_NullProjectPathAndVersion_SerializeAsNull()
        {
            BridgeSession.SetProjectPathForTests(null);
            BridgeSession.SetGodotVersionForTests(null);

            var json = BridgeJson.BuildPingJson();

            Assert.Contains("\"projectPath\":null", json);
            Assert.Contains("\"godotVersion\":null", json);
        }

        [Fact]
        public void BuildPingJson_BridgeVersionMatchesSession()
        {
            var json = BridgeJson.BuildPingJson();
            Assert.Contains($"\"bridgeVersion\":\"{BridgeSession.BridgeVersion}\"", json);
        }

        [Fact]
        public void BuildPingJson_ModeIsAlwaysLive()
        {
            // Godot has no batch mode; the bridge is always live.
            var json = BridgeJson.BuildPingJson();
            Assert.Contains("\"mode\":\"live\"", json);
        }

        [Fact]
        public void BuildPingJson_EscapesProjectPathSpecialChars()
        {
            // A project path with a quote / backslash must be escaped — otherwise the JSON breaks
            // and the MCP client's res.json() rejects the body. The escape must be a JSON escape,
            // not a C# string escape.
            BridgeSession.SetProjectPathForTests("C:\\Users\\\"qa\"\\project");

            var json = BridgeJson.BuildPingJson();

            using var doc = JsonDocument.Parse(json);
            Assert.Equal("C:\\Users\\\"qa\"\\project", doc.RootElement.GetProperty("projectPath").GetString());
        }

        [Fact]
        public void BuildPingFallbackJson_HasExpectedShape()
        {
            // Before IsInitialized, /ping returns the fallback with HTTP 503. Same field set as the
            // live payload so a client parsing the body doesn't need a separate schema for the
            // not-ready case.
            var json = BridgeJson.BuildPingFallbackJson();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(false, root.GetProperty("connected").GetBoolean());
            Assert.Null(root.GetProperty("projectPath").GetString());
            Assert.Null(root.GetProperty("godotVersion").GetString());
            Assert.Equal(BridgeSession.BridgeVersion, root.GetProperty("bridgeVersion").GetString());
            Assert.Equal("live", root.GetProperty("mode").GetString());
            Assert.Equal(true, root.GetProperty("compiling").GetBoolean());
            Assert.Equal(false, root.GetProperty("isPlaying").GetBoolean());
        }
    }
}
