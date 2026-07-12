#if TOOLS
#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Per-session bearer token utilities. P5.2 — copy of Unity Open MCP's
    /// <c>BridgeAuthToken</c> (byte-for-byte policy): mint a 256-bit hex token, parse a Bearer
    /// <c>Authorization</c> header, and compare two tokens in constant time. Pure (no Godot APIs)
    /// so it is unit-testable in the binary-less host.
    ///
    /// <para>
    /// The token is always minted into the instance lock on bridge start
    /// (<see cref="BridgeInstanceLock.Acquire"/>) regardless of <c>authMode</c>, so an operator can
    /// flip from <c>"none"</c> to <c>"required"</c> without a restart and the MCP server can always
    /// attach the header when present (<c>mcp-server/src/live-client.ts</c>). Enforcement is decided
    /// by <see cref="BridgeAuthCheck"/> at request time.
    /// </para>
    ///
    /// <para>
    /// Hex-encoding keeps the token ASCII-safe across the lock file, HTTP headers, and the TS-side
    /// discovery parser (<c>mcp-server/src/instance-discovery.ts</c> <c>authToken</c> field).
    /// </para>
    /// </summary>
    public static class BridgeAuthToken
    {
        /// <summary>32 bytes → 256-bit token.</summary>
        public const int ByteLength = 32;

        /// <summary>
        /// Hex length = <see cref="ByteLength"/> * 2. Exposed so tests can pin the format without
        /// hardcoding a magic number.
        /// </summary>
        public const int HexLength = ByteLength * 2;

        const string BearerPrefix = "Bearer ";

        /// <summary>
        /// Mint a fresh token. Never returns null/empty. Uses the cryptographic RNG so the token is
        /// unpredictable — a non-crypto RNG would let an attacker who can read the lock file (shared
        /// machine / CI runner) predict a future session's token.
        /// </summary>
        public static string Generate()
        {
            var bytes = new byte[ByteLength];
            RandomNumberGenerator.Fill(bytes);
            var sb = new StringBuilder(HexLength);
            for (int i = 0; i < ByteLength; i++)
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>
        /// Constant-time equality. Avoids early-exit timing leaks when comparing the request's bearer
        /// value against the expected token. Different lengths cannot match, but the full shorter
        /// length is still walked so a timing profile can't reveal the expected length. Both inputs
        /// must be hex strings in practice, but this helper is encoding-agnostic. Copied verbatim
        /// from Unity's <c>BridgeAuthToken.EqualsConstantTime</c>.
        /// </summary>
        public static bool EqualsConstantTime(string a, string b)
        {
            if (a == null) a = "";
            if (b == null) b = "";

            var aLen = a.Length;
            var bLen = b.Length;
            var maxLen = System.Math.Max(aLen, bLen);

            var diff = (byte)(aLen ^ bLen);
            for (int i = 0; i < maxLen; i++)
            {
                var ca = (char)0;
                var cb = (char)0;
                if (i < aLen) ca = a[i];
                if (i < bLen) cb = b[i];
                diff |= (byte)(ca ^ cb);
            }
            return diff == 0;
        }

        /// <summary>
        /// Parse an <c>Authorization</c> header value into the bare token, tolerating surrounding
        /// whitespace and case differences in the scheme. Returns null when the header is absent or
        /// not a Bearer value — callers treat null as a 401. Copied verbatim from Unity's
        /// <c>BridgeAuthToken.ExtractBearer</c>.
        /// </summary>
        public static string? ExtractBearer(string? headerValue)
        {
            if (string.IsNullOrEmpty(headerValue)) return null;
            var s = headerValue.Trim();
            if (s.Length <= BearerPrefix.Length) return null;
            // Case-insensitive scheme match.
            if (string.Compare(s, 0, BearerPrefix, 0, BearerPrefix.Length,
                    CultureInfo.InvariantCulture, CompareOptions.OrdinalIgnoreCase) != 0)
                return null;
            var token = s.Substring(BearerPrefix.Length).Trim();
            return token.Length == 0 ? null : token;
        }
    }
}
#endif
