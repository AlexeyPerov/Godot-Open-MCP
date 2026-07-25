#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Verify.Editor
{
    /// <summary>
    /// The verify entry point: holds the registered <see cref="IVerifyRule"/> set, runs scoped scans,
    /// and builds <see cref="CheckpointFingerprint"/>s for the gate delta. Ported (copy) from Unity
    /// Open MCP's <c>VerifyRunner</c>, with two intentional deltas for Godot:
    ///
    /// <list type="bullet">
    ///   <item><b>Auto-registration:</b> Unity registers default rules via
    ///     <c>[UnityEditor.InitializeOnLoadMethod]</c>, which has no reliable Godot mono equivalent.
    ///     Instead <see cref="RegisterDefaults"/> is an idempotent public method the verify
    ///     EditorPlugin / gate wiring (P3.5) calls on enable, and <see cref="EnsureDefaultsRegistered"/>
    ///     lazily guarantees registration before any run. P3.1 ships no concrete rules, so
    ///     <see cref="RegisterDefaults"/> is currently a no-op placeholder — P3.2–P3.4 add the real
    ///     rules here.</item>
    ///   <item><b>Logging:</b> Unity routes defensive warnings through
    ///     <c>UnityEngine.Debug.LogWarning</c>; Godot routes them through the <see cref="VerifyLog"/>
    ///     seam (mirrors <c>BridgeLog</c>) so a thrown rule or a slow checkpoint is visible without a
    ///     live editor, and the binary-less test host can swap the sinks to no-ops.</item>
    /// </list>
    ///
    /// <para>
    /// Editor-only (<c>#if TOOLS</c>) because it touches <see cref="VerifyLog"/>; the pure contract
    /// types in <c>Core/</c> it operates on are engine-agnostic. The verify test csproj defines TOOLS
    /// (same as the bridge tests) so this compiles into the binary-less host.
    /// </para>
    /// </summary>
    public static class VerifyRunner
    {
        /// <summary>
        /// Soft budget for a <see cref="VerifyRunMode.Checkpoint"/> pass. The gate runs a checkpoint
        /// on every mutation, so a slow checkpoint blocks the agent; exceeding this only logs a warning
        /// (it does not fail the run).
        /// </summary>
        private const long CheckpointBudgetMs = 2000;

        private static readonly List<IVerifyRule> RegisteredRules = new();
        private static bool _defaultsRegistered;

        /// <summary>The currently registered rules, in registration order.</summary>
        public static IReadOnlyList<IVerifyRule> Rules => RegisteredRules;

        /// <summary>
        /// Register the built-in rules. Idempotent. P3.1 defined the contract; P3.2–P3.4 populate this
        /// with the broken-references / missing-scripts / import-health rules. Safe to call repeatedly
        /// and from the verify EditorPlugin enable path.
        /// </summary>
        public static void RegisterDefaults()
        {
            if (_defaultsRegistered) return;
            _defaultsRegistered = true;
            RegisteredRules.Add(new Rules.BrokenReferences.BrokenReferencesRule());
            RegisteredRules.Add(new Rules.MissingScripts.MissingScriptsRule());
            RegisteredRules.Add(new Rules.ImportHealth.ImportHealthRule());
        }

        /// <summary>
        /// Guarantee <see cref="RegisterDefaults"/> has run at least once before a scan. Lazy so the
        /// package is usable without explicit plugin wiring, and idempotent so it is cheap on repeat
        /// calls.
        /// </summary>
        private static void EnsureDefaultsRegistered()
        {
            if (!_defaultsRegistered) RegisterDefaults();
        }

        /// <summary>Register a rule. A duplicate <see cref="IVerifyRule.Id"/> is ignored.</summary>
        public static void RegisterRule(IVerifyRule rule)
        {
            if (!RegisteredRules.Exists(r => r.Id == rule.Id))
                RegisteredRules.Add(rule);
        }

        /// <summary>Clear all registered rules and reset the defaults-registered flag (test seam).</summary>
        public static void ClearRules()
        {
            RegisteredRules.Clear();
            _defaultsRegistered = false;
        }

        /// <summary>
        /// Run the registered rules over <paramref name="scope"/>. When <paramref name="ruleIds"/> is
        /// null/empty, all registered rules run; otherwise only the requested known ids run and the
        /// unknown ones are reported via <see cref="VerifyResult.UnknownRuleIds"/>. A rule that throws
        /// is caught and logged — it never propagates to crash a scoped gate check.
        /// </summary>
        public static VerifyResult RunScoped(VerifyScope scope, string[]? ruleIds, VerifyRunMode mode)
        {
            EnsureDefaultsRegistered();
            var sw = Stopwatch.StartNew();
            var issues = new List<VerifyIssue>();

            string[] unknownRuleIds;
            string[] availableRuleIds;
            List<IVerifyRule> rulesToRun;

            if (ruleIds != null && ruleIds.Length > 0)
            {
                var requested = new HashSet<string>(ruleIds);
                var known = new HashSet<string>(RegisteredRules.Select(r => r.Id));
                unknownRuleIds = requested.Where(id => !known.Contains(id)).ToArray();
                availableRuleIds = RegisteredRules.Select(r => r.Id).ToArray();
                rulesToRun = RegisteredRules.Where(r => requested.Contains(r.Id)).ToList();
            }
            else
            {
                unknownRuleIds = Array.Empty<string>();
                availableRuleIds = Array.Empty<string>();
                rulesToRun = RegisteredRules.ToList();
            }

            var categoriesRun = rulesToRun.Select(r => r.Id).ToArray();

            foreach (var rule in rulesToRun)
            {
                try
                {
                    rule.Scan(scope, mode, issues);
                }
                catch (Exception e)
                {
                    // A throwing rule must not poison a scoped gate check: log and continue so the
                    // remaining rules still report. Mirrors Unity's defensive catch.
                    VerifyLog.Warning($"[VerifyRunner] Rule '{rule.Id}' threw: {e.Message}");
                }
            }

            sw.Stop();

            if (mode == VerifyRunMode.Checkpoint && sw.ElapsedMilliseconds > CheckpointBudgetMs)
            {
                VerifyLog.Warning(
                    $"[VerifyRunner] Checkpoint took {sw.ElapsedMilliseconds}ms " +
                    $"(budget: {CheckpointBudgetMs}ms) for paths: {string.Join(", ", scope.Paths ?? Array.Empty<string>())}");
            }

            return new VerifyResult(issues, categoriesRun, sw.ElapsedMilliseconds, unknownRuleIds, availableRuleIds);
        }

        /// <summary>
        /// Build a <see cref="CheckpointFingerprint"/> over <paramref name="scope"/> for the gate delta
        /// (P3.6 <c>delta</c> tool). When <paramref name="ruleIds"/> is null/empty, all registered rules
        /// contribute a fingerprint.
        /// </summary>
        public static CheckpointFingerprint CreateCheckpoint(VerifyScope scope, string[]? ruleIds)
        {
            // Run the baseline in Validate mode, NOT Checkpoint mode.
            //
            // The gate pairs this "before" fingerprint with a Validate-mode "after" scan and computes
            // new = after − before. Every rule deliberately detects *less* under Checkpoint (it skips
            // the dangling-usage walk, the dangling-script-id walk and the duplicate-uid pass — all
            // Error severity), so a baseline captured in Checkpoint mode is not comparable to the
            // after-set: any pre-existing issue that only the Validate pass can see shows up as a NEW
            // error. That made every gated mutation on a file with a pre-existing dangling reference
            // hard-fail under Enforce, and made ApplyFixGateRunner roll back correct fixes with
            // "fix introduced N new error(s)" — with no way for an agent to make progress, and the
            // phantom errors indistinguishable from real regressions.
            //
            // The Checkpoint mode value still exists for callers that want the cheap pass on its own;
            // the budget warning below simply no longer fires for this path.
            var result = RunScoped(scope, ruleIds, VerifyRunMode.Validate);
            var id = $"cp_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            var fingerprints = new Dictionary<string, RuleFingerprint>();

            foreach (var category in result.CategoriesRun)
            {
                var categoryIssues = result.Issues.Where(i => i.RuleId == category).ToList();
                var errors = categoryIssues.Count(i => i.Severity == VerifySeverity.Error);
                var warnings = categoryIssues.Count(i => i.Severity == VerifySeverity.Warning);
                var keys = new HashSet<string>(categoryIssues.Select(IssueKey.Build));
                fingerprints[category] = new RuleFingerprint(errors, warnings, keys);
            }

            return new CheckpointFingerprint(id, fingerprints);
        }
    }
}
#endif
