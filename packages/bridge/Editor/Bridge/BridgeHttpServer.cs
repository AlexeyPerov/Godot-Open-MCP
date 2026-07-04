#if TOOLS
#nullable enable
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// The Godot-side HTTP bridge. P1.3 ships the listener lifecycle and <c>GET /ping</c> only —
    /// no <c>/tools/{name}</c> dispatch (P2.1), no instance lock (P1.4), no auth (P5.2). The
    /// listener binds loopback on a per-project port and routes readiness probes; later phases add
    /// tool dispatch, the gate flow, SSE events, and auth atop the same transport.
    ///
    /// <para>
    /// Adapted from Unity Open MCP's <c>BridgeHttpServer</c>. The transport + lifecycle shape is
    /// copied 1:1 (an <see cref="HttpListener"/> on a dedicated listener thread, requests handed to
    /// the ThreadPool, a <see cref="Start"/>/see <see cref="Stop"/> pair driven by the plugin's
    /// enable/disable); the boot entry point differs because Godot has no
    /// <c>[InitializeOnLoad]</c> — <c>GodotOpenMcpPlugin._EnterTree</c> calls
    /// <see cref="Start"/> instead. Every <c>EditorInterface</c> / scene-tree API call in later
    /// phases routes through <c>MainThreadDispatcher</c>; the worker thread never touches editor
    /// APIs directly (packages/bridge/AGENTS.md §Transport).
    /// </para>
    ///
    /// <para>
    /// <b>Port resolution.</b> P1.3 uses a fixed default (<see cref="DefaultPort"/>) with an env-var
    /// override so a single project can boot and be probed. P1.4 replaces <see cref="ResolvePort"/>
    /// with the deterministic per-project formula (<c>20000 + sha256(projectPath) % 10000</c>) so two
    /// projects can run concurrently on different ports; the formula lives in
    /// <c>InstancePortResolver</c> and is kept byte-for-byte in sync with the TS-side discovery.
    /// </para>
    /// </summary>
    public static class BridgeHttpServer
    {
        /// <summary>
        /// Env var that overrides the deterministic default port. Mirrors Unity's
        /// <c>UNITY_OPEN_MCP_BRIDGE_PORT</c>; the Godot analog is
        /// <c>GODOT_OPEN_MCP_BRIDGE_PORT</c>. An explicit override always wins so a pinned config
        /// keeps working.
        /// </summary>
        public const string PortEnvVar = "GODOT_OPEN_MCP_BRIDGE_PORT";

        /// <summary>
        /// The default port when no override is set. P1.4's deterministic resolver will replace this
        /// constant's role in <see cref="ResolvePort"/>; until then this is the port the single-project
        /// P1.3 smoke binds.
        /// </summary>
        public const int DefaultPort = 6043;

        const string LogPrefix = "Godot Open MCP";

        static HttpListener? _listener;
        static Thread? _listenerThread;
        static volatile bool _running;
        static int _port;

        /// <summary>The port the listener is bound to, or 0 when stopped.</summary>
        public static int Port => _port;

        /// <summary>True when the listener is bound and serving requests.</summary>
        public static bool IsRunning => _running;

        /// <summary>
        /// Bind the listener and start serving. Idempotent: a redundant start is a no-op. Resolves
        /// the bind address (loopback only in P1.3) and port, then opens the
        /// <see cref="HttpListener"/> and spawns the listener thread. On bind failure the bridge
        /// stays down but the editor remains usable — the fault is surfaced via <c>GD.PushError</c>
        /// and <see cref="BridgeSession.Connected"/> stays false so <c>/ping</c> is unreachable.
        /// </summary>
        public static void Start() => Start(ResolvePort());

        /// <summary>
        /// Bind the listener on a specific port. Used by <see cref="Start"/> (which resolves the port
        /// via <see cref="ResolvePort"/>) and by the test seam <see cref="StartOnFreePortForTests"/>.
        /// Internal so only the plugin (production) and tests call it.
        /// </summary>
        internal static void Start(int port)
        {
            if (_running)
                return;

            // P1.3: resolve the bind address through the policy. Loopback is always allowed; remote
            // is refused (P5.2 widens this to remote+auth). The decision is made BEFORE touching
            // HttpListener so a misconfigured project fails fast with the actionable refusal
            // message instead of a generic listener exception.
            var bindDecision = BridgeBindAddress.Decide(BridgeBindAddress.Default);
            if (!bindDecision.Allowed)
            {
                BridgeLog.Error($"[{LogPrefix}] Refusing to start HTTP bridge: {bindDecision.RefusalReason}");
                _running = false;
                BridgeSession.SetConnected(false);
                return;
            }
            var effectiveBind = bindDecision.ResolvedAddress;

            try
            {
                _port = port;
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://{effectiveBind}:{_port}/");
                _listener.Start();
                _running = true;
                BridgeSession.SetConnected(true);

                _listenerThread = new Thread(ListenLoop)
                {
                    Name = "Godot Open MCP Bridge HTTP Listener",
                    IsBackground = true,
                };
                _listenerThread.Start();

                BridgeLog.Info($"[{LogPrefix}] Bridge listening on http://{effectiveBind}:{_port}/");
            }
            catch (Exception e)
            {
                // Bind failure (port in use, permission, etc.). The editor stays usable; the bridge
                // is just down for this session. P1.4's deterministic port makes "port in use"
                // collisions between projects rare; an explicit env override remains the escape hatch.
                BridgeLog.Error($"[{LogPrefix}] Failed to start HTTP listener on port {_port}: {e.Message}");
                _running = false;
                BridgeSession.SetConnected(false);
                // Tear down anything half-armed so a retry starts clean.
                TryCleanupListener();
                _port = 0;
            }
        }

        /// <summary>
        /// Stop the listener and join the listener thread. Idempotent: a redundant stop is a no-op.
        /// Called by <c>GodotOpenMcpPlugin._ExitTree</c> on disable / editor quit.
        /// </summary>
        public static void Stop()
        {
            if (!_running)
                return;
            _running = false;
            BridgeSession.SetConnected(false);

            TryCleanupListener();
            TryJoinListenerThread();

            _port = 0;
            BridgeLog.Info($"[{LogPrefix}] Bridge HTTP listener stopped.");
        }

        /// <summary>
        /// Resolve the listener port. P1.3 precedence:
        /// <list type="number">
        ///   <item><see cref="PortEnvVar"/> env var, when a valid port.</item>
        ///   <item><see cref="DefaultPort"/>.</item>
        /// </list>
        /// P1.4 inserts the deterministic per-project hash between (2) and the constant default,
        /// reading <see cref="BridgeSession.ProjectPath"/> as the hash input. Kept as its own method
        /// so the P1.4 widening is a localized diff.
        /// </summary>
        static int ResolvePort()
        {
            var envValue = System.Environment.GetEnvironmentVariable(PortEnvVar);
            if (!string.IsNullOrEmpty(envValue)
                && int.TryParse(envValue, out var envParsed)
                && IsValidPort(envParsed))
            {
                return envParsed;
            }
            return DefaultPort;
        }

        static bool IsValidPort(int port) => port >= 1 && port <= 65535;

        /// <summary>
        /// The listener loop. Blocks on <see cref="HttpListener.GetContext"/>, then hands each
        /// request to the ThreadPool so one slow request cannot stall the listener. Exits when
        /// <see cref="_running"/> flips false (Stop sets it then stops the listener, which unblocks
        /// GetContext with an exception we swallow).
        /// </summary>
        static void ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext? context;
                try
                {
                    context = _listener?.GetContext();
                }
                catch (HttpListenerException)
                {
                    // Listener stopped or interrupted — exit the loop. A real bind-time fault is
                    // surfaced by Start's try/catch, not here.
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception e)
                {
                    if (_running)
                        BridgeLog.Error($"[{LogPrefix}] HTTP listener error: {e.Message}");
                    break;
                }

                if (context == null)
                    break;

                // Dispatch on the ThreadPool so the listener is free to accept the next request.
                // /ping reads only static session state and needs no main-thread hop; later-phase
                // tool dispatch will marshal editor API access through MainThreadDispatcher from
                // inside the handler.
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
            }
        }

        /// <summary>
        /// Route one request. P1.3 handles <c>/ping</c> and a 404 fallback; later phases add
        /// <c>/tools</c>, <c>/tools/{name}</c>, <c>/instance</c>, <c>/events</c>, and the auth gate
        /// that runs before routing. Every branch writes its own response and the finally closes
        /// the response stream.
        /// </summary>
        static void HandleRequest(HttpListenerContext context)
        {
            try
            {
                // Trim trailing slash so /ping and /ping/ route identically (mirrors Unity).
                var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "/";

                switch (path)
                {
                    case "/ping":
                        HandlePing(context);
                        break;
                    default:
                        BridgeHttpResponse.SendNotFound(context, path);
                        break;
                }
            }
            catch
            {
                // Last-resort envelope so a handler throw still produces JSON instead of a half-
                // written response. The 404 path above cannot throw; future tool-dispatch handlers
                // will surface structured execution_error envelopes here.
                try
                {
                    BridgeHttpResponse.SendJson(context, 500,
                        "{\"error\":{\"code\":\"bridge_internal_error\",\"message\":\"Unhandled bridge exception\"}}");
                }
                catch
                {
                    // Response already committed by the failing handler — nothing more we can do.
                }
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        /// <summary>
        /// <c>GET /ping</c> — live bridge status snapshot. Returns the deterministic payload from
        /// <see cref="BridgeJson.BuildPingJson"/> with HTTP 200 once the session is initialized; a
        /// 503 with the fallback payload before that (mirrors Unity's HandlePing). The handler reads
        /// only <see cref="BridgeSession"/>'s cached statics, so it needs no main-thread hop and is
        /// safe to run on the ThreadPool worker.
        /// </summary>
        static void HandlePing(HttpListenerContext context)
        {
            if (!BridgeSession.IsInitialized)
            {
                BridgeHttpResponse.SendJson(context, 503, BridgeJson.BuildPingFallbackJson());
                return;
            }
            BridgeHttpResponse.SendJson(context, 200, BridgeJson.BuildPingJson());
        }

        // --- Cleanup helpers --------------------------------------------------------------

        static void TryCleanupListener()
        {
            try
            {
                _listener?.Stop();
            }
            catch (Exception e)
            {
                BridgeLog.Warning($"[{LogPrefix}] Error stopping HTTP listener: {e.Message}");
            }
            try
            {
                _listener?.Close();
            }
            catch
            {
                // Already closed — benign.
            }
            _listener = null;
        }

        static void TryJoinListenerThread()
        {
            try
            {
                _listenerThread?.Join(2000);
            }
            catch
            {
                // Thread join interrupted — the IsBackground flag means it won't outlive the process.
            }
            _listenerThread = null;
        }

        // ---------------------------------------------------------------------------------------------------
        // Pure-managed test seam. The HTTP integration test (BridgeHttpServerTests) needs a real
        // HttpListener on an OS-assigned port to exercise the /ping readiness path end-to-end. It
        // cannot call Start() (which would bind DefaultPort and collide with a concurrent test run
        // or a developer's editor), so it calls this helper: pick a free port via a throwaway
        // TcpListener, close it, and start the real listener there. The TOCTOU window between close
        // and bind is acceptable for test isolation. Internal (same-assembly only); never referenced
        // by production code.

        /// <summary>
        /// Test-only: start the listener on an OS-assigned free port and return that port. The
        /// caller reads <see cref="Port"/> to build the request URL and calls <see cref="Stop"/> in
        /// cleanup. Mirrors the role of Unity's edit-mode tests that read
        /// <c>BridgeHttpServer.Port</c> dynamically after [InitializeOnLoad] started the listener.
        /// </summary>
        internal static int StartOnFreePortForTests()
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }
            Start(port);
            return port;
        }
    }
}
#endif
