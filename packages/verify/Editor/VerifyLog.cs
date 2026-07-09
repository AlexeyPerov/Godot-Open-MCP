#if TOOLS
#nullable enable
using System;
using Godot;

namespace GodotOpenMcp.Verify.Editor
{
    /// <summary>
    /// Logging seam for the verify package, mirroring the bridge's <c>BridgeLog</c>. Production routes
    /// every log through <see cref="Info"/> / <see cref="Warning"/> / <see cref="Error"/>, which default
    /// to <see cref="GD.Print"/> / <see cref="GD.PushWarning"/> / <see cref="GD.PushError"/>. The test
    /// host swaps these for no-ops via <see cref="SetLoggersForTests"/> so a rule that logs on a slow
    /// checkpoint or a thrown scan does not P/Invoke into native Godot in the binary-less xUnit host
    /// (verify is a standalone package — there is no loaded Godot native library under the unit tests).
    ///
    /// <para>
    /// Editor-only (<c>#if TOOLS</c>): logging is an editor concern, and the only Godot API this type
    /// touches is <see cref="GD"/>. The pure-managed rule/fix/registry contracts in <c>Core/</c> and
    /// <c>Fixes/</c> do not reference this type directly — <see cref="Core.VerifyRunner"/> routes its
    /// defensive warnings through <see cref="Warning"/> so a throwing rule is visible without crashing
    /// a scoped gate check.
    /// </para>
    /// </summary>
    internal static class VerifyLog
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
        /// Restore via <see cref="ResetForTests"/> on teardown.
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
