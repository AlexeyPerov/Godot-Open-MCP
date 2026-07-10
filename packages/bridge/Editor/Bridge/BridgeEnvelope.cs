#if TOOLS
#nullable enable
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Wire string for a <see cref="GateOutcome"/>. Centralized so the envelope builder and any future
    /// consumer spell the outcomes identically. Lower-case to match the rest of the JSON contract
    /// (<c>ok</c>, <c>result</c>, <c>gate</c>).
    /// </summary>
    internal static class GateOutcomeWire
    {
        internal const string Skipped = "skipped";
        internal const string Passed = "passed";
        internal const string Failed = "failed";
        internal const string Warned = "warned";

        internal static string From(GateOutcome outcome) => outcome switch
        {
            GateOutcome.Skipped => Skipped,
            GateOutcome.Passed => Passed,
            GateOutcome.Failed => Failed,
            GateOutcome.Warned => Warned,
            _ => Skipped,
        };
    }

    /// <summary>
    /// Canonical bridge response envelope builders (P2.1). Every tool dispatch outcome — success
    /// or failure — is wrapped into one of two envelope shapes so the MCP-side client
    /// (<c>LiveClient.postTool</c>) parses a single contract:
    ///
    /// <list type="bullet">
    ///   <item><b>Success:</b> <c>{ "ok": true, "result": &lt;handler output&gt; }</c></item>
    ///   <item><b>Failure:</b> <c>{ "ok": false, "error": { "code": "...", "message": "..." } }</c></item>
    /// </list>
    ///
    /// <para>
    /// This is the canonical envelope. Unity Open MCP uses a richer gate-aware envelope
    /// (<c>{ mutation: { success, ... }, gate: {...} }</c> for mutating tools, direct bodies for
    /// read-only tools); the Godot port keeps the simpler <c>{ok,result,error}</c> shape and folds
    /// the gate telemetry into the <c>result</c> object (a prepended <c>gate</c> block) so the
    /// TS-side client (<c>live-client.ts</c>) — which only reads <c>ok</c>/<c>result</c>/<c>error</c>
    /// and passes <c>result</c> verbatim — needs no change. The <c>ok</c> + <c>error.code</c> fields
    /// stay stable across the P2 → P3.5 widening.
    /// </para>
    /// </summary>
    internal static class BridgeEnvelope
    {
        /// <summary>
        /// Build the success envelope around <paramref name="resultJson"/>. The handler output is
        /// spliced verbatim — it must already be valid JSON (an object, array, or scalar literal).
        /// A null output produces <c>"result":null</c> so a handler that returns no payload still
        /// produces a well-formed success envelope.
        /// </summary>
        internal static string BuildSuccess(string? resultJson)
        {
            var sb = new StringBuilder(64 + (resultJson?.Length ?? 0));
            sb.Append("{\"ok\":true,\"result\":");
            sb.Append(resultJson ?? "null");
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Build the failure envelope around a stable <paramref name="code"/> + human-readable
        /// <paramref name="message"/>. Both are JSON-escaped via <see cref="BridgeJson"/>. The
        /// envelope is always an object; a null message becomes <c>"message":null</c>.
        /// </summary>
        internal static string BuildFailure(string code, string? message)
        {
            var sb = new StringBuilder(80);
            sb.Append("{\"ok\":false,\"error\":{");
            sb.Append("\"code\":").Append(BridgeJson.EscapeString(code));
            sb.Append(",\"message\":").Append(BridgeJson.EscapeString(message));
            sb.Append("}}");
            return sb.ToString();
        }

        /// <summary>
        /// Build the failure envelope directly from a <see cref="ToolDispatchResult"/> failure.
        /// The caller must have already established the result is a failure
        /// (<see cref="ToolDispatchResult.Success"/> is false); a success result passed here
        /// surfaces a defensive <c>execution_error</c> envelope rather than emitting a misleading
        /// success-as-failure body.
        /// </summary>
        internal static string BuildFailure(ToolDispatchResult result)
        {
            if (result.Success)
            {
                return BuildFailure(
                    "execution_error",
                    "BuildFailure called with a successful ToolDispatchResult — bridge bug.");
            }
            return BuildFailure(result.ErrorCode ?? "execution_error", result.ErrorMessage);
        }

        // --- P3.5 gate-aware envelopes --------------------------------------------------
        //
        // The gate outcome is folded INTO the canonical {ok,result,error} envelope rather than
        // widening it, so the TS client (live-client.ts) keeps parsing unchanged:
        //   - mutation FAILED  → ok:false + error.code/message (BuildFailure). The gate telemetry is
        //     dropped — a failed mutation has nothing to validate, and the agent's next step is to
        //     fix the mutation error, not to read a delta.
        //   - mutation OK      → ok:true + result = handler output with a prepended `gate` block.
        //     The agent sees both the mutation result and the gate outcome in one pass.

        /// <summary>
        /// Build the paths_hint_required failure envelope. Emitted when a mutating tool is dispatched
        /// with an active gate (<c>enforce</c>/<c>warn</c>) and no <c>paths_hint</c>. The message names
        /// the tool and the effective gate mode so an agent can re-issue with a non-empty scope without
        /// guessing. Per <c>packages/bridge/AGENTS.md</c> §Gate policy there is no whole-project
        /// fallback — the caller must enumerate the scope.
        /// </summary>
        internal static string BuildPathsHintRequired(string toolName, string gateMode)
        {
            return BuildFailure("paths_hint_required",
                $"Tool '{toolName}' is mutating and the gate mode is '{gateMode}', but 'paths_hint' is " +
                "empty. There is no whole-project fallback — pass a non-empty 'paths_hint' (the res:// " +
                "paths the mutation touches) so the gate can checkpoint and validate the scope.");
        }

        /// <summary>
        /// Build the envelope from a gate dispatch result. A failed mutation surfaces as
        /// <see cref="BuildFailure(ToolDispatchResult)"/> (the gate did not add actionable signal — the
        /// mutation itself faulted). A successful mutation surfaces as <see cref="BuildGateSuccess"/>
        /// with the gate block prepended into the result.
        /// </summary>
        internal static string BuildFromGateResult(GateDispatchResult result, string gateMode)
        {
            var mutation = result.Mutation;
            if (mutation == null || !mutation.Success)
            {
                return BuildFailure(mutation ?? ToolDispatchResult.Fail("execution_error",
                    "GateDispatchResult carried no mutation result — bridge bug."));
            }
            return BuildGateSuccess(result, gateMode);
        }

        /// <summary>
        /// Build the success envelope with a prepended <c>gate</c> block. When the handler's output is
        /// a JSON object (starts with <c>{</c>), the gate block is inserted as the FIRST key so an
        /// agent reading top-down sees the safety verdict before the mutation payload. When the output
        /// is not an object (an array, scalar, or null — rare for mutating tools), the output is nested
        /// under <c>mutation</c> alongside the gate block. The handler output is otherwise spliced
        /// verbatim — it must already be valid JSON.
        /// </summary>
        internal static string BuildGateSuccess(GateDispatchResult result, string gateMode)
        {
            var mutation = result.Mutation!;
            var output = mutation.Output ?? "null";

            var gateBlock = BuildGateBlockJson(result, gateMode);
            // P3.7 — a non-dry-run apply_fix that failed or introduced new errors is rolled back to its
            // pre-fix state by ApplyFixGateRunner. Surface that as a top-level `rollback` block so an
            // agent can tell "the fix ran and stuck" from "the fix was undone — no project change
            // remains". Null when no rollback ran (the common case for every other mutating tool).
            var rollbackBlock = result.RolledBack ? BuildRollbackBlockJson(result) : null;

            // Fast path: the handler returned an object. Insert `"gate":{...},` (and `"rollback":{...},`
            // when present) right after the opening brace. The output keeps all its own keys; the gate
            // and rollback blocks are purely additive.
            if (output.Length > 0 && output[0] == '{')
            {
                var sb = new StringBuilder(64 + gateBlock.Length + output.Length + (rollbackBlock?.Length ?? 0));
                sb.Append("{\"ok\":true,\"result\":{");
                sb.Append("\"gate\":");
                sb.Append(gateBlock);
                if (rollbackBlock != null)
                {
                    sb.Append(",\"rollback\":");
                    sb.Append(rollbackBlock);
                }
                sb.Append(',');
                // Splice the rest of the handler object (skip its opening brace).
                sb.Append(output, 1, output.Length - 1);
                sb.Append('}');
                return sb.ToString();
            }

            // Non-object output: wrap under `mutation` so the result stays a well-formed object.
            var wrapped = new StringBuilder(80 + gateBlock.Length + output.Length + (rollbackBlock?.Length ?? 0));
            wrapped.Append("{\"ok\":true,\"result\":{\"gate\":");
            wrapped.Append(gateBlock);
            if (rollbackBlock != null)
            {
                wrapped.Append(",\"rollback\":");
                wrapped.Append(rollbackBlock);
            }
            wrapped.Append(",\"mutation\":");
            wrapped.Append(output);
            wrapped.Append("}}");
            return wrapped.ToString();
        }

        /// <summary>
        /// Serialize the P3.7 rollback block: <c>{ rolledBack, reason, restoredPaths[] }</c>. Emitted only
        /// when <see cref="GateDispatchResult.RolledBack"/> is true. Restored paths are the <c>res://</c>
        /// forms <c>ApplyFixGateRunner</c> normalized. Mirrors Unity's <c>BridgeJson</c> rollback block.
        /// </summary>
        static string BuildRollbackBlockJson(GateDispatchResult result)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"rolledBack\":true");
            sb.Append(",\"reason\":").Append(BridgeJson.EscapeString(result.RollbackReason));
            sb.Append(",\"restoredPaths\":[");
            var paths = result.RestoredPaths;
            if (paths != null)
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BridgeJson.EscapeString(paths[i]));
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Serialize the gate block. Always emits <c>mode</c>, <c>outcome</c>, <c>ran</c>, and
        /// <c>failed</c>; emits <c>checkpointId</c>, <c>categoriesRun</c>, timings, <c>delta</c>, and
        /// <c>agentNextSteps</c> only when the gate ran. Strings are escaped via
        /// <see cref="BridgeJson.EscapeString"/>.
        /// </summary>
        static string BuildGateBlockJson(GateDispatchResult result, string gateMode)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"mode\":").Append(BridgeJson.EscapeString(gateMode));
            sb.Append(",\"outcome\":\"").Append(GateOutcomeWire.From(result.Outcome)).Append('"');
            sb.Append(",\"ran\":").Append(result.GateRan ? "true" : "false");
            sb.Append(",\"failed\":").Append(result.GateFailed ? "true" : "false");

            if (result.GateRan)
            {
                if (result.CheckpointId != null)
                    sb.Append(",\"checkpointId\":").Append(BridgeJson.EscapeString(result.CheckpointId));

                sb.Append(",\"categoriesRun\":[");
                if (result.CategoriesRun != null)
                {
                    for (int i = 0; i < result.CategoriesRun.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(BridgeJson.EscapeString(result.CategoriesRun[i]));
                    }
                }
                sb.Append(']');

                sb.Append(",\"checkpointMs\":").Append(result.CheckpointDurationMs);
                sb.Append(",\"validateMs\":").Append(result.ValidationDurationMs);
                sb.Append(",\"totalMs\":").Append(result.TotalGateDurationMs);

                var delta = result.Delta;
                sb.Append(",\"delta\":{\"newErrors\":").Append(delta?.NewErrors ?? 0);
                sb.Append(",\"newWarnings\":").Append(delta?.NewWarnings ?? 0);
                sb.Append(",\"resolvedErrors\":").Append(delta?.ResolvedErrors ?? 0);
                sb.Append(",\"resolvedWarnings\":").Append(delta?.ResolvedWarnings ?? 0);
                sb.Append('}');

                sb.Append(",\"agentNextSteps\":[");
                var steps = result.AgentNextSteps;
                if (steps != null)
                {
                    for (int i = 0; i < steps.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(BridgeJson.EscapeString(steps[i]));
                    }
                }
                sb.Append(']');
            }

            sb.Append('}');
            return sb.ToString();
        }
    }
}
#endif
