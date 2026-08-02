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
    ///     uid, line number, expected vs actual).</item>
    ///   <item><see cref="RootCause"/> + <see cref="Remediation"/> (P14.5) carry the explainability
    ///     taxonomy for this issue code: a stable machine-readable root-cause code (see
    ///     <see cref="IssueExplainability.RootCauses"/>) + clean user-visible remediation copy. They are
    ///     keyed by ruleId|issueCode in <see cref="IssueExplainability"/> and materialized per-instance by
    ///     each rule's <c>MakeIssue</c> helper. Optional — the 5-arg / 6-arg constructors leave them null
    ///     (backward-compatible: existing gate/delta logic ignores them, and <see cref="IssueKey"/> does
    ///     not include them).</item>
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

        /// <summary>
        /// Stable, machine-readable root-cause code from the explainability taxonomy (one of
        /// <see cref="IssueExplainability.RootCauses"/>), or null when not supplied. Agents may branch on
        /// this; the code is identical across every instance of the same issue code. P14.5 (additive).
        /// </summary>
        public string? RootCause { get; }

        /// <summary>
        /// Clean, user-visible remediation guidance for this issue code (no internal ids), or null when
        /// not supplied. Identical across every instance of the same issue code. P14.5 (additive).
        /// </summary>
        public string? Remediation { get; }

        public VerifyIssue(string ruleId, VerifySeverity severity, string assetPath, string issueCode, string description)
            : this(ruleId, severity, assetPath, issueCode, description, null) { }

        public VerifyIssue(string ruleId, VerifySeverity severity, string assetPath, string issueCode,
            string description, IReadOnlyDictionary<string, string>? evidence)
            : this(ruleId, severity, assetPath, issueCode, description, evidence, null, null) { }

        /// <summary>
        /// Full constructor (P14.5): carries evidence + the explainability taxonomy pair. Rules' MakeIssue
        /// helpers resolve the pair via <see cref="IssueExplainability.TryGet"/> and forward it here.
        /// </summary>
        public VerifyIssue(string ruleId, VerifySeverity severity, string assetPath, string issueCode,
            string description, IReadOnlyDictionary<string, string>? evidence,
            string? rootCause, string? remediation)
        {
            RuleId = ruleId;
            Severity = severity;
            AssetPath = assetPath;
            IssueCode = issueCode;
            Description = description;
            Evidence = evidence;
            RootCause = rootCause;
            Remediation = remediation;
        }
    }
}
