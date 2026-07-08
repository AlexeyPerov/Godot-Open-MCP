#nullable enable
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Structured snapshot of a Godot editor scene tab returned by the scene tool family
    /// (<c>godot_open_mcp_scene_open</c> / <c>scene_save</c> / <c>scene_list_opened</c>, P2.6). Holds
    /// no live <c>Node</c> handle — it is built on the main thread from the edited scene root or an
    /// open-scene path, then serialized off the main thread, so it touches no Godot native object
    /// once constructed.
    ///
    /// <para>
    /// Godot ↔ Unity mapping: a Godot scene is a <c>PackedScene</c> on disk (<c>res://*.tscn</c>)
    /// instanced as the editor's edited root <c>Node</c>. Unity's scene summary carries a build
    /// index + <c>isDirty</c> + <c>isLoaded</c>; Godot 4.3 has no build index for editor scenes and
    /// no public dirty-state query (see <see cref="IsDirty"/>), so this DTO carries
    /// <see cref="RootName"/> + <see cref="RootType"/> (the Godot analog of Unity's per-scene root
    /// summary) plus an <see cref="IsActive"/> flag (Godot 4.3 exposes the open-scene set as paths
    /// plus a single edited root, not per-open-scene roots).
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so it is unit-testable in the
    /// binary-less xUnit host. The on-editor <see cref="SceneTools"/> handlers populate it; this
    /// type only holds data and knows how to serialize itself via <see cref="AppendJsonTo"/> (the
    /// bridge carries no System.Text.Json / Newtonsoft dependency per
    /// <c>packages/bridge/AGENTS.md</c> §Transport, so the DTO serializes itself with
    /// <see cref="BridgeJson"/>).
    /// </para>
    /// </summary>
    internal sealed class SceneSummary
    {
        /// <summary>
        /// <c>res://</c> path of the scene file (<c>res://levels/level_1.tscn</c>). Null for a
        /// freshly-created scene that has never been saved (Godot surfaces these in the edited root
        /// with an empty <c>GetSceneFilePath()</c>).
        /// </summary>
        public string? Path { get; set; } = null;

        /// <summary>
        /// Name of the scene — the root Node's name for the active/edited scene, or the file stem
        /// (without extension) for a non-active open scene (Godot 4.3 exposes no root accessor for
        /// non-active open scenes, so only the path is available there). Null when neither is known.
        /// </summary>
        public string? Name { get; set; } = null;

        /// <summary>
        /// True when the scene has unsaved changes. <b>Godot 4.3 has no public
        /// <c>EditorInterface</c> API to query dirty state</b> (the editor tracks it internally via
        /// <c>EditorUndoRedoManager</c>, which is not bound to C#). The bridge tracks this
        /// best-effort: a flag is set whenever a bridge handler calls
        /// <c>MarkSceneAsUnsaved()</c> and cleared on save/open. This catches the agent-driven
        /// danger case (an agent mutates a scene then opens another without saving) but does NOT
        /// reflect edits a human makes directly in the editor — an intentional delta from Unity,
        /// which has a clean <c>isDirty</c> query.
        /// </summary>
        public bool IsDirty { get; set; } = false;

        /// <summary>
        /// Godot class name of the scene's root Node (e.g. <c>Node3D</c>, <c>Node</c>), when known.
        /// Null for a non-active open scene (no root accessor in Godot 4.3).
        /// </summary>
        public string? RootType { get; set; } = null;

        /// <summary>
        /// True when this scene is the editor's currently-edited (active) scene. There is exactly one
        /// active scene; the rest are open tabs.
        /// </summary>
        public bool IsActive { get; set; } = false;

        /// <summary>
        /// Append this scene as a JSON object to <paramref name="sb"/>. Field order is fixed
        /// (path, name, isDirty, rootType, isActive) so diffing clients don't flap on reordering.
        /// Strings flow through <see cref="BridgeJson"/> for escaping.
        /// </summary>
        internal void AppendJsonTo(StringBuilder sb)
        {
            sb.Append('{');
            sb.Append("\"path\":").Append(BridgeJson.EscapeString(Path)).Append(',');
            sb.Append("\"name\":").Append(BridgeJson.EscapeString(Name)).Append(',');
            sb.Append("\"isDirty\":").Append(IsDirty ? "true" : "false").Append(',');
            sb.Append("\"rootType\":").Append(BridgeJson.EscapeString(RootType)).Append(',');
            sb.Append("\"isActive\":").Append(IsActive ? "true" : "false");
            sb.Append('}');
        }

        /// <summary>Serialize this scene as a standalone JSON object string.</summary>
        public string ToJsonString()
        {
            var sb = new StringBuilder(96);
            AppendJsonTo(sb);
            return sb.ToString();
        }
    }
}
