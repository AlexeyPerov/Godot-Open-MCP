#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Structured snapshot of the Godot editor's current node selection — returned by
    /// <c>godot_open_mcp_editor_selection_get</c> and embedded in
    /// <c>godot_open_mcp_editor_selection_set</c> results (P4.6).
    ///
    /// <para>
    /// Godot's <c>EditorSelection</c> (obtained from <c>EditorInterface.GetSelection()</c>) selects
    /// scene-tree <c>Node</c>s only — there is no Unity-style asset-GUID / Transform / Component
    /// selection distinction, and no first-class "active object". So this model carries a flat list of
    /// selected nodes (each as a <see cref="NodeData"/>, built on the main thread from the live node)
    /// plus a convenience pointer to the "active" one. Godot has no first-class active-object concept;
    /// the LAST selected node is reported as active, matching how the editor inspector tracks the
    /// most-recently-clicked node (adapted from the Godot-MCP behavior reference
    /// <c>SelectionData</c>).
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the DTO's serialization is
    /// unit-testable in the binary-less xUnit host. The on-editor handler
    /// (<see cref="EditorSelectionTools"/>) populates it from <c>EditorInterface</c> on the main
    /// thread; this type only holds data and knows how to serialize itself via
    /// <see cref="BridgeJson"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Intentional deltas from Unity</b> (<c>packages/bridge/AGENTS.md</c> §Unity-first): Unity's
    /// <c>selection-get</c> carries asset GUIDs, instance IDs, and component references. The Godot DTO
    /// is node-only — no asset/component/global-object fields. <see cref="ActiveNode"/> is the
    /// last-selected node because Godot has no equivalent explicit active object.
    /// </para>
    /// </summary>
    internal sealed class EditorSelectionData
    {
        /// <summary>All currently-selected scene-tree nodes, in selection order. Each entry is a
        /// shallow <see cref="NodeData"/> (<c>hierarchyDepth: 0</c>, children null).</summary>
        public List<NodeData> Nodes { get; set; } = new();

        /// <summary>The active (last-selected) node, or null when the selection is empty. Godot has no
        /// first-class active-object concept; the last selected node is reported here.</summary>
        public NodeData? ActiveNode { get; set; }

        /// <summary>Number of selected nodes.</summary>
        public int Count { get; set; }

        /// <summary>The <c>res://</c> path of the active edited scene, or null when no scene is being
        /// edited.</summary>
        public string? ScenePath { get; set; }

        /// <summary>Only emitted by the set handler: true when the previous selection was cleared
        /// (always true for a replace operation). Null for the get handler (field omitted).</summary>
        public bool? Cleared { get; set; }

        /// <summary>
        /// Append this selection as a JSON object. Field order is fixed (nodes, activeNode, count,
        /// scenePath, [cleared]) so diffing clients don't flap on reordering. Strings flow through
        /// <see cref="BridgeJson"/> for escaping; the nodes list recurses through each
        /// <see cref="NodeData.AppendJsonTo"/>.
        /// </summary>
        internal void AppendJsonTo(StringBuilder sb)
        {
            sb.Append('{');
            sb.Append("\"nodes\":");
            sb.Append('[');
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Nodes[i].AppendJsonTo(sb);
            }
            sb.Append(']');
            sb.Append(",\"activeNode\":");
            ActiveNode?.AppendJsonTo(sb);
            if (ActiveNode == null) sb.Append("null");
            sb.Append(",\"count\":").Append(Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"scenePath\":").Append(BridgeJson.EscapeString(ScenePath));
            if (Cleared.HasValue)
                sb.Append(",\"cleared\":").Append(Cleared.Value ? "true" : "false");
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
    }
}
