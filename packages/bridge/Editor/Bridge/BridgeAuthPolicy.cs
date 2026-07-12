#if TOOLS
#nullable enable

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Auth mode constants + validation. P5.2 — copy of Unity Open MCP's
    /// <c>BridgeAuthPolicy</c>, adapted to route the effective mode through
    /// <see cref="BridgeProjectSettings"/> (the Godot settings reader for
    /// <c>.godot-open-mcp/settings.json</c>) instead of Unity's
    /// <c>BridgeProjectSettings.AuthMode</c> / dock UI <c>Changed</c> event (the Hub dock UI is
    /// Phase 11, so no event is wired here).
    ///
    /// <para>
    /// Modes:
    /// <list type="bullet">
    ///   <item><c>none</c> (default) — ignores the bearer token the client sends; preserves the
    ///   localhost-trust behavior the bridge shipped with before P5.2.</item>
    ///   <item><c>required</c> — enforces the bearer token on every HTTP route.</item>
    /// </list>
    /// The token is always minted into the instance lock so the project can flip to
    /// <c>required</c> with no restart (packages/bridge/AGENTS.md §Auth).
    /// </para>
    /// </summary>
    public static class BridgeAuthPolicy
    {
        /// <summary>No auth — allow all requests (localhost-trust default).</summary>
        public const string None = "none";

        /// <summary>Require a valid Bearer token on every request.</summary>
        public const string Required = "required";

        /// <summary>The default mode when no setting is present.</summary>
        public const string Default = None;

        /// <summary>The full set of valid mode strings.</summary>
        public static readonly string[] ValidModes = { None, Required };

        /// <summary>
        /// The effective auth mode from project settings. Canonicalized through
        /// <see cref="BridgeProjectSettings.AuthMode"/> so callers never see an out-of-set value
        /// (missing/invalid values in the settings file are coerced to the default at read time).
        /// </summary>
        public static string GetDefault() => BridgeProjectSettings.AuthMode;

        /// <summary>
        /// True only for the two canonical mode strings. Used by the settings reader to decide
        /// whether to coerce a file value to the default, and by tests to pin the valid set.
        /// </summary>
        public static bool IsValid(string? mode) => mode == None || mode == Required;
    }
}
#endif
