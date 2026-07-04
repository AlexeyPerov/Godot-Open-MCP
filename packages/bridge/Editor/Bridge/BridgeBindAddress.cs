#if TOOLS
#nullable enable
namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Pure decision over the HTTP listener bind address. P1.3 ships loopback-only: the bridge
    /// refuses any non-loopback address outright. Auth-gated remote bind (Unity's
    /// <c>BridgeBindAddress</c> remote+<c>authMode:"required"</c> path) lands in P5.2 alongside the
    /// auth token, at which point this type grows a <c>Decide(address, authMode)</c> overload and
    /// a <c>Remote</c> constant mirroring Unity's.
    ///
    /// <para>
    /// Kept as its own type — rather than inlined in <see cref="BridgeHttpServer.Start"/> — so the
    /// verdict is unit-testable without a live <c>HttpListener</c>, mirroring the Unity reference's
    /// <c>BridgeBindAddressTests</c>. The decision is made BEFORE the listener is constructed so a
    /// misconfigured project fails fast with an actionable message instead of a generic listener
    /// exception (packages/bridge/AGENTS.md §Transport: "binds 127.0.0.1 by default").
    /// </para>
    /// </summary>
    public static class BridgeBindAddress
    {
        /// <summary>Loopback IPv4 — the only address the P1.3 bridge will bind.</summary>
        public const string Loopback = "127.0.0.1";

        /// <summary>The default bind address when project settings do not override it.</summary>
        public const string Default = Loopback;

        /// <summary>
        /// Decision for a start attempt. P1.3 has only the <c>Allow</c> outcome (loopback) and the
        /// <c>Refuse</c> outcome (anything else); P5.2 will widen <c>Allow</c> to cover remote+auth.
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
        /// True only for the canonical loopback string. P5.2 will widen this to also accept
        /// <c>0.0.0.0</c> once the auth gate that governs remote bind lands.
        /// </summary>
        public static bool IsValid(string? address) => address == Loopback;

        /// <summary>Resolve and decide in one call. P1.3 refuses everything that is not loopback.</summary>
        public static BindDecision Decide(string? bindAddress)
        {
            var resolved = IsValid(bindAddress) ? bindAddress! : Default;
            // Loopback is always allowed; there is no remote path in P1.3. When remote bind is
            // introduced (P5.2) this branch grows an auth check identical to Unity's Decide.
            if (!IsRemote(resolved))
                return BindDecision.Allow(resolved);

            // Unreachable in P1.3 (IsValid would have coerced to Default first); kept so the
            // P5.2 widening is a diff, not a rewrite, and so Decide never silently permits a
            // non-loopback address if IsValid is extended ahead of the auth gate.
            return BindDecision.Refuse(resolved,
                "Remote bind is not supported in this bridge version. The bridge binds " +
                "127.0.0.1 only — local-first loopback HTTP is the supported transport.");
        }

        /// <summary>True when the address would expose the bridge beyond loopback.</summary>
        public static bool IsRemote(string? address) => address != Loopback;
    }
}
#endif
