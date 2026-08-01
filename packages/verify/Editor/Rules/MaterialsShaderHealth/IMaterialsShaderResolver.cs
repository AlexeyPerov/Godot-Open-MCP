#nullable enable
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Rules.MaterialsShaderHealth
{
    /// <summary>
    /// One entry in a directory listing the resolver returns. A materials-shader twin of
    /// <c>ProjectHealth.ProjectFolderEntry</c> / <c>SceneStructureHealth.SceneFolderEntry</c>: the
    /// directory walk the <c>materials_shader_health</c> rule performs to reach every <c>.tres</c>
    /// material and <c>.gdshader</c> in a scoped subtree is identical to those walks, so the entry carries
    /// exactly the same fields (canonical <c>res://</c> path + name + kind).
    ///
    /// <para>
    /// Public because <see cref="LiveMaterialsShaderResolver"/> (a public type) returns it. The fields are
    /// immutable; the type is a pure data carrier with no Godot API surface.
    /// </para>
    /// </summary>
    public sealed class MaterialsFolderEntry
    {
        /// <summary>
        /// Canonical <c>res://</c> path of the entry. Directories carry a trailing <c>/</c>
        /// (e.g. <c>res://Materials/</c>); files never do (e.g. <c>res://Materials/wood.tres</c>).
        /// </summary>
        public string ResPath { get; }

        /// <summary>Native basename (the last path segment, no directory).</summary>
        public string Name { get; }

        /// <summary>True for directories, false for files.</summary>
        public bool IsDirectory { get; }

        public MaterialsFolderEntry(string resPath, string name, bool isDirectory)
        {
            ResPath = resPath;
            Name = name;
            IsDirectory = isDirectory;
        }
    }

    /// <summary>
    /// The directory-listing + reference-resolution seam the materials-shader rule uses. The rule parses
    /// <c>.tres</c> material/shader refs and <c>.gdshader</c> includes offline (see
    /// <see cref="MaterialsShaderParser"/>); to resolve those references it needs the same path/uid
    /// existence checks <c>broken_references</c>' <c>IResourceResolver</c> performs, and to reach every
    /// material/shader under a directory scope it needs the one-level directory listing
    /// <c>project_health</c> / <c>scene_structure_health</c> use. <see cref="IsReferenced"/> is the
    /// P13.1-style reverse-edge probe for <c>unused_material</c>: it reports whether any file in the
    /// project references the given <c>res://</c> path or <c>uid://</c> token.
    ///
    /// <para>
    /// It is an interface (not a static call into <c>DirAccess</c>/<c>FileAccess</c>/
    /// <c>ResourceLoader</c>) for the same two reasons <c>IProjectHealthResolver</c> /
    /// <c>IResourceResolver</c> are: (1) the parser + rule are pure-managed and must compile into the
    /// binary-less xUnit host without P/Invoking native Godot; (2) tests inject an in-memory resolver
    /// (which directories exist, which material text each holds, which refs resolve) so the rule is
    /// exercised against fixture content with no project on disk.
    /// </para>
    ///
    /// <para>
    /// Production wiring (<see cref="LiveMaterialsShaderResolver"/>) is <c>#if TOOLS</c> and delegates to a
    /// one-level <c>DirAccess</c> walk, <c>ResourceLoader.Exists</c>/<c>FileAccess.FileExists</c>, and a
    /// text scan over the project files for the reverse edge. The rule never touches those types directly.
    /// The walk skips the same internal directories (<c>.godot</c>, VCS, <c>node_modules</c>) the live
    /// resolvers skip.
    /// </para>
    /// </summary>
    internal interface IMaterialsShaderResolver
    {
        /// <summary>
        /// List the immediate children of a <c>res://</c> directory. Returns directory entries first
        /// (each with a trailing <c>/</c>), then file entries, each group sorted by name — deterministic
        /// ordering so two scans of the same subtree emit issues in the same order (gate-delta stability).
        /// Never null; returns an empty list when the directory does not exist or is unreadable. Internal
        /// directories (<c>.godot</c>, <c>.git</c>, <c>.hg</c>, <c>.svn</c>, <c>node_modules</c>) are NEVER
        /// surfaced.
        /// </summary>
        IReadOnlyList<MaterialsFolderEntry> ListDirectory(string? resDir);

        /// <summary>
        /// Whether a <c>res://</c>-rooted path points at a file that exists on disk. Guards the parse
        /// against a <c>.tres</c>/<c>.gdshader</c> that vanished between checkpoint and validate, and is
        /// the <c>path=</c> resolution basis for <c>missing_shader</c> / <c>orphan_shader_include</c>.
        /// Returns <c>false</c> for null/empty/non-<c>res://</c> input.
        /// </summary>
        bool PathExists(string? resPath);

        /// <summary>
        /// Whether a <c>uid://</c> identifier is registered in the project's uid table. The
        /// <c>uid=</c> resolution basis for <c>missing_shader</c>: Godot prefers uid over path and a
        /// missing uid is the stronger signal of a deleted target. Returns <c>false</c> for null/empty
        /// input.
        /// </summary>
        bool UidExists(string? uid);

        /// <summary>
        /// Whether any file under <c>res://</c> references the given target. A reference exists when an
        /// <c>[ext_resource]</c> declares <c>path=</c>/<c>uid=</c> matching the target, or a bare
        /// <c>uid://</c> token appears in a script/resource body — the P13.1 reverse-edge semantics. Used
        /// by the <c>unused_material</c> signal (Full-mode only, expensive). The self-reference (the
        /// material's own file) is NOT counted. Returns <c>false</c> for null/empty target or when nothing
        /// references it.
        /// </summary>
        bool IsReferenced(string? resPathOrUid, string? ownerResPath);
    }
}
