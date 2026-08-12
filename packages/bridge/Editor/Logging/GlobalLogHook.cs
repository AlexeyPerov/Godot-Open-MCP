#if TOOLS
#nullable enable
using System;
using System.Reflection;
using Godot;
using GodotOpenMcp.Bridge.Runtime.Logging;

namespace GodotOpenMcp.Bridge.Editor.Logging
{
    /// <summary>
    /// Arms the global Godot 4.5+ <c>Logger</c> hook (P18.3) so the <see cref="GodotLogCollector"/>
    /// ingests the full editor Output (prints + push_warning/push_error), not just the addon's own
    /// activity. Enriches <c>console_get_logs</c> — no new tool.
    /// </summary>
    ///
    /// <remarks>
    /// <para>
    /// <b>Why reflection + a GDScript <c>Logger</c> subclass?</b> The bridge compiles against
    /// <c>GodotSharp 4.3.0</c> (the engine floor). The managed <c>Logger</c> class +
    /// <c>OS.add_logger</c> only exist on Godot 4.5+, so a compile-time C# subclass would fail to
    /// build. Reflection resolves against the <b>runtime-loaded</b> <c>OS</c> type, so
    /// <c>GetMethod("AddLogger")</c> returns the method on a 4.5+ editor (and <c>null</c> on 4.3,
    /// yielding a clean no-op). The actual <c>Logger</c> subclass lives in GDScript
    /// (<c>engine_log_sink.gd</c>) — GDScript can natively <c>extends Logger</c> on 4.5+ and is only
    /// loaded when the version gate passes. The GDScript instance forwards captured lines into C#
    /// via the <see cref="EngineLogSink"/> bridge object.
    /// </para>
    ///
    /// <para>
    /// <b>Graceful no-op is the contract.</b> Anything going wrong — unsupported version, GDScript
    /// file missing on a partial install, reflection miss, or registration throwing — leaves
    /// <see cref="LogCaptureMode"/> at <c>AddonOnly</c> and logs a single warning. The collector
    /// then behaves exactly as it did before P18.3. Never throws out of the caller.
    /// </para>
    ///
    /// <para>
    /// <b>No unregister.</b> Godot's API exposes no <c>remove_logger</c>. <see cref="Disarm"/>
    /// clears the GDScript sink reference so its overrides become no-ops, and flips
    /// <see cref="LogCaptureMode"/> back to <c>AddonOnly</c>; the registered logger itself stays
    /// (harmless once its sink is null). The sink reference is also dropped on the C# side so the
    /// RefCounted can be collected.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>); not unit-tested in the binary-less host (touches Godot APIs).
    /// The version-gating decision (<see cref="GodotVersionInfo"/>) and the capture-metadata render
    /// (<see cref="LogCaptureMode"/>) ARE pure-managed and unit-tested. Live capture on 4.5+ is
    /// verified by the headless Godot smoke / manual check (per the P18.3 verification plan).
    /// </remarks>
    internal static class GlobalLogHook
    {
        /// <summary>Resource path of the GDScript <c>Logger</c> subclass. The installer loads this
        /// only when <see cref="GodotVersionInfo.SupportsGlobalLogHook"/> is true.</summary>
        const string SinkScriptPath = "res://addons/godot_open_mcp/Editor/Logging/engine_log_sink.gd";

        /// <summary>
        /// The armed <c>EngineLogSink</c> instance (held so the RefCounted is not collected while the
        /// logger is registered), or null when the hook is not armed. Cleared on <see cref="Disarm"/>.
        /// </summary>
        static EngineLogSink? _sink;

        /// <summary>
        /// Try to arm the global <c>Logger</c> hook for the given Godot version. Called at plugin
        /// enable (after the collector + BridgeLog sink are installed). Sets
        /// <see cref="LogCaptureMode"/> to <c>NativeOutput</c> on success, leaves it
        /// <c>AddonOnly</c> on any failure / unsupported version. Idempotent: a redundant arm while
        /// armed is a no-op. Never throws.
        /// </summary>
        /// <returns>True if the hook armed (or was already armed); false on no-op.</returns>
        internal static bool TryArm(string? godotVersionString)
        {
            if (_sink != null)
                return true; // already armed

            var version = GodotVersionInfo.Parse(godotVersionString);
            if (!version.SupportsGlobalLogHook)
            {
                // Expected on the 4.3 floor — quiet no-op (not a warning; the floor is supported).
                LogCaptureMode.Set(LogCaptureModeKind.AddonOnly);
                return false;
            }

            try
            {
                var script = GD.Load<GDScript>(SinkScriptPath);
                if (script == null)
                {
                    Warn("global log hook: engine_log_sink.gd not found — staying addon-only.");
                    return false;
                }

                // Instantiate the GDScript Logger subclass. script.New() returns a Variant holding
                // a RefCounted (the Logger instance); convert so we can Set the sink property.
                var instance = script.New().As<RefCounted>();
                if (instance == null)
                {
                    Warn("global log hook: engine_log_sink.gd did not instantiate a RefCounted — staying addon-only.");
                    return false;
                }

                // Create + assign the C# sink. Held statically so it outlives the registration.
                _sink = new EngineLogSink();
                instance.Set("sink", _sink);

                // Reflectively invoke OS.AddLogger(Logger). Against GodotSharp 4.3.0 the method is
                // absent → GetMethod returns null (clean no-op). On a 4.5+ runtime the loaded OS
                // type has it → Invoke registers the logger.
                var addLogger = typeof(OS).GetMethod(
                    "AddLogger",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (addLogger == null)
                {
                    Warn($"global log hook: OS.AddLogger not available on this build (reported version '{godotVersionString}') — staying addon-only.");
                    _sink = null;
                    return false;
                }

                addLogger.Invoke(null, new object?[] { instance });

                LogCaptureMode.Set(LogCaptureModeKind.NativeOutput);
                BridgeLog.Info($"global log hook armed (Godot {version.Major}.{version.Minor}); console_get_logs now reflects native editor output.");
                return true;
            }
            catch (Exception e)
            {
                // Any failure → stay addon-only. The collector keeps working; the agent simply sees
                // addon-only capture via the metadata. Report once so the failure is discoverable.
                Warn($"global log hook: arm failed ({e.Message}); staying addon-only.");
                _sink = null;
                LogCaptureMode.Set(LogCaptureModeKind.AddonOnly);
                return false;
            }
        }

        /// <summary>
        /// Disarm the hook. Clears the GDScript sink reference (its overrides then no-op) and flips
        /// <see cref="LogCaptureMode"/> back to <c>AddonOnly</c>. Called at plugin disable. The
        /// registered <c>Logger</c> is NOT removed (Godot exposes no unregister); once the sink is
        /// null it captures nothing. Idempotent. Never throws.
        /// </summary>
        internal static void Disarm()
        {
            if (_sink == null)
                return;
            try
            {
                // The GDScript instance holds the only strong ref besides ours; dropping both lets
                // the RefCounted be collected. The Logger registration itself stays (no-op now).
                _sink = null;
            }
            catch (Exception e)
            {
                Warn($"global log hook: disarm error ({e.Message}).");
            }
            finally
            {
                _sink = null;
                LogCaptureMode.Set(LogCaptureModeKind.AddonOnly);
            }
        }

        // Route diagnostics through BridgeLog so the line is captured by the addon collector (tagged
        // bridge) AND visible in the editor Output. Wraps GD.PushWarning so it never throws into the
        // arming caller.
        static void Warn(string message)
        {
            try { BridgeLog.Warning(message); }
            catch { /* never break the caller */ }
        }
    }
}
#endif
