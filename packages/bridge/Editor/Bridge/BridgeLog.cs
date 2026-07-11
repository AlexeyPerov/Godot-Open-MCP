#if TOOLS
#nullable enable
using System;
using System.Threading;
using Godot;
using GodotOpenMcp.Bridge.Runtime.Logging;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Logging seam for the bridge. Production routes every log through <see cref="Info"/> /
    /// <see cref="Warning"/> / <see cref="Error"/>, which default to <see cref="GD.Print"/> /
    /// <see cref="GD.PushWarning"/> / <see cref="GD.PushError"/> AND forward to the
    /// <see cref="GodotLogCollector"/> (P4.7) so the <c>console_get_logs</c> tool can surface recent
    /// bridge activity. The HTTP integration test (<c>BridgeHttpServerTests</c>) swaps these
    /// delegates for no-ops so <see cref="BridgeHttpServer.Start"/> / <see cref="BridgeHttpServer.Stop"/>
    /// — which log on the success and teardown paths — do not P/Invoke into the native Godot library
    /// that is not loaded in the binary-less xUnit host (the same reason the dispatcher tests use the
    /// <c>*ForTests</c> seams instead of driving a real Node's <c>_Process</c>).
    ///
    /// <para>
    /// Without this seam the integration test would fault on <c>GD.Print("Bridge listening...")
    /// </c> the moment <see cref="BridgeHttpServer.Start"/> binds, masking the real test result
    /// behind a test-host crash. The delegate swap is test-only and never reaches production.
    /// </para>
    ///
    /// <para>
    /// <b>P4.7 collector sink.</b> Every log is also appended to the process-wide
    /// <see cref="GodotLogCollector"/> (installed at plugin enable), tagged
    /// <see cref="GodotLogSource.Bridge"/>. The forward uses a re-entrancy guard
    /// (<see cref="_forwardingToCollector"/>) so a hypothetical future collector write path that
    /// re-logged through BridgeLog would not recurse — the collector itself does not log back, but
    /// the guard is cheap insurance.
    /// </para>
    /// </summary>
    internal static class BridgeLog
    {
        static Action<string?> _info = DefaultInfo;
        static Action<string?> _warning = DefaultWarning;
        static Action<string?> _error = DefaultError;

        /// <summary>
        /// Optional sink that forwards each log line to the <see cref="GodotLogCollector"/>. Set at
        /// plugin enable (after the collector is installed); cleared on disable. Null when the
        /// collector is not armed (e.g. in test hosts), so logging stays a no-op collector-wise.
        /// </summary>
        static Action<GodotLogType, string>? _collectorSink;

        /// <summary>
        /// Re-entrancy guard: set while forwarding to the collector so a collector write path that
        /// re-logged through BridgeLog would not recurse. Thread-local so concurrent log calls on
        /// different threads do not mask each other.
        /// </summary>
        [ThreadStatic]
        static bool _forwardingToCollector;

        static void DefaultInfo(string? message) => GD.Print(message);
        static void DefaultWarning(string? message) => GD.PushWarning(message);
        static void DefaultError(string? message) => GD.PushError(message);

        /// <summary>Info-level log (maps to <see cref="GD.Print"/> in production + collector
        /// forward).</summary>
        internal static void Info(string message)
        {
            _info(message);
            ForwardToCollector(GodotLogType.Log, message);
        }

        /// <summary>Warning-level log (maps to <see cref="GD.PushWarning"/> in production + collector
        /// forward).</summary>
        internal static void Warning(string message)
        {
            _warning(message);
            ForwardToCollector(GodotLogType.Warning, message);
        }

        /// <summary>Error-level log (maps to <see cref="GD.PushError"/> in production + collector
        /// forward).</summary>
        internal static void Error(string message)
        {
            _error(message);
            ForwardToCollector(GodotLogType.Error, message);
        }

        /// <summary>
        /// Forward a line to the collector (when armed). Guarded against re-entrancy: if the collector
        /// write path ever re-logs through BridgeLog, the nested call is skipped (the guard is on the
        /// forwarding path only, so the Godot sink still fires — the goal is just to avoid infinite
        /// collector→BridgeLog→collector recursion).
        /// </summary>
        static void ForwardToCollector(GodotLogType type, string message)
        {
            if (_collectorSink == null) return;
            if (_forwardingToCollector) return;
            _forwardingToCollector = true;
            try
            {
                _collectorSink(type, message);
            }
            catch
            {
                // The collector sink must never break the caller's log path. Swallow.
            }
            finally
            {
                _forwardingToCollector = false;
            }
        }

        // --- collector sink lifecycle -----------------------------------------------

        /// <summary>
        /// Install the collector forward sink so every <see cref="Info"/>/<see cref="Warning"/>/
        /// <see cref="Error"/> call also appends to the process-wide <see cref="GodotLogCollector"/>.
        /// Called at plugin enable after the collector is installed. The sink captures the collector
        /// via <see cref="GodotLogCollector.GetOrCreate"/> at call time so a hot-reload that installs
        /// a fresh buffer is picked up without re-arming the sink.
        /// </summary>
        internal static void InstallCollectorSink()
        {
            _collectorSink = (type, message) =>
            {
                GodotLogCollector.GetOrCreate().Append(type, message, stackTrace: null,
                    source: GodotLogSource.Bridge);
            };
        }

        /// <summary>Clear the collector forward sink. Called on plugin disable.</summary>
        internal static void RemoveCollectorSink()
        {
            _collectorSink = null;
        }

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
            _collectorSink = null;
        }
    }
}
#endif
