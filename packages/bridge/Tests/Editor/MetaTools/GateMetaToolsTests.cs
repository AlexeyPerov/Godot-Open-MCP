#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using GodotOpenMcp.Bridge.Editor;
using GodotOpenMcp.Verify.Core;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// Tests for the P3.6 gate meta-tools' structured-error + session-recovery paths and the
    /// <see cref="GateTools"/> registration. The healthy paths of <see cref="ValidateEditTool"/> /
    /// <see cref="CheckpointCreateTool"/> / <see cref="DeltaTool"/> route through
    /// <see cref="VerifyGateAdapter"/>, which instantiates the live verify rules (touching
    /// <c>ResourceLoader</c> / <c>EditorInterface</c>); those paths are covered by the headless-Godot
    /// smoke, not the binary-less unit host.
    ///
    /// <para>
    /// What IS unit-tested here, without a live editor:
    /// <list type="bullet">
    ///   <item><see cref="ValidateEditTool.Execute"/> <c>missing_parameter</c> path (empty/absent
    ///   paths) — returns before the adapter is touched.</item>
    ///   <item><see cref="DeltaTool.Execute"/> <c>missing_parameter</c> path (empty checkpoint_id).</item>
    ///   <item><see cref="DeltaTool.Execute"/> <c>checkpoint unavailable</c> path — the load-bearing
    ///   acceptance criterion: a missing checkpoint returns a structured <c>unavailable</c> payload
    ///   via <c>Ok</c>, NOT a hard failure. <see cref="CheckpointStore.Get"/> returns null and the
    ///   tool builds the recovery guidance with no adapter call.</item>
    ///   <item><see cref="GateTools.RegisterGateTools"/> — all three tools register with the correct
    ///   <c>godot_open_mcp_*</c> names, read-only, <c>core</c> group.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Ported (copy for the structured-error / recovery test shape) from Unity Open MCP's meta-tool
    /// tests; the JSON shape assertions parse the output with <see cref="JsonDocument"/> so a
    /// malformed payload surfaces as a parse failure, not a string-substring false-pass.
    /// </para>
    /// </summary>
    public class GateMetaToolsTests
    {
        public GateMetaToolsTests()
        {
            // DeltaTool's checkpoint-missing path consults the process-static CheckpointStore; Clear
            // it so a prior test's stored entry cannot mask the missing-checkpoint case.
            CheckpointStore.Clear();
        }

        // --- validate_edit: missing_parameter -----------------------------------

        [Theory]
        [InlineData("{}")]                                    // absent
        [InlineData("{\"paths\":null}")]                      // literal null
        [InlineData("{\"label\":\"no paths\"}")]              // unrelated fields only
        public void ValidateEdit_AbsentOrNullPaths_ReturnsMissingParameter(string body)
        {
            var result = ValidateEditTool.Execute(body);
            Assert.False(result.Success);
            Assert.Equal("missing_parameter", result.ErrorCode);
        }

        [Fact]
        public void ValidateEdit_EmptyPathsArray_ReturnsMissingParameter()
        {
            var result = ValidateEditTool.Execute("{\"paths\":[]}");
            Assert.False(result.Success);
            Assert.Equal("missing_parameter", result.ErrorCode);
            Assert.Contains("'paths'", result.ErrorMessage);
        }

        // --- delta: missing_parameter -------------------------------------------

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"checkpoint_id\":null}")]
        [InlineData("{\"paths\":[\"res://Main.tscn\"]}")]
        public void Delta_EmptyCheckpointId_ReturnsMissingParameter(string body)
        {
            var result = DeltaTool.Execute(body);
            Assert.False(result.Success);
            Assert.Equal("missing_parameter", result.ErrorCode);
            Assert.Contains("'checkpoint_id'", result.ErrorMessage);
        }

        // --- delta: checkpoint unavailable (acceptance criterion) ---------------

        [Fact]
        public void Delta_UnknownCheckpointId_ReturnsUnavailableNotError()
        {
            // Load-bearing: a missing checkpoint must NOT be a hard failure. It returns Ok with a
            // structured `unavailable` payload so the agent can fall back to validate_edit. This is
            // the P3.6 acceptance criterion "delta response distinguishes 'checkpoint unavailable'
            // from hard execution failures".
            var result = DeltaTool.Execute("{\"checkpoint_id\":\"cp_does_not_exist\"}");

            Assert.True(result.Success, "missing checkpoint must return Ok, not Fail");
            Assert.NotNull(result.Output);

            using var doc = JsonDocument.Parse(result.Output!);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("passed").GetBoolean(),
                "passed must be true so the agent treats this as 'no new errors'");
            Assert.True(root.GetProperty("unavailable").GetBoolean(),
                "unavailable must be true to signal the baseline is gone");
            Assert.Contains("no longer available", root.GetProperty("warning").GetString());

            // The agentNextSteps must reference the godot_open_mcp_* tools (ADR-003), NOT the Unity
            // tool names — guards against a verbatim Unity copy leaking unity_open_mcp_* into the
            // recovery text. Not every step names a tool (the first explains the situation), so
            // assert: at least one step names a godot tool, and none name a unity tool.
            var steps = root.GetProperty("agentNextSteps").EnumerateArray();
            int count = 0;
            bool anyGodotTool = false;
            foreach (var step in steps)
            {
                count++;
                var text = step.GetString()!;
                if (text.Contains("godot_open_mcp_")) anyGodotTool = true;
                Assert.DoesNotContain("unity_open_mcp_", text);
            }
            Assert.True(count >= 3, "expected at least 3 recovery next-steps");
            Assert.True(anyGodotTool, "at least one next-step should name a godot_open_mcp_* tool");
        }

        [Fact]
        public void Delta_AvailableCheckpoint_ButAdapterNotStubbed_DoesNotCrashOnMissing()
        {
            // Belt-and-braces: even if an entry somehow exists with a fingerprint the adapter cannot
            // re-validate in the binary-less host, the missing-checkpoint branch is the one we assert
            // here. Store an entry, then confirm a different id still returns unavailable.
            CheckpointStore.Store(new CheckpointStoreEntry
            {
                CheckpointId = "cp_present",
                Timestamp = "2026-07-01T00:00:00Z",
                LastAccessedUtc = "2026-07-01T00:00:00Z",
                Paths = new[] { "res://Main.tscn" },
                Categories = Array.Empty<string>(),
                Fingerprint = new CheckpointFingerprint("cp_present",
                    new Dictionary<string, RuleFingerprint>()),
            });

            var result = DeltaTool.Execute("{\"checkpoint_id\":\"cp_other\"}");
            Assert.True(result.Success);
            using var doc = JsonDocument.Parse(result.Output!);
            Assert.True(doc.RootElement.GetProperty("unavailable").GetBoolean());
        }

        // --- GateTools registration ---------------------------------------------

        [Fact]
        public void RegisterGateTools_RegistersAllThreeToolsReadOnlyCoreGroup()
        {
            BridgeToolRegistry.ResetForTests();
            GateTools.RegisterGateTools();

            Assert.True(BridgeToolRegistry.Contains(GateTools.ValidateEditToolName));
            Assert.True(BridgeToolRegistry.Contains(GateTools.CheckpointCreateToolName));
            Assert.True(BridgeToolRegistry.Contains(GateTools.DeltaToolName));

            Assert.Equal("godot_open_mcp_validate_edit", GateTools.ValidateEditToolName);
            Assert.Equal("godot_open_mcp_checkpoint_create", GateTools.CheckpointCreateToolName);
            Assert.Equal("godot_open_mcp_delta", GateTools.DeltaToolName);

            // All three are read-only (bypass the gate) and in the core group.
            var entry = BridgeToolRegistry.TryGet(GateTools.ValidateEditToolName, out var ve);
            Assert.True(entry);
            Assert.False(ve!.IsMutating);
            Assert.Equal("off", ve.DefaultGate);
            Assert.Equal("core", ve.Group);

            Assert.True(BridgeToolRegistry.TryGet(GateTools.CheckpointCreateToolName, out var ce));
            Assert.False(ce!.IsMutating);
            Assert.Equal("core", ce.Group);

            Assert.True(BridgeToolRegistry.TryGet(GateTools.DeltaToolName, out var de));
            Assert.False(de!.IsMutating);
            Assert.Equal("core", de.Group);

            BridgeToolRegistry.ResetForTests();
        }
    }
}
