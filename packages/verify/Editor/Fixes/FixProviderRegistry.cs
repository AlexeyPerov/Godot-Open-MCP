#nullable enable
using System.Collections.Generic;
using GodotOpenMcp.Verify.Core;

namespace GodotOpenMcp.Verify.Fixes
{
    /// <summary>
    /// Human-readable description of what a fix would do, plus the <see cref="Safe"/> flag that
    /// decides whether the gate may auto-suggest it. Ported (copy) from Unity Open MCP's
    /// <c>FixDescription</c>. <see cref="Safe"/> <c>true</c> = the only fixes the gate auto-suggests;
    /// <c>false</c> = the fix can destroy data or has side effects beyond the target issue, so
    /// <c>apply_fix</c> requires the agent to choose it explicitly.
    /// </summary>
    public sealed class FixDescription
    {
        // Mutable DTO fields populated via object initializer by IFixProvider.Describe (mirrors the
        // Unity original's plain-field shape). Initialized to null! so nullable analysis treats the
        // post-initializer state as populated — providers always assign every field.
        public string FixId = null!;
        public string IssueId = null!;
        public string AssetPath = null!;
        public string Description = null!;
        public bool Safe;
    }

    /// <summary>
    /// Outcome of applying a fix. Ported (copy) from Unity Open MCP's <c>FixResult</c>.
    /// <see cref="TouchedPaths"/> lists the <c>res://</c> paths the fix modified so the gate can scope
    /// its post-fix re-validation; null when the fix was a no-op.
    /// </summary>
    public sealed class FixResult
    {
        // Mutable DTO fields populated via object initializer by IFixProvider.Apply.
        public bool Success;
        public string Description = null!;
        public string[] TouchedPaths = null!;
    }

    /// <summary>
    /// A fix candidate the gate can advertise alongside an issue so an agent sees every option (safe
    /// vs unsafe) in one pass, not just the first match <see cref="FixProviderRegistry.TryGetFixInfo"/>
    /// returns. Ported (copy) from Unity Open MCP's <c>FixCandidate</c>. <see cref="Safe"/> mirrors the
    /// provider's <see cref="IFixProvider.Describe"/>.
    /// </summary>
    public sealed class FixCandidate
    {
        // Mutable DTO field populated via object initializer by CandidatesForIssue.
        public string FixId = null!;
        public bool Safe;
    }

    /// <summary>
    /// A fix provider resolves one or more issue codes for one or more rules. Every fix implements
    /// this interface and registers via <see cref="FixProviderRegistry.Register"/> or
    /// <see cref="FixProviderRegistry.RegisterDefaults"/> (see <c>packages/verify/AGENTS.md</c>).
    /// Ported (copy) from Unity Open MCP's <c>IFixProvider</c>.
    ///
    /// <para>
    /// <b>Contract rules:</b>
    /// <list type="bullet">
    ///   <item><see cref="FixId"/> is stable and unique across all providers (e.g.
    ///     <c>remove_missing_script</c>).</item>
    ///   <item><see cref="CanFix"/> inspects only the ruleId + issueCode portion of the canonical issue
    ///     id (see <see cref="IssueKey"/>), so a synthetic test key matches the same provider set a
    ///     real issue would.</item>
    ///   <item><see cref="Describe"/> returns the real <see cref="FixDescription.Safe"/> flag; if it
    ///     throws, the registry treats the fix as unsafe rather than masking it.</item>
    /// </list>
    /// </para>
    /// </summary>
    public interface IFixProvider
    {
        /// <summary>Stable, unique fix identifier (e.g. <c>remove_missing_script</c>).</summary>
        string FixId { get; }

        /// <summary>
        /// True if this provider can resolve the given canonical issue id
        /// (<c>{ruleId}|{severity}|{assetPath}|{issueCode}</c>). Only ruleId + issueCode are inspected.
        /// </summary>
        bool CanFix(string issueId);

        /// <summary>Human-readable description + <see cref="FixDescription.Safe"/> flag for an issue.</summary>
        FixDescription Describe(string issueId);

        /// <summary>Apply the fix and return the outcome + touched paths.</summary>
        FixResult Apply(string issueId);
    }

    /// <summary>
    /// The fix registry: holds the registered <see cref="IFixProvider"/> set and resolves providers for
    /// rules / issues. This is the bidirectional link between <c>IVerifyRule</c> (problem) and
    /// <c>IFixProvider</c> (remedy) — the canonical issue id (<see cref="IssueKey"/>) is the join key.
    /// Ported (copy) from Unity Open MCP's <c>FixProviderRegistry</c>, with one intentional delta:
    /// <see cref="RegisterDefaults"/> is an idempotent public method (not a Unity
    /// <c>[InitializeOnLoadMethod]</c>) the verify EditorPlugin / gate wiring calls on enable — same
    /// pattern as <c>VerifyRunner.RegisterDefaults</c>. P3.1 ships no concrete fixes; P3.7 adds the
    /// first <c>Safe: true</c> providers here.
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>) — like the <c>Core/</c> contract types
    /// it is engine-agnostic and unit-testable in the binary-less host.
    /// </para>
    /// </summary>
    public static class FixProviderRegistry
    {
        private static readonly List<IFixProvider> _providers = new();
        private static bool _defaultsRegistered;

        /// <summary>
        /// Register the built-in fix providers. Idempotent. P3.1 ships no concrete fixes; P3.7
        /// populates this with the first <c>Safe: true</c> providers (e.g. <c>remove_missing_script</c>
        /// analog). Safe to call repeatedly and from the verify EditorPlugin enable path.
        /// </summary>
        public static void RegisterDefaults()
        {
            if (_defaultsRegistered) return;
            _defaultsRegistered = true;
            // P3.7+: _providers.Add(new RemoveMissingScriptFix());
        }

        /// <summary>
        /// Guarantee <see cref="RegisterDefaults"/> has run at least once before a lookup, so the
        /// package is usable without explicit plugin wiring.
        /// </summary>
        private static void EnsureDefaultsRegistered()
        {
            if (!_defaultsRegistered) RegisterDefaults();
        }

        /// <summary>Register a provider. A duplicate <see cref="IFixProvider.FixId"/> is ignored.</summary>
        public static void Register(IFixProvider provider)
        {
            EnsureDefaultsRegistered();
            if (!_providers.Exists(p => p.FixId == provider.FixId))
                _providers.Add(provider);
        }

        /// <summary>Find a provider by its <see cref="IFixProvider.FixId"/>, or null.</summary>
        public static IFixProvider? Find(string fixId)
        {
            EnsureDefaultsRegistered();
            foreach (var provider in _providers)
                if (provider.FixId == fixId) return provider;
            return null;
        }

        /// <summary>
        /// Resolve the first fix matching a rule+issue pair plus the provider's real <see cref="FixDescription.Safe"/>
        /// flag. The synthetic key carries a placeholder asset path — providers' <see cref="IFixProvider.CanFix"/>
        /// only inspects ruleId+issueCode, so this matches the same provider set a real issue would.
        ///
        /// <para>
        /// <see cref="safe"/> is taken from <see cref="IFixProvider.Describe"/> so unsafe providers are
        /// surfaced accurately in <c>validate_edit</c> / <c>scan_paths</c> envelopes. If
        /// <see cref="IFixProvider.Describe"/> throws, <see cref="safe"/> defaults to <c>false</c> so the
        /// gate never auto-applies something it cannot reason about.
        /// </para>
        /// </summary>
        /// <returns>True if any provider can fix the pair; <paramref name="fixId"/> + <paramref name="safe"/> are set.</returns>
        public static bool TryGetFixInfo(string ruleId, string issueCode, out string? fixId, out bool safe)
        {
            EnsureDefaultsRegistered();
            fixId = null;
            safe = false;

            var testKey = $"{ruleId}|ERROR|res://__test__.tscn|{issueCode}";
            foreach (var provider in _providers)
            {
                if (!provider.CanFix(testKey)) continue;

                fixId = provider.FixId;
                try
                {
                    safe = provider.Describe(testKey).Safe;
                }
                catch
                {
                    // If Describe throws for any reason, default to unsafe so the gate never
                    // auto-applies something it cannot reason about.
                    safe = false;
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// Every fix that can resolve the given canonical issue id. Unlike <see cref="TryGetFixInfo"/>
        /// (first match) this returns the full set so <c>apply_fix</c> can advertise all available
        /// fixes per issue — agents then choose safe vs unsafe.
        /// </summary>
        public static string[] FixesForIssue(string issueId)
        {
            EnsureDefaultsRegistered();
            if (string.IsNullOrEmpty(issueId)) return System.Array.Empty<string>();
            var result = new List<string>();
            foreach (var provider in _providers)
                if (provider.CanFix(issueId)) result.Add(provider.FixId);
            return result.ToArray();
        }

        /// <summary>
        /// Every fix candidate for a rule+issue pair, each with its real <see cref="FixDescription.Safe"/>
        /// flag. Used by <c>scan_paths</c> / <c>validate_edit</c> to emit a <c>fixCandidates[]</c> block
        /// so agents see safe vs unsafe options up front. Like <see cref="TryGetFixInfo"/> this builds a
        /// synthetic key — providers' <see cref="IFixProvider.CanFix"/> only inspect ruleId+issueCode.
        /// </summary>
        public static FixCandidate[] CandidatesForIssue(string ruleId, string issueCode)
        {
            EnsureDefaultsRegistered();
            if (string.IsNullOrEmpty(ruleId) || string.IsNullOrEmpty(issueCode))
                return System.Array.Empty<FixCandidate>();

            var testKey = $"{ruleId}|ERROR|res://__test__.tscn|{issueCode}";
            var result = new List<FixCandidate>();
            foreach (var provider in _providers)
            {
                if (!provider.CanFix(testKey)) continue;
                bool safe;
                try
                {
                    safe = provider.Describe(testKey).Safe;
                }
                catch
                {
                    safe = false;
                }
                result.Add(new FixCandidate { FixId = provider.FixId, Safe = safe });
            }
            return result.ToArray();
        }

        /// <summary>All registered fix ids (for the <c>capabilities</c> tool / catalog).</summary>
        public static string[] AvailableFixIds()
        {
            EnsureDefaultsRegistered();
            var result = new string[_providers.Count];
            for (var i = 0; i < _providers.Count; i++)
                result[i] = _providers[i].FixId;
            return result;
        }

        /// <summary>Clear all providers and reset the defaults-registered flag (test seam).</summary>
        public static void Clear()
        {
            _providers.Clear();
            _defaultsRegistered = false;
        }
    }
}
