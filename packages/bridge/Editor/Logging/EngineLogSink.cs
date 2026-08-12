#if TOOLS
#nullable enable
using Godot;
using GodotOpenMcp.Bridge.Runtime.Logging;

namespace GodotOpenMcp.Bridge.Editor.Logging
{
    /// <summary>
    /// C# ↔ GDScript bridge object that receives captured engine/editor log lines from the global
    /// Godot 4.5+ <c>Logger</c> hook (P18.3) and appends them to the <see cref="GodotLogCollector"/>
    /// tagged <see cref="GodotLogSource.Engine"/>.
    /// </summary>
    ///
    /// <remarks>
    /// <para>
    /// <b>Why a <see cref="RefCounted"/> subclass and not a plain static?</b> The bridge compiles
    /// against <c>Godot.NET.Sdk/4.3.0</c> (the engine floor), but the managed <c>Logger</c> class +
    /// <c>OS.add_logger</c> registration API only exist on Godot 4.5+. A C# class cannot subclass
    /// <c>Logger</c> without a compile-time reference to the 4.5+ type, so the actual
    /// <c>Logger</c> subclass lives in GDScript (<c>engine_log_sink.gd</c>) and is loaded at runtime
    /// only on 4.5+. That GDScript instance needs a way to forward captured lines back into C#;
    /// deriving this sink from <see cref="RefCounted"/> exposes its public methods to GDScript (the
    /// standard Godot C# ↔ GDScript interop), letting the GDScript logger call
    /// <c>sink.on_message(...)</c> / <c>sink.on_error(...)</c> directly.
    /// </para>
    ///
    /// <para>
    /// <b>Dedup vs <see cref="BridgeLog"/>.</b> The global hook receives every <c>print</c> /
    /// <c>push_warning</c> / <c>push_error</c> — including the bridge's own emissions, which
    /// <see cref="BridgeLog"/> already forwards to the collector. <see cref="BridgeLog"/> sets a
    /// thread-static <see cref="BridgeLog.IsBridgeOriginated"/> flag for the duration of its
    /// Info/Warning/Error calls; this sink checks it and skips when set, so bridge-originated lines
    /// are captured exactly once (tagged <c>bridge</c> by BridgeLog) and everything else is captured
    /// by the hook (tagged <c>engine</c>). The flag is thread-static and the GDScript→C# call runs
    /// synchronously on the same thread that issued the <c>print</c>, so the check needs no lock.
    /// </para>
    ///
    /// <para>
    /// <b>Thread safety.</b> Godot invokes <c>Logger._log_message</c> / <c>_log_error</c> from
    /// arbitrary threads (documented), possibly concurrently. The underlying collector is
    /// lock-guarded, so these methods need no extra synchronization. They must not call
    /// <c>print</c> / <c>push_error</c> / <c>push_warning</c> (documented recursion hazard), and they
    /// never do — they append to the pure-managed collector only.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): the global hook is armed only by the editor plugin. Not
    /// unit-tested in the binary-less host (it P/Invokes the native <c>RefCounted</c> ctor); the
    /// headless Godot smoke on 4.5+ covers the live path.
    /// </remarks>
    public sealed class EngineLogSink : RefCounted
    {
        /// <summary>
        /// Forward a <c>print</c>-originated line (Godot <c>Logger._log_message</c>). <paramref name="error"/>
        /// is true for stderr-bound prints (<c>print_error</c>); mapped to <see cref="GodotLogType.Error"/>,
        /// otherwise <see cref="GodotLogType.Log"/>.
        /// </summary>
        /// <remarks>Exposed to GDScript as <c>on_message</c> (Godot bridges PascalCase → snake_case for
        /// user C# methods).</remarks>
        public void OnMessage(string message, bool error)
        {
            // Skip lines the bridge already captured via BridgeLog (single-source dedup).
            if (BridgeLog.IsBridgeOriginated) return;
            if (string.IsNullOrEmpty(message)) return;
            var type = error ? GodotLogType.Error : GodotLogType.Log;
            GodotLogCollector.GetOrCreate().Append(type, message, stackTrace: null,
                source: GodotLogSource.Engine);
        }

        /// <summary>
        /// Forward a <c>push_error</c> / <c>push_warning</c> line (Godot <c>Logger._log_error</c>).
        /// <c>error_type</c> is Godot's <c>Logger.ErrorType</c>: 1 = warning, 0/2/3 = error (script /
        /// shader errors are surfaced as errors). Only <paramref name="rationale"/> carries the
        /// human-readable text; the file/line/backtrace fields are intentionally dropped to keep the
        /// collector entry shape stable (a future enhancement can assemble a stack-trace string).
        /// </summary>
        /// <remarks>Exposed to GDScript as <c>on_error</c>.</remarks>
        public void OnError(string rationale, int errorType)
        {
            if (BridgeLog.IsBridgeOriginated) return;
            if (string.IsNullOrEmpty(rationale)) return;
            // ErrorType: 0=ERROR, 1=WARNING, 2=SCRIPT, 3=SHADER. Treat only WARNING (1) as a warning;
            // everything else is an error.
            var type = errorType == 1 ? GodotLogType.Warning : GodotLogType.Error;
            GodotLogCollector.GetOrCreate().Append(type, rationale, stackTrace: null,
                source: GodotLogSource.Engine);
        }
    }
}
#endif
