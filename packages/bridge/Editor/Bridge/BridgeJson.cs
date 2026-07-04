#if TOOLS
#nullable enable
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Hand-rolled JSON helpers for the bridge. The bridge deliberately carries no
    /// System.Text.Json / Newtonsoft dependency (packages/bridge/AGENTS.md §Transport): every
    /// response envelope is assembled with these escape + build primitives. Centralized here so
    /// the ping / gate / fault / timeout shapes stay in one place rather than scattered through
    /// <see cref="BridgeHttpServer"/>. Adapted from Unity Open MCP's <c>BridgeJson</c>; the gate /
    /// fault / timeout envelope builders will be added here in later phases alongside the gate flow.
    /// </summary>
    internal static class BridgeJson
    {
        // --- JSON string escaping -------------------------------------------------

        /// <summary>Wrap <paramref name="s"/> in quotes after escaping it; <c>null</c> → <c>"null"</c>.</summary>
        internal static string EscapeString(string? s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            EscapeStringContentTo(sb, s);
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Escape <paramref name="s"/> for placement inside a JSON string literal.</summary>
        internal static string EscapeStringContent(string? s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
            EscapeStringContentTo(sb, s);
            return sb.ToString();
        }

        /// <summary>Append the escaped content of <paramref name="s"/> (no surrounding quotes) to <paramref name="sb"/>.</summary>
        internal static void EscapeStringContentTo(StringBuilder sb, string s)
        {
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                            sb.Append("\\u").Append(((int)c).ToString("X4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
        }

        // --- /ping response body --------------------------------------------------

        /// <summary>
        /// Build the deterministic <c>GET /ping</c> response body — a live bridge status snapshot.
        /// Adapted from Unity's <c>BuildPingJson</c>: same field order and boolean shape so MCP-side
        /// readiness probes are portable, with <c>godotVersion</c> in place of <c>unityVersion</c>.
        /// All values flow through <see cref="BridgeSession"/>, which caches them on enable so a
        /// <c>/ping</c> arriving mid-editor-stall still returns a stable, deterministic payload.
        /// </summary>
        internal static string BuildPingJson()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"connected\":").Append(BridgeSession.Connected ? "true" : "false").Append(',');
            sb.Append("\"projectPath\":").Append(EscapeString(BridgeSession.ProjectPath)).Append(',');
            sb.Append("\"godotVersion\":").Append(EscapeString(BridgeSession.GodotVersion)).Append(',');
            sb.Append("\"bridgeVersion\":").Append(EscapeString(BridgeSession.BridgeVersion)).Append(',');
            sb.Append("\"mode\":").Append(EscapeString(BridgeSession.Mode)).Append(',');
            sb.Append("\"compiling\":").Append(BridgeSession.IsCompiling ? "true" : "false").Append(',');
            sb.Append("\"isPlaying\":").Append(BridgeSession.IsPlaying ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// The fallback <c>/ping</c> body returned with HTTP 503 before <see cref="BridgeSession"/>
        /// has finished its enable-time cache (so the MCP server's readiness probe gets a structured
        /// response instead of a hung connection). Mirrors Unity's <c>HandlePing</c> fallback: same
        /// shape as <see cref="BuildPingJson"/> but with <c>connected:false</c>, <c>compiling:true</c>,
        /// and null project/version strings. Deterministic so probes don't flap on field presence.
        /// </summary>
        internal static string BuildPingFallbackJson()
        {
            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"connected\":false,");
            sb.Append("\"projectPath\":null,");
            sb.Append("\"godotVersion\":null,");
            sb.Append("\"bridgeVersion\":").Append(EscapeString(BridgeSession.BridgeVersion)).Append(',');
            sb.Append("\"mode\":").Append(EscapeString(BridgeSession.Mode)).Append(',');
            sb.Append("\"compiling\":true,");
            sb.Append("\"isPlaying\":false");
            sb.Append('}');
            return sb.ToString();
        }
    }
}
#endif
