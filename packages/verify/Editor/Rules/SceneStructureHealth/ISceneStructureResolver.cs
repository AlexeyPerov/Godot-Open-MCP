#nullable enable
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Rules.SceneStructureHealth
{
    /// <summary>
    /// One entry in a directory listing the resolver returns. A scene-structure twin of
    /// <c>ProjectHealth.ProjectFolderEntry</c>: the directory walk the <c>scene_structure_health</c> rule
    /// performs to reach every <c>.tscn</c> in a scoped subtree is identical to the project-health walk,
    /// so the entry carries exactly the same fields (canonical <c>res://</c> path + name + kind).
    ///
    /// <para>
    /// Public because <see cref="LiveSceneStructureResolver"/> (a public type) returns it. The fields are
    /// immutable; the type is a pure data carrier with no Godot API surface.
    /// </para>
    /// </summary>
    public sealed class SceneFolderEntry
    {
        /// <summary>
        /// Canonical <c>res://</c> path of the entry. Directories carry a trailing <c>/</c>
        /// (e.g. <c>res://Scenes/</c>); files never do (e.g. <c>res://Scenes/Main.tscn</c>).
        /// </summary>
        public string ResPath { get; }

        /// <summary>Native basename (the last path segment, no directory).</summary>
        public string Name { get; }

        /// <summary>True for directories, false for files.</summary>
        public bool IsDirectory { get; }

        public SceneFolderEntry(string resPath, string name, bool isDirectory)
        {
            ResPath = resPath;
            Name = name;
            IsDirectory = isDirectory;
        }
    }

    /// <summary>
    /// The directory-listing + file-existence seam the scene-structure rule uses to reach <c>.tscn</c>
    /// files in a scoped subtree. The rule walks <c>.tscn</c> node trees offline (see
    /// <see cref="SceneStructureParser"/>); to reach every scene under a directory scope it needs the same
    /// one-level directory listing + file-existence checks <c>project_health</c> uses, so this interface
    /// mirrors <c>IProjectHealthResolver</c> exactly (only the entry type differs).
    ///
    /// <para>
    /// It is an interface (not a static call into <c>DirAccess</c>/<c>FileAccess</c>) for the same two
    /// reasons <c>IProjectHealthResolver</c> is: (1) the parser + rule are pure-managed and must compile
    /// into the binary-less xUnit host without P/Invoking native Godot; (2) tests inject an in-memory
    /// resolver (which directories exist, which scene text each holds) so the rule is exercised against
    /// fixture content with no project on disk.
    /// </para>
    ///
    /// <para>
    /// Production wiring (<see cref="LiveSceneStructureResolver"/>) is <c>#if TOOLS</c> and delegates to a
    /// one-level <c>DirAccess</c> walk + <c>FileAccess.FileExists</c>. The rule never touches those types
    /// directly. The walk skips the same internal directories (<c>.godot</c>, VCS, <c>node_modules</c>)
    /// the live <c>filesystem_list</c>, <c>project-index.ts</c>, and <c>LiveProjectHealthResolver</c> skip.
    /// </para>
    /// </summary>
    internal interface ISceneStructureResolver
    {
        /// <summary>
        /// List the immediate children of a <c>res://</c> directory. Returns directory entries first
        /// (each with a trailing <c>/</c>), then file entries, each group sorted by name — deterministic
        /// ordering so two scans of the same subtree emit issues in the same order (gate-delta
        /// stability). Never null; returns an empty list when the directory does not exist or is
        /// unreadable. Internal directories (<c>.godot</c>, <c>.git</c>, <c>.hg</c>, <c>.svn</c>,
        /// <c>node_modules</c>) are NEVER surfaced.
        /// </summary>
        IReadOnlyList<SceneFolderEntry> ListDirectory(string? resDir);

        /// <summary>
        /// Whether a <c>res://</c>-rooted path points at a file that exists on disk. Guards the parse
        /// against a <c>.tscn</c> that vanished between checkpoint and validate. Returns <c>false</c> for
        /// null/empty/non-<c>res://</c> input.
        /// </summary>
        bool FileExists(string? resPath);
    }
}
