#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// Unit tests for the P3.5 <see cref="BridgeRequestBody"/> extractors (<c>gate</c> mode
    /// precedence, <c>paths_hint</c> array parsing) and the gate-aware <see cref="BridgeEnvelope"/>
    /// builders. Pure-managed, no HTTP — locks the parser + envelope contracts the dispatch path
    /// depends on.
    /// </summary>
    public class GateRequestBodyAndEnvelopeTests
    {
        // -------------------------------------------------------------------
        // ExtractGateMode — request → project default → tool default
        // -------------------------------------------------------------------

        [Fact]
        public void ExtractGateMode_RequestValue_WinsOverToolDefault()
        {
            var mode = BridgeRequestBody.ExtractGateMode("{\"gate\":\"enforce\"}", toolDefault: "off");
            Assert.Equal("enforce", mode);
        }

        [Fact]
        public void ExtractGateMode_NoRequest_FallsBackToToolDefault()
        {
            // v1 has no project settings file, so the chain is request → toolDefault.
            var mode = BridgeRequestBody.ExtractGateMode("{}", toolDefault: "off");
            Assert.Equal("off", mode);
        }

        [Fact]
        public void ExtractGateMode_InvalidRequest_FallsBackToToolDefault()
        {
            // A typo'd request value does NOT silently disable the gate — it falls through to the tool
            // default rather than becoming "off".
            var mode = BridgeRequestBody.ExtractGateMode("{\"gate\":\"stricte\"}", toolDefault: "off");
            Assert.Equal("off", mode);
        }

        [Fact]
        public void ExtractGateMode_EmptyBody_FallsBackToToolDefault()
        {
            var mode = BridgeRequestBody.ExtractGateMode("", toolDefault: "warn");
            Assert.Equal("warn", mode);
        }

        [Theory]
        [InlineData("{\"gate\":\"enforce\"}", "enforce")]
        [InlineData("{\"gate\":\"warn\"}", "warn")]
        [InlineData("{\"gate\":\"off\"}", "off")]
        public void ExtractGateMode_ValidModes_ParseCorrectly(string body, string expected)
        {
            Assert.Equal(expected, BridgeRequestBody.ExtractGateMode(body, toolDefault: "off"));
        }

        // -------------------------------------------------------------------
        // ExtractPathsHint — JSON string-array parsing
        // -------------------------------------------------------------------

        [Fact]
        public void ExtractPathsHint_Absent_ReturnsNull()
        {
            Assert.Null(BridgeRequestBody.ExtractPathsHint("{\"gate\":\"enforce\"}"));
        }

        [Fact]
        public void ExtractPathsHint_EmptyArray_ReturnsEmptyArray()
        {
            var hint = BridgeRequestBody.ExtractPathsHint("{\"paths_hint\":[]}");
            Assert.NotNull(hint);
            Assert.Empty(hint);
        }

        [Fact]
        public void ExtractPathsHint_OnePath_ReturnsArray()
        {
            var hint = BridgeRequestBody.ExtractPathsHint("{\"paths_hint\":[\"res://Main.tscn\"]}");
            Assert.Equal(new[] { "res://Main.tscn" }, hint);
        }

        [Fact]
        public void ExtractPathsHint_MultiplePaths_ReturnsAllInOrder()
        {
            var hint = BridgeRequestBody.ExtractPathsHint(
                "{\"paths_hint\":[\"res://A.tscn\",\"res://B.tscn\",\"res://C.tres\"]}");
            Assert.Equal(new[] { "res://A.tscn", "res://B.tscn", "res://C.tres" }, hint);
        }

        [Fact]
        public void ExtractPathsHint_NonArrayValue_ReturnsNull()
        {
            // A string instead of an array is a contract violation → null (so the dispatch guard fires
            // paths_hint_required).
            Assert.Null(BridgeRequestBody.ExtractPathsHint("{\"paths_hint\":\"res://A.tscn\"}"));
        }

        [Fact]
        public void ExtractPathsHint_EscapedQuote_DecodesCorrectly()
        {
            // A path with an escaped quote — rare but the parser must round-trip it.
            var hint = BridgeRequestBody.ExtractPathsHint("{\"paths_hint\":[\"res://a\\\"b.tscn\"]}");
            Assert.Equal(new[] { "res://a\"b.tscn" }, hint);
        }

        // -------------------------------------------------------------------
        // BuildPathsHintRequired — structured error envelope
        // -------------------------------------------------------------------

        [Fact]
        public void BuildPathsHintRequired_IsFailureEnvelopeWithCode()
        {
            var envelope = BridgeEnvelope.BuildPathsHintRequired("godot_open_mcp_node_create", "enforce");
            Assert.Contains("\"ok\":false", envelope);
            Assert.Contains("\"paths_hint_required\"", envelope);
            Assert.Contains("godot_open_mcp_node_create", envelope);
            Assert.Contains("enforce", envelope);
        }

        // -------------------------------------------------------------------
        // BuildGateSuccess — gate block prepended into handler output
        // -------------------------------------------------------------------

        [Fact]
        public void BuildGateSuccess_ObjectOutput_PrependsGateBlockAsFirstKey()
        {
            var result = new GateDispatchResult
            {
                Mutation = ToolDispatchResult.Ok("{\"count\":1,\"name\":\"Player\"}"),
                GateRan = true,
                Outcome = GateOutcome.Passed,
                GateFailed = false,
                CheckpointId = "cp_abc",
                CategoriesRun = new[] { "broken_references" },
                Delta = new DeltaData(),
                AgentNextSteps = new[] { "Gate passed — no new issues detected." },
            };

            var envelope = BridgeEnvelope.BuildGateSuccess(result, "enforce");

            Assert.Contains("\"ok\":true", envelope);
            // The gate block is the FIRST key inside result.
            var gateIdx = envelope.IndexOf("\"gate\"", System.StringComparison.Ordinal);
            var countIdx = envelope.IndexOf("\"count\"", System.StringComparison.Ordinal);
            Assert.True(gateIdx < countIdx, "gate block must precede handler fields");
            Assert.Contains("\"outcome\":\"passed\"", envelope);
            // Handler fields survived.
            Assert.Contains("\"count\":1", envelope);
            Assert.Contains("\"name\":\"Player\"", envelope);
        }

        [Fact]
        public void BuildGateSuccess_GateNotRun_OutcomeSkipped()
        {
            var result = GateDispatchResult.Direct(ToolDispatchResult.Ok("{\"ok\":1}"));

            var envelope = BridgeEnvelope.BuildGateSuccess(result, "off");

            Assert.Contains("\"outcome\":\"skipped\"", envelope);
            Assert.Contains("\"ran\":false", envelope);
            // When the gate did not run, no delta / categoriesRun fields are emitted.
            Assert.DoesNotContain("\"delta\"", envelope);
            Assert.DoesNotContain("\"categoriesRun\"", envelope);
        }

        [Fact]
        public void BuildFromGateResult_FailedMutation_ReturnsFailureEnvelope()
        {
            var result = new GateDispatchResult
            {
                Mutation = ToolDispatchResult.Fail("no_edited_scene", "no scene open"),
                GateRan = true,
                Outcome = GateOutcome.Failed,
                GateFailed = true,
            };

            var envelope = BridgeEnvelope.BuildFromGateResult(result, "enforce");

            Assert.Contains("\"ok\":false", envelope);
            Assert.Contains("\"no_edited_scene\"", envelope);
            // No result / gate block on a mutation failure.
            Assert.DoesNotContain("\"gate\"", envelope);
        }
    }
}
