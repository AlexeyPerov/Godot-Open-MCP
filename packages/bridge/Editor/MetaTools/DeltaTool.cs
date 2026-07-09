#if TOOLS
#nullable enable
using System.Text;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// <c>godot_open_mcp_delta</c> handler — compare the current project state against a previously
    /// captured <see cref="CheckpointStoreEntry"/> and return the new/resolved issue delta. This is
    /// the explicit form of the gate's delta step (the gate computes its own delta per mutating
    /// dispatch; this tool exposes it so an agent can run the checkpoint → mutate → delta workflow
    /// across separate tool calls).
    ///
    /// <para>
    /// Ported (copy for the JSON shape; adapt for the Godot tool prefix) from Unity Open MCP's
    /// <c>DeltaTool</c>. Intentional deltas for v1:
    /// <list type="bullet">
    ///   <item><b><see cref="BridgeJson.EscapeString"/> instead of a local <c>Esc()</c>.</b></item>
    ///   <item><b><c>godot_open_mcp_*</c> tool prefix in the recovery guidance.</b> The Unity
    ///   <c>BuildUnavailableResult</c> next-steps reference <c>unity_open_mcp_*</c>; this port
    ///   references <c>godot_open_mcp_validate_edit</c> / <c>godot_open_mcp_checkpoint_create</c> /
    ///   <c>godot_open_mcp_delta</c> (ADR-003).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Session-safe recovery.</b> Checkpoints are session-scoped (in-memory) and are wiped on
    /// editor restart or assembly reload. A <c>delta</c> call against an id that is no longer in the
    /// store returns a structured <c>unavailable</c> payload via <see cref="ToolDispatchResult.Ok"/>
    /// (NOT a hard error) — <c>passed:true</c> + <c>unavailable:true</c> + recovery guidance — so the
    /// agent can treat it as "no new errors detected, but no baseline to delta against" and fall back
    /// to <c>validate_edit</c>. Surfacing it as a hard error would set <c>isError:true</c> on the MCP
    /// response and block agent workflows.
    /// </para>
    ///
    /// <para>
    /// Read-only w.r.t. project state (registered <c>isMutating:false</c>). Structured failures:
    /// <c>missing_parameter</c> (empty checkpoint_id), <c>validation_error</c> (verify threw),
    /// <c>delta_error</c> (delta computation threw — typically a malformed checkpoint key).
    /// </para>
    /// </summary>
    internal static class DeltaTool
    {
        internal static ToolDispatchResult Execute(string body)
        {
            var checkpointId = JsonBody.GetString(body, "checkpoint_id");
            if (string.IsNullOrEmpty(checkpointId))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'checkpoint_id' is required.");

            var stored = CheckpointStore.Get(checkpointId);
            if (stored == null)
            {
                // A missing checkpoint is NOT a tool failure — see the class doc's "Session-safe
                // recovery" note. Return success with an explicit `unavailable` warning so the agent
                // can proceed (e.g. fall back to validate_edit).
                return ToolDispatchResult.Ok(BuildUnavailableResult(checkpointId));
            }

            // paths defaults to the checkpoint's paths; the agent may override with a narrower scope.
            var paths = JsonBody.GetStringArray(body, "paths") ?? stored.Paths;
            var categories = stored.Categories;

            VerifyResult currentResult;
            try
            {
                currentResult = VerifyGateAdapter.ValidatePaths(paths ?? System.Array.Empty<string>(), categories);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("validation_error", e.Message);
            }

            DeltaData delta;
            try
            {
                delta = VerifyGateAdapter.ComputeDelta(stored.Fingerprint, currentResult);
            }
            catch (System.FormatException e)
            {
                return ToolDispatchResult.Fail("delta_error",
                    $"Delta computation failed: {e.Message}");
            }

            var hasNewErrors = delta.NewErrors > 0;
            return ToolDispatchResult.Ok(BuildResult(delta, hasNewErrors));
        }

        /// <summary>
        /// Build the healthy-path JSON: <c>passed</c> (strict on new errors), a <c>summary</c> of
        /// new/resolved counts, and the <c>newIssues[]</c> / <c>resolvedIssues[]</c> canonical issue
        /// keys.
        /// </summary>
        static string BuildResult(DeltaData delta, bool hasNewErrors)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"passed\":").Append(!hasNewErrors ? "true" : "false");

            sb.Append(",\"summary\":{");
            sb.Append("\"newErrors\":").Append(delta.NewErrors);
            sb.Append(",\"newWarnings\":").Append(delta.NewWarnings);
            sb.Append(",\"resolvedErrors\":").Append(delta.ResolvedErrors);
            sb.Append(",\"resolvedWarnings\":").Append(delta.ResolvedWarnings);
            sb.Append('}');

            sb.Append(",\"newIssues\":[");
            if (delta.NewIssueKeys != null)
            {
                for (int i = 0; i < delta.NewIssueKeys.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BridgeJson.EscapeString(delta.NewIssueKeys[i]));
                }
            }
            sb.Append(']');

            sb.Append(",\"resolvedIssues\":[");
            if (delta.ResolvedIssueKeys != null)
            {
                for (int i = 0; i < delta.ResolvedIssueKeys.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BridgeJson.EscapeString(delta.ResolvedIssueKeys[i]));
                }
            }
            sb.Append(']');

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Payload returned when the requested checkpoint is no longer in the session-scoped store.
        /// <c>passed:true</c> + <c>unavailable:true</c> lets the agent treat this as "no new errors
        /// detected, but I have no baseline to delta against" rather than a hard failure. The
        /// <c>agentNextSteps</c> reference the <c>godot_open_mcp_*</c> tools (ADR-003).
        /// </summary>
        static string BuildUnavailableResult(string checkpointId)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"passed\":true");
            sb.Append(",\"unavailable\":true");
            // BridgeJson.EscapeString returns the value WITH surrounding quotes; the warning field
            // embeds the checkpoint id inside an already-open string literal, so use
            // EscapeStringContent (content-only, no quotes) for the id portion.
            sb.Append(",\"warning\":\"Checkpoint '")
              .Append(BridgeJson.EscapeStringContent(checkpointId))
              .Append("' is no longer available. Checkpoints are session-scoped (in-memory) and are cleared on script recompile, assembly reload, or editor restart — this does not indicate a problem with the project.\"");
            sb.Append(",\"agentNextSteps\":[");
            sb.Append("\"The pre-change baseline is gone, so a delta cannot be computed.\",");
            sb.Append("\"To verify current state directly, call godot_open_mcp_validate_edit on the relevant paths.\",");
            sb.Append("\"For future delta checks, call godot_open_mcp_checkpoint_create immediately before mutating, then godot_open_mcp_delta right after.\"");
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
#endif
