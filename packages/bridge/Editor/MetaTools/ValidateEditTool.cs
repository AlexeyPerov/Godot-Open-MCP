#if TOOLS
#nullable enable
using System.Text;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Fixes;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// <c>godot_open_mcp_validate_edit</c> handler — a scoped read-only verify pass over
    /// <c>res://</c> paths. This is the explicit pre/post-mutation health check an agent calls when
    /// it wants the gate's validate step on its own (the gate runs it implicitly on every mutating
    /// call in <see cref="GateMode"/>.Enforce/<see cref="GateMode"/>.Warn; this tool exposes it
    /// directly so the agent can inspect state without mutating).
    ///
    /// <para>
    /// Ported (copy for the JSON shape; adapt for the Godot adapter surface) from Unity Open MCP's
    /// <c>ValidateEditTool</c>. Intentional deltas for v1:
    /// <list type="bullet">
    ///   <item><b>No <c>IssueExplainability</c>.</b> The Unity tool emits per-issue
    ///   <c>rootCause</c> / <c>remediation</c> from a static explainability table; Godot has no such
    ///   table yet, so those fields are omitted entirely (they are additive and safe to add later
    ///   without breaking the contract).</item>
    ///   <item><b>Categories only.</b> The Unity tool accepts <c>categories</c> /
    ///   <c>include_rules</c> / <c>exclude_rules</c>. P3.6 passes <c>categories</c> straight through
    ///   as the <c>ruleIds</c> arg to <see cref="VerifyGateAdapter.ValidatePaths"/> (null/empty runs
    ///   every rule); <c>include_rules</c> / <c>exclude_rules</c> are dropped — the verify package
    ///   registers exactly three cheap rules, so per-rule filtering is not worth the surface yet.
    ///   <c>categories</c> is kept because it matches the catalog field an agent matches against.</item>
    ///   <item><b>No paging.</b> Unity's <c>profile</c> / <c>page_size</c> / <c>cursor</c> /
    ///   <c>platform_profile</c> / <c>detail</c> are all omitted. The result is the full issue list;
    ///   paging is deferred until a rule whose output volume warrants it lands.</item>
    ///   <item><b><see cref="BridgeJson.EscapeString"/> instead of a local <c>Esc()</c>.</b> The Unity
    ///   reference duplicates the escape logic in every meta-tool; Godot routes through the canonical
    ///   <see cref="BridgeJson"/> helper so the escape rules live in one place.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Read-only (registered <c>isMutating:false</c>), so the dispatcher bypasses the gate and the
    /// handler's JSON output is surfaced verbatim as the <c>result</c> field. Structured failures:
    /// <c>missing_parameter</c> (empty paths), <c>validation_error</c> (verify threw). An unknown-rule
    /// request returns a structured <c>error</c> body (still <c>Ok</c> — the tool ran, the agent just
    /// asked for something that does not exist) so the MCP response stays non-error and the agent can
    /// branch on the body.
    /// </para>
    /// </summary>
    internal static class ValidateEditTool
    {
        internal static ToolDispatchResult Execute(string body)
        {
            var paths = JsonBody.GetStringArray(body, "paths");
            if (paths == null || paths.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'paths' is required and must be a non-empty array.");

            // categories is passed straight through as the ruleIds arg (null/empty → all rules). See
            // the class doc's "Categories only" delta for why include_rules/exclude_rules are dropped.
            var categories = JsonBody.GetStringArray(body, "categories");

            VerifyResult result;
            try
            {
                result = VerifyGateAdapter.ValidatePaths(paths, categories);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("validation_error", e.Message);
            }

            if (result.HasUnknownRules)
                return ToolDispatchResult.Ok(
                    BuildUnknownRulesError(result.UnknownRuleIds, result.AvailableRuleIds));

            return ToolDispatchResult.Ok(BuildResult(result));
        }

        /// <summary>
        /// Build the healthy-path JSON: <c>passed</c> (strict-error — any Error severity flips it),
        /// <c>issues[]</c> with ruleId + categoryId alias / severity / code + issueCode alias /
        /// assetPath / description / evidence / fixCandidates[] / fixId + fixSafe, then
        /// <c>categoriesRun</c>, <c>rulesApplied</c> (same set — no separate filter in v1), and
        /// <c>durationMs</c>.
        /// </summary>
        static string BuildResult(VerifyResult result)
        {
            var sb = new StringBuilder(1024);
            // validate_edit is the gate's pre-mutation check; it fails on any Error. The project
            // severity threshold flows into scan_paths and the regression gate; here the contract is
            // strict-error because validate_edit answers "is this asset currently healthy?".
            var hasErrors = result.Issues.Exists(i => i.Severity == VerifySeverity.Error);

            sb.Append("{\"passed\":").Append(!hasErrors ? "true" : "false");
            sb.Append(",\"issues\":[");
            for (int i = 0; i < result.Issues.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var issue = result.Issues[i];
                sb.Append('{');
                // categoryId mirrors ruleId so agents can match the catalog field.
                sb.Append("\"ruleId\":").Append(BridgeJson.EscapeString(issue.RuleId)).Append(',');
                sb.Append("\"categoryId\":").Append(BridgeJson.EscapeString(issue.RuleId)).Append(',');
                sb.Append("\"severity\":\"").Append(SeverityStr(issue.Severity)).Append("\",");
                sb.Append("\"code\":").Append(BridgeJson.EscapeString(issue.IssueCode)).Append(',');
                sb.Append("\"issueCode\":").Append(BridgeJson.EscapeString(issue.IssueCode)).Append(',');
                sb.Append("\"assetPath\":").Append(BridgeJson.EscapeString(issue.AssetPath)).Append(',');
                sb.Append("\"description\":").Append(BridgeJson.EscapeString(issue.Description));

                // P14.5 — surface the explainability taxonomy (rootCause + remediation) so an agent can
                // branch on the stable root-cause code and read the clean remediation guidance. Both are
                // optional on VerifyIssue; omit the fields entirely when a rule did not supply them (older
                // issues / pre-P14.5 paths) so the envelope stays backward-compatible.
                if (!string.IsNullOrEmpty(issue.RootCause))
                {
                    sb.Append(",\"rootCause\":").Append(BridgeJson.EscapeString(issue.RootCause));
                }
                if (!string.IsNullOrEmpty(issue.Remediation))
                {
                    sb.Append(",\"remediation\":").Append(BridgeJson.EscapeString(issue.Remediation));
                }

                if (issue.Evidence != null && issue.Evidence.Count > 0)
                {
                    sb.Append(",\"evidence\":{");
                    int ei = 0;
                    foreach (var kv in issue.Evidence)
                    {
                        if (ei++ > 0) sb.Append(',');
                        sb.Append(BridgeJson.EscapeString(kv.Key)).Append(':')
                          .Append(BridgeJson.EscapeString(kv.Value ?? ""));
                    }
                    sb.Append('}');
                }
                var candidates = FixProviderRegistry.CandidatesForIssue(issue.RuleId, issue.IssueCode);
                if (candidates.Length > 0)
                {
                    sb.Append(",\"fixCandidates\":[");
                    for (int ci = 0; ci < candidates.Length; ci++)
                    {
                        if (ci > 0) sb.Append(',');
                        sb.Append("{\"fixId\":").Append(BridgeJson.EscapeString(candidates[ci].FixId));
                        sb.Append(",\"safe\":").Append(candidates[ci].Safe ? "true" : "false");
                        sb.Append('}');
                    }
                    sb.Append(']');
                }
                if (FixProviderRegistry.TryGetFixInfo(issue.RuleId, issue.IssueCode, out var fixId, out var safe))
                {
                    sb.Append(",\"fixId\":").Append(BridgeJson.EscapeString(fixId));
                    sb.Append(",\"fixSafe\":").Append(safe ? "true" : "false");
                }
                sb.Append('}');
            }
            sb.Append(']');

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

            // rulesApplied mirrors categoriesRun in v1 (no separate include/exclude filter). Kept as a
            // distinct field so a future filter does not change the response shape.
            sb.Append(",\"rulesApplied\":[");
            if (result.CategoriesRun != null)
            {
                for (int i = 0; i < result.CategoriesRun.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BridgeJson.EscapeString(result.CategoriesRun[i]));
                }
            }
            sb.Append(']');

            sb.Append(",\"durationMs\":").Append(result.DurationMs);
            sb.Append('}');

            return sb.ToString();
        }

        /// <summary>
        /// Build the structured body for an unknown-rule request. Returned via
        /// <see cref="ToolDispatchResult.Ok"/> (the tool ran; the agent asked for a rule that does not
        /// exist) so the MCP response stays non-error and the agent can read the available rules.
        /// </summary>
        static string BuildUnknownRulesError(string[] unknownIds, string[] availableIds)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"error\":{\"code\":\"unknown_rule\"");
            sb.Append(",\"message\":\"Unknown rule IDs: ")
              .Append(BridgeJson.EscapeString(string.Join(", ", unknownIds))).Append("\"");
            sb.Append(",\"unknownRules\":[");
            for (int i = 0; i < unknownIds.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(unknownIds[i]));
            }
            sb.Append("],\"availableRules\":[");
            for (int i = 0; i < availableIds.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(availableIds[i]));
            }
            sb.Append("]}}");
            return sb.ToString();
        }

        static string SeverityStr(VerifySeverity s) => s switch
        {
            VerifySeverity.Error => "Error",
            VerifySeverity.Warning => "Warning",
            _ => "Info"
        };
    }
}
#endif
