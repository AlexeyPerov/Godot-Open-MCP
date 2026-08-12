#nullable enable
using System;
using System.Globalization;

namespace GodotOpenMcp.Bridge.Runtime.Logging
{
    /// <summary>
    /// Parsed Godot engine version (P18.3) — the single source of truth for version-gated feature
    /// decisions. The full version string (<c>"4.5.1.stable.mono"</c>) is cached by the editor-side
    /// session from <c>Engine.GetVersionInfo()["string"]</c>; this struct parses the leading
    /// <c>major.minor.patch</c> triple off it without touching any Godot API, so it is unit-testable
    /// in the binary-less xUnit host.
    /// </summary>
    /// <remarks>
    /// <b>Why a parser and not <c>Engine.GetVersionInfo()["major"]</c>?</b> The numeric fields ARE
    /// available on the Dictionary, but reading them requires the Godot API (editor-only, not
    /// reachable from the test host). The string form is already cached by
    /// <c>BridgeSession</c> for <c>/ping</c>, so reusing it keeps a single version source and keeps
    /// the gating logic pure-managed and deterministic under test.
    /// </remarks>
    public readonly struct GodotVersionInfo
    {
        /// <summary>Major version (e.g. 4). <c>-1</c> when the string could not be parsed.</summary>
        public readonly int Major;

        /// <summary>Minor version (e.g. 5). <c>-1</c> when the string could not be parsed.</summary>
        public readonly int Minor;

        /// <summary>Patch version (e.g. 1). <c>0</c> when omitted (e.g. <c>"4.5.stable"</c>).</summary>
        public readonly int Patch;

        /// <summary>The raw string that was parsed (null when <see cref="Parse"/> received null).</summary>
        public readonly string? Raw;

        /// <summary>True when the triple was successfully parsed (Major/Minor &gt;= 0).</summary>
        public bool IsValid => Major >= 0 && Minor >= 0;

        GodotVersionInfo(int major, int minor, int patch, string? raw)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Raw = raw;
        }

        /// <summary>The minimum Godot version that exposes the managed <c>Logger</c> class +
        /// <c>OS.add_logger</c> registration API (PR #91006, shipped with Godot 4.5). Below this, the
        /// global log hook is unavailable and the bridge stays in addon-only capture.</summary>
        public const int GlobalLogHookMajor = 4;
        public const int GlobalLogHookMinor = 5;

        /// <summary>True when this version exposes the managed <c>Logger</c>/<c>OS.add_logger</c> API
        /// (Godot 4.5+) — i.e. the global editor-Output hook can be armed. Invalid versions return
        /// false (the hook is never armed when the version is unknown).</summary>
        public bool SupportsGlobalLogHook =>
            IsValid && (Major > GlobalLogHookMajor
                || (Major == GlobalLogHookMajor && Minor >= GlobalLogHookMinor));

        /// <summary>
        /// Parse the leading <c>major.minor.patch</c> triple off a Godot version string. Lenient:
        /// anything after the first three dotted integers is ignored (the <c>.stable.mono</c>
        /// qualifier, build metadata, etc.). Missing patch defaults to 0 (<c>"4.5"</c> → 4.5.0). A
        /// null/empty/unparseable string yields an invalid struct (<see cref="IsValid"/> false).
        /// Never throws.
        /// </summary>
        /// <param name="versionString">The <c>"string"</c> field of
        /// <c>Engine.GetVersionInfo()</c> (e.g. <c>"4.3.1.stable.mono"</c>).</param>
        public static GodotVersionInfo Parse(string? versionString)
        {
            if (string.IsNullOrWhiteSpace(versionString))
                return Invalid(versionString);

            var s = versionString.AsSpan().Trim();
            // Read up to three dotted integer tokens; stop at the first non-digit/non-dot boundary
            // (the qualifier ".stable.mono" starts with a letter after the patch number).
            int[] parts = { -1, -1, 0 };
            int count = 0;
            int i = 0;
            while (i < s.Length && count < 3)
            {
                // Skip a single '.' separator between tokens.
                if (s[i] == '.')
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                if (i == start)
                {
                    // A non-digit where a number was expected (e.g. "stable") — stop collecting;
                    // whatever triple we have so far stands.
                    break;
                }
                if (int.TryParse(s.Slice(start, i - start), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var v))
                {
                    parts[count] = v;
                    count++;
                }
                else
                {
                    break;
                }
            }

            if (count < 2)
            {
                // Need at least major.minor to be meaningful.
                return Invalid(versionString);
            }
            return new GodotVersionInfo(parts[0], parts[1], parts[2], versionString);
        }

        static GodotVersionInfo Invalid(string? raw) => new GodotVersionInfo(-1, -1, 0, raw);
    }
}
