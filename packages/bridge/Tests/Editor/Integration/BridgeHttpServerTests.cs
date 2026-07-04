#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// Live HTTP integration tests for P1.3's <c>GET /ping</c>. Starts a real
    /// <see cref="System.Net.HttpListener"/> on an OS-assigned free port (via
    /// <see cref="BridgeHttpServer.StartOnFreePortForTests"/>) and exercises the readiness path
    /// end-to-end: 200 + payload when initialized, 503 + fallback when not, 404 for unknown paths.
    ///
    /// <para>
    /// Adapted from Unity's <c>BridgeHttpServerTests</c>. Unity's listener starts in
    /// <c>[InitializeOnLoad]</c> so the tests read <c>BridgeHttpServer.Port</c> dynamically; the
    /// Godot bridge starts from the plugin's <c>_EnterTree</c>, which the binary-less test host
    /// cannot drive, so the test seeds the port itself via the free-port seam. The /ping handler
    /// reads only <see cref="BridgeSession"/>'s cached statics (no main-thread hop), so a plain
    /// synchronous test can drive it.
    /// </para>
    ///
    /// <para>
    /// <see cref="BridgeSession"/>'s state is static; the collection serializes these tests against
    /// each other so a concurrent test never observes mid-mutation state, and the fixture resets
    /// the session + stops the listener around each test.
    /// </para>
    /// </summary>
    [Collection(nameof(BridgeHttpServerTests))]
    [CollectionDefinition(nameof(BridgeHttpServerTests), DisableParallelization = true)]
    public class BridgeHttpServerTests : IAsyncLifetime
    {
        static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
        int _port = 0;

        public async Task InitializeAsync()
        {
            await Task.Run(() =>
            {
                // Swap the log sinks to no-ops BEFORE Start so GD.Print/PushError (the production
                // sinks) are never called from the binary-less host — they P/Invoke into the native
                // Godot library that is not loaded here and would crash the test host. The dispatcher
                // tests avoid this by using the *ForTests seams; the HTTP integration test drives the
                // real Start/Stop lifecycle, so it needs the BridgeLog swap instead.
                BridgeLog.SetLoggersForTests(
                    info: _ => { },
                    warning: _ => { },
                    error: _ => { });

                BridgeSession.ResetForTests();
                _port = BridgeHttpServer.StartOnFreePortForTests();
            });
        }

        public Task DisposeAsync()
        {
            BridgeHttpServer.Stop();
            BridgeSession.ResetForTests();
            BridgeLog.ResetForTests();
            return Task.CompletedTask;
        }

        string BaseUrl => $"http://127.0.0.1:{_port}";

        [Fact]
        public async Task Ping_WhenNotInitialized_Returns503Fallback()
        {
            // The session is NOT marked initialized in the fixture (ResetForTests zeroes the flag),
            // so /ping must return the fallback payload with 503 — not a hung connection, not 200.
            using var resp = await HttpClient.GetAsync($"{BaseUrl}/ping");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(false, doc.RootElement.GetProperty("connected").GetBoolean());
            Assert.Equal(true, doc.RootElement.GetProperty("compiling").GetBoolean());
        }

        [Fact]
        public async Task Ping_WhenInitialized_Returns200AndExpectedShape()
        {
            BridgeSession.SetProjectPathForTests("/home/user/MyGame");
            BridgeSession.SetGodotVersionForTests("4.3.1.stable.mono");
            BridgeSession.SetConnected(true);

            using var resp = await HttpClient.GetAsync($"{BaseUrl}/ping");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("connected").GetBoolean());
            Assert.Equal("/home/user/MyGame", root.GetProperty("projectPath").GetString());
            Assert.Equal("4.3.1.stable.mono", root.GetProperty("godotVersion").GetString());
            Assert.Equal(BridgeSession.BridgeVersion, root.GetProperty("bridgeVersion").GetString());
            Assert.Equal("live", root.GetProperty("mode").GetString());
            // compiling/isPlaying default false after ResetForTests.
            Assert.False(root.GetProperty("compiling").GetBoolean());
            Assert.False(root.GetProperty("isPlaying").GetBoolean());
        }

        [Fact]
        public async Task Ping_BridgeVersion_MatchesSession()
        {
            BridgeSession.SetProjectPathForTests("/p");

            using var resp = await HttpClient.GetAsync($"{BaseUrl}/ping");
            var body = await resp.Content.ReadAsStringAsync();

            Assert.Contains($"\"bridgeVersion\":\"{BridgeSession.BridgeVersion}\"", body);
        }

        [Fact]
        public async Task Ping_Mode_IsLive()
        {
            BridgeSession.SetProjectPathForTests("/p");

            using var resp = await HttpClient.GetAsync($"{BaseUrl}/ping");
            var body = await resp.Content.ReadAsStringAsync();

            Assert.Contains("\"mode\":\"live\"", body);
        }

        [Fact]
        public async Task Ping_ReturnsJsonContentType()
        {
            BridgeSession.SetProjectPathForTests("/p");

            using var resp = await HttpClient.GetAsync($"{BaseUrl}/ping");

            Assert.Equal("application/json; charset=utf-8", resp.Content.Headers.ContentType?.ToString());
        }

        [Fact]
        public async Task Ping_TrailingSlash_RoutesSameAsBarePath()
        {
            // /ping and /ping/ must route identically — mirrors Unity's TrimEnd('/') normalization
            // and keeps a probe that appends a slash from getting a 404.
            BridgeSession.SetProjectPathForTests("/p");

            using var respBare = await HttpClient.GetAsync($"{BaseUrl}/ping");
            using var respSlash = await HttpClient.GetAsync($"{BaseUrl}/ping/");

            Assert.Equal(HttpStatusCode.OK, respBare.StatusCode);
            Assert.Equal(HttpStatusCode.OK, respSlash.StatusCode);

            var bodyBare = await respBare.Content.ReadAsStringAsync();
            var bodySlash = await respSlash.Content.ReadAsStringAsync();
            Assert.Equal(bodyBare, bodySlash);
        }

        [Fact]
        public async Task UnknownPath_Returns404WithNotFoundError()
        {
            using var resp = await HttpClient.GetAsync($"{BaseUrl}/unknown");

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        [Fact]
        public async Task Ping_Repeated_ReadingsAreDeterministic()
        {
            // A readiness probe may hit /ping many times in a row; the payload must be byte-stable
            // for a given session state so a diffing client doesn't flap.
            BridgeSession.SetProjectPathForTests("/p");
            BridgeSession.SetGodotVersionForTests("4.3");
            BridgeSession.SetConnected(true);

            var bodies = new string[3];
            for (var i = 0; i < bodies.Length; i++)
            {
                using var resp = await HttpClient.GetAsync($"{BaseUrl}/ping");
                bodies[i] = await resp.Content.ReadAsStringAsync();
            }

            Assert.Equal(bodies[0], bodies[1]);
            Assert.Equal(bodies[1], bodies[2]);
        }
    }
}
