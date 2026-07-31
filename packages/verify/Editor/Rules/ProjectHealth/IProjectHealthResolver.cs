#nullable enable
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Rules.ProjectHealth
{
    /// <summary>
    /// One entry in a directory listing the resolver returns. This is the Godot analog of the immediate
    /// children a <c>DirAccess</c> walk yields, distilled to exactly the fields the folder-structure
    /// checks need (name + kind), plus the child's own <c>res://</c> path so the rule can recurse and
    /// emit issues anchored on canonical paths. Kept deliberately small: the rule computes depth, child
    /// counts, and empty/uid-only classification from the listing; the resolver does not pre-compute
    /// them, mirroring how <c>collectProjectFiles</c> in <c>project-index.ts</c> (P7.3) returns raw
    /// paths and lets the caller derive structure.
    ///
    /// <para>
    /// Public because <see cref="LiveProjectHealthResolver"/> (a public type, mirrored from
    /// <c>LiveImportHealthResolver</c>) returns it from its <c>ListDirectory</c> method. The fields are
    /// immutable; the type is a pure data carrier with no Godot API surface.
    /// </para>
    /// </summary>
    public sealed class ProjectFolderEntry
    {
        /// <summary>
        /// Canonical <c>res://</c> path of the entry. Directories carry a trailing <c>/</c>
        /// (e.g. <c>res://Sprites/</c>); files never do (e.g. <c>res://Sprites/Player.png</c>).
        /// Matches the convention <c>filesystem_list</c> and <c>project-index.ts</c> use.
        /// </summary>
        public string ResPath { get; }

        /// <summary>Native basename (the last path segment, no directory).</summary>
        public string Name { get; }

        /// <summary>True for directories, false for files.</summary>
        public bool IsDirectory { get; }

        public ProjectFolderEntry(string resPath, string name, bool isDirectory)
        {
            ResPath = resPath;
            Name = name;
            IsDirectory = isDirectory;
        }
    }

    /// <summary>
    /// The directory-listing + file-existence seam the project-health rule uses. This is the Godot analog
    /// of the immediate-children <c>DirAccess</c> walk <c>Tool_FileSystem.List</c> performs, and of the
    /// recursive walk <c>collectProjectFiles</c> in <c>mcp-server/src/offline/project-index.ts</c> (P7.3)
    /// drives against disk. The rule uses it to:
    /// <list type="bullet">
    ///   <item><see cref="ListDirectory"/> — list the immediate children of a <c>res://</c> directory
    ///     (the rule recurses itself, so the resolver stays one-level like the live
    ///     <c>filesystem_list</c> tool).</item>
    ///   <item><see cref="FileExists"/> — confirm a <c>.tres</c>/<c>.tscn</c> the scope names directly is
    ///     still on disk before parsing it (a vanished file contributes no issues rather than a phantom
    ///     broken-asset, matching the "must not throw" contract).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// It is an interface (not a static call into <c>DirAccess</c>/<c>FileAccess</c>) for the same two
    /// reasons <see cref="ImportHealth.IImportHealthResolver"/> and
    /// <see cref="BrokenReferences.IResourceResolver"/> are: (1) the scanner/parser is pure-managed and
    /// must compile into the binary-less xUnit host without P/Invoking native Godot; (2) tests inject an
    /// in-memory resolver (which folders exist, which files each holds) so the rule is exercised against
    /// fixture content with no project on disk. See <c>packages/verify/AGENTS.md</c> and the test csproj
    /// header.
    /// </para>
    ///
    /// <para>
    /// Production wiring (<see cref="LiveProjectHealthResolver"/>) is <c>#if TOOLS</c> and delegates to a
    /// recursive <c>DirAccess</c> walk + <c>FileAccess.FileExists</c>. The rule never touches those types
    /// directly. The walk skips the same internal directories the live <c>filesystem_list</c> and the
    /// offline <c>project-index.ts</c> skip (<c>.godot</c>, VCS, <c>node_modules</c>) so a scan does not
    /// surface engine/import internals as user cruft.
    /// </para>
    /// </summary>
    internal interface IProjectHealthResolver
    {
        /// <summary>
        /// List the immediate children of a <c>res://</c> directory. Returns directory entries first
        /// (each with a trailing <c>/</c>), then file entries, each group sorted by name — the same
        /// deterministic ordering <c>filesystem_list</c> and <c>project-index.ts</c> produce, so two
        /// scans of the same tree emit issues in the same order (gate-delta stability). Never null;
        /// returns an empty list when the directory does not exist or is unreadable (the rule treats an
        /// empty listing as "no children", not an error — see <see cref="ProjectHealthRule"/>).
        ///
        /// <para>
        /// Internal directories (<c>.godot</c>, <c>.git</c>, <c>.hg</c>, <c>.svn</c>,
        /// <c>node_modules</c>) are NEVER surfaced, matching the live tool's documented safeguard — a
        /// scan must not flag the engine's import cache or VCS internals as user project cruft.
        /// </para>
        /// </summary>
        IReadOnlyList<ProjectFolderEntry> ListDirectory(string? resDir);

        /// <summary>
        /// Whether a <c>res://</c>-rooted path points at a file that exists on disk. Used to guard the
        /// broken-asset parse: a <c>.tres</c>/<c>.tscn</c> the scope names directly that vanished
        /// between checkpoint and validate contributes no issues rather than a phantom broken-asset.
        /// Returns <c>false</c> for null/empty/non-<c>res://</c> input.
        /// </summary>
        bool FileExists(string? resPath);
    }
}
