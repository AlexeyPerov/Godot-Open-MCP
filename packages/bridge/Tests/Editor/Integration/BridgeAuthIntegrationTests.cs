#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P5.2 — live HTTP integration tests for the bearer-token auth gate. Starts a real
    /// <see cref="System.Net.HttpListener"/> (via <see cref="BridgeHttpServer.StartOnFreePortForTests"/>)
    /// under <c>authMode:"required"</c> with a known token minted via the instance lock, then
    /// exercises the full policy matrix end-to-end:
    ///
    /// <list type="bullet">
    ///   <item>missing Authorization → 401 unauthorized</item>
    ///   <item>wrong token → 401 unauthorized</item>
    ///   <item>non-Bearer scheme → 401 unauthorized</item>
    ///   <item>correct Bearer → 200 (the request reaches the handler)</item>
    ///   <item>authMode "none" → 200 with no header (localhost-trust preserved)</item>
    /// </list>
    ///
    /// <para>
    /// This is its own collection (separate from <see cref="BridgeHttpServerTests"/>) because it
    /// drives a different static state: <see cref="BridgeProjectSettings"/> is pinned to
    /// <c>required</c> and <see cref="BridgeInstanceLock"/> is acquired to mint the token. The two
    /// collections serialize internally so a concurrent run never observes mid-mutation state.
    /// </para>
    /// </summary>
    [Collection(nameof(BridgeAuthIntegrationTests))]
    [CollectionDefinition(nameof(BridgeAuthIntegrationTests), DisableParallelization = true)]
    public class BridgeAuthIntegrationTests : IAsyncLifetime
    {
        static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
        int _port = 0;
        string _token = null!;

        public async Task InitializeAsync()
        {
            await Task.Run(() =>
            {
                // No-op log sinks so the listener lifecycle never P/Invokes into native Godot.
                BridgeLog.SetLoggersForTests(info: _ => { }, warning: _ => { }, error: _ => { });

                // Pin project settings to required so CheckAuth enforces on every route.
                BridgeProjectSettings.LoadFromStringForTests(
                    "/test/AuthGame", "{\"authMode\":\"required\",\"bindAddress\":\"127.0.0.1\"}");

                // Mint a known token by acquiring the instance lock. The lock writes to the
                // instances dir; sandbox it to a temp dir so we never touch the real
                // ~/.godot-open-mcp. (Reuse the lock-tests sandbox convention.)
                InstancePortResolver.InstancesDirOverride =
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        "godot-open-mcp-auth-tests-" + Guid.NewGuid().ToString("N"));
                BridgeInstanceLock.Acquire("/test/AuthGame", 0);
                _token = BridgeInstanceLock.AuthToken!;
                Assert.False(string.IsNullOrEmpty(_token), "token must be minted on Acquire");

                BridgeSession.ResetForTests();
                _port = BridgeHttpServer.StartOnFreePortForTests();
            });
        }

        public Task DisposeAsync()
        {
            BridgeHttpServer.Stop();
            BridgeSession.ResetForTests();
            try { BridgeInstanceLock.Release(); } catch { }
            try
            {
                var dir = InstancePortResolver.InstancesDirOverride;
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    System.IO.Directory.Delete(dir, recursive: true);
            }
            catch { }
            InstancePortResolver.InstancesDirOverride = null;
            BridgeProjectSettings.ResetForTests();
            BridgeLog.ResetForTests();
            return Task.CompletedTask;
        }

        string BaseUrl => $"http://127.0.0.1:{_port}";

        HttpClient AuthClient(string? bearer)
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            if (bearer != null)
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {bearer}");
            return client;
        }

        [Fact]
        public async Task Required_MissingHeader_Returns401()
        {
            using var resp = await HttpClient.GetAsync($"{BaseUrl}/ping");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("unauthorized", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        [Fact]
        public async Task Required_WrongToken_Returns401()
        {
            var wrong = new string('a', BridgeAuthToken.HexLength);
            using var client = AuthClient(wrong);
            using var resp = await client.GetAsync($"{BaseUrl}/ping");

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task Required_NonBearerScheme_Returns401()
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Add("Authorization", $"Basic {_token}");
            using var resp = await client.GetAsync($"{BaseUrl}/ping");

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task Required_CorrectBearer_AllowsPing()
        {
            using var client = AuthClient(_token);
            // Seed the session so /ping returns 200 (otherwise it returns 503 which still proves the
            // auth gate passed — but 200 is the clearer signal that the request reached the handler).
            BridgeSession.SetProjectPathForTests("/test/AuthGame");
            BridgeSession.SetConnected(true);

            using var resp = await client.GetAsync($"{BaseUrl}/ping");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        [Fact]
        public async Task Required_CorrectBearer_StillGatesUnknownPath()
        {
            // Auth passes, but the path is unknown → 404 (not 401). Proves the auth gate runs BEFORE
            // routing, and a correctly-authenticated request still gets the right downstream status.
            using var client = AuthClient(_token);
            using var resp = await client.GetAsync($"{BaseUrl}/unknown");

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }

        [Fact]
        public async Task None_AllowsRequestWithoutHeader()
        {
            // Flip the live settings to "none" and confirm the same listener (no restart) now allows
            // an unauthenticated request. This proves the gate reads the current setting per-request
            // and that the default (none) preserves localhost-trust.
            BridgeProjectSettings.LoadFromStringForTests(
                "/test/AuthGame", "{\"authMode\":\"none\",\"bindAddress\":\"127.0.0.1\"}");
            BridgeSession.SetProjectPathForTests("/test/AuthGame");
            BridgeSession.SetConnected(true);

            try
            {
                using var resp = await HttpClient.GetAsync($"{BaseUrl}/ping");

                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            }
            finally
            {
                // Restore required so a later test in this collection (which shares static state)
                // does not inherit the "none" setting.
                BridgeProjectSettings.LoadFromStringForTests(
                    "/test/AuthGame", "{\"authMode\":\"required\",\"bindAddress\":\"127.0.0.1\"}");
            }
        }
    }
}
