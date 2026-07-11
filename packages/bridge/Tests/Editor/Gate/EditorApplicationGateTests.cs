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
    /// P4.5 gate-dispatch integration tests for the editor application-state mutator
    /// (<c>godot_open_mcp_editor_application_set_state</c>). Proves:
    /// <list type="bullet">
    ///   <item>The mutator routes through <see cref="GatePolicy.Execute"/> exactly once.</item>
    ///   <item>Missing <c>paths_hint</c> under gate enforce/warn fails with
    ///   <c>paths_hint_required</c> (cheap-reject before the main-thread hop).</item>
    ///   <item>The handler-level <c>paths_hint</c> guard fires even under <c>gate:"off"</c>
    ///   (paths_hint is always required for this mutator).</item>
    ///   <item>Gate outcome (<c>passed</c>/<c>failed</c>) is surfaced in the result envelope.</item>
    ///   <item>Failed mutation surfaces the error without a gate block.</item>
    ///   <item>A <c>state_transition_timeout</c> / <c>already_playing</c> carries the observed state
    ///   via <see cref="ToolDispatchResult.FailWithOutput"/>.</item>
    /// </list>
    ///
    /// <para>
    /// The read-only <c>godot_open_mcp_editor_application_get_state</c> (default gate <c>off</c>) is
    /// NOT tested here — it bypasses the gate entirely via <c>GateDispatchResult.Direct</c>.
    /// </para>
    ///
    /// <para>
    /// The tests install <see cref="GatePolicy.SetExecuteForTests"/> so the checkpoint→validate→delta
    /// cycle is stubbed (no live verify rules / Godot editor needed). The set-state handler is stubbed
    /// — the handler's own <c>EditorInterface</c> play logic is covered by the headless Godot smoke,
    /// not here (it needs a live Godot editor for EditorInterface).
    /// </para>
    /// </summary>
    [Collection(nameof(BridgeHttpServerTests))]
    public class EditorApplicationGateTests : IAsyncLifetime
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
                    return ToolDispatchResult.Ok(output ?? "{\"requested\":{\"action\":\"start\",\"scene\":\"main\"},\"settled\":true}");
                }));
        }

        // -------------------------------------------------------------------
        // Set-state mutator routes through GatePolicy.Execute exactly once
        // -------------------------------------------------------------------

        [Fact]
        public async Task EditorSetState_EnforceGate_InvokesGatePolicyOnce()
        {
            RegisterStubTool("godot_open_mcp_editor_application_set_state",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_editor_application_set_state",
                "{\"is_playing\":true,\"paths_hint\":[\"res://project.godot\"],\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Enforce, _lastGateCall.mode);
            Assert.Equal(new[] { "res://project.godot" }, _lastGateCall.pathsHint);
        }

        [Fact]
        public async Task EditorSetState_Stop_InvokesGatePolicyOnce()
        {
            RegisterStubTool("godot_open_mcp_editor_application_set_state_stop",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_editor_application_set_state_stop",
                "{\"is_playing\":false,\"paths_hint\":[\"res://project.godot\"],\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Enforce, _lastGateCall.mode);
        }

        // -------------------------------------------------------------------
        // Missing paths_hint under enforce → paths_hint_required (cheap reject)
        // -------------------------------------------------------------------

        [Fact]
        public async Task EditorSetState_EnforceGate_NoPathsHint_ReturnsPathsHintRequired()
        {
            RegisterStubTool("godot_open_mcp_editor_application_set_state_nohint",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_editor_application_set_state_nohint",
                "{\"is_playing\":true,\"gate\":\"enforce\"}");

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
        public async Task EditorSetState_Success_SurfacesGateBlock()
        {
            RegisterStubTool("godot_open_mcp_editor_application_set_state_passed",
                isMutating: true, defaultGate: "enforce",
                output: "{\"requested\":{\"action\":\"start\",\"scene\":\"main\"}," +
                        "\"before\":{\"isPlaying\":false,\"playingScene\":null,\"editorVersion\":\"4.3.stable.mono\",\"observedAt\":\"2026-07-10T19:00:00.000Z\"}," +
                        "\"after\":{\"isPlaying\":true,\"playingScene\":\"res://main.tscn\",\"editorVersion\":\"4.3.stable.mono\",\"observedAt\":\"2026-07-10T19:00:00.000Z\"}," +
                        "\"state\":{\"isPlaying\":true,\"playingScene\":\"res://main.tscn\",\"editorVersion\":\"4.3.stable.mono\",\"observedAt\":\"2026-07-10T19:00:00.000Z\"}," +
                        "\"settled\":true,\"elapsedMs\":0,\"timeoutMs\":5000}");

            using var resp = await PostToolAsync("godot_open_mcp_editor_application_set_state_passed",
                "{\"is_playing\":true,\"paths_hint\":[\"res://project.godot\"],\"gate\":\"enforce\"}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var result = doc.RootElement.GetProperty("result");
            var gate = result.GetProperty("gate");
            Assert.Equal("enforce", gate.GetProperty("mode").GetString());
            Assert.Equal("passed", gate.GetProperty("outcome").GetString());
            Assert.True(gate.GetProperty("ran").GetBoolean());
            // Handler's own fields survive alongside the gate block.
            Assert.True(result.GetProperty("settled").GetBoolean());
            Assert.Equal("start", result.GetProperty("requested").GetProperty("action").GetString());
        }

        [Fact]
        public async Task EditorSetState_FailedMutation_SurfacesErrorWithoutResultGateBlock()
        {
            RegisterStubTool("godot_open_mcp_editor_application_set_state_fail",
                isMutating: true, defaultGate: "enforce",
                failWithOutput: ("state_transition_timeout",
                    "Play start for 'main' was not observed within 5000ms.",
                    "{\"requested\":{\"action\":\"start\",\"scene\":\"main\"}," +
                    "\"state\":{\"isPlaying\":false,\"playingScene\":null,\"editorVersion\":\"4.3.stable.mono\",\"observedAt\":\"2026-07-10T19:00:00.000Z\"}," +
                    "\"settled\":false,\"elapsedMs\":5000,\"timeoutMs\":5000,\"reason\":\"start_not_observed\"}"));

            using var resp = await PostToolAsync("godot_open_mcp_editor_application_set_state_fail",
                "{\"is_playing\":true,\"paths_hint\":[\"res://project.godot\"],\"gate\":\"enforce\"}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("state_transition_timeout",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            // A FailWithOutput surfaces the observed-state payload under `result` even on failure.
            Assert.True(doc.RootElement.TryGetProperty("result", out var resultEl));
            Assert.False(resultEl.GetProperty("settled").GetBoolean());
            Assert.False(resultEl.GetProperty("state").GetProperty("isPlaying").GetBoolean());
        }

        // -------------------------------------------------------------------
        // Gate precedence: request gate overrides tool default
        // -------------------------------------------------------------------

        [Fact]
        public async Task EditorSetState_RequestWarn_OverridesToolDefaultEnforce()
        {
            RegisterStubTool("godot_open_mcp_editor_application_set_state_precedence",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_editor_application_set_state_precedence",
                "{\"is_playing\":true,\"paths_hint\":[\"res://project.godot\"],\"gate\":\"warn\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Warn, _lastGateCall.mode);
        }

        // -------------------------------------------------------------------
        // gate:"off" still routes through the gate path (Off branch).
        // -------------------------------------------------------------------

        [Fact]
        public async Task EditorSetState_GateOff_RunsMutationDirectly()
        {
            RegisterStubTool("godot_open_mcp_editor_application_set_state_off",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_editor_application_set_state_off",
                "{\"is_playing\":true,\"paths_hint\":[\"res://project.godot\"],\"gate\":\"off\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Off, _lastGateCall.mode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }

        // -------------------------------------------------------------------
        // An already_playing carries the observed-state payload via FailWithOutput.
        // -------------------------------------------------------------------

        [Fact]
        public async Task EditorSetState_AlreadyPlaying_SurfacesObservedStateInError()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: "godot_open_mcp_editor_application_set_state_conflict",
                isMutating: true,
                defaultGate: "enforce",
                group: "editor",
                handler: _ => ToolDispatchResult.FailWithOutput(
                    "already_playing",
                    "A play process is already running scene 'res://main.tscn'.",
                    "{\"requested\":{\"action\":\"start\",\"scene\":\"current\"}," +
                    "\"state\":{\"isPlaying\":true,\"playingScene\":\"res://main.tscn\",\"editorVersion\":\"4.3.stable.mono\",\"observedAt\":\"2026-07-10T19:00:00.000Z\"}}")));

            using var resp = await PostToolAsync("godot_open_mcp_editor_application_set_state_conflict",
                "{\"is_playing\":true,\"scene\":\"current\",\"paths_hint\":[\"res://project.godot\"],\"gate\":\"enforce\"}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("already_playing",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.True(doc.RootElement.TryGetProperty("result", out var resultEl));
            Assert.True(resultEl.GetProperty("state").GetProperty("isPlaying").GetBoolean());
        }
    }
}
