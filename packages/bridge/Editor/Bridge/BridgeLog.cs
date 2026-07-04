#if TOOLS
#nullable enable
using System;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Logging seam for the bridge. Production routes every log through <see cref="Info"/> /
    /// <see cref="Warning"/> / <see cref="Error"/>, which default to <see cref="GD.Print"/> /
    /// <see cref="GD.PushWarning"/> / <see cref="GD.PushError"/>. The HTTP integration test
    /// (<c>BridgeHttpServerTests</c>) swaps these delegates for no-ops so
    /// <see cref="BridgeHttpServer.Start"/> / <see cref="BridgeHttpServer.Stop"/> — which log on
    /// the success and teardown paths — do not P/Invoke into the native Godot library that is not
    /// loaded in the binary-less xUnit host (the same reason the dispatcher tests use the
    /// <c>*ForTests</c> seams instead of driving a real Node's <c>_Process</c>).
    ///
    /// <para>
    /// Without this seam the integration test would fault on <c>GD.Print("Bridge listening...")
    /// </c> the moment <see cref="BridgeHttpServer.Start"/> binds, masking the real test result
    /// behind a test-host crash. The delegate swap is test-only and never reaches production.
    /// </para>
    /// </summary>
    internal static class BridgeLog
    {
        static Action<string?> _info = DefaultInfo;
        static Action<string?> _warning = DefaultWarning;
        static Action<string?> _error = DefaultError;

        static void DefaultInfo(string? message) => GD.Print(message);
        static void DefaultWarning(string? message) => GD.PushWarning(message);
        static void DefaultError(string? message) => GD.PushError(message);

        /// <summary>Info-level log (maps to <see cref="GD.Print"/> in production).</summary>
        internal static void Info(string message) => _info(message);

        /// <summary>Warning-level log (maps to <see cref="GD.PushWarning"/> in production).</summary>
        internal static void Warning(string message) => _warning(message);

        /// <summary>Error-level log (maps to <see cref="GD.PushError"/> in production).</summary>
        internal static void Error(string message) => _error(message);

        // --- Test seams ---------------------------------------------------------------------------

        /// <summary>
        /// Test-only: replace the log sinks. Pass <c>null</c> to leave a sink at its current value.
        /// The test fixture restores the defaults via <see cref="ResetForTests"/> on teardown so a
        /// later test sees the production <see cref="GD.*"/> sinks again.
        /// </summary>
        internal static void SetLoggersForTests(
            Action<string?>? info,
            Action<string?>? warning,
            Action<string?>? error)
        {
            if (info != null) _info = info;
            if (warning != null) _warning = warning;
            if (error != null) _error = error;
        }

        /// <summary>Test-only: restore the production <see cref="GD.*"/> sinks.</summary>
        internal static void ResetForTests()
        {
            _info = DefaultInfo;
            _warning = DefaultWarning;
            _error = DefaultError;
        }
    }
}
#endif
