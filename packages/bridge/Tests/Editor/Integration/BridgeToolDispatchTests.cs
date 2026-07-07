#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// Live HTTP integration tests for P2.1's <c>POST /tools/{name}</c> dispatch. Starts a real
    /// <see cref="System.Net.HttpListener"/> on an OS-assigned free port (via
    /// <see cref="BridgeHttpServer.StartOnFreePortForTests"/>) and exercises the dispatch path
    /// end-to-end: success envelope, unknown tool, malformed body, method-not-allowed, and the
    /// structured failure codes a handler can return.
    ///
    /// <para>
    /// The dispatch handler marshals to the editor main thread via
    /// <see cref="MainThreadDispatcher"/>. The binary-less test host cannot construct the
    /// dispatcher <see cref="Node"/> (no live <c>_Process</c> tick to drain the queue), so the
    /// fixture installs <see cref="BridgeHttpServer.SetDispatchForTests"/> — an inline dispatcher
    /// that runs the handler on the HTTP worker thread directly. This lets the integration test
    /// exercise the routing + envelope contract end-to-end; the main-thread-marshaling behavior
    /// itself (queue/drain, timeout classification) is covered by the
    /// <c>MainThreadDispatcherTests</c> unit suite, not here. Mirrors the
    /// <see cref="BridgeLog.SetLoggersForTests"/> seam pattern.
    /// </para>
    ///
    /// <para>
    /// <see cref="BridgeSession"/>'s state and the tool registry are static, and this fixture also
    /// starts a real <see cref="HttpListener"/> — the same shared resources the ping integration
    /// tests (<see cref="BridgeHttpServerTests"/>) touch. Both HTTP integration test classes are
    /// therefore placed in the SAME xUnit collection (<c>BridgeHttpServerTests</c>) so xUnit
    /// serializes them against each other; separate collections would run in parallel and collide
    /// on the listener port and the static session state. The fixture resets everything around
    /// each test. Adapted from Unity's <c>BridgeHttpServerTests</c> dispatch cases, simplified to
    /// the P2.1 canonical envelope (no gate / mutation envelope).
    /// </para>
    /// </summary>
    [Collection(nameof(BridgeHttpServerTests))]
    public class BridgeToolDispatchTests : IAsyncLifetime
    {
        static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
        int _port = 0;

        public Task InitializeAsync()
        {
            return Task.Run(() =>
            {
                // Swap the log sinks to no-ops BEFORE Start so GD.Print/PushError (the production
                // sinks) are never called from the binary-less host — same reason as the ping
                // integration test.
                BridgeLog.SetLoggersForTests(
                    info: _ => { },
                    warning: _ => { },
                    error: _ => { });

                BridgeSession.ResetForTests();
                BridgeToolRegistry.ResetForTests();
                // Register the echo stub the same way the plugin does on enable.
                BridgeToolRegistry.RegisterEchoStub();

                // Install the inline dispatcher seam so the HTTP worker thread runs handlers
                // directly instead of queueing onto a (non-existent, in the binary-less host)
                // dispatcher Node. The real main-thread marshal is unit-tested separately.
                BridgeHttpServer.SetDispatchForTests((name, body, _timeoutMs) =>
                    BridgeHttpServer.DispatchToolForTests(name, body));

                _port = BridgeHttpServer.StartOnFreePortForTests();
            });
        }

        public Task DisposeAsync()
        {
            BridgeHttpServer.Stop();
            BridgeHttpServer.ResetForTests();
            BridgeToolRegistry.ResetForTests();
            BridgeSession.ResetForTests();
            BridgeLog.ResetForTests();
            return Task.CompletedTask;
        }

        string BaseUrl => $"http://127.0.0.1:{_port}";

        /// <summary>POST JSON to /tools/{name} and return the response.</summary>
        Task<HttpResponseMessage> PostToolAsync(string toolName, string jsonBody)
        {
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            return HttpClient.PostAsync($"{BaseUrl}/tools/{toolName}", content);
        }

        [Fact]
        public async Task EchoTool_Success_ReturnsCanonicalEnvelope()
        {
            // The happy path: a registered tool runs, returns JSON output, and the bridge wraps it
            // in { "ok": true, "result": ... }. The echo stub splices the request body verbatim
            // into the result, so we can assert the round-trip.
            var requestJson = "{\"message\":\"hello\"}";
            using var resp = await PostToolAsync(BridgeToolRegistry.EchoToolName, requestJson);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json; charset=utf-8", resp.Content.Headers.ContentType?.ToString());

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            // The echo stub wraps the body in {"echo": <body>}.
            var result = root.GetProperty("result");
            Assert.Equal("hello", result.GetProperty("echo").GetProperty("message").GetString());
        }

        [Fact]
        public async Task EchoTool_EmptyBody_ReturnsSuccessWithNullEcho()
        {
            // An empty body is a valid (if degenerate) request — the echo stub surfaces null
            // rather than failing. Pins that a missing body does not produce a 400.
            using var resp = await PostToolAsync(BridgeToolRegistry.EchoToolName, "");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            // {"echo":null} — the stub substitutes "null" for an empty body.
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("result").GetProperty("echo").ValueKind);
        }

        [Fact]
        public async Task UnknownTool_Returns404ToolNotFound()
        {
            // A name the registry doesn't know is rejected before the body is read — cheap 404
            // with the structured tool_not_found code so an agent can branch.
            using var resp = await PostToolAsync("godot_open_mcp_does_not_exist", "{}");

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("tool_not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains("godot_open_mcp_does_not_exist", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
        }

        [Fact]
        public async Task ToolsPath_WithGet_Returns405MethodNotAllowed()
        {
            // /tools/{name} is POST-only (mirrors Unity). A GET must surface 405 with the
            // structured method_not_allowed code, not a 404 (the tool may exist; the method is
            // wrong).
            using var resp = await HttpClient.GetAsync($"{BaseUrl}/tools/{BridgeToolRegistry.EchoToolName}");

            Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("method_not_allowed", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        [Fact]
        public async Task PingPath_WithPost_Returns405MethodNotAllowed()
        {
            // /ping is GET-only (mirrors Unity). A POST must surface 405, not be routed to the
            // ping handler. Pins the method guard added in P2.1.
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await HttpClient.PostAsync($"{BaseUrl}/ping", content);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("method_not_allowed", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        [Fact]
        public async Task HandlerFailure_ReturnsSuccessEnvelopeWithOkFalse()
        {
            // A handler that returns ToolDispatchResult.Fail must surface as HTTP 200 with
            // ok:false + the error code/message — the request reached the dispatcher and was
            // processed; HTTP 4xx/5xx is reserved for routing/transport faults. Register a
            // one-off failing tool to assert the envelope shape.
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: "godot_open_mcp_test_fail",
                isMutating: false,
                defaultGate: "off",
                group: "core",
                handler: _ => ToolDispatchResult.Fail("invalid_request", "missing required field 'name'")));

            try
            {
                using var resp = await PostToolAsync("godot_open_mcp_test_fail", "{}");

                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                var body = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                Assert.False(root.GetProperty("ok").GetBoolean());
                Assert.Equal("invalid_request", root.GetProperty("error").GetProperty("code").GetString());
                Assert.Equal("missing required field 'name'", root.GetProperty("error").GetProperty("message").GetString());
            }
            finally
            {
                BridgeToolRegistry.ResetForTests();
                BridgeToolRegistry.RegisterEchoStub();
            }
        }

        [Fact]
        public async Task HandlerThrow_ReturnsExecutionErrorEnvelope()
        {
            // The handler contract says handlers must not throw (they return Fail). A handler that
            // throws anyway must be caught and surfaced as execution_error — never crash the worker
            // thread or produce a half-written response.
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: "godot_open_mcp_test_throw",
                isMutating: false,
                defaultGate: "off",
                group: "core",
                handler: _ => throw new InvalidOperationException("boom")));

            try
            {
                using var resp = await PostToolAsync("godot_open_mcp_test_throw", "{}");

                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                var body = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                Assert.False(root.GetProperty("ok").GetBoolean());
                Assert.Equal("execution_error", root.GetProperty("error").GetProperty("code").GetString());
                Assert.Equal("boom", root.GetProperty("error").GetProperty("message").GetString());
            }
            finally
            {
                BridgeToolRegistry.ResetForTests();
                BridgeToolRegistry.RegisterEchoStub();
            }
        }

        [Fact]
        public async Task ToolsPath_EmptyName_Returns404NotFound()
        {
            // /tools/ with no name segment is not a dispatch — fall through to the 404 not_found
            // path (there is no index handler for /tools/ in P2.1).
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await HttpClient.PostAsync($"{BaseUrl}/tools/", content);

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        [Fact]
        public async Task EchoTool_EnvelopeShapeIsCanonical()
        {
            // Pin the exact envelope shape so a C# ↔ TS drift is caught: success must be
            // { "ok": true, "result": ... } with ok as a JSON boolean (not "true" string), and
            // result must be the handler's output verbatim. The field order (ok before result)
            // is part of the contract — a diffing client should not flap on reordering.
            using var resp = await PostToolAsync(BridgeToolRegistry.EchoToolName, "{\"n\":42}");

            var body = await resp.Content.ReadAsStringAsync();
            // ok is a boolean literal, not a string.
            Assert.Contains("\"ok\":true", body);
            // result follows ok (order matters for a diffing client).
            var okIdx = body.IndexOf("\"ok\":true", StringComparison.Ordinal);
            var resultIdx = body.IndexOf("\"result\"", StringComparison.Ordinal);
            Assert.True(okIdx >= 0 && resultIdx > okIdx, $"result must follow ok in: {body}");
            // No top-level error field on success.
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.TryGetProperty("error", out _), "success envelope must not carry an error field");
        }

        [Fact]
        public async Task EchoTool_TimeoutMsField_IsAcceptedButDoesNotChangeEcho()
        {
            // The dispatcher reads timeout_ms from the body to clamp the per-call wait. The echo
            // stub splices the WHOLE body back, so timeout_ms appears in the echoed result. The
            // point of this test is that a valid timeout_ms does not fault the dispatch path.
            using var resp = await PostToolAsync(BridgeToolRegistry.EchoToolName, "{\"timeout_ms\":5000}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(5000, doc.RootElement.GetProperty("result").GetProperty("echo").GetProperty("timeout_ms").GetInt32());
        }
    }
}
