#nullable enable
using System.Globalization;
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Truthful snapshot of the Godot editor's play-process state — returned by
    /// <c>godot_open_mcp_editor_application_get_state</c> and embedded in
    /// <c>godot_open_mcp_editor_application_set_state</c> results (P4.5).
    ///
    /// <para>
    /// Godot launches the game as a SEPARATE OS process (<c>EditorInterface.PlayMainScene</c> /
    /// <c>PlayCurrentScene</c> / <c>PlayCustomScene</c> → a child process), not an in-editor playmode
    /// toggle. The state model therefore excludes Unity's <c>isPaused</c> / <c>isCompiling</c> /
    /// domain-reload fields — Godot has no editor-side equivalent contract that is both reliable and
    /// meaningful to an agent. The handler observes <c>EditorInterface.IsPlayingScene()</c> (true while a
    /// play process is running) and <c>GetPlayingScene()</c> (the <c>res://</c> path of the running
    /// scene, when available).
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the DTO's serialization is
    /// unit-testable in the binary-less xUnit host. The on-editor handler
    /// (<see cref="EditorApplicationTools"/>) populates it from <c>EditorInterface</c> on the main
    /// thread; this type only holds data and knows how to serialize itself via
    /// <see cref="BridgeJson"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Intentional deltas from Unity</b> (<c>packages/bridge/AGENTS.md</c> §Unity-first): Unity's
    /// <c>editor_status</c> returns <c>isPlaying</c> / <c>isCompiling</c> / <c>isPaused</c> /
    /// <c>currentScene</c> / <c>unityVersion</c> / <c>editorType</c>. The Godot DTO drops
    /// <c>isCompiling</c> / <c>isPaused</c> / <c>editorType</c> (no Godot equivalent), renames
    /// <c>currentScene</c> → <c>playingScene</c> (the scene the play process is running, not the edited
    /// scene), and adds <c>observedAt</c> (ISO-8601 UTC) so callers can correlate the snapshot with
    /// their own request timing.
    /// </para>
    /// </summary>
    internal sealed class EditorApplicationStateData
    {
        /// <summary>True while the editor has launched a play process
        /// (<c>EditorInterface.IsPlayingScene()</c>). False when stopped.</summary>
        public bool IsPlaying { get; set; }

        /// <summary>The <c>res://</c> path of the scene the play process is running, or null when
        /// not playing or the path is unavailable. Godot exposes this via
        /// <c>EditorInterface.GetPlayingScene()</c>.</summary>
        public string? PlayingScene { get; set; }

        /// <summary>Godot editor version string (e.g. <c>4.3.stable.mono</c>), sourced from
        /// <c>Engine.GetVersionInfo()["string"]</c>.</summary>
        public string EditorVersion { get; set; } = string.Empty;

        /// <summary>ISO-8601 UTC timestamp marking when the snapshot was observed. Lets a caller
        /// correlate the state with its own request timing and detect a stale snapshot after a
        /// timeout.</summary>
        public string? ObservedAt { get; set; }

        /// <summary>Append this state as a JSON object. Field order is fixed (isPlaying,
        /// playingScene, editorVersion, observedAt) so diffing clients don't flap on
        /// reordering.</summary>
        internal void AppendJsonTo(StringBuilder sb)
        {
            sb.Append('{');
            sb.Append("\"isPlaying\":").Append(IsPlaying ? "true" : "false").Append(',');
            sb.Append("\"playingScene\":").Append(BridgeJson.EscapeString(PlayingScene)).Append(',');
            sb.Append("\"editorVersion\":").Append(BridgeJson.EscapeString(EditorVersion)).Append(',');
            sb.Append("\"observedAt\":").Append(BridgeJson.EscapeString(ObservedAt));
            sb.Append('}');
        }

        /// <summary>Serialize to a JSON string (convenience wrapper over
        /// <see cref="AppendJsonTo"/>).</summary>
        public string ToJsonString()
        {
            var sb = new StringBuilder(128);
            AppendJsonTo(sb);
            return sb.ToString();
        }

        /// <summary>Current ISO-8601 UTC timestamp (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>). Used by the
        /// handler to stamp <see cref="ObservedAt"/> at snapshot time.</summary>
        internal static string NowObservedAt()
            => System.DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
