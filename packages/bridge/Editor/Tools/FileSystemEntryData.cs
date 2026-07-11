#nullable enable
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// One entry in a <c>godot_open_mcp_filesystem_list</c> result (P4.4) — a directory or a file
    /// inside a single level of a <c>res://</c> directory. Carries the entry's leaf name, its full
    /// <c>res://</c> path (directories in trailing-slash form), a directory/file flag, and — for
    /// files only — the importer-assigned resource type and the <c>uid://</c> (when assigned). The
    /// Godot analog of Unity Open MCP's per-asset listing hit (name, path, kind, type, GUID) — the
    /// Unity asset-path/GUID identity becomes the Godot <c>res://</c> path plus an optional UID.
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>): the on-editor
    /// <see cref="FileSystemTools"/> handler walks <c>EditorFileSystemDirectory</c> on the main
    /// thread and populates these records; this type only holds data and knows how to serialize
    /// itself via <see cref="BridgeJson"/>. The serialization path is therefore unit-testable in
    /// the binary-less xUnit host with no live Godot filesystem.
    /// </para>
    ///
    /// <para>
    /// <b>Adapted from Godot-MCP's <c>FileSystemEntry</c></b> (behavior reference): the
    /// name/path/isDirectory/resourceType/uid field set is lifted from there. The JSON field order
    /// is fixed (name, path, isDirectory, resourceType, uid) so diffing clients don't flap on
    /// reordering; directories emit <c>resourceType:null</c> and <c>uid:null</c>.
    /// </para>
    /// </summary>
    internal sealed class FileSystemEntryData
    {
        /// <summary>Leaf name of the entry (last path segment; for directories the trailing slash
        /// is stripped).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Full <c>res://</c> path. Directories are in trailing-slash form
        /// (<c>res://materials/sub/</c>); files are not (<c>res://materials/wood.tres</c>).</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>True for a directory, false for a file. Drives whether
        /// <see cref="ResourceType"/>/<see cref="Uid"/> are populated.</summary>
        public bool IsDirectory { get; set; }

        /// <summary>For files: the Godot type the importer recorded (e.g.
        /// <c>StandardMaterial3D</c>, <c>PackedScene</c>), or null when unknown. Always null for
        /// directories. Read straight from the editor filesystem index — no resource is
        /// loaded.</summary>
        public string? ResourceType { get; set; }

        /// <summary>For files: the <c>uid://</c> text identifier when the importer assigned one,
        /// or null. Always null for directories. Resolved via <c>ResourceLoader.GetResourceUid</c>
        /// + <c>ResourceUid.IdToText</c> (no load).</summary>
        public string? Uid { get; set; }

        /// <summary>Append this entry as a JSON object. Field order is fixed (name, path,
        /// isDirectory, resourceType, uid) for stable diffs. Directories emit null for the
        /// file-only fields.</summary>
        internal void AppendJsonTo(StringBuilder sb)
        {
            sb.Append('{');
            sb.Append("\"name\":").Append(BridgeJson.EscapeString(Name)).Append(',');
            sb.Append("\"path\":").Append(BridgeJson.EscapeString(Path)).Append(',');
            sb.Append("\"isDirectory\":").Append(IsDirectory ? "true" : "false").Append(',');
            sb.Append("\"resourceType\":").Append(BridgeJson.EscapeString(ResourceType)).Append(',');
            sb.Append("\"uid\":").Append(BridgeJson.EscapeString(Uid));
            sb.Append('}');
        }

        public string ToJsonString()
        {
            var sb = new StringBuilder(96);
            AppendJsonTo(sb);
            return sb.ToString();
        }
    }
}
