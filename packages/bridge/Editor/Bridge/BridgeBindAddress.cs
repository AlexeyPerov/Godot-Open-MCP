#if TOOLS
#nullable enable
namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Pure decision over the HTTP listener bind address. P1.3 shipped loopback-only; P5.2 widens
    /// the surface to accept remote bind (<c>0.0.0.0</c>) but <b>only</b> when
    /// <c>authMode:"required"</c> is set — copying Unity's
    /// <c>BridgeBindAddress.Decide(address, authMode)</c> pattern. The decision is made BEFORE the
    /// listener is constructed so a misconfigured project fails fast with an actionable message
    /// instead of a generic listener exception (packages/bridge/AGENTS.md §Transport).
    ///
    /// <para>
    /// Kept as its own type — rather than inlined in <see cref="BridgeHttpServer.Start"/> — so the
    /// verdict is unit-testable without a live <see cref="System.Net.HttpListener"/>, mirroring the
    /// Unity reference's <c>BridgeBindAddressTests</c>.
    /// </para>
    /// </summary>
    public static class BridgeBindAddress
    {
        /// <summary>Loopback IPv4 — always allowed regardless of auth mode.</summary>
        public const string Loopback = "127.0.0.1";

        /// <summary>
        /// Remote wildcard IPv4 — exposes the bridge beyond loopback. Allowed ONLY when
        /// <c>authMode:"required"</c> (token auth gates every request); refused otherwise so an
        /// accidental <c>0.0.0.0</c> on an open network never serves unauthenticated traffic.
        /// </summary>
        public const string Remote = "0.0.0.0";

        /// <summary>The default bind address when project settings do not override it.</summary>
        public const string Default = Loopback;

        /// <summary>The full set of valid bind address strings.</summary>
        public static readonly string[] ValidAddresses = { Loopback, Remote };

        /// <summary>
        /// Decision for a start attempt. <c>Allow</c> covers loopback (any auth mode) and
        /// remote+required; <c>Refuse</c> covers remote without required auth.
        /// </summary>
        public readonly struct BindDecision
        {
            /// <summary>True when the bridge may bind <see cref="ResolvedAddress"/>.</summary>
            public readonly bool Allowed;

            /// <summary>The address the bridge should bind (only meaningful when <see cref="Allowed"/>).</summary>
            public readonly string ResolvedAddress;

            /// <summary>Set when <see cref="Allowed"/> is false; an actionable refusal message.</summary>
            public readonly string? RefusalReason;

            BindDecision(bool allowed, string resolvedAddress, string? refusalReason)
            {
                Allowed = allowed;
                ResolvedAddress = resolvedAddress;
                RefusalReason = refusalReason;
            }

            /// <summary>Build an Allow decision for <paramref name="address"/>.</summary>
            public static BindDecision Allow(string address) =>
                new BindDecision(true, address, null);

            /// <summary>Build a Refuse decision carrying <paramref name="reason"/>.</summary>
            public static BindDecision Refuse(string address, string reason) =>
                new BindDecision(false, address, reason);
        }

        /// <summary>
        /// True for the two canonical bind strings (loopback + remote). Anything else coerces to the
        /// loopback default in <see cref="Decide(string?, string?)"/> so a bogus value binds
        /// loopback, never the bogus address.
        /// </summary>
        public static bool IsValid(string? address) => address == Loopback || address == Remote;

        /// <summary>
        /// Resolve and decide in one call. Loopback is always allowed; remote (<c>0.0.0.0</c>) is
        /// allowed only when <paramref name="authMode"/> is <c>"required"</c>; otherwise refused
        /// before the listener starts. An invalid/unknown <paramref name="bindAddress"/> coerces to
        /// the loopback default. <paramref name="authMode"/> is the canonical policy string already
        /// resolved from settings — the caller passes it explicitly so the decision is testable
        /// without a settings reader.
        /// </summary>
        public static BindDecision Decide(string? bindAddress, string? authMode)
        {
            var resolved = IsValid(bindAddress) ? bindAddress! : Default;
            if (!IsRemote(resolved)) return BindDecision.Allow(resolved);

            if (authMode != BridgeAuthPolicy.Required)
            {
                return BindDecision.Refuse(resolved,
                    "Remote bind (0.0.0.0) requires authMode \"required\". The bridge refuses to " +
                    "start on a non-loopback interface without token auth — set authMode to " +
                    "\"required\" in .godot-open-mcp/settings.json before enabling remote bind. " +
                    "See docs/api/bridge-http.md §Remote bind for the threat model.");
            }
            return BindDecision.Allow(resolved);
        }

        /// <summary>
        /// Single-arg decide for callers that do not know the auth mode (legacy / loopback-only
        /// paths). Treats the auth mode as <c>"none"</c>, which means remote bind is always refused
        /// on this path — a caller that wants remote bind MUST use the two-arg overload. Kept so the
        /// P1.3 call sites that always bind loopback stay a no-op diff.
        /// </summary>
        public static BindDecision Decide(string? bindAddress) => Decide(bindAddress, BridgeAuthPolicy.None);

        /// <summary>True when the address would expose the bridge beyond loopback.</summary>
        public static bool IsRemote(string? address) => address == Remote;
    }
}
#endif
