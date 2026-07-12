#nullable enable
using System;
using System.IO;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P5.2 — <see cref="BridgeProjectSettings"/> reader tests. Pins the settings-file contract
    /// (<c>.godot-open-mcp/settings.json</c>) for the two fields the bridge ships in this phase:
    /// <c>authMode</c> and <c>bindAddress</c>.
    ///
    /// <para>
    /// Key Godot delta from Unity (the spec calls this out explicitly): an <b>invalid</b>
    /// <c>authMode</c> in the file is NOT coerced to <c>none</c> at read time — the raw value is
    /// preserved so <see cref="BridgeAuthCheck.IsAuthorized"/> fails closed. Only an
    /// <b>absent</b> key / missing file yields the <c>none</c> default. <c>bindAddress</c> DOES
    /// coerce (an invalid bind would throw at listen time; coercing to loopback is the safe start).
    /// </para>
    ///
    /// <para>
    /// The static cache + <c>BridgeSession.ProjectPath</c> dependency mean these tests must not run
    /// concurrently with each other or with anything that touches the same statics. The collection
    /// serializes them; the fixture resets state around each test and swaps <see cref="BridgeLog"/>
    /// to no-op sinks so the warning paths never P/Invoke into native Godot.
    /// </para>
    /// </summary>
    [Collection(nameof(BridgeProjectSettingsTests))]
    [CollectionDefinition(nameof(BridgeProjectSettingsTests), DisableParallelization = true)]
    public class BridgeProjectSettingsTests : IDisposable
    {
        public BridgeProjectSettingsTests()
        {
            BridgeLog.SetLoggersForTests(info: _ => { }, warning: _ => { }, error: _ => { });
            BridgeProjectSettings.ResetForTests();
        }

        public void Dispose()
        {
            BridgeProjectSettings.ResetForTests();
            BridgeLog.ResetForTests();
        }

        // ----- authMode -----

        [Fact]
        public void AuthMode_DefaultIsNone_WhenFileMissing()
        {
            BridgeProjectSettings.LoadFromStringForTests("/proj", null);
            Assert.Equal("none", BridgeProjectSettings.AuthMode);
        }

        [Fact]
        public void AuthMode_DefaultIsNone_WhenKeyAbsent()
        {
            BridgeProjectSettings.LoadFromStringForTests("/proj", "{\"bindAddress\":\"127.0.0.1\"}");
            Assert.Equal("none", BridgeProjectSettings.AuthMode);
        }

        [Fact]
        public void AuthMode_ReadsRequired()
        {
            BridgeProjectSettings.LoadFromStringForTests("/proj", "{\"authMode\":\"required\"}");
            Assert.Equal("required", BridgeProjectSettings.AuthMode);
        }

        [Fact]
        public void AuthMode_ReadsNone()
        {
            BridgeProjectSettings.LoadFromStringForTests("/proj", "{\"authMode\":\"none\"}");
            Assert.Equal("none", BridgeProjectSettings.AuthMode);
        }

        [Fact]
        public void AuthMode_InvalidValue_IsPreserved_NotCoercedToNone()
        {
            // The Godot delta: invalid authMode is NOT coerced to "none". The raw value flows to
            // BridgeAuthCheck, which fails closed. This is the safer behavior — a corrupt settings
            // file denies rather than allows.
            BridgeProjectSettings.LoadFromStringForTests("/proj", "{\"authMode\":\"bogus\"}");
            Assert.Equal("bogus", BridgeProjectSettings.AuthMode);
        }

        [Fact]
        public void AuthMode_EmptyValue_IsPreserved_NotCoercedToNone()
        {
            // An empty string ("authMode":"") is an explicit (invalid) value, not the same as a
            // missing key. Like "bogus", it flows through to the check, which fails closed. This
            // is consistent with the fail-closed-on-invalid design: only an absent key / missing
            // file yields the "none" default. (The extractor returns null for a missing key and ""
            // for an empty value, so the getter can tell them apart.)
            BridgeProjectSettings.LoadFromStringForTests("/proj", "{\"authMode\":\"\"}");
            var mode = BridgeProjectSettings.AuthMode;
            Assert.Equal("", mode);
            // And the check denies — pin the end-to-end fail-closed outcome.
            Assert.False(BridgeAuthCheck.IsAuthorized(mode, null, "anytoken"));
        }

        // ----- bindAddress -----

        [Fact]
        public void BindAddress_DefaultIsLoopback_WhenFileMissing()
        {
            BridgeProjectSettings.LoadFromStringForTests("/proj", null);
            Assert.Equal(BridgeBindAddress.Loopback, BridgeProjectSettings.BindAddress);
        }

        [Fact]
        public void BindAddress_DefaultIsLoopback_WhenKeyAbsent()
        {
            BridgeProjectSettings.LoadFromStringForTests("/proj", "{\"authMode\":\"required\"}");
            Assert.Equal(BridgeBindAddress.Loopback, BridgeProjectSettings.BindAddress);
        }

        [Fact]
        public void BindAddress_ReadsRemote()
        {
            BridgeProjectSettings.LoadFromStringForTests("/proj",
                "{\"authMode\":\"required\",\"bindAddress\":\"0.0.0.0\"}");
            Assert.Equal(BridgeBindAddress.Remote, BridgeProjectSettings.BindAddress);
        }

        [Fact]
        public void BindAddress_InvalidValue_CoercesToLoopback()
        {
            // Unlike authMode, bindAddress coerces — binding a bogus address would throw at listen
            // time; coercing to loopback gives a deterministic, safe start.
            BridgeProjectSettings.LoadFromStringForTests("/proj",
                "{\"bindAddress\":\"example.com\"}");
            Assert.Equal(BridgeBindAddress.Loopback, BridgeProjectSettings.BindAddress);
        }

        // ----- on-disk Load/Save round-trip -----

        [Fact]
        public void Load_ReadsActualFile_FromDisk()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "godot-open-mcp-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(tempRoot, BridgeProjectSettings.SettingsDirName));
            var settingsPath = Path.Combine(tempRoot, BridgeProjectSettings.SettingsDirName,
                BridgeProjectSettings.SettingsFileName);
            File.WriteAllText(settingsPath, "{\"authMode\":\"required\",\"bindAddress\":\"0.0.0.0\"}");

            try
            {
                // Pin the project root so SettingsPath resolves into the temp dir, then Load() reads
                // the real file from disk. (LoadFromStringForTests(tempRoot, null) only seeds an
                // empty cache; Load() unconditionally re-reads.)
                BridgeProjectSettings.LoadFromStringForTests(tempRoot, null);
                BridgeProjectSettings.Load();
                Assert.Equal("required", BridgeProjectSettings.AuthMode);
                Assert.Equal(BridgeBindAddress.Remote, BridgeProjectSettings.BindAddress);
            }
            finally
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Save_WritesFile_AndRefreshesCache()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "godot-open-mcp-settings-" + Guid.NewGuid().ToString("N"));
            Assert.Null(Record.Exception(() => Directory.CreateDirectory(tempRoot)));

            try
            {
                BridgeProjectSettings.LoadFromStringForTests(tempRoot, null);
                BridgeProjectSettings.Save("required", "0.0.0.0");

                var settingsPath = Path.Combine(tempRoot, BridgeProjectSettings.SettingsDirName,
                    BridgeProjectSettings.SettingsFileName);
                Assert.True(File.Exists(settingsPath));

                // The cache reflects what was just written.
                Assert.Equal("required", BridgeProjectSettings.AuthMode);
                Assert.Equal(BridgeBindAddress.Remote, BridgeProjectSettings.BindAddress);

                // And the file on disk round-trips.
                var raw = File.ReadAllText(settingsPath);
                Assert.Contains("\"authMode\":\"required\"", raw);
                Assert.Contains("\"bindAddress\":\"0.0.0.0\"", raw);
            }
            finally
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

    }
}
