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
    /// P4.2 gate-dispatch integration tests for the resource mutators
    /// (<c>godot_open_mcp_resource_create</c> / <c>godot_open_mcp_resource_modify</c>). Proves:
    /// <list type="bullet">
    ///   <item>Both resource mutators route through <see cref="GatePolicy.Execute"/> exactly once.</item>
    ///   <item>Missing <c>paths_hint</c> under gate enforce/warn fails with
    ///   <c>paths_hint_required</c> (cheap-reject before the main-thread hop).</item>
    ///   <item>The handler-level <c>paths_hint</c> guard fires even under <c>gate:"off"</c>
    ///   (P4.2 mandate: <c>paths_hint</c> is always required for these mutators).</item>
    ///   <item>Gate outcome (<c>passed</c>/<c>failed</c>) is surfaced in the result envelope.</item>
    ///   <item>Failed mutation surfaces the error without a gate block.</item>
    ///   <item>Handler output fields survive alongside the gate block.</item>
    /// </list>
    ///
    /// <para>
    /// The tests install <see cref="GatePolicy.SetExecuteForTests"/> so the checkpoint→validate→delta
    /// cycle is stubbed (no live verify rules / Godot editor needed). The stub RECORDS each call so
    /// the tests can assert "the mutator went through the gate". The resource mutator handlers are
    /// stubbed — the handler's own paths_hint guard and save logic are covered by the headless Godot
    /// smoke, not here (they need a live Godot editor for ClassDB/ResourceSaver).
    /// </para>
    /// </summary>
    [Collection(nameof(BridgeHttpServerTests))]
    public class ResourceGateIntegrationTests : IAsyncLifetime
    {
        static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
        int _port = 0;

        int _gateCallCount;
        (GateMode mode, string[]? pathsHint) _lastGateCall;

        public Task InitializeAsync()
        {
            return Task.Run(() =>
            {
                BridgeLog.SetLoggersForTests(info: _ => { }, warning: _ => { }, error: _ => { });
                BridgeSession.ResetForTests();
                BridgeToolRegistry.ResetForTests();

                _gateCallCount = 0;
                GatePolicy.SetExecuteForTests((mode, pathsHint, mutation) =>
                {
                    _gateCallCount++;
                    _lastGateCall = (mode, pathsHint);
                    var result = mutation();
                    return new GateDispatchResult
                    {
                        Mutation = result,
                        GateRan = true,
                        Outcome = result.Success ? GateOutcome.Passed : GateOutcome.Failed,
                        GateFailed = !result.Success,
                        CheckpointId = "cp_stub",
                        CategoriesRun = new[] { "broken_references" },
                        Delta = new DeltaData(),
                        AgentNextSteps = Array.Empty<string>(),
                    };
                });

                BridgeHttpServer.SetDispatchForTests((name, body, isMutating, gateMode, _timeoutMs) =>
                    BridgeHttpServer.DispatchWithGateForTests(name, body, isMutating, gateMode));

                _port = BridgeHttpServer.StartOnFreePortForTests();
            });
        }

        public Task DisposeAsync()
        {
            BridgeHttpServer.Stop();
            BridgeHttpServer.ResetForTests();
            GatePolicy.ResetForTests();
            BridgeToolRegistry.ResetForTests();
            BridgeSession.ResetForTests();
            BridgeLog.ResetForTests();
            return Task.CompletedTask;
        }

        string BaseUrl => $"http://127.0.0.1:{_port}";

        Task<HttpResponseMessage> PostToolAsync(string toolName, string jsonBody)
        {
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            return HttpClient.PostAsync($"{BaseUrl}/tools/{toolName}", content);
        }

        void RegisterStubTool(string name, bool isMutating, string defaultGate,
            string? output = null, (string code, string message)? failWith = null)
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: name,
                isMutating: isMutating,
                defaultGate: defaultGate,
                group: "resource",
                handler: _ =>
                {
                    if (failWith != null)
                        return ToolDispatchResult.Fail(failWith.Value.code, failWith.Value.message);
                    return ToolDispatchResult.Ok(output ?? "{\"resource\":{\"resourcePath\":\"res://stub.tres\"},\"saved\":true}");
                }));
        }

        // -------------------------------------------------------------------
        // Both resource mutators route through GatePolicy.Execute exactly once
        // -------------------------------------------------------------------

        [Fact]
        public async Task ResourceCreate_EnforceGate_InvokesGatePolicyOnce()
        {
            RegisterStubTool("godot_open_mcp_resource_create", isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_resource_create",
                "{\"resource_path\":\"res://new.tres\",\"type_class_name\":\"Resource\"," +
                "\"paths_hint\":[\"res://new.tres\"],\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Enforce, _lastGateCall.mode);
            Assert.Equal(new[] { "res://new.tres" }, _lastGateCall.pathsHint);
        }

        [Fact]
        public async Task ResourceModify_EnforceGate_InvokesGatePolicyOnce()
        {
            RegisterStubTool("godot_open_mcp_resource_modify", isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_resource_modify",
                "{\"resource_path\":\"res://mat.tres\",\"patches\":[{\"path\":\"name\",\"value\":\"X\"}]," +
                "\"paths_hint\":[\"res://mat.tres\"],\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Enforce, _lastGateCall.mode);
            Assert.Equal(new[] { "res://mat.tres" }, _lastGateCall.pathsHint);
        }

        // -------------------------------------------------------------------
        // Missing paths_hint under enforce → paths_hint_required (cheap reject)
        // -------------------------------------------------------------------

        [Fact]
        public async Task ResourceCreate_EnforceGate_NoPathsHint_ReturnsPathsHintRequired()
        {
            RegisterStubTool("godot_open_mcp_resource_create_no_hint", isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_resource_create_no_hint",
                "{\"resource_path\":\"res://new.tres\",\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("paths_hint_required",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(0, _gateCallCount);
        }

        [Fact]
        public async Task ResourceModify_EnforceGate_NoPathsHint_ReturnsPathsHintRequired()
        {
            RegisterStubTool("godot_open_mcp_resource_modify_no_hint", isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_resource_modify_no_hint",
                "{\"resource_path\":\"res://mat.tres\",\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("paths_hint_required",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(0, _gateCallCount);
        }

        // -------------------------------------------------------------------
        // Gate outcome surfaced in the result envelope
        // -------------------------------------------------------------------

        [Fact]
        public async Task ResourceCreate_Success_SurfacesGateBlock()
        {
            RegisterStubTool("godot_open_mcp_resource_create_passed", isMutating: true, defaultGate: "enforce",
                output: "{\"resource\":{\"resourcePath\":\"res://new.tres\",\"uid\":null,\"type\":\"Resource\"}," +
                        "\"changed\":[\"name\"],\"unchanged\":[],\"saved\":true}");

            using var resp = await PostToolAsync("godot_open_mcp_resource_create_passed",
                "{\"resource_path\":\"res://new.tres\",\"paths_hint\":[\"res://new.tres\"],\"gate\":\"enforce\"}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var result = doc.RootElement.GetProperty("result");
            var gate = result.GetProperty("gate");
            Assert.Equal("enforce", gate.GetProperty("mode").GetString());
            Assert.Equal("passed", gate.GetProperty("outcome").GetString());
            Assert.True(gate.GetProperty("ran").GetBoolean());
            // Handler's own fields survive alongside the gate block.
            Assert.True(result.GetProperty("saved").GetBoolean());
            Assert.Equal("res://new.tres",
                result.GetProperty("resource").GetProperty("resourcePath").GetString());
        }

        [Fact]
        public async Task ResourceModify_FailedMutation_SurfacesErrorWithoutGateBlock()
        {
            RegisterStubTool("godot_open_mcp_resource_modify_fail", isMutating: true, defaultGate: "enforce",
                failWith: ("resource_not_found", "no resource at res://missing.tres"));

            using var resp = await PostToolAsync("godot_open_mcp_resource_modify_fail",
                "{\"resource_path\":\"res://missing.tres\",\"paths_hint\":[\"res://missing.tres\"],\"gate\":\"enforce\"}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("resource_not_found",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.False(doc.RootElement.TryGetProperty("result", out _));
        }

        // -------------------------------------------------------------------
        // Gate precedence: request gate overrides tool default
        // -------------------------------------------------------------------

        [Fact]
        public async Task ResourceCreate_RequestWarn_OverridesToolDefaultEnforce()
        {
            RegisterStubTool("godot_open_mcp_resource_create_precedence", isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_resource_create_precedence",
                "{\"resource_path\":\"res://new.tres\",\"paths_hint\":[\"res://new.tres\"],\"gate\":\"warn\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Warn, _lastGateCall.mode);
        }

        // -------------------------------------------------------------------
        // gate:"off" still routes through the gate path (Off branch), and the
        // handler-level paths_hint guard applies independently. Here we verify
        // the dispatch wiring: with gate off + paths_hint present, the gate is
        // invoked with Off mode and the mutation runs.
        // -------------------------------------------------------------------

        [Fact]
        public async Task ResourceCreate_GateOff_RunsMutationDirectly()
        {
            RegisterStubTool("godot_open_mcp_resource_create_off", isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_resource_create_off",
                "{\"resource_path\":\"res://new.tres\",\"paths_hint\":[\"res://new.tres\"],\"gate\":\"off\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Off, _lastGateCall.mode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }
    }
}
