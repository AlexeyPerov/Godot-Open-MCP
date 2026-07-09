#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// Pure-logic tests for <see cref="GatePolicy"/>'s decision matrix, agent-next-step guidance,
    /// issue-key parsing, and gate-mode parsing. Ported (copy) from Unity Open MCP's
    /// <c>GatePolicyTests</c>, adapted for Godot tool names (<c>godot_open_mcp_*</c>) and Godot issue
    /// codes (<c>broken_scene_reference</c>, <c>missing_script</c>).
    ///
    /// <para>
    /// These tests do NOT exercise <see cref="GatePolicy.Execute"/> — that path calls
    /// <see cref="VerifyGateAdapter"/> which needs live verify rules. The dispatch-level proof that
    /// mutators route through <see cref="GatePolicy.Execute"/> lives in
    /// <see cref="GateDispatchIntegrationTests"/>; the delta math lives in
    /// <see cref="VerifyGateAdapterTests"/>.
    /// </para>
    /// </summary>
    public class GatePolicyTests
    {
        // -------------------------------------------------------------------
        // ResolveOutcome — the mutate→gate decision matrix
        // -------------------------------------------------------------------

        [Fact]
        public void ResolveOutcome_NewErrors_Enforce_Fails()
        {
            var delta = Delta(errors: 2);
            var (outcome, gateFailed) = GatePolicy.ResolveOutcome(GateMode.Enforce, delta);

            Assert.Equal(GateOutcome.Failed, outcome);
            Assert.True(gateFailed, "new errors in Enforce must hard-fail the gate");
        }

        [Fact]
        public void ResolveOutcome_NewErrors_Warn_WarnsButDoesNotFail()
        {
            var delta = Delta(errors: 2);
            var (outcome, gateFailed) = GatePolicy.ResolveOutcome(GateMode.Warn, delta);

            Assert.Equal(GateOutcome.Warned, outcome);
            Assert.False(gateFailed, "warn mode must not hard-fail on errors");
        }

        [Fact]
        public void ResolveOutcome_NewWarnings_Warn_Warns()
        {
            var delta = Delta(warnings: 3);
            var (outcome, gateFailed) = GatePolicy.ResolveOutcome(GateMode.Warn, delta);

            Assert.Equal(GateOutcome.Warned, outcome);
            Assert.False(gateFailed);
        }

        [Fact]
        public void ResolveOutcome_NewWarnings_Enforce_Passes()
        {
            // In Enforce mode only errors block; warnings alone pass.
            var delta = Delta(warnings: 3);
            var (outcome, gateFailed) = GatePolicy.ResolveOutcome(GateMode.Enforce, delta);

            Assert.Equal(GateOutcome.Passed, outcome);
            Assert.False(gateFailed);
        }

        [Fact]
        public void ResolveOutcome_NoNewIssues_Passes()
        {
            var delta = Delta();
            var (outcome, gateFailed) = GatePolicy.ResolveOutcome(GateMode.Enforce, delta);

            Assert.Equal(GateOutcome.Passed, outcome);
            Assert.False(gateFailed);
        }

        [Fact]
        public void ResolveOutcome_ErrorsTakePriority_OverWarnings()
        {
            // Both new errors and warnings present -> the error branch wins.
            var delta = Delta(errors: 1, warnings: 5);
            var (outcome, _) = GatePolicy.ResolveOutcome(GateMode.Enforce, delta);
            Assert.Equal(GateOutcome.Failed, outcome);
        }

        // -------------------------------------------------------------------
        // GenerateAgentNextSteps — actionable guidance per outcome
        // -------------------------------------------------------------------

        [Fact]
        public void NextSteps_Failed_MentionsErrorCount_AndValidateEdit()
        {
            var delta = Delta(errors: 2,
                issueKeys: new[] { "broken_references|ERROR|res://Scenes/Main.tscn|broken_scene_reference" });
            var steps = GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Failed);

            Assert.NotEmpty(steps);
            Assert.Contains("2 new error(s)", Join(steps));
            Assert.Contains("validate_edit", Join(steps));
        }

        [Fact]
        public void NextSteps_Failed_MalformedIssueKey_DoesNotCrash()
        {
            // A key with fewer than 4 pipe-separated parts must not throw.
            var delta = Delta(errors: 1, issueKeys: new[] { "garbage" });
            var steps = GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Failed);

            Assert.NotEmpty(steps);
            Assert.Contains("Review the affected asset", Join(steps));
        }

        [Fact]
        public void NextSteps_Failed_NullIssueKeys_DoesNotCrash()
        {
            var delta = Delta(errors: 1, issueKeys: null);
            Assert.Null(delta.NewIssueKeys);
            var ex = Record.Exception(() => GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Failed));
            Assert.Null(ex);
        }

        [Fact]
        public void NextSteps_Warned_WarningsOnly_SuggestsReviewGuidance()
        {
            var delta = Delta(warnings: 4);
            var steps = GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Warned);

            Assert.Contains("4 new warning(s)", Join(steps));
            Assert.Contains("validate_edit", Join(steps));
        }

        [Fact]
        public void NextSteps_Passed_WithResolvedErrors_NotesResolution()
        {
            var delta = Delta(resolvedErrors: 3);
            var steps = GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Passed);

            Assert.Contains("3 previously reported error(s) resolved", Join(steps));
        }

        [Fact]
        public void NextSteps_Passed_Clean_ReportsNoNewIssues()
        {
            var delta = Delta();
            var steps = GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Passed);

            Assert.Contains("no new issues detected", Join(steps));
        }

        // -------------------------------------------------------------------
        // ParseIssueKey — robustness
        // -------------------------------------------------------------------

        [Fact]
        public void ParseIssueKey_WellFormed_ParsesAllParts()
        {
            var parsed = GatePolicy.ParseIssueKey(
                "broken_references|ERROR|res://Scenes/Main.tscn|broken_scene_reference");

            Assert.NotNull(parsed);
            var p = parsed!.Value;
            Assert.Equal("broken_references", p.RuleId);
            Assert.Equal("ERROR", p.Severity);
            Assert.Equal("res://Scenes/Main.tscn", p.AssetPath);
            Assert.Equal("broken_scene_reference", p.IssueCode);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("only|three|parts")]
        [InlineData("a|b|c|d|e")]
        public void ParseIssueKey_Malformed_ReturnsNull(string? key)
        {
            Assert.Null(GatePolicy.ParseIssueKey(key));
        }

        // -------------------------------------------------------------------
        // ParseMode — string → GateMode (case-sensitive; unknown → Enforce)
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("warn", GateMode.Warn)]
        [InlineData("off", GateMode.Off)]
        [InlineData("enforce", GateMode.Enforce)]
        [InlineData("", GateMode.Enforce)]
        [InlineData(null, GateMode.Enforce)]
        [InlineData("garbage", GateMode.Enforce)]
        [InlineData("WARN", GateMode.Enforce,
            "mode parsing is case-sensitive; unknown → Enforce")]
        public void ParseMode_Maps_KnownAndUnknownValues(string? mode, GateMode expected, string? _ = null)
        {
            Assert.Equal(expected, GatePolicy.ParseMode(mode));
        }

        // -------------------------------------------------------------------
        // helpers
        // -------------------------------------------------------------------

        static DeltaData Delta(
            int errors = 0, int warnings = 0,
            int resolvedErrors = 0,
            string[]? issueKeys = null)
        {
            return new DeltaData
            {
                NewErrors = errors,
                NewWarnings = warnings,
                ResolvedErrors = resolvedErrors,
                NewIssueKeys = issueKeys,
            };
        }

        static string Join(string[] steps) => string.Join("\n", steps);
    }
}
