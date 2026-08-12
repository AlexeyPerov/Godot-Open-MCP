#nullable enable
using System.Globalization;
using System.Text;

namespace GodotOpenMcp.Bridge.Runtime.Logging
{
    /// <summary>
    /// Active capture mode for the console-log collector (P18.3). The collector itself always
    /// captures the addon's own <c>BridgeLog</c> activity; on Godot 4.5+ a global <c>Logger</c> hook
    /// can be armed to also ingest the broader editor Output (prints + push_warning/push_error). This
    /// static carries the active mode so the <c>console_get_logs</c> response can report it honestly
    /// via <see cref="AppendCaptureJsonTo"/>.
    /// </summary>
    /// <remarks>
    /// Pure-managed (no Godot API, no <c>#if TOOLS</c>) so the capture-metadata rendering is
    /// unit-testable in the binary-less xUnit host. The editor-only <c>GlobalLogHook</c> sets the
    /// mode after a successful (or failed/no-op) hook installation; the default is
    /// <see cref="AddonOnly"/>, matching the pre-P18.3 behavior exactly.
    /// </remarks>
    public enum LogCaptureModeKind
    {
        /// <summary>Addon-only capture (the pre-P18.3 default): the collector holds only the
        /// addon's own <c>BridgeLog</c> + tool-handler activity. This is the mode on Godot 4.3
        /// (current floor) and whenever the 4.5+ hook fails to arm.</summary>
        AddonOnly = 0,

        /// <summary>Native-output capture: a global <c>Logger</c> hook is armed (Godot 4.5+) and the
        /// collector ingests the editor's print/push_warning/push_error stream from the moment the
        /// plugin enabled. Pre-enable boot lines are NOT captured (the hook registers at enable
        /// time).</summary>
        NativeOutput = 1,
    }

    /// <summary>
    /// Process-wide capture-mode state + capture-capability JSON renderer (P4.7 metadata, made
    /// dynamic in P18.3). Read by <c>ConsoleTools.GetLogs</c> to populate the <c>capture</c> object;
    /// written by the editor-only <c>GlobalLogHook</c>.
    /// </summary>
    public static class LogCaptureMode
    {
        static volatile LogCaptureModeKind _kind = LogCaptureModeKind.AddonOnly;

        /// <summary>The active capture mode. Volatile-read so the HTTP worker thread sees the latest
        /// arming decision without a lock.</summary>
        public static LogCaptureModeKind Kind => _kind;

        /// <summary>True when a global engine error/message sink is armed (Godot 4.5+
        /// <c>Logger</c> hook). Surfaced as <c>capture.engineErrorSinkActive</c>.</summary>
        public static bool EngineErrorSinkActive => _kind == LogCaptureModeKind.NativeOutput;

        /// <summary>True when native editor Output is being tapped (best-effort: from plugin enable
        /// onward — pre-enable boot lines are missed). Surfaced as
        /// <c>capture.nativeOutputComplete</c>. Conservative name retained for back-compat with the
        /// P4.7 contract; the <c>mode</c> token is the clearer signal.</summary>
        public static bool NativeOutputComplete => _kind == LogCaptureModeKind.NativeOutput;

        /// <summary>The human-readable mode token emitted in the <c>capture</c> object:
        /// <c>"addon_only"</c> (pre-P18.3 behavior / 4.3 / failed hook) or <c>"native_output"</c>
        /// (4.5+ hook armed).</summary>
        public static string ModeToken => _kind == LogCaptureModeKind.NativeOutput
            ? "native_output"
            : "addon_only";

        /// <summary>Set the active capture mode. Called by the editor-only <c>GlobalLogHook</c> after
        /// a successful arm (<c>NativeOutput</c>) or a no-op / disarm (<c>AddonOnly</c>).
        /// Volatile-written so readers pick it up without a lock.</summary>
        internal static void Set(LogCaptureModeKind kind) => _kind = kind;

        /// <summary>
        /// Append the <c>capture</c> capability object to <paramref name="sb"/> (without the
        /// enclosing braces — the caller places it inside the response object). Field order is fixed
        /// (<c>nativeOutputComplete</c>, <c>engineErrorSinkActive</c>, <c>mode</c>) so diffing clients
        /// don't flap on reordering. Mirrors the P4.7 shape with a new <c>mode</c> token.
        /// </summary>
        public static void AppendCaptureJsonTo(StringBuilder sb)
        {
            // Snapshot the volatile read once so the three fields are consistent within one render.
            var kind = _kind;
            var native = kind == LogCaptureModeKind.NativeOutput;
            sb.Append("\"nativeOutputComplete\":")
                .Append(native ? "true" : "false");
            sb.Append(",\"engineErrorSinkActive\":")
                .Append(native ? "true" : "false");
            sb.Append(",\"mode\":")
                .Append(kind == LogCaptureModeKind.NativeOutput ? "\"native_output\"" : "\"addon_only\"");
        }

        // --- Test seams ------------------------------------------------------

        /// <summary>Test-only: reset to the pre-enable default. Static state outlives a single test,
        /// so a test exercising the metadata render MUST call this first to start from a clean
        /// baseline. Never referenced by production code.</summary>
        internal static void ResetForTests() => _kind = LogCaptureModeKind.AddonOnly;
    }
}
