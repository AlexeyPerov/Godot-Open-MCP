#if TOOLS
#nullable enable
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Canonical bridge response envelope builders (P2.1). Every tool dispatch outcome — success
    /// or failure — is wrapped into one of two envelope shapes so the MCP-side client
    /// (<c>LiveClient.postTool</c>) parses a single contract:
    ///
    /// <list type="bullet">
    ///   <item><b>Success:</b> <c>{ "ok": true, "result": &lt;handler output&gt; }</c></item>
    ///   <item><b>Failure:</b> <c>{ "ok": false, "error": { "code": "...", "message": "..." } }</c></item>
    /// </list>
    ///
    /// <para>
    /// This is the P2.1 canonical envelope. Unity Open MCP uses a richer gate-aware envelope
    /// (<c>{ mutation: { success, ... }, gate: {...} }</c> for mutating tools, direct bodies for
    /// read-only tools); P2.1 deliberately ships the simpler <c>{ok,result,error}</c> shape
    /// because the gate flow is deferred to P3.5 (per execution-plan P2.1 §Intentional deltas).
    /// When the gate lands, the envelope may widen — but <c>ok</c> + <c>error.code</c> will stay
    /// stable so existing clients keep parsing.
    /// </para>
    /// </summary>
    internal static class BridgeEnvelope
    {
        /// <summary>
        /// Build the success envelope around <paramref name="resultJson"/>. The handler output is
        /// spliced verbatim — it must already be valid JSON (an object, array, or scalar literal).
        /// A null output produces <c>"result":null</c> so a handler that returns no payload still
        /// produces a well-formed success envelope.
        /// </summary>
        internal static string BuildSuccess(string? resultJson)
        {
            var sb = new StringBuilder(64 + (resultJson?.Length ?? 0));
            sb.Append("{\"ok\":true,\"result\":");
            sb.Append(resultJson ?? "null");
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Build the failure envelope around a stable <paramref name="code"/> + human-readable
        /// <paramref name="message"/>. Both are JSON-escaped via <see cref="BridgeJson"/>. The
        /// envelope is always an object; a null message becomes <c>"message":null</c>.
        /// </summary>
        internal static string BuildFailure(string code, string? message)
        {
            var sb = new StringBuilder(80);
            sb.Append("{\"ok\":false,\"error\":{");
            sb.Append("\"code\":").Append(BridgeJson.EscapeString(code));
            sb.Append(",\"message\":").Append(BridgeJson.EscapeString(message));
            sb.Append("}}");
            return sb.ToString();
        }

        /// <summary>
        /// Build the failure envelope directly from a <see cref="ToolDispatchResult"/> failure.
        /// The caller must have already established the result is a failure
        /// (<see cref="ToolDispatchResult.Success"/> is false); a success result passed here
        /// surfaces a defensive <c>execution_error</c> envelope rather than emitting a misleading
        /// success-as-failure body.
        /// </summary>
        internal static string BuildFailure(ToolDispatchResult result)
        {
            if (result.Success)
            {
                return BuildFailure(
                    "execution_error",
                    "BuildFailure called with a successful ToolDispatchResult — bridge bug.");
            }
            return BuildFailure(result.ErrorCode ?? "execution_error", result.ErrorMessage);
        }
    }
}
#endif
