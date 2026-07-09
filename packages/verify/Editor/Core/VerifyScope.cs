#nullable enable

namespace GodotOpenMcp.Verify.Core
{
    /// <summary>
    /// The asset paths a verify pass covers plus whether to widen the scan to their dependents.
    /// Paths are <c>res://</c>-rooted Godot paths (e.g. <c>res://Scenes/Main.tscn</c>) — the Godot
    /// analog of Unity's <c>Assets/...</c> scope. Ported (copy) from Unity Open MCP's
    /// <c>VerifyScope</c>; the field set is identical because the gate passes <c>paths_hint</c> into
    /// exactly this shape. <see cref="IncludeDependents"/> is reserved for the reference-graph rules
    /// (P3.2+); the scaffold ships it so later phases do not reshape the constructor.
    /// </summary>
    public sealed class VerifyScope
    {
        /// <summary><c>res://</c>-rooted paths the scan is scoped to. Never null.</summary>
        public string[] Paths;

        /// <summary>
        /// When true, a rule may widen its scan to assets that depend on <see cref="Paths"/> (the
        /// reverse-dependency closure). Off by default so a scoped gate check stays cheap.
        /// </summary>
        public bool IncludeDependents;

        public VerifyScope(string[] paths, bool includeDependents = false)
        {
            Paths = paths;
            IncludeDependents = includeDependents;
        }
    }
}
