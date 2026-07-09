#nullable enable

namespace GodotOpenMcp.Verify.Rules.BrokenReferences
{
    /// <summary>
    /// Resolves the two kinds of external references a Godot <c>.tscn</c>/<c>.tres</c> can carry:
    /// <c>res://</c> paths and <c>uid://</c> identifiers. This is the Godot analog of Unity's
    /// <c>AssetDatabase.LoadAssetAtPath</c> / <c>AssetDatabase.GUIDToAssetPath</c> — the single seam the
    /// broken-references scanner uses to decide whether an <c>[ext_resource]</c> declaration points at a
    /// loadable target.
    ///
    /// <para>
    /// It is an interface (not a static call into <c>ResourceLoader</c>/<c>ResourceUID</c>) for two
    /// reasons, both inherited from the verify package's binary-less-test philosophy (see
    /// <c>packages/verify/AGENTS.md</c> and the test csproj header):
    /// <list type="bullet">
    ///   <item>The scanner (<see cref="SceneRefParser"/>) is pure-managed and must compile into the
    ///     xUnit host without P/Invoking into native Godot. A static <c>ResourceLoader.Exists</c> call
    ///     would either force the whole scanner behind <c>#if TOOLS</c> (killing unit testability) or
    ///     crash at runtime in the binary-less host.</item>
    ///   <item>Tests inject an in-memory resolver (e.g. "treat <c>res://Player.tscn</c> as existing, but
    ///     <c>res://Gone.tres</c> as missing") so the rule can be exercised against fixture text without
    ///     a project on disk.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Production wiring (<see cref="LiveResourceResolver"/>) is <c>#if TOOLS</c> and delegates to
    /// <c>ResourceLoader.Exists</c> / <c>ResourceUID.HasID</c>. The scanner never touches either type
    /// directly.
    /// </para>
    /// </summary>
    public interface IResourceResolver
    {
        /// <summary>
        /// Whether a <c>res://</c>-rooted path points at a resource the engine could load. Callers pass
        /// the raw <c>path=</c> value from an <c>[ext_resource]</c> header (e.g.
        /// <c>res://Scenes/Player.tscn</c>). Returns <c>false</c> for null/empty/non-<c>res://</c> input
        /// so the scanner can treat those as broken without a separate path-validation branch.
        /// </summary>
        bool PathExists(string? resPath);

        /// <summary>
        /// Whether a <c>uid://</c> identifier is registered in the project's uid table. Callers pass the
        /// raw <c>uid=</c> value (e.g. <c>uid://c4cp0al3ljsjv</c>). Returns <c>false</c> for null/empty
        /// input. A resolvable uid does not guarantee the path is also current — Godot relocates assets
        /// by uid — so a missing uid is the stronger signal of a deleted target.
        /// </summary>
        bool UidExists(string? uid);
    }
}
