#if TOOLS
#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Per-project deterministic port + instance discovery paths. P1.4 — copy of Unity Open MCP's
    /// <c>InstancePortResolver</c>, adapted to the Godot home directory convention
    /// (<c>~/.godot-open-mcp/instances/</c>) and the <c>GODOT_OPEN_MCP_BRIDGE_PORT</c> override
    /// (packages/bridge/AGENTS.md §Multi-instance).
    ///
    /// <para>
    /// Two Godot projects running bridges simultaneously can't share a fixed port. We derive the port
    /// deterministically from the project path (<c>20000 + (sha256(path) % 10000)</c>), so the bridge
    /// and the MCP server agree without any shared config. An explicit override
    /// (<c>GODOT_OPEN_MCP_BRIDGE_PORT</c> env var) always wins — it's the escape hatch for users who
    /// pin a port and for CI flows that allocate ports externally.
    /// </para>
    ///
    /// <para>
    /// The port formula mirrors <c>mcp-server/src/instance-discovery.ts</c> byte for byte: take the
    /// first 8 bytes of SHA256(path) as a big-endian unsigned 64-bit integer, mod 10000, + 20000. The
    /// 8-byte prefix keeps the modulo inside Int64 range so C# (<see cref="ulong"/>) and TypeScript
    /// (<c>BigInt</c>) agree exactly; a full 256-bit modulo would diverge across language BigInts. The
    /// cross-side consistency is pinned by tests on both sides
    /// (<c>InstancePortResolverTests.cs</c> / <c>instance-discovery.test.ts</c>).
    /// </para>
    ///
    /// <para>
    /// Path normalization (forward-slash, trailing-slash trim) is applied BEFORE hashing so the same
    /// project resolves to the same port whether its path was recorded with a trailing separator or
    /// not. We deliberately do NOT lowercase: on macOS/Linux paths are case-sensitive, and
    /// lowercasing would collide distinct projects. Windows is case-insensitive but the Editor
    /// reports the canonical casing, so this is a non-issue in practice.
    /// </para>
    /// </summary>
    public static class InstancePortResolver
    {
        /// <summary>Port range: [20000, 29999]. Matches the spec and the MCP server.</summary>
        public const int PortRangeStart = 20000;

        /// <summary>Size of the deterministic port range.</summary>
        public const int PortRangeSize = 10000;

        /// <summary>
        /// The env-var name that overrides the deterministic port. Mirrors
        /// <c>mcp-server/src/instance-discovery.ts</c>, which reads the same name. P1.4 only reads
        /// the env var; a CLI-arg override may be added later alongside CLI wiring.
        /// </summary>
        public const string PortOverrideEnvVar = "GODOT_OPEN_MCP_BRIDGE_PORT";

        /// <summary>
        /// <c>~/.godot-open-mcp/instances</c> — one lock file per running bridge instance, keyed by
        /// project hash. Overridable via <see cref="InstancesDirOverride"/> for tests (so they don't
        /// write into the real <c>~/.godot-open-mcp</c>). Production callers leave it null.
        /// </summary>
        public static string InstancesDir
        {
            get
            {
                if (!string.IsNullOrEmpty(InstancesDirOverride))
                    return InstancesDirOverride;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".godot-open-mcp",
                    "instances");
            }
        }

        /// <summary>
        /// Test-only override for the instances dir. Not for production use. Set to an absolute temp
        /// dir in test setup, restored to null in teardown.
        /// </summary>
        public static string? InstancesDirOverride;

        /// <summary>
        /// The full path of the lock file for <paramref name="projectPath"/>:
        /// <c>&lt;InstancesDir&gt;/&lt;sha256(projectPath)&gt;.json</c>.
        /// </summary>
        public static string LockPath(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                throw new ArgumentNullException(nameof(projectPath));
            return Path.Combine(InstancesDir, ProjectHash(projectPath) + ".json");
        }

        /// <summary>
        /// SHA256 of the normalized path, lowercase hex. Used as the lock file name and as the
        /// <c>projectHash</c> field written into the lock JSON so the MCP server can verify it matched
        /// the project it expected.
        /// </summary>
        public static string ProjectHash(string projectPath)
        {
            var normalized = NormalizePath(projectPath);
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>
        /// Deterministic port for a project path: <c>20000 + (sha256(path) % 10000)</c>.
        /// </summary>
        public static int ComputePort(string projectPath)
        {
            var hashHex = ProjectHash(projectPath);
            // First 8 bytes (16 hex chars) as a big-endian UInt64, mod 10000. C#'s UInt64 and
            // TypeScript's BigInt agree on this exact value.
            var prefix = hashHex.Substring(0, 16);
            var value = ulong.Parse(prefix, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return PortRangeStart + (int)(value % (ulong)PortRangeSize);
        }

        /// <summary>
        /// Resolve the bridge port with override precedence:
        /// <list type="number">
        ///   <item><c>GODOT_OPEN_MCP_BRIDGE_PORT</c> env var (caller-provided, already parsed).</item>
        ///   <item>deterministic hash of the project path.</item>
        /// </list>
        /// Pass null for <paramref name="envPort"/> when the caller found no override;
        /// <see cref="IsValidPort"/> is the caller's responsibility (parse + range check) so the
        /// resolver only trusts values the caller already validated. P1.4's env-only surface mirrors
        /// the Godot bridge (no CLI args yet); a <c>cliPort</c> parameter will be added when the CLI
        /// ships a <c>--bridge-port</c> flag.
        /// </summary>
        public static int ResolvePort(string projectPath, int? envPort)
        {
            if (envPort.HasValue && IsValidPort(envPort.Value)) return envPort.Value;
            return ComputePort(projectPath);
        }

        /// <summary>True when <paramref name="port"/> is in the valid TCP range.</summary>
        public static bool IsValidPort(int port) => port >= 1 && port <= 65535;

        /// <summary>
        /// Normalize before hashing: forward slashes, no trailing slash. Mirrors the TS side
        /// (<c>instance-discovery.ts</c> <c>normalizePath</c>) byte for byte.
        /// </summary>
        public static string NormalizePath(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return "";
            var norm = projectPath.Replace('\\', '/');
            while (norm.Length > 1 && norm.EndsWith('/'))
                norm = norm.Substring(0, norm.Length - 1);
            return norm;
        }
    }
}
#endif
