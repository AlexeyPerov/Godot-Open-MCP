#nullable enable

namespace GodotOpenMcp.Verify.Core
{
    /// <summary>
    /// Per-issue severity (not per-rule). The gate delta treats <see cref="Error"/> as a failure;
    /// <see cref="Warning"/> is informational. Ported (copy) from Unity Open MCP's
    /// <c>VerifySeverity</c>. The string form is centralized in <see cref="IssueKey"/> so all
    /// producers (rule emit, gate delta, MCP tool envelopes) spell it the same way.
    /// </summary>
    public enum VerifySeverity
    {
        Error,
        Warning
    }
}
