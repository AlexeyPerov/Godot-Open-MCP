#if TOOLS
#nullable enable
using System;
using System.IO;
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Reader for the project-local bridge settings file. P5.2 — adapt of Unity Open MCP's
    /// <c>BridgeProjectSettings</c>, narrowed to the auth-relevant fields the bridge ships in this
    /// phase: <c>authMode</c> and <c>bindAddress</c>. The rest of Unity's settings surface
    /// (<c>disabledTools</c>, deny lists, audit log, fair-queue tunables, ...) lands in later
    /// phases; P5.2 owns only what auth needs.
    ///
    /// <para>
    /// Schema (v1) at <c>&lt;project&gt;/.godot-open-mcp/settings.json</c>:
    /// <code>
    /// {
    ///   "authMode": "none" | "required",
    ///   "bindAddress": "127.0.0.1" | "0.0.0.0"
    /// }
    /// </code>
    /// The file is optional — when absent, the defaults are <c>authMode:"none"</c> and
    /// <c>bindAddress:"127.0.0.1"</c> (loopback). The directory is created on first write.
    /// </para>
    ///
    /// <para>
    /// <b>Intentional delta from Unity (fail-closed on invalid authMode).</b> Unity's reader coerces
    /// a missing/invalid <c>authMode</c> to the default (<c>none</c>) at load time, so an invalid
    /// value never reaches the auth check. The Godot port keeps the <b>raw</b> file value and lets
    /// <see cref="BridgeAuthCheck.IsAuthorized"/> fail closed on unknown policy (see
    /// <c>specs/execution/P5/P5.2.md</c> §"Intentional deltas"). This means a corrupt settings file
    /// denies rather than allows — the safer behavior for shared machines / CI runners. The default
    /// (file missing entirely) is still <c>none</c>, so the upgrade path is additive.
    /// </para>
    ///
    /// <para>
    /// The bridge carries no JSON dependency (System.Text.Json / Newtonsoft), so the file is parsed
    /// with the same hand-rolled scalar/string extraction <see cref="BridgeInstanceLock"/> uses for
    /// the lock JSON. Only the two fields above are read; unknown keys are ignored. Atomic write
    /// (<c>.tmp</c> + rename) matches Unity's pattern and the instance lock's.
    /// </para>
    /// </summary>
    public static class BridgeProjectSettings
    {
        /// <summary>Directory name under the project root holding the settings file.</summary>
        public const string SettingsDirName = ".godot-open-mcp";

        /// <summary>File name inside the settings directory.</summary>
        public const string SettingsFileName = "settings.json";

        const string TempSuffix = ".tmp";

        static string? _cachedRaw;
        static string? _cachedAuthMode;
        static string? _cachedBindAddress;
        static bool _loaded;

        /// <summary>
        /// The absolute path to the settings file, or null when no project root is known. Resolved
        /// from <see cref="BridgeSession.ProjectPath"/> (cached on plugin enable).
        /// </summary>
        public static string? SettingsPath
        {
            get
            {
                var root = GetProjectRoot();
                if (string.IsNullOrEmpty(root)) return null;
                return Path.Combine(root, SettingsDirName, SettingsFileName);
            }
        }

        /// <summary>
        /// The effective <c>authMode</c>. Returns the <b>raw</b> string from the file
        /// (NOT coerced to <c>none</c> when invalid — see the class doc for the fail-closed delta).
        /// The default <c>"none"</c> is returned only when the file is missing entirely or the key
        /// is absent; an explicit invalid value in the file is preserved so the check denies.
        /// </summary>
        public static string AuthMode
        {
            get
            {
                if (!_loaded) Load();
                // If the file carried an explicit value (even invalid), pass it through so the check
                // fails closed. Only absent/missing → default "none".
                if (_cachedAuthMode != null) return _cachedAuthMode;
                return BridgeAuthPolicy.Default;
            }
        }

        /// <summary>
        /// The effective <c>bindAddress</c>, canonicalized through <see cref="BridgeBindAddress"/>.
        /// Invalid values coerce to the loopback default (binding a bogus address would throw at
        /// listen time; coercing here gives a deterministic, safe start).
        /// </summary>
        public static string BindAddress
        {
            get
            {
                if (!_loaded) Load();
                return BridgeBindAddress.IsValid(_cachedBindAddress)
                    ? _cachedBindAddress!
                    : BridgeBindAddress.Default;
            }
        }

        /// <summary>
        /// Load the settings file from disk into the cached fields. Safe to call repeatedly
        /// (idempotent re-read). On any read/parse failure, falls back to defaults and logs a
        /// warning via <see cref="BridgeLog"/> (never throws — the bridge must start even with a
        /// corrupt settings file).
        /// </summary>
        public static void Load()
        {
            _cachedRaw = null;
            _cachedAuthMode = null;
            _cachedBindAddress = null;

            var path = SettingsPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _loaded = true;
                return;
            }

            try
            {
                _cachedRaw = File.ReadAllText(path);
                // authMode: raw value preserved (even if invalid) for the fail-closed check.
                _cachedAuthMode = ExtractString(_cachedRaw, "authMode");
                // bindAddress: raw value; canonicalized in the getter (invalid → loopback).
                _cachedBindAddress = ExtractString(_cachedRaw, "bindAddress");
            }
            catch (Exception e)
            {
                BridgeLog.Warning(
                    $"[BridgeProjectSettings] Failed to read '{path}': {e.Message}. Using defaults.");
                _cachedRaw = null;
                _cachedAuthMode = null;
                _cachedBindAddress = null;
            }
            finally
            {
                _loaded = true;
            }
        }

        /// <summary>
        /// Persist the settings file. P5.2 surface: writes only <c>authMode</c> and
        /// <c>bindAddress</c>. Used by the setter paths (and, in later phases, the Hub UI). Creates
        /// the settings directory if missing. Atomic write (<c>.tmp</c> + rename).
        /// </summary>
        public static void Save(string? authMode, string? bindAddress)
        {
            var path = SettingsPath;
            if (string.IsNullOrEmpty(path))
            {
                BridgeLog.Warning(
                    "[BridgeProjectSettings] No project root available; cannot save settings.");
                return;
            }

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = BuildJson(authMode ?? AuthMode, bindAddress ?? BindAddress);
                var tmp = path + TempSuffix;
                File.WriteAllText(tmp, json);
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            catch (Exception e)
            {
                BridgeLog.Error($"[BridgeProjectSettings] Failed to write '{path}': {e.Message}");
                return;
            }

            // Refresh the in-memory cache so a subsequent read reflects what we just persisted.
            _cachedAuthMode = authMode;
            _cachedBindAddress = bindAddress;
            _loaded = true;
        }

        /// <summary>
        /// Test-only: load from a raw JSON string instead of from disk, and pin the project root so
        /// <see cref="SettingsPath"/> resolves deterministically. Mirrors the
        /// <see cref="InstancePortResolver.InstancesDirOverride"/> pattern. Cleared by
        /// <see cref="ResetForTests"/>.
        /// </summary>
        internal static void LoadFromStringForTests(string? projectRoot, string? json)
        {
            _projectRootForTests = projectRoot;
            _cachedRaw = json;
            _cachedAuthMode = json != null ? ExtractString(json, "authMode") : null;
            _cachedBindAddress = json != null ? ExtractString(json, "bindAddress") : null;
            _loaded = true;
        }

        /// <summary>Test-only: clear the cache + project-root override so the next read re-resolves.</summary>
        internal static void ResetForTests()
        {
            _projectRootForTests = null;
            _cachedRaw = null;
            _cachedAuthMode = null;
            _cachedBindAddress = null;
            _loaded = false;
        }

        // ----- internals -----

        static string? _projectRootForTests;

        static string? GetProjectRoot()
        {
            if (_projectRootForTests != null) return _projectRootForTests;
            return BridgeSession.ProjectPath;
        }

        static string BuildJson(string authMode, string bindAddress)
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"authMode\":").Append(BridgeJson.EscapeString(authMode)).Append(',');
            sb.Append("\"bindAddress\":").Append(BridgeJson.EscapeString(bindAddress));
            sb.Append('}');
            return sb.ToString();
        }

        // Same minimal string-field extractor BridgeInstanceLock uses: finds "key":"value" and
        // returns the unescaped inner value, or null when absent. Tolerates the standard JSON string
        // escapes so quoted settings values round-trip correctly.
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
                                int.TryParse(json.Substring(i + 2, 4), System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out var code))
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
            return sb.ToString();
        }
    }
}
#endif
