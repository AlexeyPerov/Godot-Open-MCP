#nullable enable
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Core
{
    /// <summary>
    /// A single finding produced by an <see cref="IVerifyRule"/>. This is the stable link key of the
    /// whole gate/verify/capability surface: its <see cref="RuleId"/> + <see cref="Severity"/> +
    /// <see cref="AssetPath"/> + <see cref="IssueCode"/> tuple is serialized by <see cref="IssueKey"/>
    /// into the canonical <c>{ruleId}|{severity}|{assetPath}|{issueCode}</c> form that MCP tool
    /// responses (<c>validate_edit</c>, <c>scan_paths</c>), the capability catalog, and the gate
    /// delta all share. Ported (copy) from Unity Open MCP's <c>VerifyIssue</c>.
    ///
    /// <para>
    /// <b>Contract rules</b> (see <c>packages/verify/AGENTS.md</c>):
    /// <list type="bullet">
    ///   <item><see cref="IssueCode"/> MUST be non-empty — it is the link key between rules and fixes
    ///     (<c>FixProviderRegistry</c> matches on ruleId|issueCode). A rule that emits an issue with
    ///     an empty code breaks fix linkage.</item>
    ///   <item><see cref="Severity"/> is set per-issue, not per-rule, so one scanner can emit both
    ///     errors and warnings.</item>
    ///   <item><see cref="Evidence"/> is additive and optional — the per-instance trigger (broken ref
    ///     uid, line number, expected vs actual). The static root-cause/remediation text does NOT
    ///     live here; it is keyed by ruleId|issueCode elsewhere so it is not repeated per issue.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so it is unit-testable in a
    /// binary-less xUnit host — same convention as the bridge DTOs (<c>NodeData</c> et al.).
    /// </para>
    /// </summary>
    public sealed class VerifyIssue
    {
        /// <summary>The emitting rule's stable <see cref="IVerifyRule.Id"/> (e.g. <c>broken_references</c>).</summary>
        public string RuleId { get; }

        /// <summary>Per-issue severity. The gate delta treats <see cref="VerifySeverity.Error"/> as failure.</summary>
        public VerifySeverity Severity { get; }

        /// <summary>The <c>res://</c>-rooted asset path the issue was found on (e.g. <c>res://Scenes/Main.tscn</c>).</summary>
        public string AssetPath { get; }

        /// <summary>
        /// Stable issue code for this finding (e.g. <c>broken_scene_reference</c>, <c>missing_script</c>).
        /// Links this issue to its fix providers — never empty.
        /// </summary>
        public string IssueCode { get; }

        /// <summary>Human-readable description of what was found (single-line, agent-facing).</summary>
        public string Description { get; }

        /// <summary>
        /// Optional per-instance evidence: the specific broken ref / value / location that triggered
        /// this issue (broken uid, ext_resource path, line, expected vs actual, ...). Null when a rule
        /// does not supply it. Additive only.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Evidence { get; }

        public VerifyIssue(string ruleId, VerifySeverity severity, string assetPath, string issueCode, string description)
            : this(ruleId, severity, assetPath, issueCode, description, null)
        {
        }

        public VerifyIssue(string ruleId, VerifySeverity severity, string assetPath, string issueCode,
            string description, IReadOnlyDictionary<string, string>? evidence)
        {
            RuleId = ruleId;
            Severity = severity;
            AssetPath = assetPath;
            IssueCode = issueCode;
            Description = description;
            Evidence = evidence;
        }
    }
}
