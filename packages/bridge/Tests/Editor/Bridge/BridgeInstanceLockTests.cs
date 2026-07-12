#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// Instance lock file + stale-lock cleanup tests for P1.4.
    ///
    /// <para>
    /// Lock I/O is sandboxed to a temp dir via <see cref="InstancePortResolver.InstancesDirOverride"/>
    /// so tests never touch the real <c>~/.godot-open-mcp/instances</c>. We can't easily fake a
    /// different PID (<see cref="BridgeInstanceLock.Acquire"/> writes the current process's PID), so
    /// the stale-lock cleanup test plants a fake lock JSON with a guaranteed-dead PID before calling
    /// <see cref="BridgeInstanceLock.Acquire"/>, then verifies it disappeared.
    /// </para>
    ///
    /// <para>
    /// <see cref="BridgeInstanceLock"/>'s state is static and outlives a single test, and the test
    /// mutates <see cref="InstancePortResolver.InstancesDirOverride"/> (also static). The
    /// <c>[Collection]</c> attribute serializes these tests against each other so a concurrent test
    /// never observes mid-mutation state. The fixture also swaps <see cref="BridgeLog"/> to no-op
    /// sinks so the warning paths (e.g. failed sweep, failed heartbeat write) never P/Invoke into the
    /// native Godot library that is not loaded in the binary-less xUnit host — the same pattern
    /// <see cref="BridgeHttpServerTests"/> uses for the listener lifecycle.
    /// </para>
    ///
    /// <para>
    /// Adapted from Unity's <c>BridgeInstanceLockTests</c>. P5.2 adds the auth-token cases
    /// (written into the lock JSON on Acquire, correct hex length, preserved across UpdateState
    /// heartbeats, minted fresh on each Acquire, cleared on Release).
    /// </para>
    /// </summary>
    [Collection(nameof(BridgeInstanceLockTests))]
    [CollectionDefinition(nameof(BridgeInstanceLockTests), DisableParallelization = true)]
    public class BridgeInstanceLockTests : IDisposable
    {
        const string TestProjectPath = "/test/MyGame";
        const string OtherProjectPath = "/test/OtherGame";

        string _tempDir = null!;

        public BridgeInstanceLockTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "godot-open-mcp-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            InstancePortResolver.InstancesDirOverride = _tempDir;

            // Swap to no-op sinks so the warning paths in Acquire/UpdateState/Release never reach
            // GD.PushWarning (which P/Invokes into the native Godot library not loaded here).
            BridgeLog.SetLoggersForTests(info: _ => { }, warning: _ => { }, error: _ => { });

            // Reset internal state so a prior test's Acquire doesn't leak into this one.
            ForceReleaseForTest();
        }

        public void Dispose()
        {
            ForceReleaseForTest();
            InstancePortResolver.InstancesDirOverride = null;
            BridgeLog.ResetForTests();
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        // Forcing release without touching the real file: just clear the _acquired flag so Acquire
        // can run again. The on-disk lock (if any) from a prior test was written to the previous temp
        // dir, which is already gone.
        static void ForceReleaseForTest()
        {
            try { BridgeInstanceLock.Release(); } catch { }
        }

        [Fact]
        public void Acquire_WritesLockFile_WithExpectedShape()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);

            var lockPath = InstancePortResolver.LockPath(TestProjectPath);
            Assert.True(File.Exists(lockPath));
            var json = File.ReadAllText(lockPath);

            // Required fields present. Field names mirror the TS-side InstanceLock type in
            // instance-discovery.ts (P1.6 will pin the cross-side parser).
            foreach (var field in new[]
            {
                "\"pid\"", "\"port\"", "\"projectPath\"", "\"projectHash\"",
                "\"startedAt\"", "\"updatedAt\"", "\"heartbeatAt\"",
                "\"state\"", "\"isPlaying\"", "\"isCompiling\"",
                "\"bridgeVersion\"", "\"godotVersion\""
            })
            {
                Assert.Contains(field, json);
            }

            Assert.Contains("\"port\":22028", json);
            Assert.Contains("\"projectPath\":\"/test/MyGame\"", json);
            Assert.Contains("\"state\":\"idle\"", json);
            Assert.Contains($"\"projectHash\":\"{InstancePortResolver.ProjectHash(TestProjectPath)}\"", json);
        }

        [Fact]
        public void Acquire_IsIdempotent_OverwritesSameProjectLock()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            var first = File.ReadAllText(InstancePortResolver.LockPath(TestProjectPath));

            // Re-acquire with a new port (simulates the bridge restarting on the same project). The
            // lock should be replaced atomically, not appended to or rejected.
            BridgeInstanceLock.Acquire(TestProjectPath, 22029);
            var second = File.ReadAllText(InstancePortResolver.LockPath(TestProjectPath));

            Assert.NotEqual(first, second);
            Assert.Contains("\"port\":22029", second);
        }

        // P5.2 — the lock now carries the per-session bearer token (authToken), minted on Acquire.
        // These pin the field presence, hex length, position in the JSON (between port and
        // projectPath — mirrors the TS-side InstanceLock.authToken field), preservation across
        // heartbeat rewrites, fresh-mint-on-reacquire, and clear-on-Release.
        [Fact]
        public void Acquire_WritesAuthToken_OfExpectedHexLength()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            var json = File.ReadAllText(InstancePortResolver.LockPath(TestProjectPath));

            // The authToken value is 64 lowercase hex chars (256-bit). Match the quoted value so we
            // don't accidentally match a different field.
            var idx = json.IndexOf("\"authToken\":\"", System.StringComparison.Ordinal);
            Assert.True(idx >= 0, "lock JSON must include an authToken field");
            var start = idx + "\"authToken\":\"".Length;
            var end = json.IndexOf('"', start);
            Assert.True(end > start, "authToken value must be a quoted string");
            var token = json.Substring(start, end - start);
            Assert.Equal(BridgeAuthToken.HexLength, token.Length);
            foreach (var c in token)
            {
                Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
                    $"authToken contained non-lowercase-hex char '{c}'");
            }
        }

        [Fact]
        public void Acquire_AuthToken_PlacedBetweenPortAndProjectPath()
        {
            // The field order mirrors the lock JSON contract: port → authToken → projectPath.
            // Pinning the order keeps the TS-side parser (which is field-name tolerant) stable and
            // matches the P5.2 spec's contract diagram.
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            var json = File.ReadAllText(InstancePortResolver.LockPath(TestProjectPath));

            var portIdx = json.IndexOf("\"port\":", System.StringComparison.Ordinal);
            var authIdx = json.IndexOf("\"authToken\":", System.StringComparison.Ordinal);
            var pathIdx = json.IndexOf("\"projectPath\":", System.StringComparison.Ordinal);

            Assert.True(portIdx >= 0 && authIdx > portIdx && pathIdx > authIdx,
                "authToken must appear between port and projectPath");
        }

        [Fact]
        public void AuthToken_PropertyReflects_MintedToken()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            Assert.NotNull(BridgeInstanceLock.AuthToken);
            Assert.Equal(BridgeAuthToken.HexLength, BridgeInstanceLock.AuthToken!.Length);
        }

        [Fact]
        public void Acquire_MintsFreshToken_OnEachAcquire()
        {
            // A bridge restart must invalidate any previously discovered token — a stale lock file
            // with an old token should not let a recycled-port attacker in. Each Acquire mints anew.
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            var first = BridgeInstanceLock.AuthToken;
            Assert.NotNull(first);

            ForceReleaseForTest();
            BridgeInstanceLock.Acquire(TestProjectPath, 22029);
            var second = BridgeInstanceLock.AuthToken;
            Assert.NotNull(second);

            Assert.NotEqual(first, second);
        }

        [Fact]
        public void UpdateState_PreservesAuthToken_AcrossHeartbeats()
        {
            // UpdateState must NOT rotate the token — the MCP server discovers it once and reuses
            // it for the session. A rotating token would break every in-flight request on each
            // heartbeat tick.
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            var minted = BridgeInstanceLock.AuthToken;
            Assert.NotNull(minted);

            BridgeInstanceLock.UpdateState(BridgeInstanceLock.StateCompiling, false, true);
            Assert.Equal(minted, BridgeInstanceLock.AuthToken);

            BridgeInstanceLock.UpdateState(BridgeInstanceLock.StatePlaying, true, false);
            Assert.Equal(minted, BridgeInstanceLock.AuthToken);

            // And the on-disk lock still carries the same token after the rewrites.
            var json = File.ReadAllText(InstancePortResolver.LockPath(TestProjectPath));
            Assert.Contains($"\"authToken\":\"{minted}\"", json);
        }

        [Fact]
        public void Release_ClearsAuthToken()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            Assert.NotNull(BridgeInstanceLock.AuthToken);

            BridgeInstanceLock.Release();
            Assert.Null(BridgeInstanceLock.AuthToken);
        }

        [Fact]
        public void UpdateState_RewritesLock_WithFreshHeartbeat()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            BridgeInstanceLock.UpdateState(BridgeInstanceLock.StateCompiling, isPlaying: false, isCompiling: true);

            var json = File.ReadAllText(InstancePortResolver.LockPath(TestProjectPath));
            Assert.Contains("\"state\":\"compiling\"", json);
            Assert.Contains("\"isCompiling\":true", json);
        }

        [Fact]
        public void UpdateState_NoOp_BeforeAcquire()
        {
            // No Acquire yet → UpdateState must not write anything.
            Assert.Null(Record.Exception(() =>
                BridgeInstanceLock.UpdateState(BridgeInstanceLock.StateIdle, false, false)));
            Assert.False(File.Exists(InstancePortResolver.LockPath(TestProjectPath)));
        }

        [Fact]
        public void Release_DeletesLock()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            var path = InstancePortResolver.LockPath(TestProjectPath);
            Assert.True(File.Exists(path));

            BridgeInstanceLock.Release();
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void Release_Idempotent()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            Assert.Null(Record.Exception(() => BridgeInstanceLock.Release()));
            // Second release is a no-op (already not acquired).
            Assert.Null(Record.Exception(() => BridgeInstanceLock.Release()));
        }

        [Fact]
        public void Acquire_WithEmptyProjectPath_IsNoOp()
        {
            BridgeInstanceLock.Acquire("", 22028);
            Assert.False(BridgeInstanceLock.IsAcquired);
            // Nothing was written for the empty project path.
            Assert.False(File.Exists(InstancePortResolver.LockPath("/nonexistent")));
        }

        [Fact]
        public void ReadCurrentJson_ReturnsLockContent_WhenAcquired()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            var json = BridgeInstanceLock.ReadCurrentJson();
            Assert.NotNull(json);
            Assert.Contains("\"port\":22028", json);
        }

        [Fact]
        public void ReadCurrentJson_ReturnsNull_WhenNotAcquired()
        {
            Assert.Null(BridgeInstanceLock.ReadCurrentJson());
        }

        // ----- stale-lock cleanup -----

        [Fact]
        public void Acquire_DeletesStaleLockForDeadPid()
        {
            // Plant a lock for a DIFFERENT project with a guaranteed-dead PID.
            // (999_999_999 is effectively never a real OS pid.)
            var otherPath = InstancePortResolver.LockPath(OtherProjectPath);
            Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
            File.WriteAllText(otherPath,
                "{\"pid\":999999999,\"port\":25000,\"projectPath\":\"" +
                OtherProjectPath + "\",\"state\":\"idle\"}");

            Assert.True(File.Exists(otherPath));

            BridgeInstanceLock.Acquire(TestProjectPath, 22028);

            Assert.False(File.Exists(otherPath),
                "Stale lock for a dead PID must be cleaned up by Acquire");
        }

        [Fact]
        public void Acquire_LeavesLockForLivePid()
        {
            // Plant a lock for a different project, using OUR own pid (which is guaranteed alive —
            // it's the test runner). This simulates another live Godot instance holding its own lock.
            var otherPath = InstancePortResolver.LockPath(OtherProjectPath);
            Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
            var livePid = System.Diagnostics.Process.GetCurrentProcess().Id;
            File.WriteAllText(otherPath,
                "{\"pid\":" + livePid + ",\"port\":25000,\"projectPath\":\"" +
                OtherProjectPath + "\",\"state\":\"idle\"}");

            BridgeInstanceLock.Acquire(TestProjectPath, 22028);

            Assert.True(File.Exists(otherPath),
                "Lock for a live PID must NOT be cleaned up");
        }

        [Fact]
        public void Acquire_LeavesMalformedLocks()
        {
            // A malformed lock (no parseable pid) is left alone — we don't know whose it is, so
            // deleting it would risk clobbering another tool's instance file.
            var otherPath = InstancePortResolver.LockPath(OtherProjectPath);
            Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
            File.WriteAllText(otherPath, "{\"notPid\":\"oops\"}");

            BridgeInstanceLock.Acquire(TestProjectPath, 22028);

            Assert.True(File.Exists(otherPath),
                "Malformed locks (no pid) must be left in place");
        }

        [Fact]
        public void LockFile_WriteIsAtomic_TmpFileDoesNotRemain()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            BridgeInstanceLock.UpdateState(BridgeInstanceLock.StatePlaying, true, false);

            // The .tmp.<pid> scratch file must have been renamed into place; no leftover temp files
            // for this project's lock.
            var lockPath = InstancePortResolver.LockPath(TestProjectPath);
            var dir = Path.GetDirectoryName(lockPath)!;
            var leftovers = Directory.GetFiles(dir, Path.GetFileName(lockPath) + "*.tmp*");
            Assert.Empty(leftovers);
        }

        // ----- TryParseSnapshot (pure JSON field extraction for diagnostics) -----

        [Fact]
        public void TryParseSnapshot_NullOrEmpty_NotValid()
        {
            Assert.False(BridgeInstanceLock.TryParseSnapshot(null).Valid);
            Assert.False(BridgeInstanceLock.TryParseSnapshot("").Valid);
        }

        [Fact]
        public void TryParseSnapshot_NoPid_NotValid()
        {
            // Without a pid the payload can't be trusted as a real lock.
            var snap = BridgeInstanceLock.TryParseSnapshot("{\"port\":22028,\"state\":\"idle\"}");
            Assert.False(snap.Valid);
        }

        [Fact]
        public void TryParseSnapshot_FullPayload_ExtractsAllFields()
        {
            var json =
                "{\"pid\":12345,\"port\":22028,\"projectPath\":\"/p\"," +
                "\"state\":\"compiling\",\"updatedAt\":\"2026-06-26T10:00:00Z\"," +
                "\"heartbeatAt\":\"2026-06-26T10:00:01Z\"}";
            var snap = BridgeInstanceLock.TryParseSnapshot(json);

            Assert.True(snap.Valid);
            Assert.Equal(12345, snap.Pid);
            Assert.Equal(22028, snap.Port);
            Assert.Equal("compiling", snap.State);
            Assert.Equal("2026-06-26T10:00:00Z", snap.UpdatedAt);
            Assert.Equal("2026-06-26T10:00:01Z", snap.HeartbeatAt);
        }

        [Fact]
        public void TryParseSnapshot_HandlesEscapedStringValues()
        {
            // state value with an escaped quote — the extractor must unescape.
            var json = "{\"pid\":1,\"state\":\"a\\\"b\"}";
            var snap = BridgeInstanceLock.TryParseSnapshot(json);
            Assert.True(snap.Valid);
            Assert.Equal("a\"b", snap.State);
        }

        [Fact]
        public void TryParseSnapshot_MissingOptionalFields_ReturnsNulls()
        {
            var snap = BridgeInstanceLock.TryParseSnapshot("{\"pid\":7}");
            Assert.True(snap.Valid);
            Assert.Equal(7, snap.Pid);
            Assert.Equal(0, snap.Port); // absent → default
            Assert.Null(snap.State);
            Assert.Null(snap.UpdatedAt);
            Assert.Null(snap.HeartbeatAt);
        }

        [Fact]
        public void TryParseSnapshot_RoundTripsRealAcquiredLock()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            var json = BridgeInstanceLock.ReadCurrentJson();
            Assert.NotNull(json);

            var snap = BridgeInstanceLock.TryParseSnapshot(json);
            Assert.True(snap.Valid);
            Assert.Equal(22028, snap.Port);
            Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id, snap.Pid);
            Assert.Equal(BridgeInstanceLock.StateIdle, snap.State);
        }
    }
}
