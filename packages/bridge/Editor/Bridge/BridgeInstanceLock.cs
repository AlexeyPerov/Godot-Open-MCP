#if TOOLS
#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Instance lock + heartbeat file. P1.4 — copy of Unity Open MCP's <c>BridgeInstanceLock</c>,
    /// adapted to the Godot home-dir convention and the bridge's diagnostic surface
    /// (<see cref="BridgeLog"/> instead of <c>UnityEngine.Debug</c>). P5.2 mints the per-session
    /// bearer token via <c>BridgeAuthToken.Generate</c> and mirrors it into the lock JSON as
    /// <c>authToken</c>; the MCP server auto-discovers it (<c>mcp-server/src/instance-discovery.ts</c>
    /// <c>resolveAuthToken</c>) and attaches <c>Authorization: Bearer</c> on every request.
    ///
    /// <para>
    /// Each running bridge instance owns a lock file at
    /// <c>~/.godot-open-mcp/instances/&lt;projectHash&gt;.json</c>. The file doubles as the
    /// heartbeat: it carries the current editor state (<c>idle</c> / <c>compiling</c> /
    /// <c>reloading</c> / <c>playing</c> / ...) and will be rewritten periodically by a heartbeat
    /// tick (a later phase) plus on every forced state transition. The MCP server reads it to
    /// discover the right port per project without an HTTP round-trip and without sharing config with
    /// the bridge.
    /// </para>
    ///
    /// <para>
    /// Lifecycle:
    /// <list type="bullet">
    ///   <item><see cref="Acquire"/> — called once when the HTTP listener starts. Sweeps stale locks
    ///   (PID no longer alive) across ALL instances, then writes this instance's lock.</item>
    ///   <item><see cref="UpdateState"/> — called by the heartbeat tick and on transitions. Atomic
    ///   write (<c>.tmp</c> + rename, same pattern as Unity's <c>BridgeProjectSettings</c>).</item>
    ///   <item><see cref="Release"/> — deletes the lock on graceful shutdown. Best-effort — a crashed
    ///   editor leaves a stale lock that the next <see cref="Acquire"/> cleans up.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Thread-safety: <see cref="Acquire"/>/<see cref="UpdateState"/>/<see cref="Release"/> may be
    /// called from the main thread (heartbeat tick) or the listener worker (Start/Stop). File I/O is
    /// atomic via rename; concurrent <see cref="UpdateState"/> calls are last-writer-wins and the
    /// heartbeat is the only steady-state writer, so this is safe in practice. The <c>.tmp</c> file
    /// path is per-PID to avoid collisions.
    /// </para>
    /// </summary>
    public static class BridgeInstanceLock
    {
        const string TempSuffix = ".tmp";

        // State values written into the lock. Mirror the TS-side parser in
        // mcp-server/src/instance-discovery.ts (InstanceState type).
        public const string StateIdle = "idle";
        public const string StateCompiling = "compiling";
        public const string StateReloading = "reloading";
        public const string StateEnteringPlaymode = "entering_playmode";
        public const string StatePlaying = "playing";
        public const string StateExitingPlaymode = "exiting_playmode";

        // Last-written snapshot, kept in memory so UpdateState can rewrite only the fields that
        // changed without re-reading the file. Volatile read/written from main + worker threads.
        static volatile bool _acquired;
        static string? _acquiredProjectPath;
        static int _acquiredPort;
        static string? _acquiredProjectHash;
        static int _pid;
        static DateTime _startedAt;

        // P5.2 — per-session bearer token. Always minted on Acquire so the MCP server can send it
        // regardless of the project's authMode; enforcement is decided by BridgeAuthCheck at request
        // time. Read by the HTTP auth check (BridgeHttpServer.CheckAuth) and mirrored into the lock
        // JSON below. Minted once per Acquire (a bridge restart invalidates any previously discovered
        // token); preserved across heartbeat UpdateState rewrites so the token does not rotate under
        // a running session.
        static string? _authToken;

        // Last state written, cached so the heartbeat tick can refresh `heartbeatAt` without
        // recomputing editor state (which would need main-thread-only Godot APIs). A real state
        // transition still goes through UpdateState, which updates these.
        static volatile string _lastState = StateIdle;
        static volatile bool _lastPlaying;
        static volatile bool _lastCompiling;

        /// <summary>
        /// Heartbeat tick interval. The MCP server treats a heartbeat older than 10s as stale and
        /// classifies the instance as <c>dead_bridge</c> (see <c>instance-discovery.ts</c>
        /// <c>HEARTBEAT_STALE_MS</c>), so this must stay comfortably under that bound. 3s gives a 3x
        /// margin for a slow disk or a briefly blocked editor without a false "dead" verdict.
        /// </summary>
        internal const int HeartbeatIntervalMs = 3000;

        // Steady-state heartbeat writer. A plain threading Timer rather than a Godot _Process hook:
        // the lock writer touches no Godot APIs, so it must not be coupled to the editor node
        // lifecycle (and it must keep ticking while the main thread is busy compiling — that is
        // precisely the window the dead-bridge classifier reasons about). Never a foreground thread,
        // so it cannot keep the editor or the test host alive.
        static Timer? _heartbeatTimer;

        /// <summary>True once <see cref="Acquire"/> has successfully written this instance's lock.</summary>
        public static bool IsAcquired => _acquired;

        /// <summary>The project path the lock was acquired for, or null when not acquired.</summary>
        public static string? CurrentProjectPath => _acquiredProjectPath;

        /// <summary>The port the lock was acquired for, or 0 when not acquired.</summary>
        public static int CurrentPort => _acquiredPort;

        /// <summary>
        /// P5.2 — the per-session bearer token minted on <see cref="Acquire"/>. Null before Acquire
        /// and after <see cref="Release"/>. Read by <see cref="BridgeHttpServer.CheckAuth"/> for the
        /// constant-time compare against the request's Bearer header.
        /// </summary>
        public static string? AuthToken => _authToken;

        /// <summary>
        /// Write the initial lock and sweep stale locks. Safe to call on the listener worker thread
        /// (<see cref="BridgeHttpServer.Start"/>) — no Godot APIs.
        /// </summary>
        public static void Acquire(string projectPath, int port)
        {
            if (string.IsNullOrEmpty(projectPath))
            {
                BridgeLog.Warning(
                    "[BridgeInstanceLock] No project path available; skipping lock acquire.");
                return;
            }

            try
            {
                EnsureInstancesDir();
                SweepStaleLocks();
            }
            catch (Exception e)
            {
                // Sweep failure must not block the bridge start.
                BridgeLog.Warning(
                    $"[BridgeInstanceLock] Stale-lock sweep failed: {e.Message}");
            }

            _acquiredProjectPath = projectPath;
            _acquiredPort = port;
            _acquiredProjectHash = InstancePortResolver.ProjectHash(projectPath);
            _pid = Process.GetCurrentProcess().Id;
            _startedAt = DateTime.UtcNow;
            // P5.2 — mint a fresh token on every Acquire so a bridge restart invalidates any
            // previously discovered token. UpdateState preserves the minted token across heartbeat
            // rewrites (it reuses this static, not regenerated).
            _authToken = BridgeAuthToken.Generate();

            _lastState = StateIdle;
            _lastPlaying = false;
            _lastCompiling = false;

            try
            {
                WriteLock(StateIdle, isPlaying: false, isCompiling: false, DateTime.UtcNow);
                _acquired = true;
            }
            catch (Exception e)
            {
                BridgeLog.Warning(
                    $"[BridgeInstanceLock] Failed to write lock file: {e.Message}");
                _acquired = false;
            }

            // Arm the heartbeat only once the lock actually landed. Without a steady heartbeat the
            // MCP server would classify this live bridge as `dead_bridge` ~10s after Acquire.
            if (_acquired)
                StartHeartbeat();
        }

        /// <summary>
        /// (Re)arm the heartbeat timer. Idempotent: a redundant call disposes the previous timer
        /// first, so a re-Acquire (plugin disable → enable, domain reload) never leaves two writers.
        /// </summary>
        static void StartHeartbeat()
        {
            StopHeartbeat();
            try
            {
                _heartbeatTimer = new Timer(
                    _ => Heartbeat(),
                    null,
                    HeartbeatIntervalMs,
                    HeartbeatIntervalMs);
            }
            catch (Exception e)
            {
                // A timer we cannot arm is not fatal — the lock is still written, it just goes stale.
                BridgeLog.Warning($"[BridgeInstanceLock] Failed to arm heartbeat: {e.Message}");
            }
        }

        /// <summary>
        /// Stop the heartbeat writer but keep the lock file on disk. Called when the listener stops
        /// (plugin disable, editor quit, assembly reload). This is what produces the "stale heartbeat
        /// + live PID" signature the MCP server reads as <c>dead_bridge</c>/<c>reloading</c>: the file
        /// stays so the instance remains discoverable, but its <c>heartbeatAt</c> stops advancing.
        /// Use <see cref="Release"/> only when the lock should actually be deleted.
        /// </summary>
        public static void SuspendHeartbeat() => StopHeartbeat();

        static void StopHeartbeat()
        {
            var t = _heartbeatTimer;
            _heartbeatTimer = null;
            try { t?.Dispose(); } catch { /* best-effort */ }
        }

        /// <summary>
        /// One heartbeat tick: rewrite the lock with the cached state so <c>heartbeatAt</c> advances.
        /// Best-effort — a failed tick is logged at most once per transition to avoid log spam on a
        /// read-only or full disk.
        /// </summary>
        static void Heartbeat()
        {
            if (!_acquired) return;
            try
            {
                WriteLock(_lastState, _lastPlaying, _lastCompiling, DateTime.UtcNow);
            }
            catch
            {
                // Swallowed deliberately: this runs on a timer thread every few seconds, and the
                // failure mode (stale heartbeat) is already an observable signal on the MCP side.
            }
        }

        /// <summary>
        /// Rewrite the lock with fresh state. Called by the heartbeat tick and on forced transitions.
        /// No-op if <see cref="Acquire"/> has not run or failed.
        /// </summary>
        public static void UpdateState(string? state, bool isPlaying, bool isCompiling)
        {
            if (!_acquired) return;
            // Cache so the heartbeat tick keeps re-asserting the current state rather than reverting
            // the lock to "idle" on the next tick.
            _lastState = state ?? StateIdle;
            _lastPlaying = isPlaying;
            _lastCompiling = isCompiling;
            try
            {
                WriteLock(state ?? StateIdle, isPlaying, isCompiling, DateTime.UtcNow);
            }
            catch (Exception e)
            {
                // Heartbeat write is best-effort; don't tear down the editor.
                BridgeLog.Warning(
                    $"[BridgeInstanceLock] Heartbeat write failed: {e.Message}");
            }
        }

        /// <summary>Delete the lock on graceful shutdown. Best-effort. No-op when not acquired.</summary>
        public static void Release()
        {
            if (!_acquired) return;
            // Stop the writer before deleting, or an in-flight tick could recreate the file.
            StopHeartbeat();
            var path = InstancePortResolver.LockPath(_acquiredProjectPath!);
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                BridgeLog.Warning(
                    $"[BridgeInstanceLock] Failed to delete lock file: {e.Message}");
            }
            finally
            {
                _acquired = false;
                _authToken = null;
            }
        }

        /// <summary>
        /// Snapshot of the current lock file content as a JSON string. Used by the <c>/instance</c>
        /// HTTP endpoint (a later phase) so the MCP server can verify the live bridge against the
        /// on-disk lock without trusting the file alone. Returns null when no lock is held or the
        /// file can't be read.
        /// </summary>
        public static string? ReadCurrentJson()
        {
            if (!_acquired) return null;
            var path = InstancePortResolver.LockPath(_acquiredProjectPath!);
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Lightweight read-only view of the fields the UI / diagnostics care about. Extracted from
        /// the lock JSON by <see cref="TryParseSnapshot"/> without a JSON dependency (the bridge
        /// carries no System.Text.Json / Newtonsoft).
        /// </summary>
        public readonly struct LockSnapshot
        {
            /// <summary>True when the source payload carried a parseable <c>pid</c>.</summary>
            public readonly bool Valid;

            /// <summary>The PID held by the lock (0 when absent).</summary>
            public readonly int Pid;

            /// <summary>The port held by the lock (0 when absent).</summary>
            public readonly int Port;

            /// <summary>The editor state string (idle/compiling/...).</summary>
            public readonly string? State;

            /// <summary>The <c>updatedAt</c> timestamp string from the lock.</summary>
            public readonly string? UpdatedAt;

            /// <summary>The <c>heartbeatAt</c> timestamp string from the lock.</summary>
            public readonly string? HeartbeatAt;

            public LockSnapshot(bool valid, int pid, int port, string? state, string? updatedAt, string? heartbeatAt)
            {
                Valid = valid;
                Pid = pid;
                Port = port;
                State = state;
                UpdatedAt = updatedAt;
                HeartbeatAt = heartbeatAt;
            }
        }

        /// <summary>
        /// Parse the diagnostic fields out of a lock JSON string. Pure (no file I/O, no Godot APIs)
        /// so it is unit-testable. Returns <c>Valid=false</c> on any malformed input rather than
        /// throwing.
        /// </summary>
        public static LockSnapshot TryParseSnapshot(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return new LockSnapshot(false, 0, 0, null, null, null);

            var pid = ExtractInt(json, "pid");
            var port = ExtractInt(json, "port");
            var state = ExtractString(json, "state");
            var updatedAt = ExtractString(json, "updatedAt");
            var heartbeatAt = ExtractString(json, "heartbeatAt");
            // pid is the minimum signal that this is a real lock payload.
            var valid = pid > 0;
            // A missing port should read as 0 (the natural "absent" sentinel for a port number), not
            // -1 which ExtractInt returns for absent keys.
            if (port < 0) port = 0;
            return new LockSnapshot(valid, pid, port, state, updatedAt, heartbeatAt);
        }

        // ----- internals -----

        static void WriteLock(string state, bool isPlaying, bool isCompiling, DateTime now)
        {
            var path = InstancePortResolver.LockPath(_acquiredProjectPath!);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = BuildJson(state, isPlaying, isCompiling, now);
            var tmp = path + TempSuffix + "." + _pid;
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }

        static string BuildJson(string state, bool isPlaying, bool isCompiling, DateTime now)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"pid\":").Append(_pid).Append(',');
            sb.Append("\"port\":").Append(_acquiredPort).Append(',');
            // P5.2 — authToken mirrors the TS-side InstanceLock.authToken field. Always minted on
            // Acquire (even when authMode is "none") so the project can flip to "required" with no
            // restart and the MCP server can always attach the header when present. Inserted between
            // port and projectPath per the lock JSON contract.
            sb.Append("\"authToken\":").Append(BridgeJson.EscapeString(_authToken)).Append(',');
            sb.Append("\"projectPath\":").Append(BridgeJson.EscapeString(_acquiredProjectPath)).Append(',');
            sb.Append("\"projectHash\":").Append(BridgeJson.EscapeString(NullToEmpty(_acquiredProjectHash))).Append(',');
            sb.Append("\"startedAt\":").Append(BridgeJson.EscapeString(IsoUtc(_startedAt))).Append(',');
            sb.Append("\"updatedAt\":").Append(BridgeJson.EscapeString(IsoUtc(now))).Append(',');
            sb.Append("\"heartbeatAt\":").Append(BridgeJson.EscapeString(IsoUtc(now))).Append(',');
            sb.Append("\"state\":").Append(BridgeJson.EscapeString(state ?? StateIdle)).Append(',');
            sb.Append("\"isPlaying\":").Append(isPlaying ? "true" : "false").Append(',');
            sb.Append("\"isCompiling\":").Append(isCompiling ? "true" : "false").Append(',');
            sb.Append("\"bridgeVersion\":").Append(BridgeJson.EscapeString(BridgeSession.BridgeVersion)).Append(',');
            sb.Append("\"godotVersion\":").Append(BridgeJson.EscapeString(NullToEmpty(BridgeSession.GodotVersion)));
            sb.Append('}');
            return sb.ToString();
        }

        // BridgeSession.GodotVersion etc. are strings but may be null before init; coerce to empty
        // string so EscapeString produces "" not "null".
        static string NullToEmpty(string? s) => s ?? "";

        static string IsoUtc(DateTime dt)
        {
            // Round-trip ISO-8601 in UTC. Universal sortable pattern + Z is the simplest form every
            // JSON parser accepts.
            return dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        static void EnsureInstancesDir()
        {
            var dir = InstancePortResolver.InstancesDir;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        // Scan ~/.godot-open-mcp/instances/*.json and delete any whose pid is no longer alive.
        // Treats parse errors and access errors as "leave it alone" — a malformed lock for someone
        // else's instance is not ours to touch. We DO touch our own project's lock below in
        // WriteLock (overwrite), so a stale own-project lock gets replaced regardless.
        static void SweepStaleLocks()
        {
            var dir = InstancePortResolver.InstancesDir;
            if (!Directory.Exists(dir)) return;

            string[] files;
            try { files = Directory.GetFiles(dir, "*.json"); }
            catch { return; }

            foreach (var file in files)
            {
                int pid;
                try
                {
                    var json = File.ReadAllText(file);
                    pid = ExtractPid(json);
                }
                catch
                {
                    continue;
                }

                if (pid <= 0) continue;

                if (IsPidAlive(pid)) continue;

                try { File.Delete(file); }
                catch
                {
                    // best-effort
                }
            }
        }

        // Minimal pid extractor: pull the integer value of "pid":N out of the lock JSON without a
        // JSON parser.
        static int ExtractPid(string json) => ExtractInt(json, "pid");

        // Generic integer-field extractor: finds "key":N and returns N, or -1 when the key is absent
        // / unparseable. Used by TryParseSnapshot.
        static int ExtractInt(string json, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = json.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return -1;
            var colon = json.IndexOf(':', idx + quotedKey.Length);
            if (colon < 0) return -1;
            var start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            var end = start;
            while (end < json.Length && json[end] >= '0' && json[end] <= '9') end++;
            if (end == start) return -1;
            return int.TryParse(json.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;
        }

        // Generic string-field extractor: finds "key":"value" and returns the unescaped inner value,
        // or null when absent. Handles the standard \" \\ \/ \b \f \n \r \t \uXXXX escapes so quoted
        // lock fields round-trip correctly. Used by TryParseSnapshot.
        static string? ExtractString(string json, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = json.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colon = json.IndexOf(':', idx + quotedKey.Length);
            if (colon < 0) return null;
            var start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            if (start >= json.Length || json[start] != '"') return null;
            start++;
            var sb = new StringBuilder();
            var i = start;
            while (i < json.Length)
            {
                var c = json[i];
                if (c == '"') return sb.ToString();
                if (c == '\\' && i + 1 < json.Length)
                {
                    var next = json[i + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); i += 2; continue;
                        case '\\': sb.Append('\\'); i += 2; continue;
                        case '/': sb.Append('/'); i += 2; continue;
                        case 'b': sb.Append('\b'); i += 2; continue;
                        case 'f': sb.Append('\f'); i += 2; continue;
                        case 'n': sb.Append('\n'); i += 2; continue;
                        case 'r': sb.Append('\r'); i += 2; continue;
                        case 't': sb.Append('\t'); i += 2; continue;
                        case 'u':
                            if (i + 5 < json.Length &&
                                int.TryParse(json.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            {
                                sb.Append((char)code);
                                i += 6;
                                continue;
                            }
                            break;
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString(); // unterminated string — return what we have
        }

        // kill -0 equivalent. Process.GetProcessById throws on a dead pid on all platforms; the
        // returned Process is then immediately discarded. The exception path is the common case for
        // stale locks.
        static bool IsPidAlive(int pid)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                try { p.Dispose(); } catch { }
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            // An access-denied / permission error means the pid exists but we can't introspect it —
            // treat as alive so we never delete a lock for a running instance we just can't see.
            catch (System.ComponentModel.Win32Exception) { return true; }
        }
    }
}
#endif
