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
    /// P4.4 gate-dispatch integration tests for the filesystem reimport mutator
    /// (<c>godot_open_mcp_filesystem_reimport</c>). Proves:
    /// <list type="bullet">
    ///   <item>The mutator routes through <see cref="GatePolicy.Execute"/> exactly once.</item>
    ///   <item>Missing <c>paths_hint</c> under gate enforce/warn fails with
    ///   <c>paths_hint_required</c> (cheap-reject before the main-thread hop).</item>
    ///   <item>The handler-level <c>paths_hint</c> guard fires even under <c>gate:"off"</c>
    ///   (P4.4 mandate: <c>paths_hint</c> is always required for this mutator).</item>
    ///   <item>Gate outcome (<c>passed</c>/<c>failed</c>) is surfaced in the result envelope.</item>
    ///   <item>Failed mutation surfaces the error without a gate block.</item>
    ///   <item>Handler output fields survive alongside the gate block.</item>
    /// </list>
    ///
    /// <para>
    /// The read-only <c>godot_open_mcp_filesystem_list</c> (default gate <c>off</c>) is NOT tested
    /// here — it bypasses the gate entirely via <c>GateDispatchResult.Direct</c>, the same path as
    /// <c>resource_find</c>/<c>resource_get_data</c>.
    /// </para>
    ///
    /// <para>
    /// The tests install <see cref="GatePolicy.SetExecuteForTests"/> so the checkpoint→validate→delta
    /// cycle is stubbed (no live verify rules / Godot editor needed). The reimport handler is stubbed
    /// — the handler's own paths_hint guard and EditorFileSystem logic are covered by the headless
    /// Godot smoke, not here (they need a live Godot editor for EditorInterface/FileAccess).
    /// </para>
    /// </summary>
    [Collection(nameof(BridgeHttpServerTests))]
    public class FileSystemReimportGateTests : IAsyncLifetime
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
                group: "filesystem",
                handler: _ =>
                {
                    if (failWith != null)
                        return ToolDispatchResult.Fail(failWith.Value.code, failWith.Value.message);
                    return ToolDispatchResult.Ok(output ?? "{\"reimported\":true}");
                }));
        }

        // -------------------------------------------------------------------
        // Reimport mutator routes through GatePolicy.Execute exactly once
        // -------------------------------------------------------------------

        [Fact]
        public async Task FilesystemReimport_EnforceGate_InvokesGatePolicyOnce()
        {
            RegisterStubTool("godot_open_mcp_filesystem_reimport",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_filesystem_reimport",
                "{\"files\":[\"res://a.tres\"],\"paths_hint\":[\"res://a.tres\"],\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Enforce, _lastGateCall.mode);
            Assert.Equal(new[] { "res://a.tres" }, _lastGateCall.pathsHint);
        }

        [Fact]
        public async Task FilesystemReimport_FullScan_InvokesGatePolicyOnce()
        {
            RegisterStubTool("godot_open_mcp_filesystem_reimport_scan",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_filesystem_reimport_scan",
                "{\"paths_hint\":[\"res://\"],\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Enforce, _lastGateCall.mode);
            Assert.Equal(new[] { "res://" }, _lastGateCall.pathsHint);
        }

        // -------------------------------------------------------------------
        // Missing paths_hint under enforce → paths_hint_required (cheap reject)
        // -------------------------------------------------------------------

        [Fact]
        public async Task FilesystemReimport_EnforceGate_NoPathsHint_ReturnsPathsHintRequired()
        {
            RegisterStubTool("godot_open_mcp_filesystem_reimport_nohint",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_filesystem_reimport_nohint",
                "{\"files\":[\"res://a.tres\"],\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("paths_hint_required",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(0, _gateCallCount);
        }

        [Fact]
        public async Task FilesystemReimport_FullScan_NoPathsHint_ReturnsPathsHintRequired()
        {
            RegisterStubTool("godot_open_mcp_filesystem_reimport_scan_nohint",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_filesystem_reimport_scan_nohint",
                "{\"gate\":\"enforce\"}");

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
        public async Task FilesystemReimport_Success_SurfacesGateBlock()
        {
            RegisterStubTool("godot_open_mcp_filesystem_reimport_passed",
                isMutating: true, defaultGate: "enforce",
                output: "{\"mode\":\"files\",\"requestedFiles\":[\"res://a.tres\"]," +
                        "\"reimportedCount\":1," +
                        "\"settle\":{\"scanStarted\":true,\"settled\":true,\"scanningProgress\":null,\"elapsedMs\":0,\"reason\":null}," +
                        "\"reimported\":true}");

            using var resp = await PostToolAsync("godot_open_mcp_filesystem_reimport_passed",
                "{\"files\":[\"res://a.tres\"],\"paths_hint\":[\"res://a.tres\"],\"gate\":\"enforce\"}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var result = doc.RootElement.GetProperty("result");
            var gate = result.GetProperty("gate");
            Assert.Equal("enforce", gate.GetProperty("mode").GetString());
            Assert.Equal("passed", gate.GetProperty("outcome").GetString());
            Assert.True(gate.GetProperty("ran").GetBoolean());
            // Handler's own fields survive alongside the gate block.
            Assert.True(result.GetProperty("reimported").GetBoolean());
            Assert.Equal("files", result.GetProperty("mode").GetString());
            Assert.Equal(1, result.GetProperty("reimportedCount").GetInt32());
        }

        [Fact]
        public async Task FilesystemReimport_FailedMutation_SurfacesErrorWithoutGateBlock()
        {
            RegisterStubTool("godot_open_mcp_filesystem_reimport_fail",
                isMutating: true, defaultGate: "enforce",
                failWith: ("file_not_found", "no file at res://missing.tres"));

            using var resp = await PostToolAsync("godot_open_mcp_filesystem_reimport_fail",
                "{\"files\":[\"res://missing.tres\"],\"paths_hint\":[\"res://missing.tres\"],\"gate\":\"enforce\"}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("file_not_found",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.False(doc.RootElement.TryGetProperty("result", out _));
        }

        // -------------------------------------------------------------------
        // Gate precedence: request gate overrides tool default
        // -------------------------------------------------------------------

        [Fact]
        public async Task FilesystemReimport_RequestWarn_OverridesToolDefaultEnforce()
        {
            RegisterStubTool("godot_open_mcp_filesystem_reimport_precedence",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_filesystem_reimport_precedence",
                "{\"files\":[\"res://a.tres\"],\"paths_hint\":[\"res://a.tres\"],\"gate\":\"warn\"}");

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
        public async Task FilesystemReimport_GateOff_RunsMutationDirectly()
        {
            RegisterStubTool("godot_open_mcp_filesystem_reimport_off",
                isMutating: true, defaultGate: "enforce");

            using var resp = await PostToolAsync("godot_open_mcp_filesystem_reimport_off",
                "{\"files\":[\"res://a.tres\"],\"paths_hint\":[\"res://a.tres\"],\"gate\":\"off\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Off, _lastGateCall.mode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }

        // -------------------------------------------------------------------
        // A reimport_failed carries the observed-state payload via FailWithOutput.
        // -------------------------------------------------------------------

        [Fact]
        public async Task FilesystemReimport_Failure_SurfacesObservedStateInError()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: "godot_open_mcp_filesystem_reimport_failed_state",
                isMutating: true,
                defaultGate: "enforce",
                group: "filesystem",
                handler: _ => ToolDispatchResult.FailWithOutput(
                    "reimport_failed",
                    "EditorFileSystem.ReimportFiles returned ErrInvalid.",
                    "{\"mode\":\"files\",\"requestedFiles\":[\"res://a.tres\"]," +
                    "\"reimportedCount\":0," +
                    "\"settle\":{\"scanStarted\":false,\"settled\":false,\"scanningProgress\":null,\"elapsedMs\":0,\"reason\":\"not_attempted\"}," +
                    "\"reimported\":false}")));

            using var resp = await PostToolAsync("godot_open_mcp_filesystem_reimport_failed_state",
                "{\"files\":[\"res://a.tres\"],\"paths_hint\":[\"res://a.tres\"],\"gate\":\"enforce\"}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("reimport_failed",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            // The observed-state payload is surfaced under `result` even on failure.
            Assert.True(doc.RootElement.TryGetProperty("result", out var resultEl));
            Assert.False(resultEl.GetProperty("reimported").GetBoolean());
            Assert.Equal("files", resultEl.GetProperty("mode").GetString());
        }
    }
}
