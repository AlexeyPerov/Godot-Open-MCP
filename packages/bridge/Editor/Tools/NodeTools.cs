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

        /// <summary>The MCP tool name for the node creator (P2.3). First mutating node tool.</summary>
        internal const string NodeCreateToolName = "godot_open_mcp_node_create";

        /// <summary>
        /// Register the node tool family. P2.2 adds the read-only <c>godot_open_mcp_node_find</c>
        /// (no gate path, group <c>node</c>); P2.3 adds the mutating <c>godot_open_mcp_node_create</c>
        /// (group <c>node</c>, default gate <c>off</c> — the gate flow lands in P3.5 and is a no-op
        /// until then). Registered once at plugin enable; safe to call again on re-enable (the
        /// registry is idempotent). Later node tools (modify/set-parent/...) register alongside
        /// these from <c>GodotOpenMcpPlugin._EnterTree</c>.
        /// </summary>
        internal static void RegisterNodeTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: NodeFindToolName,
                isMutating: false,
                defaultGate: "off",
                group: "node",
                handler: Find));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: NodeCreateToolName,
                isMutating: true,
                defaultGate: "off",
                group: "node",
                handler: Create));
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

        // --- godot_open_mcp_node_create (P2.3) --------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_node_create</c>. The first mutating node tool. Two creation
        /// modes, adapted from Godot-MCP's <c>Tool_Node.Create</c> (behavior reference, read-only):
        /// <list type="bullet">
        /// <item><description><c>instance_scene_path</c> (priority) — <c>ResourceLoader.Load&lt;PackedScene&gt;</c>
        /// then <c>Instantiate()</c>.</description></item>
        /// <item><description><c>type_class_name</c> (fallback, default <c>"Node"</c>) —
        /// <c>ClassDB.Instantiate</c> after <c>ClassDB.ClassExists</c> / <c>CanInstantiate</c>
        /// validation.</description></item>
        /// </list>
        ///
        /// <para>
        /// The new node is parented to the edited scene root (or an optional parent path), its
        /// <c>Owner</c> set to the edited scene root, and — for instanced sub-trees — descendants
        /// without an owner get the scene root as owner via <see cref="SetOwnerRecursive"/>. Owner
        /// assignment is the Godot-specific step that makes the node persist in <c>.tscn</c> on save;
        /// Unity has no equivalent (a GameObject in a scene is saved implicitly). The scene is marked
        /// unsaved (<c>EditorInterface.MarkSceneAsUnsaved</c>) and the new node selected.
        /// </para>
        ///
        /// <para>
        /// <b>No gate yet.</b> P2.3 ships the mutating handler without gate wrapping (gate flow is
        /// P3.5); the request-level <c>gate</c> and <c>paths_hint</c> args are accepted by the schema
        /// for forward-compat but are no-ops here. There is no editor Undo registration yet — agents
        /// should rely on the (future) gate checkpoint for reversibility until P3.5 lands.
        /// </para>
        ///
        /// Structured failures: <c>no_edited_scene</c>, <c>parent_not_found</c>, <c>invalid_type</c>,
        /// <c>invalid_scene_path</c>, <c>create_failed</c>. Must not throw — exceptions are caught by
        /// the dispatcher and surfaced as <c>execution_error</c>.
        /// </summary>
        internal static ToolDispatchResult Create(string body)
        {
            var request = NodeCreateBody.Parse(body);

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
            {
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling node_create.");
            }

            // Resolve the parent (default: edited scene root). A non-empty parent_node_path that
            // does not resolve is a hard error — creating a node under a phantom parent would leave
            // it unparented to the root silently, masking the agent's intent.
            Node parent = root;
            if (!string.IsNullOrEmpty(request.ParentNodePath))
            {
                parent = ResolveParent(root, request.ParentNodePath!);
                if (parent == null)
                {
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Parent node not found at path '{request.ParentNodePath}'.");
                }
            }

            // Build the node. Instanced scene takes precedence over typed instantiation, matching
            // Godot-MCP's Tool_Node.Create precedence and Unity's primitive-vs-empty branching.
            Node? node;
            if (request.IsInstanceScene)
            {
                var scenePath = request.InstanceScenePath!;
                if (!ResourceLoader.Exists(scenePath))
                {
                    return ToolDispatchResult.Fail(
                        "invalid_scene_path",
                        $"Scene resource not found at '{scenePath}'.");
                }
                var packed = ResourceLoader.Load<PackedScene>(scenePath);
                if (packed == null)
                {
                    // ResourceLoader.Exists was true but Load<PackedScene> returned null — the
                    // resource exists but is not a PackedScene (e.g. a .tres that is a Material).
                    return ToolDispatchResult.Fail(
                        "invalid_scene_path",
                        $"Resource at '{scenePath}' is not a PackedScene.");
                }
                try
                {
                    node = packed.Instantiate();
                }
                catch (System.Exception e)
                {
                    return ToolDispatchResult.Fail("create_failed",
                        $"Failed to instantiate PackedScene '{scenePath}': {e.Message}");
                }
            }
            else
            {
                var className = request.EffectiveTypeClassName;
                if (!ClassDB.ClassExists(className))
                {
                    return ToolDispatchResult.Fail(
                        "invalid_type",
                        $"Unknown Godot class '{className}'.");
                }
                if (!ClassDB.CanInstantiate(className))
                {
                    // ClassDB knows the class but it cannot be instantiated directly (abstract, or
                    // a singleton like OS / ClassDB itself). Fail with a distinct message so an
                    // agent can pick a concrete subclass.
                    return ToolDispatchResult.Fail(
                        "invalid_type",
                        $"Class '{className}' exists but cannot be instantiated.");
                }
                try
                {
                    var variant = ClassDB.Instantiate(className);
                    node = variant.As<Node>();
                }
                catch (System.Exception e)
                {
                    return ToolDispatchResult.Fail("create_failed",
                        $"Failed to instantiate class '{className}': {e.Message}");
                }
            }

            if (node == null)
            {
                // Instantiate returned something that did not coerce to Node — treat as a creation
                // failure rather than dereferencing null below.
                return ToolDispatchResult.Fail("create_failed",
                    "Instantiation succeeded but the result was not a Node.");
            }

            // Name is optional — Godot assigns a default name for the type when omitted.
            if (!string.IsNullOrEmpty(request.Name))
                node.Name = request.Name;

            try
            {
                parent.AddChild(node);
            }
            catch (System.Exception e)
            {
                // AddChild can fault (e.g. a node already has a parent, or a parent that rejects the
                // child). Free the orphaned node so it is not leaked into the SceneTree without an
                // owner.
                node.QueueFree();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add node to parent: {e.Message}");
            }

            // Owner must be the edited scene root for the node (and its sub-tree) to be persisted on
            // save. SetOwnerRecursive covers instanced PackedScene descendants that don't already
            // have an owner (nodes internal to an instanced scene keep their own owner). Adapted
            // from Godot-MCP's Tool_Node.Create (copy fidelity — Owner semantics are Godot-specific).
            node.Owner = root;
            SetOwnerRecursive(node, root);

            ApplyTransform(node, request);

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            EditorInterface.Singleton.EditNode(node);

            return ToolDispatchResult.Ok(ToNodeData(node).ToJsonString());
        }

        /// <summary>
        /// Resolve a parent path against the edited scene root. Reuses
        /// <see cref="NodePathNormalizer"/> so the same path forms as <c>node_find</c> are accepted
        /// (<c>Main</c>, <c>Main/Player</c>, <c>/root/Main/Player</c>, <c>.</c> for the root).
        /// Returns null when the path does not resolve. Must run on the main thread.
        /// </summary>
        static Node? ResolveParent(Node editedRoot, string parentPath)
        {
            var path = NodePathNormalizer.Normalize(parentPath, editedRoot.Name.ToString());
            if (string.IsNullOrEmpty(path) || path == ".")
                return editedRoot;
            return editedRoot.GetNodeOrNull(path);
        }

        /// <summary>
        /// Set the scene-root owner on a node's descendants so an instanced sub-tree is saved inline
        /// with the scene. Skips descendants that already have an owner (e.g. nodes internal to an
        /// instanced PackedScene that should remain owned by their own scene). Adapted verbatim from
        /// Godot-MCP's <c>Tool_Node.Create.SetOwnerRecursive</c> (copy fidelity). Must run on the
        /// main thread.
        /// </summary>
        static void SetOwnerRecursive(Node node, Node owner)
        {
            int count = node.GetChildCount(includeInternal: false);
            for (int i = 0; i < count; i++)
            {
                var child = node.GetChild(i, includeInternal: false);
                if (child == null)
                    continue;
                if (child.Owner == null)
                {
                    child.Owner = owner;
                    SetOwnerRecursive(child, owner);
                }
            }
        }

        /// <summary>
        /// Apply optional <c>position</c> / <c>rotation</c> / <c>scale</c> fields. Only
        /// <c>Node3D</c> and <c>Node2D</c> carry spatial transforms; a non-spatial node (plain
        /// <c>Node</c>, <c>Control</c> without a 2D/3D base) silently ignores transform fields
        /// rather than erroring — an agent creating a <c>Node</c> with an incidental position field
        /// should not see a hard failure. Rotation is in degrees (matches the editor Inspector).
        /// Transform parsing is best-effort: a malformed vector string is ignored (the node is
        /// still created with its default transform), surfaced to the agent only via the result's
        /// unmodified transform. Must run on the main thread.
        /// </summary>
        static void ApplyTransform(Node node, NodeCreateBody request)
        {
            if (node is Node3D n3d)
            {
                if (TryParseVector3(request.Position, out var pos, defaultZ: 0f))
                    n3d.Position = pos;
                if (TryParseVector3(request.Rotation, out var rotDeg, defaultZ: 0f))
                    n3d.RotationDegrees = rotDeg;
                if (TryParseVector3(request.Scale, out var scl, defaultZ: 1f))
                    n3d.Scale = scl;
            }
            else if (node is Node2D n2d)
            {
                if (TryParseVector2(request.Position, out var pos))
                    n2d.Position = pos;
                if (TryParseVector2(request.Rotation, out var rotDeg))
                    n2d.RotationDegrees = rotDeg;
                if (TryParseVector2(request.Scale, out var scl, defaultXY: 1f))
                    n2d.Scale = scl;
            }
        }

        /// <summary>
        /// Parse a <c>"x,y,z"</c> string into a <see cref="Vector3"/>. Accepts 2-component input
        /// (<c>"x,y"</c>) by filling the Z slot with <paramref name="defaultZ"/>. Returns false
        /// (leaving <paramref name="v"/> at origin) on a malformed string — the caller treats that
        /// as "no transform applied" rather than erroring. Invariant-culture so a locale with a
        /// comma decimal separator does not corrupt parsing.
        /// </summary>
        static bool TryParseVector3(string? text, out Vector3 v, float defaultZ)
        {
            v = Vector3.Zero;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var parts = text!.Trim().Split(',');
            if (parts.Length < 2)
                return false;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return false;
            float z = defaultZ;
            if (parts.Length >= 3 &&
                float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedZ))
                z = parsedZ;
            v = new Vector3(x, y, z);
            return true;
        }

        /// <summary>
        /// Parse a <c>"x,y"</c> string into a <see cref="Vector2"/>. Returns false on a malformed
        /// string. <paramref name="defaultXY"/> seeds both components for the scale case where a
        /// single value should broadcast (0 for position/rotation, 1 for scale — the caller passes
        /// the right default).
        /// </summary>
        static bool TryParseVector2(string? text, out Vector2 v, float defaultXY = 0f)
        {
            v = new Vector2(defaultXY, defaultXY);
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var parts = text!.Trim().Split(',');
            if (parts.Length < 2)
                return false;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return false;
            v = new Vector2(x, y);
            return true;
        }
    }
}
#endif
