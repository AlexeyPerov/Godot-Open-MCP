#if TOOLS
#nullable enable
using System.Text;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Fixes;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// <c>godot_open_mcp_apply_fix</c> handler (P3.7). Resolves a fix for a canonical issue id and either
    /// previews it (dry-run) or applies it. Registered as <c>isMutating:true</c> so the dispatcher routes a
    /// non-dry-run apply through <see cref="ApplyFixGateRunner"/> (gate + rollback); a dry-run apply is
    /// short-circuited to the read-only path in <see cref="BridgeHttpServer"/> (no rollback needed for a
    /// preview).
    ///
    /// <para>
    /// Ported (copy for the contract/JSON shape; adapt for the Godot escape helper) from Unity Open MCP's
    /// <c>ApplyFixTool</c>. Intentional deltas for v1:
    /// <list type="bullet">
    ///   <item><b><see cref="BridgeJson.EscapeString"/> instead of a local <c>Esc()</c>.</b> The Unity
    ///   reference duplicates the escape logic in every meta-tool; Godot routes through the canonical
    ///   <see cref="BridgeJson"/> helper so the escape rules live in one place (same delta as the P3.6
    ///   meta-tools).</item>
    ///   <item><b>No judgment-call providers yet.</b> Unity's apply_fix branches on provider type
    ///   (<c>RelinkBrokenGuidFix</c> needs <c>target_guid</c>, the materials fixes need
    ///   <c>target_texture</c>/<c>target_shader</c>). P3.7 ships one <c>Safe: true</c> provider
    ///   (<c>remove_missing_script</c>) that takes no extra params, so the typed-provider branches are
    ///   dropped. A later phase that adds an unsafe provider widens this switch — the registry plumbing
    ///   (<see cref="FixProviderRegistry"/>) is already in place.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Structured failures (all <c>ok:false</c> with a stable code): <c>missing_parameter</c> (empty
    /// issue_id), <c>invalid_issue_id</c> (malformed key), <c>fix_not_applicable</c> (provider cannot fix
    /// this issue), <c>fix_failed</c> (<c>Apply</c> returned <c>!Success</c>), <c>fix_error</c>
    /// (<c>Apply</c> threw). The <c>unknown_fix</c> case is an <c>ok:true</c> body (the tool ran; the agent
    /// asked for a fix id that does not exist) carrying <c>availableFixIds</c> +
    /// <c>applicableFixIdsForIssue</c> so the agent can self-correct without a second round-trip.
    /// </para>
    /// </summary>
    internal static class ApplyFixTool
    {
        internal static ToolDispatchResult Execute(string body)
        {
            var fixId = JsonBody.GetString(body, "fix_id");
            var issueId = JsonBody.GetString(body, "issue_id");
            var dryRun = JsonBody.GetBool(body, "dry_run", true);

            if (string.IsNullOrEmpty(issueId))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'issue_id' is required and must be non-empty.");

            if (!IssueKey.TryParse(issueId, out _, out _, out _, out _))
                return ToolDispatchResult.Fail("invalid_issue_id",
                    $"Issue id '{issueId}' is not a valid issue key. Expected format: {{ruleId}}|{{severity}}|{{assetPath}}|{{issueCode}}");

            // If fix_id is omitted, surface every fix that can resolve the issue so the agent can pick
            // (safe vs unsafe). Mirrors the per-issue fix listing the catalog advertises.
            if (string.IsNullOrEmpty(fixId))
            {
                var available = FixProviderRegistry.FixesForIssue(issueId);
                return ToolDispatchResult.Ok(BuildFixListResult(issueId, available));
            }

            var provider = FixProviderRegistry.Find(fixId!);
            if (provider == null)
                return ToolDispatchResult.Ok(BuildUnknownFixError(fixId!, issueId));

            if (!provider.CanFix(issueId))
                return ToolDispatchResult.Fail("fix_not_applicable",
                    $"Fix '{fixId}' cannot be applied to issue '{issueId}'.");

            if (dryRun)
            {
                var desc = provider.Describe(issueId);
                return ToolDispatchResult.Ok(BuildDryRunResult(desc));
            }

            FixResult result;
            try
            {
                // P3.7 ships only the no-param remove_missing_script provider. A later phase that adds a
                // provider needing a judgment-call param (e.g. a relink fix needing target_guid) widens
                // this into a typed switch like Unity's ApplyFixTool.
                result = provider.Apply(issueId);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("fix_error",
                    $"Fix application failed: {e.Message}");
            }

            if (!result.Success)
                return ToolDispatchResult.Fail("fix_failed", result.Description);

            return ToolDispatchResult.Ok(BuildApplyResult(result));
        }

        static string BuildFixListResult(string issueId, string[] availableFixIds)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"dryRun\":true");
            sb.Append(",\"issueId\":").Append(BridgeJson.EscapeString(issueId));
            sb.Append(",\"availableFixIds\":[");
            if (availableFixIds != null)
            {
                for (int i = 0; i < availableFixIds.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BridgeJson.EscapeString(availableFixIds[i]));
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string BuildDryRunResult(FixDescription desc)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"dryRun\":true");
            sb.Append(",\"fixId\":").Append(BridgeJson.EscapeString(desc.FixId));
            sb.Append(",\"issueId\":").Append(BridgeJson.EscapeString(desc.IssueId));
            sb.Append(",\"assetPath\":").Append(BridgeJson.EscapeString(desc.AssetPath));
            sb.Append(",\"description\":").Append(BridgeJson.EscapeString(desc.Description));
            sb.Append(",\"safe\":").Append(desc.Safe ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        static string BuildApplyResult(FixResult result)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"dryRun\":false");
            sb.Append(",\"success\":true");
            sb.Append(",\"description\":").Append(BridgeJson.EscapeString(result.Description));
            sb.Append(",\"touchedPaths\":[");
            if (result.TouchedPaths != null)
            {
                for (int i = 0; i < result.TouchedPaths.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BridgeJson.EscapeString(result.TouchedPaths[i]));
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string BuildUnknownFixError(string fixId, string issueId)
        {
            var available = FixProviderRegistry.AvailableFixIds();
            var applicable = FixProviderRegistry.FixesForIssue(issueId);
            var sb = new StringBuilder(256);
            sb.Append("{\"error\":{\"code\":\"unknown_fix\"");
            sb.Append(",\"message\":").Append(
                BridgeJson.EscapeString($"Unknown fix id '{fixId}'."));
            sb.Append(",\"availableFixIds\":[");
            for (int i = 0; i < available.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(available[i]));
            }
            sb.Append("]");
            sb.Append(",\"applicableFixIdsForIssue\":[");
            if (applicable != null)
            {
                for (int i = 0; i < applicable.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BridgeJson.EscapeString(applicable[i]));
                }
            }
            sb.Append("]}}");
            return sb.ToString();
        }
    }
}
#endif
