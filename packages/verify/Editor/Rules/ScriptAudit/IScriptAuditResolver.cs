#nullable enable
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Rules.ScriptAudit
{
    /// <summary>
    /// One entry in a directory listing the resolver returns. A script-audit twin of
    /// <c>MaterialsShaderHealth.MaterialsFolderEntry</c> / <c>ProjectHealth.ProjectFolderEntry</c> /
    /// <c>SceneStructureHealth.SceneFolderEntry</c>: the directory walk the <c>script_audit</c> rule
    /// performs to reach every <c>.gd</c>/<c>.cs</c>/<c>.tscn</c>/<c>.tres</c> in a scoped subtree is
    /// identical to those walks, so the entry carries exactly the same fields (canonical <c>res://</c> path
    /// + name + kind).
    ///
    /// <para>
    /// Public because <see cref="LiveScriptAuditResolver"/> (a public type) returns it. The fields are
    /// immutable; the type is a pure data carrier with no Godot API surface.
    /// </para>
    /// </summary>
    public sealed class ScriptFolderEntry
    {
        /// <summary>
        /// Canonical <c>res://</c> path of the entry. Directories carry a trailing <c>/</c>
        /// (e.g. <c>res://Scripts/</c>); files never do (e.g. <c>res://Scripts/Player.gd</c>).
        /// </summary>
        public string ResPath { get; }

        /// <summary>Native basename (the last path segment, no directory).</summary>
        public string Name { get; }

        /// <summary>True for directories, false for files.</summary>
        public bool IsDirectory { get; }

        public ScriptFolderEntry(string resPath, string name, bool isDirectory)
        {
            ResPath = resPath;
            Name = name;
            IsDirectory = isDirectory;
        }
    }

    /// <summary>
    /// The directory-listing seam the script-audit rule uses to reach every script + scene/resource under a
    /// scoped subtree. The rule parses <c>.tscn</c>/<c>.tres</c> headers + bodies and <c>.gd</c>/<c>.cs</c>
    /// class declarations offline (see <see cref="ScriptClassParser"/>); to walk a directory scope it needs
    /// the one-level directory listing <c>project_health</c> / <c>scene_structure_health</c> /
    /// <c>materials_shader_health</c> use.
    ///
    /// <para>
    /// <b>Why this seam is narrower than the sibling resolvers:</b> the sibling rules
    /// (<c>materials_shader_health</c>, <c>broken_references</c>) also expose <c>PathExists</c> /
    /// <c>UidExists</c> because they resolve references <i>against</i> the project's path/uid tables.
    /// Script-audit does not resolve references against the uid table — it resolves a scene's
    /// <c>script = ExtResource("id")</c> to its declared <c>[ext_resource path=]</c> (pure text) and then
    /// reads that script file's text via the injected file reader. The file reader returns <c>null</c> for
    /// a missing file (handled by the rule as "not this rule's domain" — that is <c>missing_scripts</c>'
    /// signal), so no separate existence check is needed. Keeping the seam to just the directory walk
    /// means tests inject less state and the contract stays minimal.
    /// </para>
    ///
    /// <para>
    /// It is an interface (not a static call into <c>DirAccess</c>) for the same two reasons the sibling
    /// resolvers are interfaces: (1) the parser + rule are pure-managed and must compile into the
    /// binary-less xUnit host without P/Invoking native Godot; (2) tests inject an in-memory resolver
    /// (which directories exist) so the rule is exercised against fixture content with no project on disk.
    /// </para>
    ///
    /// <para>
    /// Production wiring (<see cref="LiveScriptAuditResolver"/>) is <c>#if TOOLS</c> and delegates to a
    /// one-level <c>DirAccess</c> walk. The rule never touches <c>DirAccess</c> directly. The walk skips
    /// the same internal directories (<c>.godot</c>, VCS, <c>node_modules</c>) the live resolvers skip.
    /// </para>
    /// </summary>
    internal interface IScriptAuditResolver
    {
        /// <summary>
        /// List the immediate children of a <c>res://</c> directory. Returns directory entries first
        /// (each with a trailing <c>/</c>), then file entries, each group sorted by name — deterministic
        /// ordering so two scans of the same subtree emit issues in the same order (gate-delta stability).
        /// Never null; returns an empty list when the directory does not exist or is unreadable. Internal
        /// directories (<c>.godot</c>, <c>.git</c>, <c>.hg</c>, <c>.svn</c>, <c>node_modules</c>) are NEVER
        /// surfaced.
        /// </summary>
        IReadOnlyList<ScriptFolderEntry> ListDirectory(string? resDir);
    }
}
