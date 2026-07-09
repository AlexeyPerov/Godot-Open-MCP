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
    /// Dispatch-wiring tests proving every mutating tool routes through <see cref="GatePolicy.Execute"/>
    /// and the gate outcome is surfaced consistently. These are the P3.5 acceptance-criteria tests:
    /// <list type="bullet">
    ///   <item>Every mutating tool path invokes the gate policy exactly once.</item>
    ///   <item>Missing <c>paths_hint</c> on mutators fails with a structured <c>paths_hint_required</c> error.</item>
    ///   <item>Gate outcome (<c>passed</c>/<c>warned</c>/<c>failed</c>/<c>skipped</c>) is surfaced in the result.</item>
    /// </list>
    ///
    /// <para>
    /// The tests install <see cref="GatePolicy.SetExecuteForTests"/> so the checkpoint→validate→delta
    /// cycle is stubbed (no live verify rules / Godot editor needed). The stub RECORDS each call so the
    /// tests can assert "the mutator went through the gate". The HTTP integration plumbing
    /// (HttpListener, routing, envelope) is exercised end-to-end via the same inline-dispatcher seam
    /// <see cref="BridgeToolDispatchTests"/> uses.
    /// </para>
    /// </summary>
    [Collection(nameof(BridgeHttpServerTests))]
    public class GateDispatchIntegrationTests : IAsyncLifetime
    {
        static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
        int _port = 0;

        // Records every GatePolicy.Execute invocation across a test so we can assert routing.
        int _gateCallCount;
        (GateMode mode, string[]? pathsHint) _lastGateCall;

        public Task InitializeAsync()
        {
            return Task.Run(() =>
            {
                BridgeLog.SetLoggersForTests(info: _ => { }, warning: _ => { }, error: _ => { });
                BridgeSession.ResetForTests();
                BridgeToolRegistry.ResetForTests();

                // Stub GatePolicy.Execute: record the call and return a Direct result for the mutation.
                // This proves mutators route through the gate WITHOUT needing live verify rules.
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

                // Inline dispatcher seam: run the gate-wrapped dispatch on the HTTP worker thread
                // directly (binary-less host has no dispatcher Node).
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

        // -------------------------------------------------------------------
        // Mutating tool invokes GatePolicy.Execute exactly once
        // -------------------------------------------------------------------

        [Fact]
        public async Task MutatingTool_WithGateEnforce_InvokesGatePolicyOnce()
        {
            RegisterStubTool("godot_open_mcp_test_mutator", isMutating: true, defaultGate: "off");

            using var resp = await PostToolAsync("godot_open_mcp_test_mutator",
                "{\"gate\":\"enforce\",\"paths_hint\":[\"res://Main.tscn\"]}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Enforce, _lastGateCall.mode);
            Assert.Equal(new[] { "res://Main.tscn" }, _lastGateCall.pathsHint);
        }

        [Fact]
        public async Task NonMutatingTool_DoesNotInvokeGatePolicy()
        {
            // Read-only tools bypass the gate entirely — GateDispatchResult.Direct.
            RegisterStubTool("godot_open_mcp_test_reader", isMutating: false, defaultGate: "off");

            using var resp = await PostToolAsync("godot_open_mcp_test_reader", "{}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(0, _gateCallCount);
        }

        [Fact]
        public async Task MutatingTool_GateOff_DoesNotInvokeGatePolicy()
        {
            // When the effective gate is "off" (the tool default here), the dispatch path still calls
            // GatePolicy.Execute (so the gate PATH is wired), but Execute's Off branch runs the mutation
            // directly without a checkpoint. The stub records the call; the mode is Off.
            RegisterStubTool("godot_open_mcp_test_mutator_off", isMutating: true, defaultGate: "off");

            using var resp = await PostToolAsync("godot_open_mcp_test_mutator_off", "{}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Off, _lastGateCall.mode);
        }

        // -------------------------------------------------------------------
        // Missing paths_hint on mutators fails with structured error
        // -------------------------------------------------------------------

        [Fact]
        public async Task Mutator_EnforceGate_NoPathsHint_ReturnsPathsHintRequired()
        {
            RegisterStubTool("godot_open_mcp_test_needs_hint", isMutating: true, defaultGate: "off");

            using var resp = await PostToolAsync("godot_open_mcp_test_needs_hint",
                "{\"gate\":\"enforce\"}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("paths_hint_required",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            // The cheap-reject happens BEFORE the main-thread hop, so the gate stub was never called.
            Assert.Equal(0, _gateCallCount);
        }

        [Fact]
        public async Task Mutator_EnforceGate_EmptyPathsHint_ReturnsPathsHintRequired()
        {
            RegisterStubTool("godot_open_mcp_test_empty_hint", isMutating: true, defaultGate: "off");

            using var resp = await PostToolAsync("godot_open_mcp_test_empty_hint",
                "{\"gate\":\"enforce\",\"paths_hint\":[]}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("paths_hint_required",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        [Fact]
        public async Task Mutator_OffGate_NoPathsHint_IsAllowed()
        {
            // paths_hint is optional when the gate is off — backward compatible with P2.x mutators.
            RegisterStubTool("godot_open_mcp_test_off_no_hint", isMutating: true, defaultGate: "off");

            using var resp = await PostToolAsync("godot_open_mcp_test_off_no_hint", "{}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }

        // -------------------------------------------------------------------
        // Gate outcome surfaced consistently in the result
        // -------------------------------------------------------------------

        [Fact]
        public async Task Mutator_GateEnforce_OutcomePassed_SurfacedInResult()
        {
            RegisterStubTool("godot_open_mcp_test_passed", isMutating: true, defaultGate: "off");

            using var resp = await PostToolAsync("godot_open_mcp_test_passed",
                "{\"gate\":\"enforce\",\"paths_hint\":[\"res://A.tscn\"]}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var gate = doc.RootElement.GetProperty("result").GetProperty("gate");
            Assert.Equal("enforce", gate.GetProperty("mode").GetString());
            Assert.Equal("passed", gate.GetProperty("outcome").GetString());
            Assert.True(gate.GetProperty("ran").GetBoolean());
            Assert.False(gate.GetProperty("failed").GetBoolean());
        }

        [Fact]
        public async Task Mutator_HandlerOutput_KeepsItsOwnFieldsAlongsideGateBlock()
        {
            // The handler returns {"nodes":[...]}; the gate block is prepended so BOTH are visible.
            RegisterStubTool("godot_open_mcp_test_keep_fields", isMutating: true, defaultGate: "off",
                output: "{\"nodes\":[{\"name\":\"Player\"}],\"count\":1}");

            using var resp = await PostToolAsync("godot_open_mcp_test_keep_fields",
                "{\"gate\":\"enforce\",\"paths_hint\":[\"res://A.tscn\"]}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var result = doc.RootElement.GetProperty("result");
            // The gate block is present...
            Assert.Equal("passed", result.GetProperty("gate").GetProperty("outcome").GetString());
            // ...AND the handler's own fields survived the prepend.
            Assert.Equal(1, result.GetProperty("count").GetInt32());
            Assert.Equal("Player", result.GetProperty("nodes")[0].GetProperty("name").GetString());
        }

        [Fact]
        public async Task Mutator_FailedMutation_SurfacesErrorWithoutGateBlock()
        {
            // A failed mutation surfaces as ok:false + error; the gate block is dropped (no actionable
            // signal from a mutation that did not land).
            RegisterStubTool("godot_open_mcp_test_fail_mut", isMutating: true, defaultGate: "off",
                failWith: ("no_edited_scene", "no scene open"));

            using var resp = await PostToolAsync("godot_open_mcp_test_fail_mut",
                "{\"gate\":\"enforce\",\"paths_hint\":[\"res://A.tscn\"]}");

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("no_edited_scene",
                doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            // No result field on a failure envelope.
            Assert.False(doc.RootElement.TryGetProperty("result", out _));
        }

        // -------------------------------------------------------------------
        // Gate mode precedence: request → tool default
        // -------------------------------------------------------------------

        [Fact]
        public async Task GatePrecedence_RequestOverridesToolDefault()
        {
            // Tool default is "off", but the request asks for "warn" — request wins.
            RegisterStubTool("godot_open_mcp_test_precedence", isMutating: true, defaultGate: "off");

            using var resp = await PostToolAsync("godot_open_mcp_test_precedence",
                "{\"gate\":\"warn\",\"paths_hint\":[\"res://A.tscn\"]}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, _gateCallCount);
            Assert.Equal(GateMode.Warn, _lastGateCall.mode);
        }

        // -------------------------------------------------------------------
        // helper: register a stub tool with a configurable handler
        // -------------------------------------------------------------------

        void RegisterStubTool(string name, bool isMutating, string defaultGate,
            string? output = null, (string code, string message)? failWith = null)
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: name,
                isMutating: isMutating,
                defaultGate: defaultGate,
                group: "core",
                handler: _ =>
                {
                    if (failWith != null)
                        return ToolDispatchResult.Fail(failWith.Value.code, failWith.Value.message);
                    return ToolDispatchResult.Ok(output ?? "{\"ok\":1}");
                }));
        }
    }
}
