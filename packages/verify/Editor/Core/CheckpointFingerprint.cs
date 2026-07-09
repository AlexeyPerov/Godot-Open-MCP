#nullable enable
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Core
{
    /// <summary>
    /// A per-rule fingerprint captured at checkpoint time so the gate delta can compare before/after
    /// without re-emitting full issue text. The <see cref="IssueKeys"/> set lets the delta detect
    /// exactly which issues were added/resolved, not just count drift. Ported (copy) from Unity Open
    /// MCP's <c>RuleFingerprint</c>.
    /// </summary>
    public sealed class RuleFingerprint
    {
        /// <summary>Number of <see cref="VerifySeverity.Error"/> issues this rule emitted at checkpoint.</summary>
        public int Errors { get; }

        /// <summary>Number of <see cref="VerifySeverity.Warning"/> issues this rule emitted at checkpoint.</summary>
        public int Warnings { get; }

        /// <summary>The canonical <see cref="IssueKey"/> forms of every issue this rule emitted.</summary>
        public HashSet<string> IssueKeys { get; }

        public RuleFingerprint(int errors, int warnings, HashSet<string> issueKeys)
        {
            Errors = errors;
            Warnings = warnings;
            IssueKeys = issueKeys;
        }
    }

    /// <summary>
    /// A snapshot of project health at one moment, keyed by a short checkpoint id. Produced by
    /// <see cref="VerifyRunner.CreateCheckpoint"/> and consumed by the gate delta (<c>delta</c> tool,
    /// P3.6) to compute added/resolved issues across a mutation. Ported (copy) from Unity Open MCP's
    /// <c>CheckpointFingerprint</c>.
    /// </summary>
    public sealed class CheckpointFingerprint
    {
        /// <summary>Short opaque id (<c>cp_&lt;6 hex&gt;</c>) for logging and MCP tool responses.</summary>
        public string CheckpointId { get; }

        /// <summary>Per-rule fingerprint, keyed by <see cref="IVerifyRule.Id"/>.</summary>
        public Dictionary<string, RuleFingerprint> Fingerprints { get; }

        public CheckpointFingerprint(string checkpointId, Dictionary<string, RuleFingerprint> fingerprints)
        {
            CheckpointId = checkpointId;
            Fingerprints = fingerprints;
        }
    }
}
