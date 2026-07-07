#nullable enable
using System.Collections.Generic;
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Structured snapshot of a Godot <c>Node</c> returned by the node tool family. Holds no live
    /// <c>Node</c> handle — it is built on the main thread from a resolved node and then serialized
    /// off the main thread, so it touches no Godot native object once constructed.
    ///
    /// <para>
    /// Godot has no Unity-style <c>Component</c> concept: a node IS its type plus its built-in
    /// properties plus an optional attached script. So <see cref="Type"/> carries the node's class
    /// name (e.g. <c>"Node3D"</c>) and <see cref="ScriptResourcePath"/> carries the <c>res://</c>
    /// path of the attached script if any — together they model what a Unity GameObject would
    /// express as a list of components. This is the Godot analog of Unity Open MCP's per-GameObject
    /// summary built by <c>BuildGameObjectSummary</c>.
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so it is unit-testable in the
    /// binary-less xUnit host. The on-editor <see cref="NodeTools.ToNodeData"/> populates it; this
    /// type only holds data and knows how to serialize itself via <see cref="AppendJsonTo"/> (the
    /// bridge carries no System.Text.Json / Newtonsoft dependency per
    /// <c>packages/bridge/AGENTS.md</c> §Transport, so the DTO serializes itself with
    /// <see cref="BridgeJson"/>).
    /// </para>
    /// </summary>
    internal sealed class NodeData
    {
        /// <summary>Instance id of the Node (Godot <c>GodotObject.GetInstanceId()</c>). Stable
        /// identity within the session.</summary>
        public ulong InstanceId { get; set; } = 0;

        /// <summary>Node name (the last segment of its scene-tree path).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Absolute scene-tree path of the Node, e.g. <c>/root/Main/Player</c>.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Godot class name of the Node, e.g. <c>Node3D</c>, <c>Sprite2D</c>. The Godot
        /// analog of a Unity component set.</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary><c>res://</c> path of the script attached to the Node, or null when no script
        /// is attached.</summary>
        public string? ScriptResourcePath { get; set; } = null;

        /// <summary>Number of direct children of the Node (excluding internal children).</summary>
        public int ChildCount { get; set; } = 0;

        /// <summary>Direct/recursive children, populated only when a hierarchy depth &gt; 0 was
        /// requested. Null when no hierarchy was requested.</summary>
        public List<NodeData>? Children { get; set; } = null;

        /// <summary>
        /// Append this node as a JSON object to <paramref name="sb"/>. Field order is fixed
        /// (instanceId, name, path, type, scriptResourcePath, childCount, children) so diffing
        /// clients don't flap on reordering. Strings flow through <see cref="BridgeJson"/> for
        /// escaping; the children list, when present, recurses through the same writer.
        /// </summary>
        internal void AppendJsonTo(StringBuilder sb)
        {
            sb.Append('{');
            sb.Append("\"instanceId\":").Append(InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"name\":").Append(BridgeJson.EscapeString(Name)).Append(',');
            sb.Append("\"path\":").Append(BridgeJson.EscapeString(Path)).Append(',');
            sb.Append("\"type\":").Append(BridgeJson.EscapeString(Type)).Append(',');
            sb.Append("\"scriptResourcePath\":").Append(BridgeJson.EscapeString(ScriptResourcePath)).Append(',');
            sb.Append("\"childCount\":").Append(ChildCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"children\":");
            if (Children == null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append('[');
                for (int i = 0; i < Children.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Children[i].AppendJsonTo(sb);
                }
                sb.Append(']');
            }
            sb.Append('}');
        }

        /// <summary>Serialize this node as a standalone JSON object string.</summary>
        public string ToJsonString()
        {
            var sb = new StringBuilder(128);
            AppendJsonTo(sb);
            return sb.ToString();
        }
    }
}
