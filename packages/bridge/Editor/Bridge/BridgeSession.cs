#if TOOLS
#nullable enable
using System.Threading;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Process-wide bridge session state. P1.1 shipped only the version constant; P1.3 extends it
    /// with the readiness fields <c>/ping</c> reads (project path, Godot version, connected /
    /// compiling / playing flags). Later phases extend this with instance-lock bookkeeping (P1.4)
    /// and the auth token (P5.2).
    ///
    /// <para>
    /// Mirrors the role of Unity Open MCP's <c>BridgeSession</c> — a static surface the HTTP
    /// handlers and instance lock read from — adapted to Godot where the editor plugin (not
    /// <c>[InitializeOnLoad]</c>) is the boot entry point. The static values are cached once on
    /// enable (<see cref="InitializeForEnable"/>) so a <c>/ping</c> arriving mid-editor-stall
    /// returns a stable, deterministic payload rather than re-querying engine state on the
    /// listener worker thread (which must not touch <c>EditorInterface</c> / scene-tree APIs —
    /// packages/bridge/AGENTS.md §Transport).
    /// </para>
    ///
    /// <para>
    /// The volatile flags (<c>IsCompiling</c>, <c>IsPlaying</c>) are best-effort snapshots for the
    /// readiness payload. Godot has no <c>EditorApplication.isCompiling</c>-equivalent static, so
    /// these stay false in P1.3 (a subsequent phase can wire them to the editor's signals). They
    /// are written under a lock from the main thread and read lock-free from the HTTP worker.
    /// </para>
    /// </summary>
    public static class BridgeSession
    {
        /// <summary>
        /// Bridge version reported over HTTP by <c>/ping</c> and mirrored in the instance lock
        /// (P1.4). Synced from <c>version.json</c> by <c>scripts/sync-version.mjs</c>; never
        /// hand-edit.
        /// </summary>
        public static string BridgeVersion => "0.0.1";

        /// <summary>
        /// Wire mode reported by <c>/ping</c>. Always <c>"live"</c> for the in-editor bridge —
        /// there is no offline/batch mode in Godot (architecture.md: "Godot has no headless editor
        /// batch mode"). Kept as a constant so the readiness payload shape matches Unity's.
        /// </summary>
        public const string Mode = "live";

        // --- Cached on enable (main thread) -------------------------------------------------

        static string? _projectPath;
        static string? _godotVersion;

        // --- Volatile flags (main-thread writes, lock-free reads on the HTTP worker) --------

        static volatile bool _connected;
        static volatile bool _isCompiling;
        static volatile bool _isPlaying;
        static int _initialized; // 0/1 via Interlocked — the IsInitialized gate

        /// <summary>
        /// The project root on disk (the parent of <c>res://</c>), cached when the plugin enables.
        /// Null until <see cref="InitializeForEnable"/> runs. Used by <c>/ping</c> and, in P1.4, by
        /// the deterministic port resolver. Returned as the OS path, not <c>res://</c>, so the
        /// resolver's sha256 is stable across editor sessions.
        /// </summary>
        public static string? ProjectPath => _projectPath;

        /// <summary>
        /// The Godot engine version string (e.g. <c>"4.3.1.stable.mono"</c>), cached when the plugin
        /// enables. Surfaced by <c>/ping</c> as <c>godotVersion</c> (the Godot analog of Unity's
        /// <c>unityVersion</c>). Null until <see cref="InitializeForEnable"/> runs.
        /// </summary>
        public static string? GodotVersion => _godotVersion;

        /// <summary>
        /// True once the HTTP listener has bound its port and is serving requests. Set from
        /// <see cref="BridgeHttpServer.Start"/> / <see cref="BridgeHttpServer.Stop"/>; <c>/ping</c>
        /// reads it to populate the <c>connected</c> field.
        /// </summary>
        public static bool Connected => _connected;

        /// <summary>
        /// Best-effort "editor is currently recompiling C#" flag. P1.3 leaves this false (Godot has
        /// no static compile-state hook the bridge can poll without touching editor-only APIs from
        /// the worker thread); a later phase wires it to the editor's build signals.
        /// </summary>
        public static bool IsCompiling => _isCompiling;

        /// <summary>
        /// Best-effort "editor is in play mode" flag. P1.3 leaves this false (no main-thread refresh
        /// tick yet); a later phase refreshes it from <see cref="EditorInterface"/>'s play state on
        /// a main-thread tick so <c>/ping</c> can report play-mode without a worker-thread
        /// <c>EditorInterface</c> call.
        /// </summary>
        public static bool IsPlaying => _isPlaying;

        /// <summary>
        /// True once <see cref="InitializeForEnable"/> has cached the session's static state and the
        /// plugin is ready to serve <c>/ping</c>. Until it flips, <c>/ping</c> returns the fallback
        /// payload with HTTP 503 (mirrors Unity's <c>BridgeSession.IsInitialized</c> gate).
        /// </summary>
        public static bool IsInitialized => _initialized != 0;

        /// <summary>
        /// Cache the session's static state when the plugin enables. Runs on the editor main thread
        /// (called from <c>GodotOpenMcpPlugin._EnterTree</c>), so it may freely touch engine APIs.
        /// Idempotent: a redundant enable re-caches the same values without side effects.
        /// </summary>
        internal static void InitializeForEnable()
        {
            // Project path: the OS path of the project root (parent of res://). GodotSharp exposes
            // res:// via ProjectSettings, and GlobalizePath turns it into an absolute OS path. P1.4's
            // port resolver hashes this exact string, so it must be stable and absolute.
            _projectPath = ResolveProjectPath();

            // Godot version: Engine.GetVersionInfo returns a Dictionary with a "string" field carrying
            // the full build qualifier (e.g. "4.3.1.stable.mono"). Surfaced as-is over /ping.
            _godotVersion = ResolveGodotVersion();

            Interlocked.Exchange(ref _initialized, 1);
        }

        /// <summary>
        /// Reset the volatile session state when the plugin disables. Does NOT clear the cached
        /// project path / version — those are immutable for the editor session and a re-enable
        /// would just re-cache the same values. Mirrors Unity's SetConnected(false) on teardown.
        /// </summary>
        internal static void ResetForDisable()
        {
            _connected = false;
            _isCompiling = false;
            _isPlaying = false;
            Interlocked.Exchange(ref _initialized, 0);
        }

        /// <summary>
        /// Set the <see cref="Connected"/> flag. Called by <see cref="BridgeHttpServer"/> when the
        /// listener binds (true) or stops (false). Mirrors Unity's
        /// <c>BridgeSession.SetConnected</c>.
        /// </summary>
        internal static void SetConnected(bool value) => _connected = value;

        /// <summary>
        /// Resolve the project root OS path. Guarded so a failure never throws out of enable — the
        /// worst case is a null <c>projectPath</c> in the readiness payload, which P1.4's resolver
        /// turns into a stable-but-generic fallback port (mirrors Unity's GetProjectPathForPort).
        /// </summary>
        static string? ResolveProjectPath()
        {
            try
            {
                var resPath = ProjectSettings.GlobalizePath("res://");
                if (!string.IsNullOrEmpty(resPath))
                {
                    // GlobalizePath returns the project root with a trailing separator on most
                    // platforms; trim it so the resolver hashes a canonical form. Godot's
                    // GetParent isn't available on a string, so do it by trimming separators.
                    return resPath.TrimEnd('/', '\\');
                }
            }
            catch
            {
                // Editor not fully initialized yet — leave null; re-enable re-caches.
            }
            return null;
        }

        /// <summary>
        /// Resolve the Godot version string from <see cref="Engine.GetVersionInfo"/>. Guarded so a
        /// failure leaves <c>godotVersion</c> null rather than faulting enable.
        /// </summary>
        static string? ResolveGodotVersion()
        {
            try
            {
                var info = Engine.GetVersionInfo();
                if (info != null && info.TryGetValue("string", out var s) && s.VariantType == Variant.Type.String)
                    return s.AsString();
            }
            catch
            {
                // Engine not ready — leave null; re-enable re-caches.
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------------------
        // Pure-managed test seams. The session's state is static and outlives a single test, and the
        // production getters read cached fields that are only set on enable. The unit tests cannot
        // drive a real Godot enable cycle (EditorInterface / ProjectSettings / Engine fault the
        // binary-less host), so these seams let them set the cached values and the IsInitialized /
        // Connected flags directly and assert the /ping JSON shape deterministically. They are
        // internal (same-assembly only) and never referenced by production code.

        /// <summary>
        /// Test-only: set the cached project path and mark the session initialized, WITHOUT touching
        /// Godot APIs. Lets a unit test pin the <c>/ping</c> payload deterministically.
        /// </summary>
        internal static void SetProjectPathForTests(string? projectPath)
        {
            _projectPath = projectPath;
            Interlocked.Exchange(ref _initialized, 1);
        }

        /// <summary>Test-only: set the cached Godot version string.</summary>
        internal static void SetGodotVersionForTests(string? version) => _godotVersion = version;

        /// <summary>Test-only: set the volatile compiling flag.</summary>
        internal static void SetCompilingForTests(bool value) => _isCompiling = value;

        /// <summary>Test-only: set the volatile playing flag.</summary>
        internal static void SetPlayingForTests(bool value) => _isPlaying = value;

        /// <summary>
        /// Test-only: reset ALL session state to its pre-enable defaults. Static state outlives a
        /// single test, so a test exercising the enable/initialized edges MUST call this first to
        /// start from a clean baseline.
        /// </summary>
        internal static void ResetForTests()
        {
            _projectPath = null;
            _godotVersion = null;
            _connected = false;
            _isCompiling = false;
            _isPlaying = false;
            Interlocked.Exchange(ref _initialized, 0);
        }
    }
}
#endif
