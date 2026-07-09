#nullable enable

namespace GodotOpenMcp.Verify.Rules.BrokenReferences
{
    /// <summary>
    /// Stable issue-code constants emitted by <see cref="BrokenReferencesRule"/>. Codes are part of the
    /// stable API surface — MCP responses, the capability catalog, and <c>FixProviderRegistry</c> all
    /// match on the <c>ruleId|issueCode</c> tuple (<c>packages/verify/AGENTS.md</c>). The single P3.2
    /// code is <see cref="BrokenSceneReference"/>; the per-instance detail (which ext_resource / uid /
    /// SubResource id, which line) lives in <see cref="Core.VerifyIssue.Evidence"/>, not in the code.
    ///
    /// <para>
    /// Greenfield for Godot — Unity's <c>MissingReferences.IssueMapper</c> defines a large flat list of
    /// codes (<c>missing_guid</c>, <c>missing_fileid</c>, ...). Godot's reference model is simpler: a
    /// <c>[ext_resource]</c> declaration carries <c>uid=</c> + <c>path=</c> + <c>id=</c>, and usage sites
    /// reference it via <c>ExtResource("id")</c> or <c>SubResource("id")</c>. We collapse all broken
    /// cases into one code (<c>broken_scene_reference</c>) because the fix surface (P3.7) keys on the
    /// code, not on the failure mode — every broken ref is resolved the same way (relink or remove).
    /// </para>
    /// </summary>
    internal static class IssueCodes
    {
        /// <summary>
        /// A reference inside a <c>.tscn</c>/<c>.tres</c> could not be resolved: either an
        /// <c>[ext_resource]</c> whose <c>path=</c>/<c>uid=</c> points at nothing loadable, or a usage of
        /// an <c>ExtResource("id")</c>/<c>SubResource("id")</c> whose id was never declared. The exact
        /// failure mode is carried in <c>Evidence["kind"]</c> (see <see cref="EvidenceKinds"/>).
        /// </summary>
        public const string BrokenSceneReference = "broken_scene_reference";
    }

    /// <summary>
    /// Values placed in <c>Evidence["kind"]</c> to distinguish failure modes without multiplying issue
    /// codes. Agent-facing diagnostics can branch on this; fix providers (P3.7) can use it to pick a
    /// strategy. Kept internal because the stable surface is the single issue code above.
    /// </summary>
    internal static class EvidenceKinds
    {
        /// <summary>
        /// An <c>[ext_resource path="res://..."]</c> whose path does not resolve to a file on disk (the
        /// Godot analog of Unity's <c>missing_guid</c>: the target asset is gone).
        /// </summary>
        public const string ExtResourcePathMissing = "ext_resource_path_missing";

        /// <summary>
        /// An <c>[ext_resource uid="uid://..."]</c> whose uid does not resolve through Godot's
        /// <c>ResourceUID</c> table (the target was moved/deleted and its uid deregistered). Distinct
        /// from a missing path because uid is the canonical relocation-stable handle — a missing uid
        /// usually means the asset was deleted, not moved.
        /// </summary>
        public const string ExtResourceUidMissing = "ext_resource_uid_missing";

        /// <summary>
        /// A usage of <c>ExtResource("id")</c> whose string id was never declared by any
        /// <c>[ext_resource ...]</c> header in the same file. The Godot analog of Unity's
        /// <c>missing_local_fileid</c>: a dangling local id, usually left behind by a partial edit.
        /// </summary>
        public const string DanglingExtResource = "dangling_ext_resource";

        /// <summary>
        /// A usage of <c>SubResource("id")</c> whose string id was never declared by any
        /// <c>[sub_resource ...]</c> header in the same file.
        /// </summary>
        public const string DanglingSubResource = "dangling_sub_resource";
    }
}
