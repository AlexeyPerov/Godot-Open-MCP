#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Fixes;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Gate mode the bridge applies around a mutating tool call. Maps 1:1 to the <c>gate</c> field the
    /// MCP tools expose. Ported (copy) from Unity Open MCP's <c>GateMode</c>.
    /// </summary>
    public enum GateMode
    {
        /// <summary>
        /// Run the checkpoint → mutate → validate → delta cycle; new errors fail the dispatch
        /// (<see cref="GateOutcome.Failed"/>). The mutation still ran — the gate outcome is surfaced
        /// alongside the result so the agent can react.
        /// </summary>
        Enforce,

        /// <summary>
        /// Run the cycle but never hard-fail: new errors surface as <see cref="GateOutcome.Warned"/>.
        /// Use for exploratory edits where the agent wants the safety telemetry without blocking.
        /// </summary>
        Warn,

        /// <summary>
        /// Skip the cycle entirely (<see cref="GateOutcome.Skipped"/>). The mutation runs directly with
        /// no checkpoint/validate overhead. This is every tool's default until an agent opts in.
        /// </summary>
        Off
    }

    /// <summary>
    /// What the gate concluded about a dispatch. Surfaced in the response envelope as
    /// <c>gate.outcome</c>. Ported (copy) from Unity Open MCP's <c>GateOutcome</c>.
    /// </summary>
    public enum GateOutcome
    {
        /// <summary>The gate did not run (mode <see cref="GateMode.Off"/> or non-mutating tool).</summary>
        Skipped,

        /// <summary>The gate ran and found no new issues (delta clean, or only warnings in Enforce).</summary>
        Passed,

        /// <summary>The gate ran and the mutation FAILED before validation could run, OR validation threw.</summary>
        Failed,

        /// <summary>
        /// The gate ran and found new issues but did not hard-fail (warn mode with new errors, or any mode
        /// with only new warnings in warn mode).
        /// </summary>
        Warned
    }

    /// <summary>
    /// Before/after issue delta computed by <see cref="VerifyGateAdapter.ComputeDelta"/>. Carries both
    /// counts (for the gate decision) and canonical issue keys (for agent-next-step guidance).
    /// Ported (copy) from Unity Open MCP's <c>DeltaData</c>.
    /// </summary>
    public sealed class DeltaData
    {
        /// <summary>New <see cref="VerifySeverity.Error"/> issues introduced by the mutation.</summary>
        public int NewErrors;

        /// <summary>New <see cref="VerifySeverity.Warning"/> issues introduced by the mutation.</summary>
        public int NewWarnings;

        /// <summary>Errors present at checkpoint that the mutation resolved.</summary>
        public int ResolvedErrors;

        /// <summary>Warnings present at checkpoint that the mutation resolved.</summary>
        public int ResolvedWarnings;

        /// <summary>Canonical issue keys (<c>{ruleId}|{severity}|{assetPath}|{issueCode}</c>) for new issues.</summary>
        public string[]? NewIssueKeys;

        /// <summary>Canonical issue keys for resolved issues.</summary>
        public string[]? ResolvedIssueKeys;
    }

    /// <summary>
    /// Outcome of dispatching one tool through the gate. Wraps the tool's own
    /// <see cref="ToolDispatchResult"/> with the gate telemetry (checkpoint id, outcome, delta, timing,
    /// agent guidance). When the gate did not run (read-only tool or <see cref="GateMode"/>.Off), only
    /// <see cref="Mutation"/> / <see cref="GateRan"/> / <see cref="Outcome"/> / <see cref="GateFailed"/>
    /// are populated. Ported (copy) from Unity Open MCP's <c>GateDispatchResult</c>, with the apply_fix
    /// rollback fields added in P3.7 (<see cref="RolledBack"/> / <see cref="RollbackReason"/> /
    /// <see cref="RestoredPaths"/>) and the batch / settle / dirty-scene / logs fields stripped (later
    /// phases).
    /// </summary>
    public sealed class GateDispatchResult
    {
        /// <summary>The underlying tool result (success/output or failure code/message).</summary>
        public ToolDispatchResult? Mutation;

        /// <summary>True when the checkpoint→validate→delta cycle actually ran (false for read-only / Off).</summary>
        public bool GateRan;

        /// <summary>The gate's conclusion. <see cref="GateOutcome.Skipped"/> when <see cref="GateRan"/> is false.</summary>
        public GateOutcome Outcome;

        /// <summary>The checkpoint id this dispatch validated against (null when the gate did not run).</summary>
        public string? CheckpointId;

        /// <summary>Rule ids that ran during validation (null when the gate did not run).</summary>
        public string[]? CategoriesRun;

        /// <summary>Milliseconds spent capturing the pre-mutation checkpoint.</summary>
        public long CheckpointDurationMs;

        /// <summary>Milliseconds spent running the post-mutation validation scan.</summary>
        public long ValidationDurationMs;

        /// <summary>Total gate path wall-clock (checkpoint + mutate + validate + delta).</summary>
        public long TotalGateDurationMs;

        /// <summary>The before/after issue delta (null when the gate did not run).</summary>
        public DeltaData? Delta;

        /// <summary>
        /// True when the gate hard-failed (Enforce + new errors, or mutation/validate faulted). The
        /// dispatch layer surfaces this as <c>gate.failed</c> so an agent can branch. Note the mutation
        /// still ran — the gate outcome is informational unless a future caller chooses to roll back.
        /// </summary>
        public bool GateFailed;

        /// <summary>Actionable, agent-facing next-step hints derived from the delta + outcome.</summary>
        public string[]? AgentNextSteps;

        /// <summary>
        /// P3.7 — true when a non-dry-run <c>apply_fix</c> was rolled back to its pre-fix state. Set by
        /// <c>ApplyFixGateRunner</c> when the fix failed to apply OR the gate detected new errors under
        /// Enforce after the fix. When true, <see cref="RestoredPaths"/> lists the <c>res://</c> paths
        /// restored and <see cref="RollbackReason"/> explains the trigger. Surfaced as a top-level
        /// <c>rollback</c> block in the response envelope.
        /// </summary>
        public bool RolledBack;

        /// <summary>Human-readable reason the fix was rolled back (null when <see cref="RolledBack"/> is false).</summary>
        public string? RollbackReason;

        /// <summary>The <c>res://</c> paths restored to their pre-fix bytes (null when no rollback ran).</summary>
        public string[]? RestoredPaths;

        /// <summary>
        /// Build a non-gate result (read-only tool or <see cref="GateMode.Off"/>): the mutation result
        /// is surfaced directly, <see cref="GateRan"/> is false, and the outcome mirrors the mutation's
        /// success. Centralizes the shape so the dispatch path has one factory for the "no gate" case.
        /// </summary>
        public static GateDispatchResult Direct(ToolDispatchResult mutation) => new GateDispatchResult
        {
            Mutation = mutation,
            GateRan = false,
            Outcome = mutation.Success ? GateOutcome.Skipped : GateOutcome.Failed,
            GateFailed = !mutation.Success,
        };
    }

    /// <summary>
    /// The single mandatory gate execution path every mutating tool routes through
    /// (<c>packages/bridge/AGENTS.md</c> §Gate policy). Runs checkpoint → mutate → validate → delta and
    /// resolves the outcome via <see cref="ResolveOutcome"/>. Never throws — checkpoint/validate faults
    /// are caught and surfaced as <see cref="GateOutcome.Failed"/> with agent guidance.
    ///
    /// <para>
    /// Ported (copy) from Unity Open MCP's <c>GatePolicy</c>, with these deltas for v1:
    /// <list type="bullet">
    ///   <item>No <c>CheckpointStore</c> mirror (in-memory history for the bridge UI is a later phase;
    ///   the checkpoint id is still surfaced in the response for agent-side correlation).</item>
    ///   <item>No gate-budget overshoot warning (the verify runner already warns on a slow checkpoint;
    ///   duplicating it here is noise until a real overshoot shows up).</item>
    ///   <item>Agent next steps reference <c>godot_open_mcp_*</c> tools and Godot issue codes
    ///   (<c>broken_scene_reference</c>, <c>missing_script</c>).</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class GatePolicy
    {
        /// <summary>
        /// Test seam: when non-null, <see cref="Execute"/> delegates to this stub instead of running the
        /// real checkpoint→validate→delta cycle. Lets the dispatch integration tests prove every mutator
        /// routes through <see cref="Execute"/> WITHOUT needing live verify rules / a Godot editor.
        /// Mirrors the <see cref="BridgeHttpServer.SetDispatchForTests"/> seam pattern. Never set in
        /// production; cleared by <see cref="ResetForTests"/>.
        /// </summary>
        static Func<GateMode, string[]?, Func<ToolDispatchResult>, GateDispatchResult>? _executeForTests;

        /// <summary>Test-only: install an Execute stub (or null to restore the real path).</summary>
        internal static void SetExecuteForTests(
            Func<GateMode, string[]?, Func<ToolDispatchResult>, GateDispatchResult>? stub) =>
            _executeForTests = stub;

        /// <summary>Test-only: clear the Execute stub.</summary>
        internal static void ResetForTests() => _executeForTests = null;

        /// <summary>
        /// Run the gate around <paramref name="mutation"/>. The single mandatory entry point for every
        /// mutating tool — the dispatch layer never calls <paramref name="mutation"/> directly when the
        /// gate is active. Returns a <see cref="GateDispatchResult"/> carrying the mutation result plus
        /// gate telemetry; the caller builds the response envelope from it.
        ///
        /// <para>
        /// <b>Mode <see cref="GateMode.Off"/>.</b> The mutation runs directly with no checkpoint/validate
        /// overhead. The outcome is <see cref="GateOutcome.Skipped"/> on success (the gate chose not to
        /// run) or <see cref="GateOutcome.Failed"/> on a mutation failure (the mutation itself faulted —
        /// distinct from a gate failure). This is every tool's default, so the common path stays cheap.
        /// </para>
        ///
        /// <para>
        /// <b>Enforce / Warn with an empty <paramref name="pathsHint"/>.</b> There is no whole-project
        /// fallback (<c>packages/bridge/AGENTS.md</c> §Gate policy), so an empty hint cannot scope a
        /// checkpoint. The mutation still runs (the agent asked for it) but the gate is skipped — the
        /// dispatch layer is responsible for rejecting an empty hint BEFORE reaching here when the gate
        /// is active. Reaching this branch means the dispatch guard did not fire (e.g. a tool that
        /// derives its own scope); the gate degrades gracefully rather than crashing.
        /// </para>
        /// </summary>
        internal static GateDispatchResult Execute(
            GateMode mode,
            string[]? pathsHint,
            Func<ToolDispatchResult> mutation)
        {
            // Test seam short-circuits the whole cycle so dispatch tests assert "the mutator went
            // through the gate" without live verify rules.
            if (_executeForTests != null)
                return _executeForTests(mode, pathsHint, mutation);

            if (mode == GateMode.Off)
            {
                var offResult = mutation();
                return new GateDispatchResult
                {
                    Mutation = offResult,
                    GateRan = false,
                    Outcome = offResult.Success ? GateOutcome.Skipped : GateOutcome.Failed,
                    GateFailed = !offResult.Success,
                };
            }

            if (pathsHint == null || pathsHint.Length == 0)
            {
                // Defensive: the dispatch layer rejects an empty hint before reaching here when the gate
                // is active. If we do reach here, degrade gracefully (run the mutation, skip the gate)
                // rather than throwing — the mutation is the agent's explicit intent.
                var noPathResult = mutation();
                return new GateDispatchResult
                {
                    Mutation = noPathResult,
                    GateRan = false,
                    Outcome = noPathResult.Success ? GateOutcome.Skipped : GateOutcome.Failed,
                    GateFailed = !noPathResult.Success,
                };
            }

            var gateSw = Stopwatch.StartNew();

            // (1) Checkpoint. A FormatException here means a checkpoint key was malformed — surface it
            // as a gate failure with actionable guidance rather than crashing the dispatch.
            CheckpointFingerprint checkpoint;
            long checkpointMs;
            try
            {
                var cpSw = Stopwatch.StartNew();
                checkpoint = VerifyGateAdapter.CreateCheckpoint(pathsHint, null);
                checkpointMs = cpSw.ElapsedMilliseconds;
            }
            catch (FormatException e)
            {
                BridgeLog.Error($"[GatePolicy] Checkpoint key validation failed: {e.Message}");
                return new GateDispatchResult
                {
                    Mutation = ToolDispatchResult.Fail("checkpoint_validation",
                        $"Checkpoint key validation failed: {e.Message}"),
                    GateRan = true,
                    Outcome = GateOutcome.Failed,
                    GateFailed = true,
                    AgentNextSteps = new[] { $"Checkpoint key validation failed: {e.Message}" },
                };
            }

            // (2) Mutate. A failed mutation short-circuits the gate — there is nothing to validate
            // against a mutation that did not land.
            var mutationResult = mutation();
            if (!mutationResult.Success)
            {
                gateSw.Stop();
                return new GateDispatchResult
                {
                    Mutation = mutationResult,
                    GateRan = true,
                    Outcome = GateOutcome.Failed,
                    CheckpointId = checkpoint.CheckpointId,
                    CheckpointDurationMs = checkpointMs,
                    TotalGateDurationMs = gateSw.ElapsedMilliseconds,
                    GateFailed = true,
                    AgentNextSteps = new[]
                    {
                        "Mutation failed before gate could validate. Fix the mutation error and retry."
                    },
                };
            }

            // (3) Validate. A thrown scan is caught (VerifyRunner already catches per-rule throws, but a
            // catastrophic runner fault must still surface cleanly).
            VerifyResult validation;
            try
            {
                validation = VerifyGateAdapter.ValidatePaths(pathsHint, null);
            }
            catch (Exception e)
            {
                gateSw.Stop();
                return new GateDispatchResult
                {
                    Mutation = mutationResult,
                    GateRan = true,
                    Outcome = GateOutcome.Failed,
                    CheckpointId = checkpoint.CheckpointId,
                    CheckpointDurationMs = checkpointMs,
                    TotalGateDurationMs = gateSw.ElapsedMilliseconds,
                    GateFailed = true,
                    AgentNextSteps = new[] { $"Validation scan exception: {e.Message}" },
                };
            }

            // (4) Delta. A FormatException here means a delta key was malformed — same loud-fail policy
            // as the checkpoint branch.
            DeltaData delta;
            try
            {
                delta = VerifyGateAdapter.ComputeDelta(checkpoint, validation);
            }
            catch (FormatException e)
            {
                gateSw.Stop();
                BridgeLog.Error($"[GatePolicy] Delta key validation failed: {e.Message}");
                return new GateDispatchResult
                {
                    Mutation = mutationResult,
                    GateRan = true,
                    Outcome = GateOutcome.Failed,
                    CheckpointId = checkpoint.CheckpointId,
                    CheckpointDurationMs = checkpointMs,
                    ValidationDurationMs = validation.DurationMs,
                    TotalGateDurationMs = gateSw.ElapsedMilliseconds,
                    GateFailed = true,
                    AgentNextSteps = new[] { $"Delta key validation failed: {e.Message}" },
                };
            }

            gateSw.Stop();

            var (outcome, gateFailed) = ResolveOutcome(mode, delta);
            var nextSteps = GenerateAgentNextSteps(delta, outcome);

            return new GateDispatchResult
            {
                Mutation = mutationResult,
                GateRan = true,
                Outcome = outcome,
                CheckpointId = checkpoint.CheckpointId,
                CategoriesRun = validation.CategoriesRun,
                CheckpointDurationMs = checkpointMs,
                ValidationDurationMs = validation.DurationMs,
                TotalGateDurationMs = gateSw.ElapsedMilliseconds,
                Delta = delta,
                GateFailed = gateFailed,
                AgentNextSteps = nextSteps,
            };
        }

        /// <summary>
        /// Pure decision matrix mapping (mode, delta) → (outcome, gateFailed). Exposed for unit testing.
        ///
        /// <list type="bullet">
        ///   <item>New errors + Enforce → <see cref="GateOutcome.Failed"/> (hard fail).</item>
        ///   <item>New errors + Warn   → <see cref="GateOutcome.Warned"/> (do not hard-fail).</item>
        ///   <item>New warnings + Warn → <see cref="GateOutcome.Warned"/>.</item>
        ///   <item>New warnings + Enforce → <see cref="GateOutcome.Passed"/> (warnings alone never block).</item>
        ///   <item>Nothing new → <see cref="GateOutcome.Passed"/>.</item>
        /// </list>
        /// Errors take priority over warnings when both are present.
        /// </summary>
        internal static (GateOutcome outcome, bool gateFailed) ResolveOutcome(GateMode mode, DeltaData delta)
        {
            if (delta.NewErrors > 0)
            {
                return mode == GateMode.Enforce
                    ? (GateOutcome.Failed, true)
                    : (GateOutcome.Warned, false);
            }

            if (delta.NewWarnings > 0)
            {
                return mode == GateMode.Warn
                    ? (GateOutcome.Warned, false)
                    : (GateOutcome.Passed, false);
            }

            return (GateOutcome.Passed, false);
        }

        /// <summary>
        /// Build agent-facing next-step hints from the delta + outcome. Exposed for unit testing. The
        /// hints name <c>godot_open_mcp_*</c> tools and link the known safe fix for a broken-reference /
        /// missing-script issue so an agent can self-correct without a second round-trip.
        /// </summary>
        internal static string[] GenerateAgentNextSteps(DeltaData delta, GateOutcome outcome)
        {
            var steps = new List<string>();

            switch (outcome)
            {
                case GateOutcome.Failed:
                    AddIssueHints(steps, delta.NewIssueKeys, delta.NewErrors, delta.NewWarnings, isFailed: true);
                    break;
                case GateOutcome.Warned:
                    if (delta.NewErrors > 0)
                        AddIssueHints(steps, delta.NewIssueKeys, delta.NewErrors, delta.NewWarnings, isFailed: false);
                    else
                        steps.Add($"Gate detected {delta.NewWarnings} new warning(s). Consider reviewing with godot_open_mcp_validate_edit before proceeding.");
                    break;
                case GateOutcome.Passed:
                    if (delta.ResolvedErrors > 0)
                        steps.Add($"Gate passed — {delta.ResolvedErrors} previously reported error(s) resolved.");
                    else
                        steps.Add("Gate passed — no new issues detected.");
                    break;
            }

            return steps.ToArray();
        }

        static void AddIssueHints(List<string> steps, string[]? issueKeys, int newErrors, int newWarnings, bool isFailed)
        {
            var firstKey = issueKeys != null && issueKeys.Length > 0 ? issueKeys[0] : null;
            var parsed = ParseIssueKey(firstKey);

            var modeLabel = isFailed ? "" : " (warn mode)";
            steps.Add($"Gate detected {newErrors} new error(s){modeLabel}. First: {FormatIssue(parsed, firstKey)}");

            if (parsed != null)
            {
                var p = parsed.Value;
                if (FixProviderRegistry.TryGetFixInfo(p.RuleId, p.IssueCode, out var fixId, out var safe) && fixId != null)
                {
                    var safeLabel = safe ? "safe" : "unsafe";
                    steps.Add($"Consider godot_open_mcp_apply_fix with fix_id {fixId} ({safeLabel}; dry_run first)");
                }

                steps.Add($"Use godot_open_mcp_find_references for {p.AssetPath} to assess downstream impact");
            }
            else
            {
                steps.Add("Review the affected asset and fix the introduced issue before retrying.");
            }

            if (isFailed)
                steps.Add("Fix the issue and retry; use godot_open_mcp_validate_edit to verify without mutation.");
        }

        static string FormatIssue(IssueKeyParts? parsed, string? rawKey)
        {
            if (parsed == null) return rawKey ?? "unknown";
            var p = parsed.Value;
            return $"{p.IssueCode} on {p.AssetPath}";
        }

        /// <summary>
        /// Parse the first canonical issue key into its parts. Returns null for null/empty/wrong-part-count
        /// keys so the hint builder can degrade to a generic message instead of throwing. Mirrors Unity's
        /// <c>ParseIssueKey</c> and the <see cref="IssueKey.TryParse"/> contract (exactly four pipe parts).
        /// </summary>
        internal static IssueKeyParts? ParseIssueKey(string? key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var parts = key.Split('|');
            // Exactly four pipe-separated parts — matches IssueKey.TryParse. Fewer is malformed; more
            // means a stray '|' leaked into a field and the key must be rejected, not truncated.
            if (parts.Length != 4) return null;
            return new IssueKeyParts(parts[0], parts[1], parts[2], parts[3]);
        }

        /// <summary>
        /// Parse a gate mode string. Case-sensitive (matches Unity): <c>"warn"</c> → Warn,
        /// <c>"off"</c> → Off, anything else (including null/empty/unknown) → Enforce. The dispatch layer
        /// only reaches this for mutators after resolving request → project → tool precedence, so the
        /// fallback is the strictest mode (Enforce), never Off — a typo must not silently disable safety.
        /// </summary>
        internal static GateMode ParseMode(string? mode) => mode switch
        {
            "warn" => GateMode.Warn,
            "off" => GateMode.Off,
            _ => GateMode.Enforce,
        };
    }

    /// <summary>
    /// Parsed components of a canonical issue key, for agent-next-step hint construction. Mirrors Unity's
    /// <c>IssueKeyParts</c>. Kept as a struct since it is a short-lived value passed by value into the
    /// hint builder.
    /// </summary>
    internal readonly struct IssueKeyParts
    {
        public readonly string RuleId;
        public readonly string Severity;
        public readonly string AssetPath;
        public readonly string IssueCode;

        public IssueKeyParts(string ruleId, string severity, string assetPath, string issueCode)
        {
            RuleId = ruleId;
            Severity = severity;
            AssetPath = assetPath;
            IssueCode = issueCode;
        }
    }
}
#endif
