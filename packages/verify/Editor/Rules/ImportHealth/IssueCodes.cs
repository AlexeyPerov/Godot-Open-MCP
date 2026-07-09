#nullable enable

namespace GodotOpenMcp.Verify.Rules.ImportHealth
{
    /// <summary>
    /// Stable issue-code constants emitted by <see cref="ImportHealthRule"/>. Codes are part of the
    /// stable API surface — MCP responses, the capability catalog, and <c>FixProviderRegistry</c> all
    /// match on the <c>ruleId|issueCode</c> tuple (<c>packages/verify/AGENTS.md</c>). The per-instance
    /// detail (which sidecar, which uid, which importer) lives in <see cref="Core.VerifyIssue.Evidence"/>,
    /// not in the code.
    ///
    /// <para>
    /// Greenfield for Godot — Unity has no <c>.import</c>-sidecar concept (Unity's import metadata lives
    /// in the <c>.meta</c> + <c>Library/</c> cache, and uid integrity is a Godot-specific concern). The
    /// two codes map directly to the P3.4 acceptance criteria ("Detect orphan <c>.import</c> artifacts"
    /// → <see cref="OrphanImport"/>; "Detect duplicate/conflicting UID declarations" →
    /// <see cref="DuplicateUid"/>). The fix surface (P3.7) keys on these codes; e.g. an
    /// <c>remove_orphan_import</c> fix would match <c>import_health|orphan_import</c>.
    /// </para>
    /// </summary>
    internal static class IssueCodes
    {
        /// <summary>
        /// A <c>.import</c> sidecar whose <c>source=</c> points at a file that no longer exists on disk.
        /// Godot writes the sidecar next to every imported asset; when the source is deleted by hand (or
        /// moved without the editor reindexing), the sidecar is left behind as orphan metadata. Left
        /// un-cleaned it can cause nondeterministic re-imports and phantom editor errors. Severity is
        /// <c>Warning</c>, not <c>Error</c>: an orphan sidecar does not break scene load (the engine
        /// ignores a sidecar whose source is gone after a rescan), but it is project-level cruft a clean
        /// project should not accumulate. The source path is carried in <c>Evidence["source"]</c>.
        /// </summary>
        public const string OrphanImport = "orphan_import";

        /// <summary>
        /// Two or more <c>.import</c> sidecars declare the same <c>uid://</c> identifier. A uid must be
        /// globally unique — Godot uses it as the canonical relocation-stable handle, and a collision
        /// means the engine cannot deterministically resolve which asset a <c>uid://</c> reference points
        /// at, causing nondeterministic load/import failures. Severity is <c>Error</c>: a uid collision
        /// is a real integrity break, not informational cruft — Godot will refuse to reimport or will
        /// silently pick one. The colliding uid and the set of sidecars declaring it are carried in
        /// evidence (<c>uid</c>, <c>conflictingPaths</c>).
        /// </summary>
        public const string DuplicateUid = "duplicate_uid";
    }
}
