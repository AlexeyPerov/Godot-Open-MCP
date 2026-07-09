#nullable enable
using System;

namespace GodotOpenMcp.Verify.Core
{
    /// <summary>
    /// Builds and parses the canonical issue identity string <c>{ruleId}|{severity}|{assetPath}|{issueCode}</c>.
    /// This is THE link key of the entire gate/verify/fix/capability surface — <c>validate_edit</c> /
    /// <c>scan_paths</c> envelopes carry it, <see cref="Fixes.IFixProvider.CanFix"/> matches on it, and
    /// <see cref="CheckpointFingerprint"/> dedupes issues by it. Ported (copy) from Unity Open MCP's
    /// <c>IssueKey</c>, including the case-insensitive severity matcher (regression
    /// <c>specs/feedback.md 2026-07-03</c> in Unity): scan/validate emit <c>"Error"</c>/<c>"Warning"</c>
    /// while <see cref="Build"/> emits <c>"ERROR"</c>/<c>"WARN"</c>, and an agent may transcribe either
    /// into <c>apply_fix</c>, so the parser must accept any case plus the long <c>WARNING</c> form.
    ///
    /// <para>
    /// Pure-managed (no Godot API surface), unit-tested in the binary-less host.
    /// </para>
    /// </summary>
    public static class IssueKey
    {
        /// <summary>Build the canonical key from components. Throws on empty/pipe-bearing components.</summary>
        public static string Build(string ruleId, VerifySeverity severity, string assetPath, string issueCode)
        {
            ValidateComponents(ruleId, assetPath, issueCode);
            var sev = SeverityToken(severity);
            return $"{ruleId}|{sev}|{assetPath}|{issueCode}";
        }

        /// <summary>Build the canonical key from a <see cref="VerifyIssue"/>.</summary>
        public static string Build(VerifyIssue issue)
        {
            return Build(issue.RuleId, issue.Severity, issue.AssetPath, issue.IssueCode);
        }

        /// <summary>
        /// Parse a key back into its components. Returns false for null/empty, wrong part count,
        /// unrecognized severity, or empty ruleId/assetPath/issueCode — never throws.
        /// </summary>
        public static bool TryParse(string? key, out string? ruleId, out VerifySeverity severity,
            out string? assetPath, out string? issueCode)
        {
            ruleId = null;
            severity = default;
            assetPath = null;
            issueCode = null;

            if (string.IsNullOrEmpty(key)) return false;

            var parts = key.Split('|');
            if (parts.Length != 4) return false;

            ruleId = parts[0];
            var sevStr = parts[1];
            assetPath = parts[2];
            issueCode = parts[3];

            if (string.IsNullOrEmpty(ruleId)) return false;
            // Accept any case of the severity token. scan_paths / validate_edit emit "Error"/"Warning"
            // (Title-case), IssueKey.Build emits "ERROR"/"WARN" (upper), and an agent may hand-transcribe
            // either. Also accept the long form "WARNING" for symmetry. A genuinely malformed severity
            // (e.g. "CRITICAL") still rejects so a real typo surfaces.
            if (!TryMatchSeverity(sevStr, out severity)) return false;
            if (string.IsNullOrEmpty(assetPath)) return false;
            if (string.IsNullOrEmpty(issueCode)) return false;

            return true;
        }

        /// <summary>Throw <see cref="FormatException"/> if the key is malformed.</summary>
        public static void ValidateKey(string key)
        {
            if (!TryParse(key, out _, out _, out _, out _))
                throw new FormatException(
                    $"Malformed issue key: '{key}'. Expected format: {{ruleId}}|{{severity}}|{{assetPath}}|{{issueCode}}");
        }

        /// <summary>The canonical upper-case severity token this builder emits.</summary>
        public static string SeverityToken(VerifySeverity severity) =>
            severity == VerifySeverity.Error ? "ERROR" : "WARN";

        // Case-insensitive severity matcher. Recognizes ERROR, WARN, and the long WARNING so all
        // producers and hand-transcribed keys parse uniformly.
        private static bool TryMatchSeverity(string? sevStr, out VerifySeverity severity)
        {
            severity = VerifySeverity.Warning;
            if (string.IsNullOrEmpty(sevStr)) return false;
            switch (sevStr.ToUpperInvariant())
            {
                case "ERROR": severity = VerifySeverity.Error; return true;
                case "WARN":
                case "WARNING": severity = VerifySeverity.Warning; return true;
                default: return false;
            }
        }

        private static void ValidateComponents(string ruleId, string assetPath, string issueCode)
        {
            if (string.IsNullOrEmpty(ruleId))
                throw new ArgumentException("Issue key ruleId must not be empty.", nameof(ruleId));
            if (string.IsNullOrEmpty(assetPath))
                throw new ArgumentException("Issue key assetPath must not be empty.", nameof(assetPath));
            if (string.IsNullOrEmpty(issueCode))
                throw new ArgumentException("Issue key issueCode must not be empty.", nameof(issueCode));
            if (ruleId.Contains('|'))
                throw new ArgumentException($"Issue key ruleId must not contain '|': '{ruleId}'", nameof(ruleId));
            if (assetPath.Contains('|'))
                throw new ArgumentException($"Issue key assetPath must not contain '|': '{assetPath}'", nameof(assetPath));
            if (issueCode.Contains('|'))
                throw new ArgumentException($"Issue key issueCode must not contain '|': '{issueCode}'", nameof(issueCode));
        }
    }
}
