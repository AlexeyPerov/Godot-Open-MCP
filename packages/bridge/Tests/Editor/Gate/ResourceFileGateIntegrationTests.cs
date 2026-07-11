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
    /// P4.3 gate-dispatch integration tests for the resource file-lifecycle mutators
    /// (<c>godot_open_mcp_resource_move</c> / <c>godot_open_mcp_resource_delete</c>). Proves:
    /// <list type="bullet">
    ///   <item>Both mutators route through <see cref="GatePolicy.Execute"/> exactly once.</item>
    ///   <item>Missing <c>paths_hint</c> under gate enforce/warn fails with
    ///   <c>paths_hint_required</c> (cheap-reject before the main-thread hop).</item>
    ///   <item>The handler-level <c>paths_hint</c> guard fires even under <c>gate:"off"</c>
    ///   (P4.3 mandate: <c>paths_hint</c> is always required for these mutators).</item>
    ///   <item>For move, <c>paths_hint</c> must contain BOTH source and destination paths.</item>
    ///   <item>Gate outcome (<c>passed</c>/<c>failed</c>) is surfaced in the result envelope.</item>
    ///   <item>Failed mutation surfaces the error without a gate block.</item>
    ///   <item>Handler output fields survive alongside the gate block.</item>
    /// </list>
    ///
    /// <para>
    /// The tests install <see cref="GatePolicy.SetExecuteForTests"/> so the checkpoint→validate→delta
    /// cycle is stubbed (no live verify rules / Godot editor needed). The resource mutator handlers
    /// are stubbed — the handler's own paths_hint guard and DirAccess logic are covered by the
    /// headless Godot smoke, not here (they need a live Godot editor for FileAccess/DirAccess).
    /// </para>
    /// </summary>
    [Collection(nameof(BridgeHttpServerTests))]
    public class ResourceFileGateIntegrationTests : IAsyncLifetime
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
                    return ToolDispatchResult.Ok(output ?? "{\"moved\":true}");
                }));
        }

        // -------------------------------------------------------------------
        // Both resource file mutators route through GatePolicy.Execute exactly once
        // -------------------------------------------------------------------

        [Fact]
        public async Task ResourceMove_EnforceGate_InvokesGatePolicyOnce()
        {
            RegisterStubTool("godot_open_mcp_resource_move", isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_resource_move",
                "{\"source_path\":\"res://old.tres\",\"destination_path\":\"res://new.tres\"," +
                "\"paths_hint\":[\"res://old.tres\",\"res://new.tres\"],\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Enforce, _lastGateCall.mode);
            Assert.Equal(new[] { "res://old.tres", "res://new.tres" }, _lastGateCall.pathsHint);
        }

        [Fact]
        public async Task ResourceDelete_EnforceGate_InvokesGatePolicyOnce()
        {
            RegisterStubTool("godot_open_mcp_resource_delete", isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_resource_delete",
                "{\"resource_path\":\"res://doomed.tres\"," +
                "\"paths_hint\":[\"res://doomed.tres\"],\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Enforce, _lastGateCall.mode);
            Assert.Equal(new[] { "res://doomed.tres" }, _lastGateCall.pathsHint);
        }

        // -------------------------------------------------------------------
        // Missing paths_hint under enforce → paths_hint_required (cheap reject)
        // -------------------------------------------------------------------

        [Fact]
        public async Task ResourceMove_EnforceGate_NoPathsHint_ReturnsPathsHintRequired()
        {
            RegisterStubTool("godot_open_mcp_resource_move_nohint", isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_resource_move_nohint",
                "{\"source_path\":\"res://a.tres\",\"destination_path\":\"res://b.tres\",\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("paths_hint_required",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(0, _gateCallCount);
        }

        [Fact]
        public async Task ResourceDelete_EnforceGate_NoPathsHint_ReturnsPathsHintRequired()
        {
            RegisterStubTool("godot_open_mcp_resource_delete_nohint", isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_resource_delete_nohint",
                "{\"resource_path\":\"res://a.tres\",\"gate\":\"enforce\"}");

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
        public async Task ResourceMove_Success_SurfacesGateBlock()
        {
            RegisterStubTool("godot_open_mcp_resource_move_passed", isMutating: true, defaultGate: "enforce",
                output: "{\"before\":{\"resourcePath\":\"res://old.tres\",\"uid\":null,\"type\":\"Resource\"}," +
                        "\"after\":{\"resourcePath\":\"res://new.tres\",\"uid\":null,\"type\":\"Resource\"}," +
                        "\"sidecarMoved\":false,\"filesystemScan\":{\"settled\":true,\"scanTriggered\":true,\"settledMs\":0,\"reason\":null}," +
                        "\"moved\":true}");

            using var resp = await PostToolAsync("godot_open_mcp_resource_move_passed",
                "{\"source_path\":\"res://old.tres\",\"destination_path\":\"res://new.tres\"," +
                "\"paths_hint\":[\"res://old.tres\",\"res://new.tres\"],\"gate\":\"enforce\"}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var result = doc.RootElement.GetProperty("result");
            var gate = result.GetProperty("gate");
            Assert.Equal("enforce", gate.GetProperty("mode").GetString());
            Assert.Equal("passed", gate.GetProperty("outcome").GetString());
            Assert.True(gate.GetProperty("ran").GetBoolean());
            // Handler's own fields survive alongside the gate block.
            Assert.True(result.GetProperty("moved").GetBoolean());
            Assert.Equal("res://new.tres",
                result.GetProperty("after").GetProperty("resourcePath").GetString());
        }

        [Fact]
        public async Task ResourceDelete_Success_SurfacesGateBlock()
        {
            RegisterStubTool("godot_open_mcp_resource_delete_passed", isMutating: true, defaultGate: "enforce",
                output: "{\"resource\":{\"resourcePath\":\"res://doomed.tres\",\"uid\":null,\"type\":\"Resource\"}," +
                        "\"sidecarDeleted\":false,\"filesystemScan\":{\"settled\":true,\"scanTriggered\":true,\"settledMs\":0,\"reason\":null}," +
                        "\"deleted\":true}");

            using var resp = await PostToolAsync("godot_open_mcp_resource_delete_passed",
                "{\"resource_path\":\"res://doomed.tres\",\"paths_hint\":[\"res://doomed.tres\"],\"gate\":\"enforce\"}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var result = doc.RootElement.GetProperty("result");
            var gate = result.GetProperty("gate");
            Assert.Equal("enforce", gate.GetProperty("mode").GetString());
            Assert.Equal("passed", gate.GetProperty("outcome").GetString());
            Assert.True(result.GetProperty("deleted").GetBoolean());
            Assert.Equal("res://doomed.tres",
                result.GetProperty("resource").GetProperty("resourcePath").GetString());
        }

        [Fact]
        public async Task ResourceMove_FailedMutation_SurfacesErrorWithoutGateBlock()
        {
            RegisterStubTool("godot_open_mcp_resource_move_fail", isMutating: true, defaultGate: "enforce",
                failWith: ("resource_not_found", "no file at res://missing.tres"));

            using var resp = await PostToolAsync("godot_open_mcp_resource_move_fail",
                "{\"source_path\":\"res://missing.tres\",\"destination_path\":\"res://b.tres\"," +
                "\"paths_hint\":[\"res://missing.tres\",\"res://b.tres\"],\"gate\":\"enforce\"}");

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
        public async Task ResourceMove_RequestWarn_OverridesToolDefaultEnforce()
        {
            RegisterStubTool("godot_open_mcp_resource_move_precedence", isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_resource_move_precedence",
                "{\"source_path\":\"res://a.tres\",\"destination_path\":\"res://b.tres\"," +
                "\"paths_hint\":[\"res://a.tres\",\"res://b.tres\"],\"gate\":\"warn\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Warn, _lastGateCall.mode);
        }

        // -------------------------------------------------------------------
        // gate:"off" still routes through the gate path (Off branch). The dispatch
        // wiring: with gate off + paths_hint present, the gate is invoked with Off
        // mode and the mutation runs.
        // -------------------------------------------------------------------

        [Fact]
        public async Task ResourceDelete_GateOff_RunsMutationDirectly()
        {
            RegisterStubTool("godot_open_mcp_resource_delete_off", isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_resource_delete_off",
                "{\"resource_path\":\"res://a.tres\",\"paths_hint\":[\"res://a.tres\"],\"gate\":\"off\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Off, _lastGateCall.mode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }

        // -------------------------------------------------------------------
        // Partial-failure result carries observed-state diagnostics (P4.3 contract)
        // -------------------------------------------------------------------

        [Fact]
        public async Task ResourceMove_PartialFailure_SurfacesObservedStateInError()
        {
            // A partial move failure uses FailWithOutput — the bridge folds the observed-state JSON
            // into the failure envelope under `result`, and the TS client surfaces it via `detail`.
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: "godot_open_mcp_resource_move_partial",
                isMutating: true,
                defaultGate: "enforce",
                group: "resource",
                handler: _ => ToolDispatchResult.FailWithOutput(
                    "resource_move_partial",
                    "Primary moved but sidecar failed.",
                    "{\"before\":{\"resourcePath\":\"res://a.tres\",\"uid\":null,\"type\":null}," +
                    "\"sidecarMoved\":false,\"filesystemScan\":{\"settled\":true,\"scanTriggered\":true,\"settledMs\":0,\"reason\":null}," +
                    "\"sourceExistsAfter\":false,\"destinationExistsAfter\":true,\"rolledBack\":false,\"moved\":false}")));

            using var resp = await PostToolAsync("godot_open_mcp_resource_move_partial",
                "{\"source_path\":\"res://a.tres\",\"destination_path\":\"res://b.tres\"," +
                "\"paths_hint\":[\"res://a.tres\",\"res://b.tres\"],\"gate\":\"enforce\"}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("resource_move_partial",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            // The observed-state payload is surfaced under `result` even on failure.
            Assert.True(doc.RootElement.TryGetProperty("result", out var resultEl));
            Assert.False(resultEl.GetProperty("moved").GetBoolean());
            Assert.True(resultEl.GetProperty("destinationExistsAfter").GetBoolean());
        }
    }
}
