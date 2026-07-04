#if TOOLS
#nullable enable
namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Process-wide bridge session state. P1.1 ships only the version constant;
    /// later phases extend this with the project path (P1.3), connected /
    /// compiling flags (P1.3), and instance-lock bookkeeping (P1.4).
    ///
    /// Mirrors the role of Unity Open MCP's <c>BridgeSession</c> (a static surface
    /// the HTTP handlers and instance lock read from), adapted to Godot where the
    /// editor plugin — not <c>[InitializeOnLoad]</c> — is the boot entry point.
    /// </summary>
    public static class BridgeSession
    {
        /// <summary>
        /// Bridge version reported over HTTP by <c>/ping</c> (P1.3) and mirrored in
        /// the instance lock (P1.4). Synced from <c>version.json</c> by
        /// <c>scripts/sync-version.mjs</c>; never hand-edit.
        /// </summary>
        public static string BridgeVersion => "0.0.1";
    }
}
#endif
