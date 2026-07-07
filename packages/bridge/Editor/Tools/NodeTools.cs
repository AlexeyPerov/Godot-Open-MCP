#if TOOLS
#nullable enable
using System.Collections.Generic;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Node tool family — the Godot analog of Unity Open MCP's
    /// <c>TypedTools/GameObjectsTools.cs</c>. P2.2 implements only the read-only
    /// <c>godot_open_mcp_node_find</c> handler; later phases (P2.3 create, P2.4 modify, P2.5
    /// set-parent/duplicate/destroy) add their handlers to this same partial class.
    ///
    /// <para>
    /// Godot ↔ Unity mapping: <c>Node</c> ↔ <c>GameObject</c>; a node's class name + attached
    /// script ↔ a GameObject's component set;
    /// <c>EditorInterface.Singleton.GetEditedSceneRoot()</c> ↔ the active scene root. Node identity
    /// is resolved by <see cref="ResolveNode"/> using either a scene-tree path
    /// (<c>/root/Main/Player</c>, <c>Main/Player</c>, or <c>.</c> for the root) or a bare name (first
    /// match) — see the <emph>Intentional deltas from Unity</emph> note below for why Godot has no
    /// cross-scene <c>instance_id</c> resolver yet.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): the handlers touch <see cref="EditorInterface"/> and live
    /// <see cref="Node"/> objects, neither of which exists in a plain (non-editor) build. The
    /// pure-managed pieces (<see cref="NodeData"/>, <see cref="NodeFindBody"/>,
    /// <see cref="NodePathNormalizer"/>) live outside this guard and are unit-tested.
    /// </summary>
    internal static partial class NodeTools
    {
        /// <summary>The MCP tool name for the read-only node locator (P2.2).</summary>
        internal const string NodeFindToolName = "godot_open_mcp_node_find";

        /// <summary>
        /// Register the P2.2 <c>godot_open_mcp_node_find</c> tool. Read-only (no gate path), group
        /// <c>node</c>. Registered once at plugin enable; safe to call again on re-enable (the
        /// registry is idempotent). Later node tools (create/modify/...) register alongside this
        /// one from <c>GodotOpenMcpPlugin._EnterTree</c>.
        /// </summary>
        internal static void RegisterNodeTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: NodeFindToolName,
                isMutating: false,
                defaultGate: "off",
                group: "node",
                handler: Find));
        }

        // --- godot_open_mcp_node_find ------------------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_node_find</c>. Two modes, mirroring Unity Open MCP's
        /// <c>gameobject_find</c> shape adapted to Godot node semantics:
        /// <list type="bullet">
        /// <item><description>Targeted (any of <c>node_path</c> / <c>name</c> set) → resolve one
        /// node, return a single-element <c>nodes</c> array (empty + <c>notFound:true</c> when the
        /// target is not in the edited scene).</description></item>
        /// <item><description>List (neither set) → walk the edited scene, apply the
        /// <c>type</c> / <c>name_contains</c> filters, cap at <c>max_results</c>, and report the
        /// remainder count in <c>truncated</c>.</description></item>
        /// </list>
        /// Structured failures (<c>no_edited_scene</c>) surface as <see cref="ToolDispatchResult.Fail"/>;
        /// a not-found target is NOT a failure (mirrors Unity: empty list + <c>notFound:true</c>).
        /// Must not throw — exceptions are caught by the dispatcher and surfaced as
        /// <c>execution_error</c>.
        /// </summary>
        internal static ToolDispatchResult Find(string body)
        {
            var request = NodeFindBody.Parse(body);

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
            {
                // No edited scene is a hard structured error — there is nothing to search, targeted
                // or list. Distinct from "the scene is open but the named node isn't there" (which
                // is notFound:true, below).
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling node_find.");
            }

            if (request.IsTargeted)
            {
                var node = ResolveNode(root, request);
                if (node == null)
                {
                    // Mirrors Unity: a targeted lookup that misses is an empty list with notFound
                    // set, NOT a failure — an agent checking "does this node exist?" branches on
                    // the flag, not on ok:false.
                    return ToolDispatchResult.Ok(BuildResultJson(null, 0, notFound: true));
                }
                var data = ToNodeData(node, request.HierarchyDepth);
                return ToolDispatchResult.Ok(BuildResultJson(data, count: 1, notFound: false));
            }

            // List mode: walk the edited scene, filter, cap.
            var matches = new List<Node>();
            CollectMatchingNodes(root, request, matches);

            int total = matches.Count;
            int cap = request.MaxResults;
            int returned = total > cap ? cap : total;
            int truncated = total - returned;

            var sb = new StringBuilder(256);
            sb.Append("{\"nodes\":[");
            for (int i = 0; i < returned; i++)
            {
                if (i > 0) sb.Append(',');
                ToNodeData(matches[i], request.HierarchyDepth).AppendJsonTo(sb);
            }
            sb.Append("],\"count\":").Append(returned);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        /// <summary>
        /// Resolve a targeted <see cref="NodeFindBody"/> to a live node in the edited scene. Must
        /// be called on the main thread. Resolution order: <see cref="NodeFindBody.NodePath"/>
        /// (priority 1) then <see cref="NodeFindBody.Name"/> (priority 2 — first match). Returns
        /// null when neither resolver hits. Adapted from Godot-MCP's <c>Tool_Node.ResolveNode</c>;
        /// see <emph>Intentional deltas from Unity</emph> in the class doc for why the Unity
        /// <c>instance_id</c> resolver is not ported in P2.2.
        /// </summary>
        internal static Node? ResolveNode(Node editedRoot, NodeFindBody request)
        {
            // 1) node_path (priority 1).
            if (!string.IsNullOrEmpty(request.NodePath))
            {
                var path = NodePathNormalizer.Normalize(request.NodePath, editedRoot.Name.ToString());
                if (string.IsNullOrEmpty(path) || path == ".")
                    return editedRoot;
                var node = editedRoot.GetNodeOrNull(path);
                if (node != null)
                    return node;
                // Fall through to name lookup only if a name was also provided — a path that
                // doesn't resolve is a hard miss otherwise.
                if (string.IsNullOrEmpty(request.Name))
                    return null;
            }

            // 2) name (priority 2, first match).
            if (!string.IsNullOrEmpty(request.Name))
                return FindByName(editedRoot, request.Name);

            return null;
        }

        /// <summary>
        /// Build a structured <see cref="NodeData"/> from a live node. When
        /// <paramref name="hierarchyDepth"/> is &gt; 0, recursively populates
        /// <see cref="NodeData.Children"/> up to that depth (0 = the node only, children null). Must
        /// be called on the main thread. Adapted from Godot-MCP's <c>Tool_Node.ToNodeData</c>.
        /// </summary>
        internal static NodeData ToNodeData(Node node, int hierarchyDepth = 0)
        {
            var data = new NodeData
            {
                InstanceId = node.GetInstanceId(),
                Name = node.Name.ToString(),
                Path = node.GetPath().ToString(),
                Type = node.GetClass(),
                ScriptResourcePath = GetAttachedScriptPath(node),
                ChildCount = node.GetChildCount(includeInternal: false),
            };

            if (hierarchyDepth > 0)
            {
                data.Children = new List<NodeData>(data.ChildCount);
                for (int i = 0; i < data.ChildCount; i++)
                {
                    var child = node.GetChild(i, includeInternal: false);
                    if (child != null)
                        data.Children.Add(ToNodeData(child, hierarchyDepth - 1));
                }
            }

            return data;
        }

        // --- helpers -----------------------------------------------------------------

        /// <summary>
        /// Recursively collect nodes under <paramref name="root"/> (inclusive of root) that match
        /// the request's <c>type</c> and <c>name_contains</c> filters. Internal children are
        /// skipped (Godot hides editor-only helpers behind the <c>includeInternal</c> flag, and an
        /// agent never wants them). The caller caps and truncates after the walk.
        /// </summary>
        static void CollectMatchingNodes(Node root, NodeFindBody request, List<Node> matches)
        {
            if (Matches(root, request))
                matches.Add(root);

            int childCount = root.GetChildCount(includeInternal: false);
            for (int i = 0; i < childCount; i++)
            {
                var child = root.GetChild(i, includeInternal: false);
                if (child != null)
                    CollectMatchingNodes(child, request, matches);
            }
        }

        static bool Matches(Node node, NodeFindBody request)
        {
            if (!string.IsNullOrEmpty(request.Type))
            {
                var cls = node.GetClass();
                // Case-sensitive match on the full class name — Godot class names are PascalCase
                // and case matters for script-attached subclasses. A bare substring match would
                // surprise an agent filtering on "Node3D" by also returning "Node3D2" (there is
                // none, but the principle holds).
                if (cls != request.Type)
                    return false;
            }
            if (!string.IsNullOrEmpty(request.NameContains))
            {
                var nodeName = node.Name.ToString();
                if (nodeName.IndexOf(request.NameContains, System.StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
            return true;
        }

        /// <summary>Depth-first first-match by name, mirroring Unity's
        /// <c>TypedTargets.FindByName</c>. Excludes internal children.</summary>
        static Node? FindByName(Node root, string name)
        {
            if (root.Name.ToString() == name)
                return root;
            int childCount = root.GetChildCount(includeInternal: false);
            for (int i = 0; i < childCount; i++)
            {
                var child = root.GetChild(i, includeInternal: false);
                if (child == null) continue;
                var found = FindByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>res:// path of the script attached to <paramref name="node"/>, or null when
        /// none. Adapted from Godot-MCP's <c>GetAttachedScriptPath</c>.</summary>
        static string? GetAttachedScriptPath(Node node)
        {
            var scriptVariant = node.GetScript();
            if (scriptVariant.VariantType == Variant.Type.Nil)
                return null;

            var script = scriptVariant.As<Script>();
            var resourcePath = script?.ResourcePath;
            return string.IsNullOrEmpty(resourcePath) ? null : resourcePath;
        }

        /// <summary>
        /// Build the targeted-mode result JSON. <paramref name="data"/> is null in the not-found
        /// case (empty array + notFound:true); otherwise a single-element array.
        /// </summary>
        static string BuildResultJson(NodeData? data, int count, bool notFound)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"nodes\":[");
            data?.AppendJsonTo(sb);
            sb.Append("],\"count\":").Append(count);
            sb.Append(",\"truncated\":0");
            if (notFound)
                sb.Append(",\"notFound\":true");
            sb.Append('}');
            return sb.ToString();
        }
    }
}
#endif
