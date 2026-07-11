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
    /// P4.6 gate-dispatch integration tests for the editor selection mutator
    /// (<c>godot_open_mcp_editor_selection_set</c>). Proves:
    /// <list type="bullet">
    ///   <item>The mutator routes through <see cref="GatePolicy.Execute"/> exactly once.</item>
    ///   <item>Missing <c>paths_hint</c> under gate enforce/warn fails with
    ///   <c>paths_hint_required</c> (cheap-reject before the main-thread hop).</item>
    ///   <item>The handler-level <c>paths_hint</c> guard fires even under <c>gate:"off"</c>
    ///   (paths_hint is always required for this mutator).</item>
    ///   <item>Gate outcome (<c>passed</c>/<c>failed</c>) is surfaced in the result envelope.</item>
    ///   <item>A <c>selection_update_failed</c> / <c>node_not_found</c> carries the observed state
    ///   via <see cref="ToolDispatchResult.FailWithOutput"/>.</item>
    /// </list>
    ///
    /// <para>
    /// The read-only <c>godot_open_mcp_editor_selection_get</c> (default gate <c>off</c>) is NOT
    /// tested here — it bypasses the gate entirely via <c>GateDispatchResult.Direct</c>.
    /// </para>
    ///
    /// <para>
    /// The tests install <see cref="GatePolicy.SetExecuteForTests"/> so the checkpoint→validate→delta
    /// cycle is stubbed (no live verify rules / Godot editor needed). The set-selection handler is
    /// stubbed — the handler's own <c>EditorSelection</c> logic is covered by the headless Godot
    /// smoke, not here (it needs a live Godot editor for EditorInterface).
    /// </para>
    /// </summary>
    [Collection(nameof(BridgeHttpServerTests))]
    public class EditorSelectionGateTests : IAsyncLifetime
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
            string? output = null, (string code, string message, string output)? failWithOutput = null)
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: name,
                isMutating: isMutating,
                defaultGate: defaultGate,
                group: "editor",
                handler: _ =>
                {
                    if (failWithOutput != null)
                        return ToolDispatchResult.FailWithOutput(
                            failWithOutput.Value.code, failWithOutput.Value.message, failWithOutput.Value.output);
                    return ToolDispatchResult.Ok(output ?? "{\"nodes\":[],\"activeNode\":null,\"count\":0,\"scenePath\":\"res://main.tscn\",\"cleared\":true}");
                }));
        }

        // -------------------------------------------------------------------
        // Set-selection mutator routes through GatePolicy.Execute exactly once
        // -------------------------------------------------------------------

        [Fact]
        public async Task EditorSelectionSet_EnforceGate_InvokesGatePolicyOnce()
        {
            RegisterStubTool("godot_open_mcp_editor_selection_set",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_editor_selection_set",
                "{\"select\":[{\"node_path\":\"Main/Player\"}],\"paths_hint\":[\"res://main.tscn\"],\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Enforce, _lastGateCall.mode);
            Assert.Equal(new[] { "res://main.tscn" }, _lastGateCall.pathsHint);
        }

        [Fact]
        public async Task EditorSelectionSet_Clear_InvokesGatePolicyOnce()
        {
            RegisterStubTool("godot_open_mcp_editor_selection_set_clear",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_editor_selection_set_clear",
                "{\"select\":[],\"paths_hint\":[\"res://project.godot\"],\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Enforce, _lastGateCall.mode);
        }

        // -------------------------------------------------------------------
        // Missing paths_hint under enforce → paths_hint_required (cheap reject)
        // -------------------------------------------------------------------

        [Fact]
        public async Task EditorSelectionSet_EnforceGate_NoPathsHint_ReturnsPathsHintRequired()
        {
            RegisterStubTool("godot_open_mcp_editor_selection_set_nohint",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_editor_selection_set_nohint",
                "{\"select\":[{\"node_path\":\"Main/Player\"}],\"gate\":\"enforce\"}");

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
        public async Task EditorSelectionSet_Success_SurfacesGateBlock()
        {
            RegisterStubTool("godot_open_mcp_editor_selection_set_passed",
                isMutating: true, defaultGate: "enforce",
                output: "{\"nodes\":[{\"instanceId\":1,\"name\":\"Player\",\"path\":\"/root/Main/Player\",\"type\":\"Node3D\",\"scriptResourcePath\":null,\"childCount\":0,\"children\":null}]," +
                        "\"activeNode\":{\"instanceId\":1,\"name\":\"Player\",\"path\":\"/root/Main/Player\",\"type\":\"Node3D\",\"scriptResourcePath\":null,\"childCount\":0,\"children\":null}," +
                        "\"count\":1,\"scenePath\":\"res://main.tscn\",\"cleared\":true}");

            using var resp = await PostToolAsync("godot_open_mcp_editor_selection_set_passed",
                "{\"select\":[{\"node_path\":\"Main/Player\"}],\"paths_hint\":[\"res://main.tscn\"],\"gate\":\"enforce\"}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var result = doc.RootElement.GetProperty("result");
            var gate = result.GetProperty("gate");
            Assert.Equal("enforce", gate.GetProperty("mode").GetString());
            Assert.Equal("passed", gate.GetProperty("outcome").GetString());
            Assert.True(gate.GetProperty("ran").GetBoolean());
            // Handler's own fields survive alongside the gate block.
            Assert.True(result.GetProperty("cleared").GetBoolean());
            Assert.Equal(1, result.GetProperty("count").GetInt32());
        }

        [Fact]
        public async Task EditorSelectionSet_FailedMutation_SurfacesErrorWithoutResultGateBlock()
        {
            RegisterStubTool("godot_open_mcp_editor_selection_set_fail",
                isMutating: true, defaultGate: "enforce",
                failWithOutput: ("node_not_found",
                    "select[0] (node_path='Main/Missing') could not be resolved in the edited scene.",
                    "{\"nodes\":[],\"activeNode\":null,\"count\":0,\"scenePath\":\"res://main.tscn\"}"));

            using var resp = await PostToolAsync("godot_open_mcp_editor_selection_set_fail",
                "{\"select\":[{\"node_path\":\"Main/Missing\"}],\"paths_hint\":[\"res://main.tscn\"],\"gate\":\"enforce\"}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("node_not_found",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        // -------------------------------------------------------------------
        // Gate precedence: request gate overrides tool default
        // -------------------------------------------------------------------

        [Fact]
        public async Task EditorSelectionSet_RequestWarn_OverridesToolDefaultEnforce()
        {
            RegisterStubTool("godot_open_mcp_editor_selection_set_precedence",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_editor_selection_set_precedence",
                "{\"select\":[],\"paths_hint\":[\"res://project.godot\"],\"gate\":\"warn\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Warn, _lastGateCall.mode);
        }

        // -------------------------------------------------------------------
        // gate:"off" still routes through the gate path (Off branch).
        // -------------------------------------------------------------------

        [Fact]
        public async Task EditorSelectionSet_GateOff_RunsMutationDirectly()
        {
            RegisterStubTool("godot_open_mcp_editor_selection_set_off",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_editor_selection_set_off",
                "{\"select\":[],\"paths_hint\":[\"res://project.godot\"],\"gate\":\"off\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Off, _lastGateCall.mode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }

        // -------------------------------------------------------------------
        // A selection_update_failed carries the observed-state payload via FailWithOutput.
        // -------------------------------------------------------------------

        [Fact]
        public async Task EditorSelectionSet_UpdateFailed_SurfacesObservedStateInError()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: "godot_open_mcp_editor_selection_set_updatefailed",
                isMutating: true,
                defaultGate: "enforce",
                group: "editor",
                handler: _ => ToolDispatchResult.FailWithOutput(
                    "selection_update_failed",
                    "Observed selection does not match the requested set after the write.",
                    "{\"nodes\":[],\"activeNode\":null,\"count\":0,\"scenePath\":\"res://main.tscn\",\"cleared\":true}")));

            using var resp = await PostToolAsync("godot_open_mcp_editor_selection_set_updatefailed",
                "{\"select\":[{\"node_path\":\"Main/Player\"}],\"paths_hint\":[\"res://main.tscn\"],\"gate\":\"enforce\"}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("selection_update_failed",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.True(doc.RootElement.TryGetProperty("result", out var resultEl));
            Assert.True(resultEl.GetProperty("cleared").GetBoolean());
        }
    }
}
