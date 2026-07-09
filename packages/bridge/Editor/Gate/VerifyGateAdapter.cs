#if TOOLS
#nullable enable
using System.Collections.Generic;
using GodotOpenMcp.Verify.Core;
using GodotOpenMcp.Verify.Editor;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// The bridge's adapter over the verify package. <see cref="GatePolicy"/> calls only this type —
    /// never <see cref="VerifyRunner"/> or the contract types directly — so the gate flow stays
    /// decoupled from how verify resolves rules and builds scopes. P3.5 ships the narrow surface the
    /// checkpoint → mutate → validate → delta path needs; later phases widen this with rule selection
    /// (P3.6 scan/validate filters), the reference graph, and the verify cache.
    ///
    /// <para>
    /// Adapted from Unity Open MCP's <c>VerifyGateAdapter</c> (copy fidelity for the delta math; adapt
    /// for the surface). Intentional deltas:
    /// <list type="bullet">
    ///   <item><b>No rule selection.</b> Unity's <c>SelectRuleIds</c> picks rules per asset extension;
    ///   P3.5 runs every registered rule (<c>ruleIds = null</c> → <see cref="VerifyRunner"/> runs all).
    ///   The verify package registers exactly three rules in P3.2–P3.4 (broken_references,
    ///   missing_scripts, import_health), so "all rules" is cheap and correct. Extension-based
    ///   selection is deferred until a rule whose cost warrants scoping lands.</item>
    ///   <item><b>No cache layer.</b> Unity records every run into a <c>VerifyCacheService</c> so
    ///   read-only tools (<c>validate_edit</c>) can serve from cache. P3.5 has no read-only verify tools
    ///   yet, so the cache would be write-only dead weight; it lands with P3.6.</item>
    ///   <item><b>No reference graph.</b> Unity exposes <c>FindReferences</c> through this adapter;
    ///   the reverse-dependency walker is a later phase here.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Editor-only (<c>#if TOOLS</c>): it calls <see cref="VerifyRunner"/> which routes its defensive
    /// warnings through <see cref="GodotOpenMcp.Verify.Editor.VerifyLog"/> (a Godot-coupled seam). The
    /// delta math itself is pure-managed and is unit-tested via <c>VerifyGateAdapterTests</c>.
    /// </para>
    /// </summary>
    internal static class VerifyGateAdapter
    {
        /// <summary>
        /// Capture a <see cref="CheckpointFingerprint"/> over <paramref name="paths"/> for the gate delta.
        /// When <paramref name="ruleIds"/> is null/empty, every registered rule contributes a fingerprint
        /// (P3.5 behavior — see the class doc's "No rule selection" delta). The scope widens to dependents
        /// only when a caller opts in (none in P3.5).
        /// </summary>
        internal static CheckpointFingerprint CreateCheckpoint(string[] paths, string[]? ruleIds)
        {
            var scope = new VerifyScope(paths);
            return VerifyRunner.CreateCheckpoint(scope, ruleIds);
        }

        /// <summary>
        /// Run a scoped <c>Validate</c> pass over <paramref name="paths"/>. <c>null</c>/<c>empty</c>
        /// <paramref name="ruleIds"/> runs all registered rules. The result feeds
        /// <see cref="ComputeDelta"/> for the gate decision and is the same shape the future
        /// <c>validate_edit</c> / <c>scan_paths</c> tools (P3.6) will serialize.
        /// </summary>
        internal static VerifyResult ValidatePaths(string[] paths, string[]? ruleIds)
        {
            var scope = new VerifyScope(paths);
            return VerifyRunner.RunScoped(scope, ruleIds, VerifyRunMode.Validate);
        }

        /// <summary>
        /// Compute the before/after issue delta for the gate decision. A "new" issue is one present in
        /// <paramref name="after"/> but absent from <paramref name="before"/>; "resolved" is the reverse.
        /// Issue identity is the canonical <see cref="IssueKey"/> string, so two scans that re-report the
        /// same broken reference on the same asset are a no-op delta, not two "new" issues.
        ///
        /// <para>
        /// Ported (copy) from Unity Open MCP's <c>VerifyGateAdapter.ComputeDelta</c>: same set-difference
        /// approach, same severity read off the key's second pipe segment (IssueKey.Build emits
        /// <c>"ERROR"</c>/<c>"WARN"</c>), same <see cref="IssueKey.ValidateKey"/> pre-check so a malformed
        /// checkpoint key surfaces a <see cref="FormatException"/> rather than being silently dropped.
        /// </para>
        /// </summary>
        internal static DeltaData ComputeDelta(CheckpointFingerprint before, VerifyResult after)
        {
            // Build the before-key set from every rule fingerprint, validating each key (a malformed key
            // in a checkpoint is a bug — surface it loudly via ValidateKey's FormatException rather than
            // silently treating it as always-resolved or always-new).
            var beforeKeys = new HashSet<string>();
            foreach (var fp in before.Fingerprints.Values)
            {
                foreach (var key in fp.IssueKeys)
                {
                    IssueKey.ValidateKey(key);
                    beforeKeys.Add(key);
                }
            }

            // Build the after-key set from the live issues. IssueKey.Build is the single canonical
            // producer; using it here guarantees the set-difference below compares identical strings.
            var afterKeys = new HashSet<string>();
            foreach (var issue in after.Issues)
            {
                afterKeys.Add(IssueKey.Build(issue));
            }

            // new = after − before ; resolved = before − after.
            var newKeys = new HashSet<string>(afterKeys);
            newKeys.ExceptWith(beforeKeys);

            var resolvedKeys = new HashSet<string>(beforeKeys);
            resolvedKeys.ExceptWith(afterKeys);

            // Count new issues by severity. Only NEW keys count — a pre-existing error that the mutation
            // did not touch must not show up as a gate failure, or every call would fail.
            var newErrors = 0;
            var newWarnings = 0;
            foreach (var issue in after.Issues)
            {
                var key = IssueKey.Build(issue);
                if (!newKeys.Contains(key)) continue;
                if (issue.Severity == VerifySeverity.Error) newErrors++;
                else newWarnings++;
            }

            // Count resolved issues by reading the severity off the canonical key (parts[1] is the
            // upper-case token IssueKey.Build emits). After-keys are already enumerated above; here we
            // walk the resolved set directly because there is no live issue object for a resolved key.
            var resolvedErrors = 0;
            var resolvedWarnings = 0;
            foreach (var key in resolvedKeys)
            {
                var parts = key.Split('|');
                if (parts.Length < 2) continue;
                if (parts[1] == "ERROR") resolvedErrors++;
                else if (parts[1] == "WARN") resolvedWarnings++;
            }

            return new DeltaData
            {
                NewErrors = newErrors,
                NewWarnings = newWarnings,
                ResolvedErrors = resolvedErrors,
                ResolvedWarnings = resolvedWarnings,
                NewIssueKeys = ToArray(newKeys),
                ResolvedIssueKeys = ToArray(resolvedKeys),
            };
        }

        /// <summary>
        /// Materialize a <see cref="HashSet{T}"/> into a fresh array. Extracted so the delta body reads
        /// as set math, not enumeration boilerplate. The order of a HashSet is undefined but stable
        /// within a run; consumers must not rely on key order (the gate surfaces keys as a set).
        /// </summary>
        static string[] ToArray(HashSet<string> set)
        {
            var arr = new string[set.Count];
            set.CopyTo(arr);
            return arr;
        }
    }
}
#endif
