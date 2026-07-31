#nullable enable

namespace GodotOpenMcp.Verify.Rules.ProjectHealth
{
    /// <summary>
    /// Stable issue-code constants emitted by <see cref="ProjectHealthRule"/>. Codes are part of the
    /// stable API surface — MCP responses, the capability catalog, and the gate delta all match on the
    /// <c>ruleId|issueCode</c> tuple (<c>packages/verify/AGENTS.md</c>). Per-instance detail (which
    /// folder, how deep, how many children, the parse error) lives in <see cref="Core.VerifyIssue.Evidence"/>,
    /// not in the code.
    ///
    /// <para>
    /// Adapted from Unity Open MCP's <c>ProjectHealth/IssueMapper</c> codes (the <c>project_*</c> family),
    /// narrowed to the Godot analogues of Unity's folder/structure/empty-scene signals. Intentional
    /// deltas for v1:
    /// <list type="bullet">
    ///   <item><b>No <c>orphan_meta</c> / <c>duplicate_guid</c>.</b> Godot has no <c>.meta</c> files;
    ///     its relocation-stable handle is <c>uid://</c>, and orphan/duplicate uid already live in the
    ///     <c>import_health</c> rule (P3.4). <c>project_health</c> covers folder/structure signals only —
    ///     no overlap (see <c>specs/execution/P14/P14.1.md</c> Design Decision 1).</item>
    ///   <item><b>No <c>missing_project_setting</c>.</b> Unity validates <c>ProjectSettings/</c> files;
    ///     Godot's equivalent is <c>project.godot</c>, whose validation belongs to a future project-config
    ///     rule, not the folder-walk rule.</item>
    ///   <item><b><c>uid_only_folder</c> is greenfield.</b> Unity's <c>project_meta_only_folder</c> flags
    ///     a folder whose only children are <c>.meta</c> sidecars. Godot's twin is a folder whose only
    ///     children are <c>.gd.uid</c> sidecars (a script moved away, leaving its uid companion).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The codes share the <c>project_</c> prefix with Unity so an agent cross-trained on both engines
    /// recognizes the family. The <c>project_health</c> rule surfaces via <c>scan_paths</c> /
    /// <c>scan_all</c> / <c>validate_edit</c> / the gate delta — no new MCP tool (see
    /// <c>specs/execution/P14/README.md</c> "No new tools").
    /// </para>
    /// </summary>
    internal static class IssueCodes
    {
        /// <summary>
        /// A directory under <c>res://</c> with no files anywhere in its subtree (no files and no
        /// sub-directories recursively). Empty directories are inert weight — Godot does not index them,
        /// but they accumulate from partial refactors (a deleted asset's parent left behind, a feature
        /// branch that removed all content). Severity is <c>Warning</c>: an empty folder does not break
        /// the project, but it is cruft a clean project should not carry. The folder path and the
        /// recursive file count (always 0) are carried in evidence.
        /// </summary>
        public const string EmptyFolder = "project_empty_folder";

        /// <summary>
        /// A directory whose only file children are Godot uid sidecars (<c>*.uid</c> — most commonly
        /// <c>.gd.uid</c> companions, left behind when a script was moved or deleted by hand instead of
        /// through the editor). Greenfield for Godot: Unity's analogue is <c>project_meta_only_folder</c>
        /// (a folder of orphan <c>.meta</c> files). Severity is <c>Warning</c>: the sidecars are stale
        /// metadata, not loadable assets. The folder path and the sidecar count are carried in evidence.
        /// </summary>
        public const string UidOnlyFolder = "project_uid_only_folder";

        /// <summary>
        /// A directory whose depth from the <c>res://</c> root exceeds the threshold (default 8). Deep
        /// nesting hurts navigability and, beyond a point, Godot's path-length handling on some
        /// platforms. Adapted from Unity's <c>project_deep_nesting</c>. Severity is <c>Warning</c>: it is
        /// a maintainability signal, not an integrity break. The folder path, the measured depth, and the
        /// threshold are carried in evidence.
        /// </summary>
        public const string DeepNesting = "project_deep_nesting";

        /// <summary>
        /// A directory with more direct child entries (files + sub-directories) than the threshold
        /// (default 200). A giant flat folder is a maintainability and tooling-perf risk (directory
        /// listings, import scans). Adapted from Unity's <c>project_large_folder</c>. Severity is
        /// <c>Warning</c>. The folder path, the child count, and the threshold are carried in evidence.
        /// </summary>
        public const string LargeFolder = "project_large_folder";

        /// <summary>
        /// A <c>.tres</c>/<c>.tscn</c> text resource that fails structural parsing — a missing
        /// <c>[gd_resource]</c>/<c>[gd_scene]</c> header, a truncated body, or otherwise malformed text
        /// the parser cannot make sense of. Godot will refuse to load such a file (or load it with
        /// dropped data). Severity is <c>Error</c>: a broken asset is an integrity break, not cruft — it
        /// can fail scene load or silently drop resources. The asset path and the parse failure detail are
        /// carried in evidence.
        /// </summary>
        public const string BrokenAsset = "project_broken_asset";

        /// <summary>
        /// A <c>.tscn</c> scene whose node tree consists of only the root node — no children. An empty
        /// scene is usually a leftover scaffold (a created-but-never-populated scene). Adapted from
        /// Unity's <c>project_empty_scene</c> (Unity checks <c>rootCount == 0</c>; Godot scenes always
        /// have exactly one root, so the Godot analogue is a single root with no children). Severity is
        /// <c>Warning</c>: an empty scene does not break the project. The scene path is carried in
        /// evidence.
        /// </summary>
        public const string EmptyScene = "project_empty_scene";
    }
}
