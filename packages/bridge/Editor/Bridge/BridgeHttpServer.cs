#if TOOLS
#nullable enable
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using GodotOpenMcp.Bridge.Runtime.MainThread;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// The Godot-side HTTP bridge. P1.3 shipped the listener lifecycle and <c>GET /ping</c>;
    /// later phases added <c>/tools/{name}</c> dispatch (P2.1), the instance lock (P1.4), the gate
    /// flow (P3.5), and bearer-token auth (P5.2). The listener binds a per-project port
    /// (loopback by default; remote bind is opt-in and requires <c>authMode:"required"</c>) and
    /// runs a single <see cref="CheckAuth"/> gate before routing so every endpoint is auth-gated
    /// equally when auth is enabled.
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

            // P5.2 — resolve the bind address + auth mode from project settings. Loopback is always
            // allowed; remote (0.0.0.0) is allowed only when authMode is "required" so an accidental
            // remote bind on an open network never serves unauthenticated traffic. The decision is
            // made BEFORE touching HttpListener so a misconfigured project fails fast with the
            // actionable refusal message instead of a generic listener exception.
            var authMode = BridgeAuthPolicy.GetDefault();
            var bindAddress = BridgeProjectSettings.BindAddress;
            var bindDecision = BridgeBindAddress.Decide(bindAddress, authMode);
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
        /// Route one request. P1.3 handled <c>/ping</c> and a 404 fallback; P2.1 adds
        /// <c>POST /tools/{name}</c> dispatch. Later phases add <c>/tools</c>,
        /// <c>/instance</c>, <c>/events</c>, and the auth gate that runs before routing. Every
        /// branch writes its own response and the finally closes the response stream.
        /// </summary>
        static void HandleRequest(HttpListenerContext context)
        {
            try
            {
                // P5.2 — auth check runs before routing so every endpoint (/ping, /tools/*, and any
                // future /instance, /events, ...) is gated equally when authMode is "required". The
                // MCP client always carries the bearer from the instance lock; a hand-rolled curl
                // without it gets a 401. No endpoint is exempt. Under authMode "none" (the default)
                // this is a single pure-function call that returns true, so the cost on the common
                // path is negligible.
                if (!CheckAuth(context))
                {
                    return;
                }

                // Trim trailing slash so /ping and /ping/ route identically (mirrors Unity).
                var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "/";

                // /ping is GET-only (mirrors Unity). A POST to /ping is a 405, not a 404, so a
                // client that mistypes the method gets an actionable error.
                if (path == "/ping")
                {
                    if (context.Request.HttpMethod == "GET")
                    {
                        HandlePing(context);
                    }
                    else
                    {
                        BridgeHttpResponse.SendMethodNotAllowed(context, "GET required for /ping");
                    }
                    return;
                }

                // POST /tools/{name} — tool dispatch (P2.1). The name segment is everything after
                // "/tools/"; an empty name falls through to the 404 below (there is no index
                // handler for /tools/ yet — the GET /tools capability endpoint arrives in a later
                // phase).
                if (path.StartsWith("/tools/", StringComparison.Ordinal))
                {
                    var toolName = path.Substring("/tools/".Length);
                    if (string.IsNullOrEmpty(toolName))
                    {
                        BridgeHttpResponse.SendNotFound(context, path);
                        return;
                    }
                    if (context.Request.HttpMethod != "POST")
                    {
                        BridgeHttpResponse.SendMethodNotAllowed(context, "POST required for tool endpoints");
                        return;
                    }
                    HandleToolDispatch(context, toolName);
                    return;
                }

                BridgeHttpResponse.SendNotFound(context, path);
            }
            catch
            {
                // Last-resort envelope so a handler throw still produces JSON instead of a half-
                // written response. The 404 path above cannot throw; tool-dispatch handler throws
                // are caught inside HandleToolDispatch and surfaced as execution_error envelopes,
                // so reaching here means something in the router itself faulted.
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
        /// P5.2 — bridge auth gate. Returns true when the request may proceed, false when a 401 has
        /// already been written. The pure decision lives in <see cref="BridgeAuthCheck.IsAuthorized"/>
        /// (constant-time token compare, fail-closed on unknown policy) so it is unit-testable without
        /// an <see cref="HttpListener"/>; this method is the thin I/O adapter that reads the
        /// <c>Authorization</c> header, resolves the policy + expected token, and writes the 401 on
        /// denial. Runs before routing so every endpoint is gated equally — no exemption for
        /// <c>/ping</c> or any future SSE/resource endpoint. Adapted from Unity's
        /// <c>BridgeHttpServer.CheckAuth</c>.
        /// </summary>
        static bool CheckAuth(HttpListenerContext context)
        {
            string? headerValue = null;
            try { headerValue = context.Request.Headers["Authorization"]; }
            catch { /* malformed header — treat as missing */ }

            var policy = BridgeAuthPolicy.GetDefault();
            var expected = BridgeInstanceLock.AuthToken;

            if (BridgeAuthCheck.IsAuthorized(policy, headerValue, expected))
                return true;

            BridgeHttpResponse.SendUnauthorized(context,
                "Missing or invalid Authorization header. Set authMode to \"none\" in " +
                ".godot-open-mcp/settings.json, or send Authorization: Bearer <token>. " +
                "The token is minted into the instance lock at " +
                "~/.godot-open-mcp/instances/<sha256(projectPath)>.json on bridge start.");
            return false;
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

    // --- POST /tools/{name} dispatch (P2.1 + P3.5) -----------------------------------
    //
    // The dispatch path enforces two bridge AGENTS.md contracts:
    //   - §Transport: every tool handler runs on the editor main thread via MainThreadDispatcher.
    //   - §Gate policy: every mutating tool routes through GatePolicy.Execute (checkpoint → mutate
    //     → validate → delta). Read-only tools bypass the gate.
    //
    // The HTTP worker thread reads the body, resolves the timeout, resolves the gate context
    // (gate mode + paths_hint), and — for mutators with an active gate and no paths_hint — rejects
    // with paths_hint_required BEFORE the main-thread hop (cheap reject, no whole-project fallback).
    // It then marshals the gate-wrapped dispatch to the main thread. The gate returns a
    // GateDispatchResult; this method wraps it into the canonical {ok,result,error} envelope
    // (BridgeEnvelope), prepending a `gate` block into the result when the gate ran.
    //
    // Failure classification (all surface as HTTP 200 with ok:false — the request reached the
    // dispatcher and was processed; HTTP-level 4xx/5xx is reserved for routing/transport
    // faults):
    //   - tool_not_found       → 404 (the name is not registered)
    //   - invalid_request      → 400 (body could not be read)
    //   - paths_hint_required  → ok:false (mutator + active gate + empty paths_hint)
    //   - main_thread_blocked  → ok:false, code main_thread_blocked (timeout, never drained)
    //   - timeout              → ok:false, code timeout (handler ran past timeout)
    //   - execution_error      → ok:false, code execution_error (handler threw)

    /// <summary>
    /// Dispatch one <c>POST /tools/{name}</c> request. Reads the body, resolves the tool and its gate
    /// context, marshals the gate-wrapped handler to the main thread, and writes the canonical
    /// envelope. The method never throws — every failure path writes a structured response. Adapted
    /// from Unity's <c>HandleToolDispatch</c> / <c>DispatchWithGate</c>, with the queue / audit /
    /// toggle / scene-dirty / lifecycle machinery stripped (later phases).
    /// </summary>
    static void HandleToolDispatch(HttpListenerContext context, string toolName)
    {
        // Unknown tool → 404 tool_not_found before reading the body (cheap reject).
        if (!BridgeToolRegistry.TryGet(toolName, out var entry))
        {
            BridgeHttpResponse.SendToolNotFound(context, toolName);
            return;
        }

        string body;
        try
        {
            body = BridgeRequestBody.ReadRequestBody(context.Request);
        }
        catch (Exception e)
        {
            BridgeHttpResponse.SendInvalidRequest(context,
                $"Failed to read request body: {e.Message}");
            return;
        }

        var timeoutMs = BridgeRequestBody.ExtractTimeoutMs(body);

        // Resolve the gate context for this dispatch. Precedence (§Gate policy):
        //   request `gate` → project default → tool default. The tool default is every tool's
        // registered DefaultGate; in P2.x every mutator ships with defaultGate "off", so the gate
        // cycle only runs when an agent opts in via the request `gate`. paths_hint is required only
        // when the effective gate is enforce/warn — there is no whole-project fallback.
        var gateMode = BridgeRequestBody.ExtractGateMode(body, entry.DefaultGate);
        var isMutating = entry.IsMutating;

        if (isMutating && gateMode != GateDefaultPolicy.Off)
        {
            // P3.7 — a dry-run apply_fix mutates nothing (it previews Describe / fix-list / unknown-fix),
            // so it does not need a paths_hint scope and is dispatched read-only (see DispatchWithGate).
            // Exempt it from the paths_hint requirement so an agent can preview a fix without inventing a
            // scope; a non-dry-run apply still requires the hint (the gate checkpoints it for rollback).
            var exemptFromPathsHint = toolName == GateTools.ApplyFixToolName
                && JsonBody.GetBool(body, "dry_run", true);
            if (!exemptFromPathsHint)
            {
                var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
                if (pathsHint == null || pathsHint.Length == 0)
                {
                    // Cheap reject on the worker thread — do not marshal to the main thread just to fail.
                    // The structured code lets an agent re-issue with paths_hint set without guessing.
                    BridgeHttpResponse.SendJson(context, 200,
                        BridgeEnvelope.BuildPathsHintRequired(toolName, gateMode));
                    return;
                }
            }
        }

        GateDispatchResult gateResult;
        try
        {
            gateResult = DispatchOnMainThread(toolName, body, isMutating, gateMode, timeoutMs);
        }
        catch (MainThreadBlockedException)
        {
            // The main thread never drained the work within the timeout — almost certainly a Godot
            // modal blocking the editor. Surface a structured error so an agent can branch (dismiss
            // the dialog, scene_save, restart) rather than retry blindly.
            var blocked = ToolDispatchResult.Fail(
                "main_thread_blocked",
                $"Tool '{toolName}' could not run — the Godot main thread is blocked by a modal " +
                "dialog (unsaved changes, export, a third-party editor window) or a long editor " +
                "operation. Do NOT raise timeout_ms. Check editor state, dismiss any open dialog, " +
                $"or restart the editor. (waited {timeoutMs}ms)");
            BridgeHttpResponse.SendJson(context, 200, BridgeEnvelope.BuildFailure(blocked));
            return;
        }
        catch (TimeoutException)
        {
            // The handler started but ran past the timeout. Distinguished from main_thread_blocked so
            // an agent can tell "the work is slow" from "the work never started".
            var timedOut = ToolDispatchResult.Fail(
                "timeout",
                $"Tool '{toolName}' started but did not finish within {timeoutMs}ms. " +
                "The tool itself is slow — raise timeout_ms or simplify the request.");
            BridgeHttpResponse.SendJson(context, 200, BridgeEnvelope.BuildFailure(timedOut));
            return;
        }
        catch (Exception e)
        {
            // Handler/dispatcher threw an unhandled exception. Surface it as execution_error rather
            // than crashing the worker.
            var fault = ToolDispatchResult.Fail("execution_error", e.Message);
            BridgeHttpResponse.SendJson(context, 200, BridgeEnvelope.BuildFailure(fault));
            return;
        }

        // Wrap into the canonical envelope. Every dispatch outcome (success or failure) is HTTP 200 —
        // the request reached the dispatcher and was processed. The ok:false flag + error.code carry
        // the failure classification; the gate block is prepended into result on a gate-run success.
        BridgeHttpResponse.SendJson(context, 200,
            BridgeEnvelope.BuildFromGateResult(gateResult, gateMode));
    }

    /// <summary>
    /// Marshal a gate-wrapped tool dispatch to the editor main thread and await its result. When the
    /// caller is already on the main thread (e.g. a test driving the dispatcher directly), the
    /// dispatch runs inline — no queue hop. Otherwise the call goes through
    /// <see cref="MainThreadDispatcher.EnqueueAsync{T}"/> with the per-call timeout, which distinguishes
    /// <c>main_thread_blocked</c> (never drained) from <c>timeout</c> (ran long).
    /// </summary>
    static GateDispatchResult DispatchOnMainThread(
        string toolName, string body, bool isMutating, string gateMode, int timeoutMs)
    {
        // Test seam: when the integration test installs an inline dispatcher, run the dispatch on the
        // calling thread directly. The binary-less test host has no live dispatcher Node to drain the
        // queue, so the real EnqueueAsync path would time out. The seam lets the HTTP integration test
        // exercise the routing + gate + envelope contract without a Godot Node; the main-thread-
        // marshaling behavior itself is covered by the MainThreadDispatcher unit tests. Never set in
        // production.
        var inlineDispatcher = _dispatchForTests;
        if (inlineDispatcher != null)
            return inlineDispatcher(toolName, body, isMutating, gateMode, timeoutMs);

        // Fast path: already on the main thread. Tests that drive the dispatcher directly skip the
        // queue hop. Production HTTP workers are NEVER on the main thread, so this branch is test-only
        // in practice — but it keeps the dispatcher unit-testable without a live dispatcher Node.
        if (MainThreadDispatcher.IsMainThread)
        {
            return DispatchWithGate(toolName, body, isMutating, gateMode);
        }

        // Marshal to the main thread and await. EnqueueAsync faults the task with
        // MainThreadBlockedException (never drained) or TimeoutException (ran long) on timeout.
        return MainThreadDispatcher.EnqueueAsync(
            () => DispatchWithGate(toolName, body, isMutating, gateMode), timeoutMs)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// Run one tool through the gate policy. Read-only tools bypass the gate
    /// (<see cref="GateDispatchResult.Direct"/>); mutating tools run the checkpoint → mutate →
    /// validate → delta cycle via <see cref="GatePolicy.Execute"/>. The <paramref name="gateMode"/>
    /// string is parsed here (once) and the parsed mode threads into <see cref="GatePolicy.Execute"/>
    /// — the policy decides whether to run the cycle based on the parsed <see cref="GateMode"/>.
    ///
    /// <para>
    /// <paramref name="gateMode"/> here is the EFFECTIVE mode after request → project → tool
    /// precedence resolution in <see cref="HandleToolDispatch"/>. paths_hint was already validated
    /// non-empty for enforce/warn before the main-thread hop; <see cref="GatePolicy.Execute"/>
    /// degrades gracefully if it is somehow empty (defensive).
    /// </para>
    /// </summary>
    static GateDispatchResult DispatchWithGate(
        string toolName, string body, bool isMutating, string gateMode)
    {
        if (!isMutating)
        {
            // Read-only tool: no gate. Run the handler directly and wrap in a non-gate result.
            return GateDispatchResult.Direct(DispatchTool(toolName, body));
        }

        // P3.7 — apply_fix has two dispatch shapes:
        //   - dry-run (default): a preview (Describe / fix list / unknown-fix). No project change, so it
        //     bypasses the gate like a read-only tool. Routing it through the gate would run a checkpoint
        //     + validate cycle for a no-op mutation, wasting a verify scan and (under enforce) potentially
        //     flagging pre-existing errors against a preview that changed nothing.
        //   - non-dry-run: the real apply. Route through ApplyFixGateRunner, which wraps the gate cycle
        //     with safe auto-fix rollback (a fix that fails or introduces new errors is restored to its
        //     pre-fix state and the envelope carries a `rollback` block).
        if (toolName == GateTools.ApplyFixToolName)
        {
            var applyDryRun = JsonBody.GetBool(body, "dry_run", true);
            if (applyDryRun)
                return GateDispatchResult.Direct(DispatchTool(toolName, body));
            var applyPathsHint = BridgeRequestBody.ExtractPathsHint(body);
            return ApplyFixGateRunner.Execute(body, gateMode, applyPathsHint);
        }

        // Mutating tool: route through the mandatory gate path. Parse the effective mode string once;
        // the paths_hint re-extraction keeps GatePolicy self-contained (it does not trust the caller's
        // pre-validation — an empty hint here degrades to a skipped gate, see GatePolicy.Execute).
        var mode = GatePolicy.ParseMode(gateMode);
        var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
        return GatePolicy.Execute(mode, pathsHint, () => DispatchTool(toolName, body));
    }

    /// <summary>
    /// Test-only seam: when non-null, <see cref="DispatchOnMainThread"/> invokes this delegate instead
    /// of marshaling through <see cref="MainThreadDispatcher"/>. The binary-less test host cannot
    /// construct the dispatcher node, so the real queue-and-drain path has nothing to drain it; this
    /// seam lets the HTTP integration test run the gate-wrapped dispatch inline and exercise the
    /// routing + gate + envelope contract. Mirrors the <see cref="BridgeLog.SetLoggersForTests"/>
    /// pattern. Never set in production; cleared by <see cref="ResetForTests"/>.
    /// </summary>
    static Func<string, string, bool, string, int, GateDispatchResult>? _dispatchForTests;

    /// <summary>Test-only: install an inline dispatcher (or null to restore the real path).</summary>
    internal static void SetDispatchForTests(
        Func<string, string, bool, string, int, GateDispatchResult>? dispatcher) =>
        _dispatchForTests = dispatcher;

    /// <summary>Test-only: clear the inline dispatcher seam.</summary>
    internal static void ResetForTests() => _dispatchForTests = null;

    /// <summary>
    /// Invoke the registered handler for <paramref name="toolName"/> with <paramref name="body"/>.
    /// Returns the handler's <see cref="ToolDispatchResult"/>; a missing handler surfaces a
    /// <c>tool_not_found</c> failure (defensive — the routing layer already rejected unknown names,
    /// but a race between Contains and TryDispatch could still miss). Mirrors Unity's
    /// <c>DispatchTool</c>.
    /// </summary>
    static ToolDispatchResult DispatchTool(string toolName, string body)
    {
        return BridgeToolRegistry.TryDispatch(toolName, body)
            ?? ToolDispatchResult.Fail("tool_not_found", $"Unknown tool: {toolName}");
    }

    /// <summary>
    /// Test-only pass-through to <see cref="DispatchWithGate"/>. Exposed so the inline dispatcher seam
    /// (<see cref="SetDispatchForTests"/>) can invoke the real gate-wrapped dispatch path (gate policy
    /// + registry lookup + handler) without re-implementing it in the test. Never referenced by
    /// production code. Carries the gate context (isMutating + gateMode) so the gate path is exercised
    /// end-to-end through the inline seam.
    /// </summary>
    internal static GateDispatchResult DispatchWithGateForTests(
        string toolName, string body, bool isMutating, string gateMode) =>
        DispatchWithGate(toolName, body, isMutating, gateMode);

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
