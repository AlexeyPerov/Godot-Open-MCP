#nullable enable

namespace GodotOpenMcp.Verify.Rules.MissingScripts
{
    /// <summary>
    /// Stable issue-code constants emitted by <see cref="MissingScriptsRule"/>. Codes are part of the
    /// stable API surface — MCP responses, the capability catalog, and <c>FixProviderRegistry</c> all
    /// match on the <c>ruleId|issueCode</c> tuple (<c>packages/verify/AGENTS.md</c>).
    ///
    /// <para>
    /// <b>Relationship to P3.2 <c>broken_scene_reference</c>:</b> the broken-references rule (P3.2) flags
    /// any unresolved <c>[ext_resource]</c> or dangling <c>ExtResource("id")</c> generically, and a broken
    /// script <c>ext_resource</c> would already surface there. This rule is the <b>script-specialized</b>
    /// view the P3.7 fix surface keys on: it carries the <c>missing_script</c> code (so
    /// <c>remove_missing_script</c> can route on <c>missing_scripts|missing_script</c> distinct from
    /// <c>broken_references|broken_scene_reference</c>) plus node-level evidence (which node carries the
    /// missing script) that the generic rule does not track. Adapted from Unity's
    /// <c>MissingReferences</c> <c>missing_script</c> code — Unity keys the same
    /// <c>remove_missing_script</c> fix off <c>missing_references|missing_script</c>.
    /// </para>
    /// </summary>
    internal static class IssueCodes
    {
        /// <summary>
        /// A node's <c>script = ExtResource("id")</c> attachment could not be resolved: either the
        /// <c>[ext_resource type="Script"]</c> it links to has a <c>path=</c>/<c>uid=</c> pointing at
        /// nothing loadable (the script file was deleted/moved), or the usage id was never declared
        /// (dangling, usually a partial edit). The exact failure mode is in <c>Evidence["kind"]</c>
        /// (see <see cref="EvidenceKinds"/>). Distinct from <c>broken_scene_reference</c> because the
        /// P3.7 <c>remove_missing_script</c> fix matches this code specifically.
        /// </summary>
        public const string MissingScript = "missing_script";
    }

    /// <summary>
    /// Values placed in <c>Evidence["kind"]</c> to distinguish failure modes without multiplying issue
    /// codes. Agent-facing diagnostics can branch on this; fix providers (P3.7) can use it to pick a
    /// strategy (the eventual <c>remove_missing_script</c> fix will handle both). Kept internal because
    /// the stable surface is the single issue code above.
    /// </summary>
    internal static class EvidenceKinds
    {
        /// <summary>
        /// A node's <c>script = ExtResource("id")</c> points at an <c>[ext_resource type="Script"]</c>
        /// whose <c>path=</c>/<c>uid=</c> does not resolve to a loadable file (the script was deleted or
        /// moved and its uid deregistered). The Godot analog of Unity's MonoBehaviour with a missing
        /// script GUID.
        /// </summary>
        public const string ScriptResourceMissing = "script_resource_missing";

        /// <summary>
        /// A node's <c>script = ExtResource("id")</c> uses an id that no <c>[ext_resource]</c> header in
        /// the same file declares — the Godot analog of a dangling local fileID. Usually left behind by a
        /// partial edit that removed the <c>[ext_resource]</c> but not the node's <c>script</c> line.
        /// </summary>
        public const string ScriptIdDangling = "script_id_dangling";
    }
}
