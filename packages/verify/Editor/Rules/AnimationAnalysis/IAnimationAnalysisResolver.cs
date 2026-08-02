#nullable enable
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Rules.AnimationAnalysis
{
    /// <summary>
    /// One entry in a directory listing the resolver returns. An animation-analysis twin of
    /// <c>MaterialsShaderHealth.MaterialsFolderEntry</c> / <c>ProjectHealth.ProjectFolderEntry</c> /
    /// <c>ScriptAudit.ScriptFolderEntry</c>: the directory walk the <c>animation_analysis</c> rule
    /// performs to reach every <c>.tres</c> AnimationPlayer/AnimationLibrary/Animation/AnimationNodeStateMachine
    /// and <c>.tscn</c> scene in a scoped subtree is identical to those walks, so the entry carries exactly
    /// the same fields (canonical <c>res://</c> path + name + kind).
    ///
    /// <para>
    /// Public because <see cref="LiveAnimationAnalysisResolver"/> (a public type) returns it. The fields
    /// are immutable; the type is a pure data carrier with no Godot API surface.
    /// </para>
    /// </summary>
    public sealed class AnimationFolderEntry
    {
        /// <summary>
        /// Canonical <c>res://</c> path of the entry. Directories carry a trailing <c>/</c>
        /// (e.g. <c>res://Animations/</c>); files never do (e.g. <c>res://Animations/Player.tres</c>).
        /// </summary>
        public string ResPath { get; }

        /// <summary>Native basename (the last path segment, no directory).</summary>
        public string Name { get; }

        /// <summary>True for directories, false for files.</summary>
        public bool IsDirectory { get; }

        public AnimationFolderEntry(string resPath, string name, bool isDirectory)
        {
            ResPath = resPath;
            Name = name;
            IsDirectory = isDirectory;
        }
    }

    /// <summary>
    /// The directory-listing + reference-resolution seam the animation-analysis rule uses. The rule parses
    /// <c>.tres</c>/<c>.tscn</c> AnimationPlayer / AnimationLibrary / Animation / AnimationNodeStateMachine
    /// text offline (see <see cref="AnimationParser"/>); to resolve clip references it needs the same
    /// path/uid existence checks <c>broken_references</c>' <c>IResourceResolver</c> performs, and to reach
    /// every animation resource under a directory scope it needs the one-level directory listing
    /// <c>project_health</c> / <c>scene_structure_health</c> / <c>materials_shader_health</c> /
    /// <c>script_audit</c> use.
    ///
    /// <para>
    /// It is an interface (not a static call into <c>DirAccess</c>/<c>ResourceLoader</c>) for the same two
    /// reasons the sibling resolvers are interfaces: (1) the parser + rule are pure-managed and must
    /// compile into the binary-less xUnit host without P/Invoking native Godot; (2) tests inject an
    /// in-memory resolver (which directories exist, which refs resolve) so the rule is exercised against
    /// fixture content with no project on disk.
    /// </para>
    ///
    /// <para>
    /// Production wiring (<see cref="LiveAnimationAnalysisResolver"/>) is <c>#if TOOLS</c> and delegates to
    /// a one-level <c>DirAccess</c> walk + <c>ResourceLoader.Exists</c>/<c>FileAccess.FileExists</c>. The
    /// rule never touches those types directly. The walk skips the same internal directories
    /// (<c>.godot</c>, VCS, <c>node_modules</c>) the sibling live resolvers skip.
    /// </para>
    /// </summary>
    internal interface IAnimationAnalysisResolver
    {
        /// <summary>
        /// List the immediate children of a <c>res://</c> directory. Returns directory entries first
        /// (each with a trailing <c>/</c>), then file entries, each group sorted by name — deterministic
        /// ordering so two scans of the same subtree emit issues in the same order (gate-delta stability).
        /// Never null; returns an empty list when the directory does not exist or is unreadable. Internal
        /// directories (<c>.godot</c>, <c>.git</c>, <c>.hg</c>, <c>.svn</c>, <c>node_modules</c>) are NEVER
        /// surfaced.
        /// </summary>
        IReadOnlyList<AnimationFolderEntry> ListDirectory(string? resDir);

        /// <summary>
        /// Whether a <c>res://</c>-rooted path points at a file that exists on disk. The <c>path=</c>
        /// resolution basis for <c>missing_clip</c> (a clip whose <c>[ext_resource path=]</c> is gone).
        /// Returns <c>false</c> for null/empty/non-<c>res://</c> input.
        /// </summary>
        bool PathExists(string? resPath);

        /// <summary>
        /// Whether a <c>uid://</c> identifier is registered in the project's uid table. The <c>uid=</c>
        /// resolution basis for <c>missing_clip</c>: Godot prefers uid over path and a missing uid is the
        /// stronger deletion signal. Returns <c>false</c> for null/empty input.
        /// </summary>
        bool UidExists(string? uid);
    }
}
