#nullable enable
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Rules.ImportHealth
{
    /// <summary>
    /// The file/uid existence + sidecar enumeration seam the import-health rule uses. This is the
    /// Godot analog of the queries Godot-MCP's <c>Tool_FileSystem.Reimport</c>/<c>.List</c> make
    /// (see <c>addons/godot_mcp/Editor/Tools/Tool_FileSystem.List.cs</c>):
    /// <list type="bullet">
    ///   <item><see cref="FileExists"/> mirrors <c>FileAccess.FileExists</c> (does the source asset
    ///     still live on disk?).</item>
    ///   <item><see cref="UidExists"/> mirrors <c>ResourceUid.Singleton.HasId</c> (is the uid
    ///     registered in the project's uid table?).</item>
    ///   <item><see cref="EnumerateImportSidecars"/> mirrors the res:// directory walk
    ///     <c>Tool_FileSystem.List</c> performs — the rule needs the full set of <c>.import</c>
    ///     sidecars under the scoped path(s) to detect duplicates, which a single-path existence
    ///     check cannot give.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// It is an interface (not a static call into <c>FileAccess</c>/<c>ResourceUid</c>) for the same
    /// two reasons <see cref="BrokenReferences.IResourceResolver"/> is: (1) the scanner/parser is
    /// pure-managed and must compile into the binary-less xUnit host without P/Invoking native Godot;
    /// (2) tests inject an in-memory resolver (which source paths exist, which uids are registered,
    /// which sidecars are present) so the rule is exercised against fixture content with no project on
    /// disk. See <c>packages/verify/AGENTS.md</c> and the test csproj header.
    /// </para>
    ///
    /// <para>
    /// Production wiring (<see cref="LiveImportHealthResolver"/>) is <c>#if TOOLS</c> and delegates to
    /// <c>FileAccess.FileExists</c> / <c>ResourceUid.Singleton.HasId</c> and a <c>DirAccess</c> walk.
    /// The rule never touches those types directly.
    /// </para>
    /// </summary>
    public interface IImportHealthResolver
    {
        /// <summary>
        /// Whether a <c>res://</c>-rooted path points at a file that exists on disk. Callers pass the
        /// <c>source=</c> value from a sidecar (e.g. <c>res://Sprites/Player.png</c>). Returns
        /// <c>false</c> for null/empty/non-<c>res://</c> input so the rule can treat those as orphan
        /// sources without a separate validation branch.
        /// </summary>
        bool FileExists(string? resPath);

        /// <summary>
        /// Whether a <c>uid://</c> identifier is registered in the project's uid table. Callers pass
        /// the raw <c>uid=</c> value (e.g. <c>uid://c4cp0al3ljsjv</c>). Returns <c>false</c> for
        /// null/empty input. Mirrors <see cref="BrokenReferences.IResourceResolver.UidExists"/>: a
        /// registered uid does not by itself mean the resource is healthy (a duplicate-uid finding
        /// needs the cross-sidecar comparison the rule performs), but a deregistered uid is a deletion
        /// signal.
        /// </summary>
        bool UidExists(string? uid);

        /// <summary>
        /// Enumerate every <c>.import</c> sidecar reachable under the given <c>res://</c> root. The root
        /// may be a directory (<c>res://Sprites</c>) or the whole tree (<c>res://</c>). Returns
        /// <c>res://</c>-rooted sidecar paths (e.g. <c>res://Sprites/Player.png.import</c>). Never
        /// null; returns an empty list when the root has no sidecars or does not exist.
        ///
        /// <para>
        /// This is the duplicate-uid walk's input: the rule parses each sidecar's <c>uid=</c> and flags
        /// collisions. The orphan check only needs <see cref="FileExists"/> per sidecar's source, so a
        /// single-path scan does not enumerate — the rule calls this once for the uid-duplicate pass
        /// and, when the scope already lists <c>.import</c> paths directly, parses those without an
        /// extra walk.
        /// </para>
        /// </summary>
        IReadOnlyList<string> EnumerateImportSidecars(string? resRoot);
    }
}
