#nullable enable
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Core
{
    /// <summary>
    /// Outcome of a <see cref="VerifyRunner.RunScoped"/> pass: the issues found, which rule ids ran,
    /// how long it took, and — when the caller requested specific rule ids — which were unknown and
    /// which were available. Ported (copy) from Unity Open MCP's <c>VerifyResult</c>; the shape is
    /// identical because the gate tools (<c>validate_edit</c>, <c>scan_paths</c>) serialize it.
    /// </summary>
    public sealed class VerifyResult
    {
        /// <summary>All issues found across the rules that ran, in emission order.</summary>
        public List<VerifyIssue> Issues { get; }

        /// <summary>The rule ids that actually ran (subset of the requested set that was known).</summary>
        public string[] CategoriesRun { get; }

        /// <summary>Wall-clock duration of the scan in milliseconds.</summary>
        public long DurationMs { get; }

        /// <summary>
        /// Requested rule ids that were not registered (empty when the caller asked for all rules or
        /// when every requested id was known). Surfaced so MCP tools can report
        /// <c>unknown_rule_ids</c> to the agent.
        /// </summary>
        public string[] UnknownRuleIds { get; }

        /// <summary>Every registered rule id at scan time, for the <c>available_rule_ids</c> field.</summary>
        public string[] AvailableRuleIds { get; }

        /// <summary>True when <see cref="UnknownRuleIds"/> is non-empty.</summary>
        public bool HasUnknownRules => UnknownRuleIds != null && UnknownRuleIds.Length > 0;

        public VerifyResult(List<VerifyIssue> issues, string[] categoriesRun, long durationMs,
            string[]? unknownRuleIds = null, string[]? availableRuleIds = null)
        {
            Issues = issues;
            CategoriesRun = categoriesRun;
            DurationMs = durationMs;
            UnknownRuleIds = unknownRuleIds ?? System.Array.Empty<string>();
            AvailableRuleIds = availableRuleIds ?? System.Array.Empty<string>();
        }
    }
}
