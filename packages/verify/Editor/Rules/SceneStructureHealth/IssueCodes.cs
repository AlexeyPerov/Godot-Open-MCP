#nullable enable

namespace GodotOpenMcp.Verify.Rules.SceneStructureHealth
{
    /// <summary>
    /// Stable issue-code constants emitted by <see cref="SceneStructureHealthRule"/>. Codes are part of the
    /// stable API surface — MCP responses, the capability catalog, and the gate delta all match on the
    /// <c>ruleId|issueCode</c> tuple (<c>packages/verify/AGENTS.md</c>). Per-instance detail (which node,
    /// how deep, how many siblings, the collided name) lives in <see cref="Core.VerifyIssue.Evidence"/>,
    /// not in the code.
    ///
    /// <para>
    /// Adapted from Unity Open MCP's <c>ScenePrefabHealth</c> rule — but the fidelity is mostly
    /// <c>adapt</c>/<c>greenfield</c>: Unity's <c>deep_nesting</c> measures prefab-variant nesting (Godot
    /// has no prefab system in v1), and Unity ships NO width / duplicate-name / empty-branch signals at
    /// all. The Godot rule borrows the depth/width/count threshold <i>philosophy</i> and defines the
    /// node-tree signals greenfield for Godot's serialized <c>.tscn</c> tree (see
    /// <c>specs/execution/P14/P14.2.md</c>). Intentional deltas for v1:
    /// <list type="bullet">
    ///   <item><b>Depth = node-tree depth, not prefab-variant depth.</b> Unity's
    ///     <c>CalculateNestingDepth</c> chases <c>PrefabUtility.GetCorrespondingObjectFromSource</c>; the
    ///     Godot analogue is the depth of a node in the scene's node tree (root = 0), computed offline from
    ///     the serialized <c>parent=</c> attributes.</item>
    ///   <item><b>No prefab-override / variant signals.</b> Godot has no prefab system in v1, so Unity's
    ///     <c>override_explosion</c> and prefab-only <c>deep_nesting</c> have no twin.</item>
    ///   <item><b><c>duplicate_node_name</c> is greenfield.</b> Godot <c>$NodePath</c> lookups require
    ///     unique names among siblings; two siblings sharing a name breaks
    ///     <c>get_node("Parent/Dup")</c>. Unity has no such signal.</item>
    ///   <item><b><c>empty_node_branch</c> is greenfield.</b> A node with children whose entire subtree
    ///     carries no content (no script, no instance, no concrete leaf) — usually leftover scaffolding.
    ///     Unity has no such signal.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The codes share the <c>scene_</c> prefix (mirroring the <c>project_</c> prefix convention the
    /// <c>project_health</c> rule set in P14.1) so an agent recognizes the family. The
    /// <c>scene_structure_health</c> rule surfaces via <c>scan_paths</c> / <c>scan_all</c> /
    /// <c>validate_edit</c> / the gate delta — no new MCP tool (see <c>specs/execution/P14/README.md</c>
    /// "No new tools").
    /// </para>
    /// </summary>
    internal static class IssueCodes
    {
        /// <summary>
        /// A node whose depth from the scene root exceeds the threshold (default 10). Deeply nested node
        /// trees hurt navigability and signal over-coupled scene structure. Adapted from Unity's
        /// <c>deep_nesting</c> philosophy, but measured as node-tree depth (root = 0) rather than
        /// prefab-variant depth. Severity is <c>Warning</c>: it is a maintainability signal, not an
        /// integrity break. The node path, the measured depth, and the threshold are carried in evidence.
        /// </summary>
        public const string DeepNesting = "scene_deep_nesting";

        /// <summary>
        /// A scene whose total <c>[node]</c> count exceeds the threshold (default 1000). A giant scene is
        /// a maintainability and editor-perf risk (slow saves, heavy instancing). Adapted from Unity's
        /// <c>scene_object_count</c> philosophy. Severity is <c>Warning</c>. The scene path, the node
        /// count, and the threshold are carried in evidence.
        /// </summary>
        public const string HighNodeCount = "scene_high_node_count";

        /// <summary>
        /// A parent node with more direct child nodes than the threshold (default 100). A wide sibling
        /// list hurts navigability and can indicate a flat layout that should be grouped. Greenfield for
        /// Godot (Unity has no width signal). Severity is <c>Warning</c>. The parent path, the child
        /// count, and the threshold are carried in evidence.
        /// </summary>
        public const string WideSiblingList = "scene_wide_sibling_list";

        /// <summary>
        /// Two sibling nodes share a name. Godot node paths must be unique among siblings — a duplicate
        /// makes <c>get_node("Parent/Dup")</c> ambiguous and breaks <c>$NodePath</c> resolution.
        /// Greenfield for Godot (Unity has no such signal). Severity is <c>Warning</c>: the editor tolerates
        /// duplicate names but resolves lookups nondeterministically, so it is a latent bug rather than an
        /// immediate load break. The parent path, the collided name, and the count are carried in evidence.
        /// </summary>
        public const string DuplicateNodeName = "scene_duplicate_node_name";

        /// <summary>
        /// A non-root branch whose entire subtree carries no content — no script attachment, no instanced
        /// sub-scene, and no concrete leaf node (a bare <c>Node</c> container with bare-<c>Node</c>
        /// children). Usually leftover scaffolding from a feature that was removed. Greenfield for Godot
        /// (Unity has no such signal). Severity is <c>Warning</c>: it is cruft, not an integrity break. The
        /// scene root is excluded (a root-only scene is <c>project_empty_scene</c>'s domain — no overlap).
        /// The branch path is carried in evidence.
        /// </summary>
        public const string EmptyNodeBranch = "scene_empty_node_branch";
    }
}
