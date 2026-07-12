#if TOOLS
#nullable enable

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Pure auth decision for one HTTP request. P5.2 — copy of Unity Open MCP's
    /// <c>BridgeAuthCheck</c> (byte-for-byte policy). Returns true when the request should be
    /// allowed to proceed; the HTTP layer (<see cref="BridgeHttpServer.CheckAuth"/>) writes the 401
    /// when this returns false.
    ///
    /// <para>
    /// Policy matrix (see <c>specs/execution/P5/P5.2.md</c>):
    /// <list type="bullet">
    ///   <item><c>none</c> — allow unconditionally (localhost-trust default).</item>
    ///   <item><c>required</c> — allow only when the presented Bearer matches the expected token
    ///   via constant-time compare. Empty/missing expected token (a bug) fails closed.</item>
    ///   <item>anything else (null, corrupt settings, typo) — <b>fail closed</b>. Never silently fall
    ///   back to <c>none</c> (would allow a request the operator did not intend) nor to
    ///   <c>required</c> (would allow a valid token through under a policy the operator did not
    ///   intend).</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class BridgeAuthCheck
    {
        /// <summary>
        /// True when the request may proceed.
        ///
        /// <para>
        /// <paramref name="policy"/> is the canonical auth mode string
        /// (<see cref="BridgeAuthPolicy.None"/> / <see cref="BridgeAuthPolicy.Required"/>). An
        /// unrecognized value fails closed. <paramref name="headerValue"/> is the raw
        /// <c>Authorization</c> header (may be null/empty/wrong scheme). <paramref name="expectedToken"/>
        /// is the per-session token minted into the lock.
        /// </para>
        /// </summary>
        public static bool IsAuthorized(string? policy, string? headerValue, string? expectedToken)
        {
            // Only "none" is an explicit opt-out.
            if (policy == BridgeAuthPolicy.None) return true;

            // An unrecognized policy (null, corrupt settings, typo) must fail closed — never silently
            // fall back to "required", since that could allow a valid token through under a policy the
            // operator did not intend. Only the explicit "required" policy proceeds to the token check.
            if (policy != BridgeAuthPolicy.Required) return false;

            if (string.IsNullOrEmpty(expectedToken)) return false;

            var presented = BridgeAuthToken.ExtractBearer(headerValue);
            if (presented == null) return false;

            return BridgeAuthToken.EqualsConstantTime(presented, expectedToken);
        }
    }
}
#endif
