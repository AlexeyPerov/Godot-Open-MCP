#if TOOLS
#nullable enable
using System.Collections.Generic;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Node tool family — the Godot analog of Unity Open MCP's
    /// <c>TypedTools/GameObjectsTools.cs</c>. P2.2 implemented the read-only
    /// <c>godot_open_mcp_node_find</c> handler; P2.3 added the mutating <c>node_create</c>; P2.4
    /// adds <c>node_modify</c> (property mutation); P2.5 adds the three tree-structure mutators
    /// (<c>set_parent</c>, <c>duplicate</c>, <c>delete</c>) to this same partial class.
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

        /// <summary>The MCP tool name for the node property mutator (P2.4).</summary>
        internal const string NodeModifyToolName = "godot_open_mcp_node_modify";

        /// <summary>The MCP tool name for the reparent mutator (P2.5).</summary>
        internal const string NodeSetParentToolName = "godot_open_mcp_node_set_parent";

        /// <summary>The MCP tool name for the duplicate mutator (P2.5).</summary>
        internal const string NodeDuplicateToolName = "godot_open_mcp_node_duplicate";

        /// <summary>The MCP tool name for the delete mutator (P2.5).</summary>
        internal const string NodeDeleteToolName = "godot_open_mcp_node_delete";

        /// <summary>
        /// Register the node tool family. P2.2 adds the read-only <c>godot_open_mcp_node_find</c>
        /// (no gate path, group <c>node</c>); P2.3 adds the mutating <c>godot_open_mcp_node_create</c>
        /// (group <c>node</c>, default gate <c>off</c> — the gate flow lands in P3.5 and is a no-op
        /// until then); P2.4 adds <c>godot_open_mcp_node_modify</c> (single + batch property mutation);
        /// P2.5 adds the three tree-structure mutators (<c>set_parent</c>, <c>duplicate</c>,
        /// <c>delete</c>). Registered once at plugin enable; safe to call again on re-enable (the
        /// registry is idempotent).
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
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: NodeModifyToolName,
                isMutating: true,
                defaultGate: "off",
                group: "node",
                handler: Modify));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: NodeSetParentToolName,
                isMutating: true,
                defaultGate: "off",
                group: "node",
                handler: SetParent));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: NodeDuplicateToolName,
                isMutating: true,
                defaultGate: "off",
                group: "node",
                handler: Duplicate));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: NodeDeleteToolName,
                isMutating: true,
                defaultGate: "off",
                group: "node",
                handler: Delete));
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

        /// <summary>
        /// Parse a <c>"r,g,b,a"</c> / <c>"r,g,b"</c> string (0–1 floats) into a <see cref="Color"/>.
        /// Accepts 3-component input by filling alpha with 1. Returns false on a malformed string.
        /// Invariant-culture so a comma-decimal locale does not corrupt parsing. Shared by the modify
        /// handler's <c>modulate</c> coercion.
        /// </summary>
        static bool TryParseColor(string? text, out Color c)
        {
            c = Colors.White;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var parts = text!.Trim().Split(',');
            if (parts.Length < 3)
                return false;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var r)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var g)) return false;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var b)) return false;
            float a = 1f;
            if (parts.Length >= 4 &&
                float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedA))
                a = parsedA;
            c = new Color(r, g, b, a);
            return true;
        }

        // --- godot_open_mcp_node_modify (P2.4) --------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_node_modify</c>. Applies property updates to one or more nodes
        /// in the edited scene. Adapted from Unity Open MCP's <c>GameObjectsTools.Modify</c> (adapt
        /// fidelity) and Godot-MCP's <c>Tool_Node.Modify</c> (behavior reference): Unity mutates
        /// component property paths; Godot sets properties on the node directly.
        ///
        /// <para>
        /// Two target shapes (at least one required — else <c>missing_parameter</c>):
        /// <list type="bullet">
        /// <item><description><c>node_path</c> (single) — one target.</description></item>
        /// <item><description><c>node_paths</c> (array) — batch; same property/transform fields applied
        /// to each resolved target.</description></item>
        /// </list>
        /// </para>
        ///
        /// <para>
        /// Property application surface (P2.4 scope — see <see cref="ApplyProperties"/> for the full
        /// coercion matrix):
        /// <list type="bullet">
        /// <item><description><c>properties</c> map — free-form <c>string→string</c> entries coerced to
        /// the right Godot type per key.</description></item>
        /// <item><description>Transform convenience fields <c>position</c> / <c>rotation</c> /
        /// <c>scale</c> at the top level (same shape as <c>node_create</c>) — applied only when the
        /// target is a <c>Node3D</c> / <c>Node2D</c>.</description></item>
        /// <item><description><c>name</c> — rename (applied per target; in a batch this can collide —
        /// the agent owns the naming).</description></item>
        /// </list>
        /// Unknown keys → collected as <c>unsupported_property</c> warnings (the batch does NOT abort);
        /// invalid values for known keys → <c>invalid_property_value</c> warnings (per-target, also
        /// non-aborting so a batch's good entries still land).
        /// </para>
        ///
        /// <para>
        /// <b>No gate yet.</b> P2.4 ships the mutating handler without gate wrapping (gate flow is
        /// P3.5); the request-level <c>gate</c> and <c>paths_hint</c> args are accepted by the schema
        /// for forward-compat but are no-ops here. There is no editor Undo registration yet.
        /// </para>
        ///
        /// Structured failures: <c>no_edited_scene</c>, <c>missing_parameter</c>. Per-target resolution
        /// misses are NOT failures — they surface as <c>node_not_found</c> warnings so the rest of the
        /// batch still applies (mirrors Unity's accumulate-don't-abort batch semantics). Must not throw
        /// — exceptions are caught by the dispatcher and surfaced as <c>execution_error</c>.
        /// </summary>
        internal static ToolDispatchResult Modify(string body)
        {
            var request = NodeModifyBody.Parse(body);

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
            {
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling node_modify.");
            }

            if (!request.HasTarget)
            {
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "node_modify requires 'node_path' (single) or 'node_paths' (array); neither was set.");
            }

            var nodes = new List<Node>();
            var warnings = new List<string>();

            foreach (var path in request.TargetPaths())
            {
                var node = ResolvePath(root, path);
                if (node == null)
                {
                    warnings.Add($"node_not_found: '{path}' skipped (not in edited scene).");
                    continue;
                }
                nodes.Add(node);
            }

            // Return NodeData[] for every resolved node (post-mutation) plus the warnings list. An empty
            // resolved set with all-miss warnings is still Ok (not a hard failure) — the agent reads the
            // warnings to see every target missed. This mirrors node_find's notFound contract.
            var sb = new StringBuilder(256);
            sb.Append("{\"nodes\":[");
            for (int i = 0; i < nodes.Count; i++)
            {
                if (i > 0) sb.Append(',');
                ApplyProperties(nodes[i], request, warnings);
                ToNodeData(nodes[i]).AppendJsonTo(sb);
            }
            sb.Append("],\"count\":").Append(nodes.Count);
            sb.Append(",\"warnings\":");
            AppendWarningsArray(sb, warnings);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        /// <summary>
        /// Resolve a scene-tree path against the edited root using the same
        /// <see cref="NodePathNormalizer"/> vocabulary as the find/create handlers. Returns null when
        /// the path does not resolve (the caller surfaces this as a warning, not a hard error).
        /// </summary>
        static Node? ResolvePath(Node editedRoot, string path)
        {
            var normalized = NodePathNormalizer.Normalize(path, editedRoot.Name.ToString());
            if (string.IsNullOrEmpty(normalized) || normalized == ".")
                return editedRoot;
            return editedRoot.GetNodeOrNull(normalized);
        }

        /// <summary>
        /// Apply the modify request's properties + transform convenience fields to a single node,
        /// appending <c>unsupported_property</c> / <c>invalid_property_value</c> warnings for anything
        /// that could not be applied. Non-aborting: a bad value on one key does not skip the rest.
        ///
        /// <para>
        /// P2.4 supported property matrix:
        /// <list type="bullet">
        /// <item><description><c>visible</c> (bool) — <see cref="CanvasItem.Visible"/> /
        /// <see cref="Node3D.Visible"/>.</description></item>
        /// <item><description><c>name</c> (string) — <see cref="Node.Name"/>.</description></item>
        /// <item><description><c>position</c> / <c>rotation</c> / <c>scale</c> (vector strings) —
        /// Node3D / Node2D transform.</description></item>
        /// <item><description><c>modulate</c> (Color string) — <see cref="CanvasItem.Modulate"/> (2D
        /// only).</description></item>
        /// <item><description>Anything else → <c>unsupported_property</c> warning.</description></item>
        /// </list>
        /// Properties from the <c>properties</c> map and the top-level transform/name fields are merged
        /// (top-level wins on a key collision, matching the schema's documented precedence). The scene
        /// is marked unsaved once at the <see cref="Modify"/> boundary when at least one target
        /// resolved.
        /// </para>
        /// </summary>
        static void ApplyProperties(Node node, NodeModifyBody request, List<string> warnings)
        {
            bool changed = false;

            // Merge the properties map with the top-level convenience fields. Top-level fields override
            // map entries with the same key (documented schema precedence) so an agent that sets both
            // gets a deterministic outcome.
            var merged = new Dictionary<string, string>(request.Properties, System.StringComparer.Ordinal);
            if (request.Position != null) merged["position"] = request.Position;
            if (request.Rotation != null) merged["rotation"] = request.Rotation;
            if (request.Scale != null) merged["scale"] = request.Scale;
            if (request.Name != null) merged["name"] = request.Name;

            foreach (var kv in merged)
            {
                var key = kv.Key;
                var val = kv.Value;
                switch (key)
                {
                    case "name":
                        // Node name rename — applied to every target in a batch (last writer owns
                        // collisions; the agent is responsible for unique naming in batch mode).
                        node.Name = val;
                        changed = true;
                        break;
                    case "visible":
                        if (TryParseBool(val, out var visible))
                        {
                            if (node is CanvasItem ci) { ci.Visible = visible; changed = true; }
                            else if (node is Node3D n3d) { n3d.Visible = visible; changed = true; }
                            else warnings.Add($"unsupported_property: '{key}' has no effect on node type '{node.GetClass()}' (not a CanvasItem/Node3D).");
                        }
                        else warnings.Add($"invalid_property_value: '{key}'='{val}' could not be parsed as bool.");
                        break;
                    case "modulate":
                        if (node is CanvasItem ciMod)
                        {
                            if (TryParseColor(val, out var color)) { ciMod.Modulate = color; changed = true; }
                            else warnings.Add($"invalid_property_value: '{key}'='{val}' could not be parsed as 'r,g,b[,a]'.");
                        }
                        else warnings.Add($"unsupported_property: '{key}' has no effect on node type '{node.GetClass()}' (not a CanvasItem).");
                        break;
                    case "position":
                    case "rotation":
                    case "scale":
                        // Transform fields handled in the Node3D/Node2D branch below to avoid a double
                        // parse — collect them and apply once.
                        break;
                    default:
                        warnings.Add($"unsupported_property: '{key}' is not a recognized P2.4 property (skipped).");
                        break;
                }
            }

            // Transform convenience fields (Node3D / Node2D only). Read from the merged map so a value
            // set via properties OR top-level is applied exactly once.
            if (node is Node3D n3)
            {
                if (merged.TryGetValue("position", out var pos3) && TryParseVector3(pos3, out var p3, defaultZ: 0f))
                { n3.Position = p3; changed = true; }
                if (merged.TryGetValue("rotation", out var rot3) && TryParseVector3(rot3, out var r3, defaultZ: 0f))
                { n3.RotationDegrees = r3; changed = true; }
                if (merged.TryGetValue("scale", out var scl3) && TryParseVector3(scl3, out var s3, defaultZ: 1f))
                { n3.Scale = s3; changed = true; }
            }
            else if (node is Node2D n2)
            {
                if (merged.TryGetValue("position", out var pos2) && TryParseVector2(pos2, out var p2))
                { n2.Position = p2; changed = true; }
                if (merged.TryGetValue("rotation", out var rot2) && TryParseVector2(rot2, out var r2))
                { n2.RotationDegrees = r2; changed = true; }
                if (merged.TryGetValue("scale", out var scl2) && TryParseVector2(scl2, out var s2, defaultXY: 1f))
                { n2.Scale = s2; changed = true; }
            }

            if (changed)
                EditorInterface.Singleton.MarkSceneAsUnsaved();
        }

        /// <summary>
        /// Parse a bool from a raw string value (the <c>properties</c> map ferries everything as
        /// strings, including JSON bare tokens <c>true</c>/<c>false</c>). Accepts common spellings
        /// case-insensitively.
        /// </summary>
        static bool TryParseBool(string raw, out bool value)
        {
            var t = raw.Trim();
            if (string.Equals(t, "true", System.StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
            if (string.Equals(t, "false", System.StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
            value = false;
            return false;
        }

        /// <summary>Append a string list as a JSON array of escaped strings.</summary>
        static void AppendWarningsArray(StringBuilder sb, List<string> warnings)
        {
            sb.Append('[');
            for (int i = 0; i < warnings.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(warnings[i]));
            }
            sb.Append(']');
        }

        // --- godot_open_mcp_node_set_parent (P2.5) ----------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_node_set_parent</c>. Reparents a node under a new parent in
        /// the edited scene. Adapted from Unity Open MCP's <c>GameObjectsTools.SetParent</c> (adapt)
        /// and Godot-MCP's <c>Tool_Node.SetParent</c> (behavior reference): Godot uses
        /// <see cref="Node.Reparent(Node, bool)"/> which handles remove + add + optional global-transform
        /// preservation in one call.
        ///
        /// <para>
        /// Cycle-safe: refuses to reparent the edited scene root, a node under itself, or a node under
        /// one of its own descendants (<c>would_create_cycle</c> / <c>cannot_reparent_root</c>).
        /// Updates <see cref="Node.Owner"/> on the moved sub-tree so the node still persists in the
        /// <c>.tscn</c> on save (Godot-specific — Unity has no equivalent).
        /// </para>
        ///
        /// <para>
        /// <b>No gate yet.</b> Same forward-compat <c>gate</c>/<c>paths_hint</c> no-op as the other P2
        /// mutators; no editor Undo yet.
        /// </para>
        ///
        /// Structured failures: <c>no_edited_scene</c>, <c>missing_parameter</c>,
        /// <c>node_not_found</c>, <c>parent_not_found</c>, <c>cannot_reparent_root</c>,
        /// <c>would_create_cycle</c>. Must not throw.
        /// </summary>
        internal static ToolDispatchResult SetParent(string body)
        {
            var nodePath = NodeModifyBody.Parse(body).NodePath;
            // Reuse the same body parser: it already extracts node_path + parent_node_path scalars.
            // We pull parent + keep_global_transform via targeted extraction to avoid a second body type.
            var parentPath = ExtractScalarFromBody(body, "parent_node_path");
            var keepGlobal = ExtractBoolFromBody(body, "keep_global_transform", defaultValue: true);

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
            {
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling node_set_parent.");
            }

            if (string.IsNullOrEmpty(nodePath))
            {
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "node_set_parent requires 'node_path' (the node to reparent).");
            }
            if (string.IsNullOrEmpty(parentPath))
            {
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "node_set_parent requires 'parent_node_path' (the new parent).");
            }

            var node = ResolvePath(root, nodePath!);
            if (node == null)
            {
                return ToolDispatchResult.Fail(
                    "node_not_found",
                    $"Node not found at path '{nodePath}'.");
            }

            if (node == root)
            {
                return ToolDispatchResult.Fail(
                    "cannot_reparent_root",
                    "Cannot reparent the edited scene root; close or replace the scene instead.");
            }

            var newParent = ResolvePath(root, parentPath!);
            if (newParent == null)
            {
                return ToolDispatchResult.Fail(
                    "parent_not_found",
                    $"New parent node not found at path '{parentPath}'.");
            }

            if (node == newParent)
            {
                return ToolDispatchResult.Fail(
                    "would_create_cycle",
                    "Cannot reparent a node under itself.");
            }
            if (IsAncestorOf(node, newParent))
            {
                return ToolDispatchResult.Fail(
                    "would_create_cycle",
                    "Cannot reparent a node under one of its own descendants (would create a cycle).");
            }

            node.Reparent(newParent, keepGlobalTransform: keepGlobal);

            // Preserve scene persistence: the reparented sub-tree must still be owned by the scene root.
            if (node.Owner == null)
                node.Owner = root;
            SetOwnerRecursive(node, root);

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            return ToolDispatchResult.Ok(ToNodeData(node).ToJsonString());
        }

        /// <summary>True when <paramref name="ancestor"/> is an ancestor of <paramref name="candidate"/>
        /// (candidate is ancestor or a descendant of ancestor). Walks the parent chain.</summary>
        static bool IsAncestorOf(Node ancestor, Node candidate)
        {
            var cursor = candidate.GetParent();
            while (cursor != null)
            {
                if (cursor == ancestor) return true;
                cursor = cursor.GetParent();
            }
            return false;
        }

        // --- godot_open_mcp_node_duplicate (P2.5) -----------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_node_duplicate</c>. Duplicates a node (and its whole sub-tree)
        /// under the same parent by default. Adapted from Unity Open MCP's
        /// <c>GameObjectsTools.Duplicate</c> (adapt) and Godot-MCP's <c>Tool_Node.Duplicate</c>
        /// (behavior reference, copy fidelity for the Owner-assignment pattern).
        ///
        /// <para>
        /// Optional <c>new_name</c> renames the duplicate; when omitted Godot assigns a unique sibling
        /// name. Optional <c>parent_node_path</c> places the duplicate under a different parent
        /// (default: same parent as the source). The duplicate's owner (and the owner of its sub-tree
        /// that has no owner yet) is set to the edited scene root so the copy persists on save.
        /// </para>
        ///
        /// <para>
        /// <b>No gate yet.</b> Same forward-compat no-op shape as the other P2 mutators; no editor Undo
        /// yet.
        /// </para>
        ///
        /// Structured failures: <c>no_edited_scene</c>, <c>missing_parameter</c>,
        /// <c>node_not_found</c>, <c>cannot_duplicate_root</c>, <c>parent_not_found</c>,
        /// <c>duplicate_failed</c>. Must not throw.
        /// </summary>
        internal static ToolDispatchResult Duplicate(string body)
        {
            var nodePath = NodeModifyBody.Parse(body).NodePath;
            var newName = ExtractScalarFromBody(body, "new_name");
            var parentPath = ExtractScalarFromBody(body, "parent_node_path");

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
            {
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling node_duplicate.");
            }

            if (string.IsNullOrEmpty(nodePath))
            {
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "node_duplicate requires 'node_path' (the node to duplicate).");
            }

            var node = ResolvePath(root, nodePath!);
            if (node == null)
            {
                return ToolDispatchResult.Fail(
                    "node_not_found",
                    $"Node not found at path '{nodePath}'.");
            }

            if (node == root)
            {
                return ToolDispatchResult.Fail(
                    "cannot_duplicate_root",
                    "Cannot duplicate the edited scene root; pick a child node instead.");
            }

            // Resolve the destination parent: default to the source's parent; an explicit parent_node_path
            // that does not resolve is a hard error (the agent asked for a specific parent that isn't there).
            Node parent = node.GetParent();
            if (!string.IsNullOrEmpty(parentPath))
            {
                parent = ResolvePath(root, parentPath!);
                if (parent == null)
                {
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Destination parent node not found at path '{parentPath}'.");
                }
            }
            if (parent == null)
            {
                return ToolDispatchResult.Fail(
                    "duplicate_failed",
                    $"Source node '{node.Name}' has no parent and no parent_node_path was given; cannot place the duplicate.");
            }

            Node? duplicate;
            try
            {
                duplicate = node.Duplicate();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "duplicate_failed",
                    $"Failed to duplicate node '{node.Name}': {e.Message}");
            }
            if (duplicate == null)
            {
                return ToolDispatchResult.Fail(
                    "duplicate_failed",
                    $"Node.Duplicate() returned null for '{node.Name}'.");
            }

            if (!string.IsNullOrEmpty(newName))
                duplicate.Name = newName!;

            try
            {
                parent.AddChild(duplicate);
            }
            catch (System.Exception e)
            {
                duplicate.QueueFree();
                return ToolDispatchResult.Fail(
                    "duplicate_failed",
                    $"Failed to add duplicate to parent: {e.Message}");
            }

            duplicate.Owner = root;
            SetOwnerRecursive(duplicate, root);

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            EditorInterface.Singleton.EditNode(duplicate);
            return ToolDispatchResult.Ok(ToNodeData(duplicate).ToJsonString());
        }

        // --- godot_open_mcp_node_delete (P2.5) --------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_node_delete</c>. Deletes one or more nodes (and their sub-trees)
        /// from the edited scene. Adapted from Unity Open MCP's <c>GameObjectsTools.Destroy</c> (adapt)
        /// and Godot-MCP's <c>Tool_Node.Delete</c> (behavior reference): Godot uses synchronous
        /// <see cref="Node.Free"/> in editor mode (QueueFree defers to the next idle frame, which never
        /// ticks deterministically under a tool-driven flow).
        ///
        /// <para>
        /// Refuses to delete the edited scene root (<c>cannot_delete_root</c>) — closing or replacing
        /// the scene is a scene-level operation, not a node-tree one. Each target is resolved; a miss
        /// surfaces as a warning in the result (the rest of the batch still deletes) so a batch with a
        /// stale path does not no-op the good entries.
        /// </para>
        ///
        /// <para>
        /// <b>No gate yet.</b> Same forward-compat no-op shape as the other P2 mutators; no editor Undo
        /// yet — deletes are irreversible until P3.5.
        /// </para>
        ///
        /// Structured failures: <c>no_edited_scene</c>, <c>missing_parameter</c>. Per-target misses are
        /// warnings, not failures (batch accumulate-don't-abort). Must not throw.
        /// </summary>
        internal static ToolDispatchResult Delete(string body)
        {
            // Reuse NodeModifyBody: it parses node_path + node_paths with the same de-duplicating
            // TargetPaths() enumerator. Optional fail_if_has_children is a P2.5 schema field.
            var request = NodeModifyBody.Parse(body);
            var failIfHasChildren = ExtractBoolFromBody(body, "fail_if_has_children", defaultValue: false);

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
            {
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling node_delete.");
            }

            if (!request.HasTarget)
            {
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "node_delete requires 'node_path' (single) or 'node_paths' (array); neither was set.");
            }

            // Snapshot identity + resolve parents BEFORE freeing — once a node is freed, GetParent/GetPath
            // are invalid. Collect (node, parent, snapshotPath) tuples, then apply fail_if_has_children
            // guards, then free. A root target is a hard failure for the whole call (the agent almost
            // certainly did not mean to delete the scene via a node op).
            var targets = new List<(Node node, Node? parent, string path)>();
            var warnings = new List<string>();
            foreach (var path in request.TargetPaths())
            {
                var node = ResolvePath(root, path);
                if (node == null)
                {
                    warnings.Add($"node_not_found: '{path}' skipped (not in edited scene).");
                    continue;
                }
                if (node == root)
                {
                    return ToolDispatchResult.Fail(
                        "cannot_delete_root",
                        "Cannot delete the edited scene root; close or replace the scene instead.");
                }
                if (failIfHasChildren && node.GetChildCount(includeInternal: false) > 0)
                {
                    warnings.Add($"has_children: '{path}' skipped (fail_if_has_children=true and node is not a leaf).");
                    continue;
                }
                targets.Add((node, node.GetParent(), node.GetPath().ToString()));
            }

            var deleted = new List<string>();
            foreach (var (node, parent, snapPath) in targets)
            {
                parent?.RemoveChild(node);
                node.Free();
                deleted.Add(snapPath);
            }

            if (deleted.Count > 0)
                EditorInterface.Singleton.MarkSceneAsUnsaved();

            var sb = new StringBuilder(128);
            sb.Append("{\"deleted\":[");
            for (int i = 0; i < deleted.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(deleted[i]));
            }
            sb.Append("],\"count\":").Append(deleted.Count);
            sb.Append(",\"warnings\":");
            AppendWarningsArray(sb, warnings);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- shared body scalar extractors (P2.5) -----------------------------------
        //
        // The P2.5 handlers reuse NodeModifyBody's node_path parser but also need parent_node_path,
        // new_name, keep_global_transform, and fail_if_has_children. Rather than widen NodeModifyBody's
        // public surface for one-off fields, these helpers extract a scalar straight off the raw body
        // using the same IndexOf style. They live here (editor-only) because they are only needed by
        // the editor-coupled handlers.

        static string? ExtractScalarFromBody(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, System.StringComparison.Ordinal);
            if (idx < 0) return null;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return null;
            if (start + 4 <= body.Length && body.Substring(start, 4) == "null") return null;
            if (body[start] == '"')
                return SliceBodyQuotedString(body, start);
            return null;
        }

        static string SliceBodyQuotedString(string body, int start)
        {
            var sb = new StringBuilder(body.Length - start);
            int i = start + 1;
            while (i < body.Length)
            {
                var c = body[i];
                if (c == '\\' && i + 1 < body.Length)
                {
                    var nxt = body[i + 1];
                    switch (nxt)
                    {
                        case '"': sb.Append('"'); i += 2; continue;
                        case '\\': sb.Append('\\'); i += 2; continue;
                        case '/': sb.Append('/'); i += 2; continue;
                        case 'n': sb.Append('\n'); i += 2; continue;
                        case 'r': sb.Append('\r'); i += 2; continue;
                        case 't': sb.Append('\t'); i += 2; continue;
                        case 'b': sb.Append('\b'); i += 2; continue;
                        case 'f': sb.Append('\f'); i += 2; continue;
                        case 'u' when i + 5 < body.Length:
                            if (int.TryParse(body.Substring(i + 2, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var code))
                                sb.Append((char)code);
                            i += 6;
                            continue;
                        default:
                            sb.Append(nxt); i += 2; continue;
                    }
                }
                if (c == '"') return sb.ToString();
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        static bool ExtractBoolFromBody(string body, string key, bool defaultValue)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, System.StringComparison.Ordinal);
            if (idx < 0) return defaultValue;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return defaultValue;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return defaultValue;
            if (start + 4 <= body.Length && body.Substring(start, 4) == "true") return true;
            if (start + 5 <= body.Length && body.Substring(start, 5) == "false") return false;
            return defaultValue;
        }
    }
}
#endif
