#nullable enable

namespace GodotOpenMcp.Verify.Rules.AnimationAnalysis
{
    /// <summary>
    /// Stable issue-code constants emitted by <see cref="AnimationAnalysisRule"/>. Codes are part of the
    /// stable API surface — MCP responses, the capability catalog, and the gate delta all match on the
    /// <c>ruleId|issueCode</c> tuple (<c>packages/verify/AGENTS.md</c>). The per-instance detail (which
    /// player, which library, which clip, which state) lives in <see cref="Core.VerifyIssue.Evidence"/>,
    /// not in the code.
    ///
    /// <para>
    /// Adapted from Unity Open MCP's <c>AnimationAnalysis</c> rule's issue mapper — but the fidelity is
    /// <c>adapt</c> for the clip/state signals (Unity keys its checks off the <c>RuntimeAnimatorController</c>
    /// / <c>AnimatorController</c> API; Godot's <c>.tres</c> text format for <c>AnimationPlayer</c>,
    /// <c>AnimationLibrary</c>, <c>Animation</c>, and <c>AnimationNodeStateMachine</c> is parseable offline).
    /// Unity's curve-density / curve-count / AnyState-overuse / state-complexity signals are skipped for v1
    /// (Godot's <c>Animation</c> text format serializes tracks/keys differently and the per-clip budget
    /// thresholds would need their own calibration pass). Intentional deltas for v1 (see the P14.5 plan):
    /// <list type="bullet">
    ///   <item><b>No curve-density / curve-count / AnyState / complexity codes.</b> Unity emits four extra
    ///     threshold-based codes; Godot ships the five signals the P14.5 freeze roster names. The rest can
    ///     land in a later breadth pass with Godot-calibrated thresholds.</item>
    ///   <item><b>State-machine reachability is offline-text-only.</b> Godot serializes an
    ///     <c>AnimationNodeStateMachine</c> as a <c>.tres</c> whose <c>transitions</c> and <c>states</c> are
    ///     inline; the reachability BFS is run over the parsed graph, not a live engine traversal.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The codes carry no family prefix (the codes are the canonical Unity names — <c>missing_clip</c>,
    /// <c>empty_clip</c>, <c>unreachable_state</c>, <c>parameter_mismatch</c>, <c>duplicate_clip</c>); the
    /// <c>animation_analysis</c> rule id disambiguates the family. The rule surfaces via
    /// <c>scan_paths</c> / <c>scan_all</c> / <c>validate_edit</c> / the gate delta — no new MCP tool.
    /// </para>
    /// </summary>
    internal static class IssueCodes
    {
        /// <summary>
        /// An <c>AnimationPlayer</c> references a clip resource (an <c>Animation</c> inside a named
        /// <c>AnimationLibrary</c>) whose <c>[ext_resource]</c>/<c>[sub_resource]</c> target does not
        /// resolve — the clip resource was deleted, moved, or the id was never declared. The Godot analog
        /// of Unity's <c>missing_clip</c> (an animator state with no motion assigned, or a missing motion).
        /// Severity is <c>Error</c>: a missing clip means the player cannot play the referenced animation —
        /// a runtime integrity break, not informational cruft. The player path, the library, and the clip
        /// name are carried in evidence.
        /// </summary>
        public const string MissingClip = "missing_clip";

        /// <summary>
        /// An <c>Animation</c> clip declares zero tracks, so it animates nothing. The Godot analog of
        /// Unity's <c>empty_clip</c> (an AnimationClip with no curves). Severity is <c>Warning</c>: an empty
        /// clip does not break load (the player loads it fine), but it is dead weight a clean project should
        /// not carry, and it usually signals an unfinished edit. The clip path and its track count (0) are
        /// carried in evidence.
        /// </summary>
        public const string EmptyClip = "empty_clip";

        /// <summary>
        /// An <c>AnimationNodeStateMachine</c> state (a <c>[sub_resource type="AnimationNodeStateMachine"]</c>
        /// inside a <c>.tres</c>) that has no inbound transition and is not the machine's entry/start state —
        /// it can never be reached at runtime. The Godot analog of Unity's <c>unreachable_state</c>.
        /// Severity is <c>Warning</c>: an unreachable state does not break load, but it is dead graph that
        /// obscures the state machine. The state-machine path, the state name, and whether a start state was
        /// declared are carried in evidence. <c>Validate</c>/<c>Full</c> only — needs the full transition
        /// graph as context.
        /// </summary>
        public const string UnreachableState = "unreachable_state";

        /// <summary>
        /// An <c>AnimationNodeStateMachine</c> transition references a parameter (an
        /// <c>AnimationNodeStateMachinePlayback</c> / <c>condition</c> / <c>advance</c> expression) that is
        /// not in the state machine's blackboard/parameter set. The Godot analog of Unity's
        /// <c>parameter_mismatch</c>. Severity is <c>Warning</c>: a missing parameter may be intentional
        /// during a refactor, but it usually signals a renamed/removed parameter the transition forgot.
        /// The state-machine path and the missing parameter name are carried in evidence.
        /// <c>Validate</c>/<c>Full</c> only.
        /// </summary>
        public const string ParameterMismatch = "parameter_mismatch";

        /// <summary>
        /// Two or more <c>Animation</c> clips have identical track data (a byte-size / track-fingerprint
        /// collision) — one is a redundant copy. The Godot analog of Unity's <c>duplicate_clip</c>.
        /// Severity is <c>Warning</c>: a duplicate does not break load, but it bloats the library and
        /// confuses edits. The clip path and the sibling paths are carried in evidence.
        /// <c>Validate</c>/<c>Full</c> only (scan) — needs the full clip set as context.
        /// </summary>
        public const string DuplicateClip = "duplicate_clip";
    }

    /// <summary>
    /// Values placed in <c>Evidence["kind"]</c> to distinguish animation-analysis failure modes. Kept
    /// internal because the stable surface is the issue codes in <see cref="IssueCodes"/>. Mirrors the
    /// <c>EvidenceKinds</c> pattern the sibling P14 rules use.
    /// </summary>
    internal static class EvidenceKinds
    {
        public const string MissingClip = "missing_clip";
        public const string EmptyClip = "empty_clip";
        public const string UnreachableState = "unreachable_state";
        public const string ParameterMismatch = "parameter_mismatch";
        public const string DuplicateClip = "duplicate_clip";
    }
}
