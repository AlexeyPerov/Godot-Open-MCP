#if TOOLS
#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Result of dispatching one tool through the bridge (P2.1). A tool handler returns either
    /// <see cref="Ok"/> (with a JSON <c>result</c> payload string) or <see cref="Fail"/> (with a
    /// stable machine-readable <c>code</c> + human-readable <c>message</c>). The HTTP dispatcher
    /// (<see cref="BridgeHttpServer"/>) wraps this into the canonical envelope via
    /// <see cref="BridgeEnvelope"/>.
    ///
    /// <para>
    /// Adapted from Unity Open MCP's <c>ToolDispatchResult</c> (copy fidelity): same
    /// success/output/errorCode/errorMessage four-field shape, same <c>Ok</c>/<c>Fail</c> factory
    /// pair. Unity's gate flow wraps this in a richer <c>GateDispatchResult</c>; P2.1 ships the
    /// flat dispatch result only — gate wrapping arrives in P3.5.
    /// </para>
    /// </summary>
    public sealed class ToolDispatchResult
    {
        /// <summary>True when the tool ran to completion successfully.</summary>
        public bool Success { get; }

        /// <summary>
        /// The tool's JSON result payload (already serialized). Null on failure. The dispatcher
        /// splices this verbatim into the <c>"result"</c> field of the success envelope — handlers
        /// are responsible for producing valid JSON.
        /// </summary>
        public string? Output { get; }

        /// <summary>
        /// Stable machine-readable error code (e.g. <c>invalid_request</c>,
        /// <c>main_thread_blocked</c>, <c>execution_error</c>). Null on success.
        /// </summary>
        public string? ErrorCode { get; }

        /// <summary>Human-readable error explanation. Null on success.</summary>
        public string? ErrorMessage { get; }

        ToolDispatchResult(bool success, string? output, string? errorCode, string? errorMessage)
        {
            Success = success;
            Output = output;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        /// <summary>Build a success result carrying <paramref name="output"/> (JSON string).</summary>
        public static ToolDispatchResult Ok(string? output = null) =>
            new ToolDispatchResult(true, output, null, null);

        /// <summary>Build a failure result carrying a stable code + message.</summary>
        public static ToolDispatchResult Fail(string code, string message) =>
            new ToolDispatchResult(false, null, code, message);
    }
}
#endif
