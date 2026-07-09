#nullable enable
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Core
{
    /// <summary>
    /// A scoped verify rule. Every rule implements this interface and lives in its own folder under
    /// <c>Editor/Rules/{RuleName}/</c> (see <c>packages/verify/AGENTS.md</c>). Ported (copy) from
    /// Unity Open MCP's <c>IVerifyRule</c>; the contract is identical because it is the stable entry
    /// point the gate calls on every checkpoint/validate/delta pass.
    ///
    /// <para>
    /// <b>Contract rules:</b>
    /// <list type="bullet">
    ///   <item><see cref="Id"/> is the stable rule identifier surfaced in MCP tool responses, the
    ///     capability catalog, and the gate delta (e.g. <c>broken_references</c>). Never empty.</item>
    ///   <item><see cref="Scan"/> appends zero or more <see cref="VerifyIssue"/>s to
    ///     <paramref name="sink"/>. Each issue MUST carry a non-empty
    ///     <see cref="VerifyIssue.IssueCode"/> so fixes can link to it.</item>
    ///   <item>A rule MUST NOT throw for ordinary malformed input — <see cref="VerifyRunner"/> catches
    ///     exceptions defensively, but a rule that throws on a normal case silently drops its issues
    ///     from a scoped gate check.</item>
    /// </list>
    /// </para>
    /// </summary>
    public interface IVerifyRule
    {
        /// <summary>Stable rule identifier (e.g. <c>broken_references</c>, <c>missing_scripts</c>).</summary>
        string Id { get; }

        /// <summary>
        /// Scan <paramref name="scope"/> in the given <paramref name="mode"/> and append findings to
        /// <paramref name="sink"/>. Must not throw on ordinary malformed assets.
        /// </summary>
        void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink);
    }
}
