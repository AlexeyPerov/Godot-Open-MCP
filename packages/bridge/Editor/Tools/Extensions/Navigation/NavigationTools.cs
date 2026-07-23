#if TOOLS
#nullable enable
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Navigation domain pack (P12.2) — seven typed tools for Godot 4.3+ navigation nodes
    /// (<c>NavigationRegion2D/3D</c>, <c>NavigationAgent2D/3D</c>, <c>NavigationLink2D/3D</c>).
    /// The second Phase 12 domain pack; mirrors the P12.1 tilemap pack's folder layout,
    /// registration shape, gate policy, and group assignment.
    ///
    /// <para>
    /// <b>Fidelity:</b> adapt — architecture (embedded domain handlers under
    /// <c>Tools/Extensions/&lt;Domain&gt;/</c>, one <c>Register*Tools()</c> called from
    /// <see cref="GodotOpenMcpPlugin"/>, every mutator declaring <c>isMutating:true</c> /
    /// <c>defaultGate:"enforce"</c> / <c>group:"navigation"</c> and validating <c>paths_hint</c>
    /// at the handler level) is copied from Unity Open MCP's
    /// <c>TypedTools/Extensions/Navigation/NavigationTools.cs</c>. The Godot deltas are:
    /// (1) Godot navigation is node-based (NavigationRegion/Agent/Link are Node subclasses, not
    /// Unity components attached to a GameObject) — create makes a node, not a component add;
    /// (2) two parallel class hierarchies (2D vs 3D) selected by a <c>dimension</c> arg — Unity
    /// has one NavMesh API in world space;
    /// (3) regions carry a navigation resource (<c>NavigationPolygon</c> 2D / <c>NavigationMesh</c>
    /// 3D) rather than a baked NavMesh data blob;
    /// (4) no bake/modifier-volume surface in v1 (the 7-tool roster is create + assign + configure
    /// + inspect only — baking from geometry is deferred);
    /// (5) no package compile gate — navigation is an engine module present in every 4.3+ build.
    /// </para>
    ///
    /// <para>
    /// <b>Gate contract.</b> The five mutating handlers (<c>region_create</c> /
    /// <c>region_set_mesh</c> / <c>agent_create</c> / <c>agent_configure</c> / <c>link_create</c>)
    /// register with <see cref="BridgeToolEntry.DefaultGate"/> <c>"enforce"</c> and validate
    /// <c>paths_hint</c> themselves (mirrors the P4.x resource/filesystem/editor mutators and the
    /// P12.1 tilemap mutators). The dispatch layer rejects an empty hint when the effective gate
    /// is not <c>off</c>; the handler-level guard ALSO fires when an agent overrides with
    /// <c>gate:"off"</c>, so <c>paths_hint</c> is always required for these tools. The read-only
    /// <c>defaults</c> / <c>get</c> have no gate surface.
    /// </para>
    ///
    /// <para>
    /// <b>Owner / persistence.</b> The three create handlers set the new node's <c>Owner</c> to the
    /// edited scene root so it persists in the <c>.tscn</c> on save — same step every node creator
    /// performs. Every mutator that changes scene state calls
    /// <see cref="EditorInterface.MarkSceneAsUnsaved"/> +
    /// <see cref="SceneTools.MarkEditedSceneDirty"/> so the bridge-tracked dirty flag the
    /// <c>scene_open</c> guard consults stays honest.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): every handler touches <see cref="EditorInterface"/> and live
    /// navigation node objects. The pure-managed pieces (<see cref="NavigationRegionCreateBody"/>
    /// / <see cref="NavigationAgentConfigureBody"/> / … / <see cref="NavDimension"/>) live outside
    /// this guard and are unit-tested.
    /// </summary>
    internal static class NavigationTools
    {
        internal const string NavigationDefaultsToolName = "godot_open_mcp_navigation_defaults";
        internal const string NavigationRegionCreateToolName = "godot_open_mcp_navigation_region_create";
        internal const string NavigationRegionSetMeshToolName = "godot_open_mcp_navigation_region_set_mesh";
        internal const string NavigationAgentCreateToolName = "godot_open_mcp_navigation_agent_create";
        internal const string NavigationAgentConfigureToolName = "godot_open_mcp_navigation_agent_configure";
        internal const string NavigationLinkCreateToolName = "godot_open_mcp_navigation_link_create";
        internal const string NavigationGetToolName = "godot_open_mcp_navigation_get";

        /// <summary>
        /// Register the navigation tool family. The five mutators declare <c>defaultGate:"enforce"</c>
        /// and <c>isMutating:true</c>; the read-only <c>defaults</c> + <c>get</c> are <c>off</c>.
        /// All seven belong to the <c>navigation</c> group (the P8 stub now filled). Registered
        /// once at plugin enable; idempotent (the registry replaces on re-register).
        /// </summary>
        internal static void RegisterNavigationTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: NavigationDefaultsToolName,
                isMutating: false,
                defaultGate: "off",
                group: "navigation",
                handler: Defaults));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: NavigationRegionCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "navigation",
                handler: RegionCreate));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: NavigationRegionSetMeshToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "navigation",
                handler: RegionSetMesh));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: NavigationAgentCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "navigation",
                handler: AgentCreate));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: NavigationAgentConfigureToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "navigation",
                handler: AgentConfigure));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: NavigationLinkCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "navigation",
                handler: LinkCreate));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: NavigationGetToolName,
                isMutating: false,
                defaultGate: "off",
                group: "navigation",
                handler: Get));
        }

        // ===========================================================================
        // 1. godot_open_mcp_navigation_defaults (read-only helper)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_navigation_defaults</c>. Pure helper — returns
        /// recommended starter scalars for a 2D or 3D NavigationAgent as a JSON object an agent
        /// can spread into <c>agent_create</c> / <c>agent_configure</c>. No scene required; no
        /// gate surface. A dimension other than "2d"/"3d" returns <c>invalid_parameter</c>.
        ///
        /// <para>
        /// The scalar keys match the Godot property names the agent will reuse in
        /// <c>agent_configure</c> (snake_case in the MCP schema → camelCase in the result mirrors
        /// the NodeData serializer convention; the agent uses the snake_case schema keys when
        /// calling configure). The defaults are conservative mid-range values, not engine
        /// defaults — they are a sensible starting point an agent can tune from.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Defaults(string body)
        {
            var request = NavigationDefaultsBody.Parse(body);
            if (request.Dimension == NavDimension.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "navigation_defaults requires 'dimension' to be one of: \"2d\", \"3d\".");

            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"dimension\":").Append(BridgeJson.EscapeString(
                request.Dimension == NavDimension.TwoD ? "2d" : "3d")).Append(',');
            sb.Append("\"agent\":{");
            if (request.Dimension == NavDimension.TwoD)
            {
                // 2D agents: radius is in pixels; max_speed in pixels/sec; distances in pixels.
                sb.Append("\"radius\":10,").Append("\"height\":0,").Append("\"maxSpeed\":200,");
                sb.Append("\"pathDesiredDistance\":20,").Append("\"targetDesiredDistance\":20,");
                sb.Append("\"avoidanceEnabled\":false");
            }
            else
            {
                // 3D agents: values in meters / m/s. Height is meaningful in 3D (agent cylinder).
                sb.Append("\"radius\":0.5,").Append("\"height\":1.8,").Append("\"maxSpeed\":5,");
                sb.Append("\"pathDesiredDistance\":1,").Append("\"targetDesiredDistance\":1,");
                sb.Append("\"avoidanceEnabled\":false");
            }
            sb.Append("}}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 2. godot_open_mcp_navigation_region_create
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_navigation_region_create</c>. Creates a
        /// <c>NavigationRegion2D</c> or <c>NavigationRegion3D</c> node in the edited scene and
        /// returns its NodeData (same shape as <c>node_create</c>). The new node's owner is the
        /// edited scene root; the scene is marked unsaved.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>invalid_parameter</c> (bad
        /// dimension), <c>no_edited_scene</c>, <c>parent_not_found</c>, <c>create_failed</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult RegionCreate(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "navigation_region_create is mutating; pass a non-empty paths_hint scoped to the edited scene path (res://...tscn).");

            var request = NavigationRegionCreateBody.Parse(body);
            if (request.Dimension == NavDimension.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "navigation_region_create requires 'dimension' to be one of: \"2d\", \"3d\".");

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling navigation_region_create.");

            Node parent = root;
            if (!string.IsNullOrEmpty(request.ParentNodePath))
            {
                parent = NodeTools.ResolvePath(root, request.ParentNodePath!);
                if (parent == null)
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Parent node not found at path '{request.ParentNodePath}'.");
            }

            Node region;
            try
            {
                // NavigationRegion2D/3D are concrete engine nodes — no ClassExists guard needed.
                region = request.Dimension == NavDimension.TwoD ? new NavigationRegion2D() : new NavigationRegion3D();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate navigation region: {e.Message}");
            }

            if (!string.IsNullOrEmpty(request.Name))
                region.Name = request.Name;

            try
            {
                parent.AddChild(region);
            }
            catch (System.Exception e)
            {
                region.QueueFree();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add navigation region to parent: {e.Message}");
            }

            region.Owner = root;

            // Both NavigationRegion2D (Node2D) and NavigationRegion3D (Node3D) have a Position, but
            // through different base classes. Apply best-effort; a malformed vector is ignored.
            ApplyPosition(region, request.Position);

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();
            EditorInterface.Singleton.EditNode(region);

            return ToolDispatchResult.Ok(NodeTools.ToNodeData(region).ToJsonString());
        }

        // ===========================================================================
        // 3. godot_open_mcp_navigation_region_set_mesh
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_navigation_region_set_mesh</c>. Loads a navigation
        /// resource from a <c>res://</c> path and assigns it to the target region. The resource
        /// type must match the region dimension: <c>NavigationPolygon</c> for a 2D region,
        /// <c>NavigationMesh</c> for a 3D region. A type mismatch returns
        /// <c>resource_load_failed</c>.
        ///
        /// <para>
        /// No baking in v1 — the resource must already exist (P12.2 scope; agents point at an
        /// existing <c>.tres</c>/<c>.res</c> navigation resource, or use the editor's bake UI to
        /// author one).
        /// </para>
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>, <c>wrong_node_type</c>,
        /// <c>resource_not_found</c>, <c>resource_load_failed</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult RegionSetMesh(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "navigation_region_set_mesh is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = NavigationRegionSetMeshBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "navigation_region_set_mesh requires 'node_path' (the navigation region to assign).");
            if (!request.HasMeshPath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "navigation_region_set_mesh requires 'mesh_path' (a res:// navigation resource).");

            var meshPath = request.MeshPath!;
            if (!ResourceLoader.Exists(meshPath))
                return ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"Navigation resource not found at '{meshPath}'.");

            // Resolve the region node first so we know which resource type to expect. The node's
            // class tells us 2D (NavigationPolygon) vs 3D (NavigationMesh).
            Node node;
            if (!TryResolveAnyNode(request.NodePath!, out node, out var resolveError))
                return resolveError;

            if (node is NavigationRegion2D region2d)
            {
                NavigationPolygon poly;
                try
                {
                    var loaded = ResourceLoader.Load<NavigationPolygon>(meshPath);
                    if (loaded == null)
                        return ToolDispatchResult.Fail(
                            "resource_load_failed",
                            $"Resource at '{meshPath}' exists but is not a NavigationPolygon (required for a NavigationRegion2D).");
                    poly = loaded;
                }
                catch (System.Exception e)
                {
                    return ToolDispatchResult.Fail("resource_load_failed",
                        $"Failed to load NavigationPolygon from '{meshPath}': {e.Message}");
                }
                region2d.NavigationPolygon = poly;

                EditorInterface.Singleton.MarkSceneAsUnsaved();
                SceneTools.MarkEditedSceneDirty();

                var sb = new StringBuilder(96);
                sb.Append('{');
                sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(region2d.GetPath().ToString())).Append(',');
                sb.Append("\"dimension\":\"2d\",");
                sb.Append("\"meshPath\":").Append(BridgeJson.EscapeString(meshPath));
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }

            if (node is NavigationRegion3D region3d)
            {
                NavigationMesh mesh;
                try
                {
                    var loaded = ResourceLoader.Load<NavigationMesh>(meshPath);
                    if (loaded == null)
                        return ToolDispatchResult.Fail(
                            "resource_load_failed",
                            $"Resource at '{meshPath}' exists but is not a NavigationMesh (required for a NavigationRegion3D).");
                    mesh = loaded;
                }
                catch (System.Exception e)
                {
                    return ToolDispatchResult.Fail("resource_load_failed",
                        $"Failed to load NavigationMesh from '{meshPath}': {e.Message}");
                }
                region3d.NavigationMesh = mesh;

                EditorInterface.Singleton.MarkSceneAsUnsaved();
                SceneTools.MarkEditedSceneDirty();

                var sb = new StringBuilder(96);
                sb.Append('{');
                sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(region3d.GetPath().ToString())).Append(',');
                sb.Append("\"dimension\":\"3d\",");
                sb.Append("\"meshPath\":").Append(BridgeJson.EscapeString(meshPath));
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }

            return ToolDispatchResult.Fail(
                "wrong_node_type",
                $"Node at '{request.NodePath}' is a '{node.GetClass()}', not a NavigationRegion2D or NavigationRegion3D.");
        }

        // ===========================================================================
        // 4. godot_open_mcp_navigation_agent_create
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_navigation_agent_create</c>. Creates a
        /// <c>NavigationAgent2D</c> or <c>NavigationAgent3D</c> node in the edited scene and
        /// returns its NodeData. The agent should be parented under the moving body it steers
        /// (a CharacterBody2D/3D or RigidBody) — the tool description surfaces this.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>invalid_parameter</c> (bad
        /// dimension), <c>no_edited_scene</c>, <c>parent_not_found</c>, <c>create_failed</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult AgentCreate(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "navigation_agent_create is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = NavigationAgentCreateBody.Parse(body);
            if (request.Dimension == NavDimension.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "navigation_agent_create requires 'dimension' to be one of: \"2d\", \"3d\".");

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling navigation_agent_create.");

            Node parent = root;
            if (!string.IsNullOrEmpty(request.ParentNodePath))
            {
                parent = NodeTools.ResolvePath(root, request.ParentNodePath!);
                if (parent == null)
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Parent node not found at path '{request.ParentNodePath}'.");
            }

            Node agent;
            try
            {
                agent = request.Dimension == NavDimension.TwoD ? new NavigationAgent2D() : new NavigationAgent3D();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate navigation agent: {e.Message}");
            }

            if (!string.IsNullOrEmpty(request.Name))
                agent.Name = request.Name;

            try
            {
                parent.AddChild(agent);
            }
            catch (System.Exception e)
            {
                agent.QueueFree();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add navigation agent to parent: {e.Message}");
            }

            agent.Owner = root;
            ApplyPosition(agent, request.Position);

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();
            EditorInterface.Singleton.EditNode(agent);

            return ToolDispatchResult.Ok(NodeTools.ToNodeData(agent).ToJsonString());
        }

        // ===========================================================================
        // 5. godot_open_mcp_navigation_agent_configure
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_navigation_agent_configure</c>. Patches clamped scalar
        /// properties on a <c>NavigationAgent2D</c> or <c>NavigationAgent3D</c>. Each scalar is
        /// applied independently (non-aborting — a bad value on one key does not skip the rest,
        /// matching <c>node_modify</c>'s contract). Unknown keys are rejected by the schema
        /// (<c>additionalProperties:false</c>), so the body parser only extracts the fixed known
        /// set. Clamps: radius &gt; 0, height ≥ 0, max_speed ≥ 0, distances &gt; 0 — a value
        /// outside the valid range is clamped to the nearest bound and the clamped value is
        /// echoed in the result.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>, <c>wrong_node_type</c>. A scalar that
        /// is present but non-numeric is silently skipped (the body parser returns null).
        /// </para>
        /// </summary>
        internal static ToolDispatchResult AgentConfigure(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "navigation_agent_configure is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = NavigationAgentConfigureBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "navigation_agent_configure requires 'node_path' (the NavigationAgent to configure).");

            if (!TryResolveAnyNode(request.NodePath!, out var node, out var resolveError))
                return resolveError;

            // Resolve to the 2D or 3D agent interface. Both expose the same scalar property names
            // (Radius / Height / MaxSpeed / PathDesiredDistance / TargetDesiredDistance /
            // AvoidanceEnabled), but through different classes — handle each branch explicitly so
            // a non-agent node returns wrong_node_type rather than a cast fault.
            float? appliedRadius = null, appliedHeight = null, appliedMaxSpeed = null;
            float? appliedPathDist = null, appliedTargetDist = null;
            bool? appliedAvoidance = null;

            if (node is NavigationAgent2D agent2d)
            {
                if (request.Radius.HasValue) { agent2d.Radius = appliedRadius = ClampPositive(request.Radius.Value); }
                if (request.Height.HasValue) { agent2d.Height = appliedHeight = ClampNonNegative(request.Height.Value); }
                if (request.MaxSpeed.HasValue) { agent2d.MaxSpeed = appliedMaxSpeed = ClampNonNegative(request.MaxSpeed.Value); }
                if (request.PathDesiredDistance.HasValue) { agent2d.PathDesiredDistance = appliedPathDist = ClampPositive(request.PathDesiredDistance.Value); }
                if (request.TargetDesiredDistance.HasValue) { agent2d.TargetDesiredDistance = appliedTargetDist = ClampPositive(request.TargetDesiredDistance.Value); }
                if (request.AvoidanceEnabled.HasValue) { agent2d.AvoidanceEnabled = appliedAvoidance = request.AvoidanceEnabled.Value; }
            }
            else if (node is NavigationAgent3D agent3d)
            {
                if (request.Radius.HasValue) { agent3d.Radius = appliedRadius = ClampPositive(request.Radius.Value); }
                if (request.Height.HasValue) { agent3d.Height = appliedHeight = ClampNonNegative(request.Height.Value); }
                if (request.MaxSpeed.HasValue) { agent3d.MaxSpeed = appliedMaxSpeed = ClampNonNegative(request.MaxSpeed.Value); }
                if (request.PathDesiredDistance.HasValue) { agent3d.PathDesiredDistance = appliedPathDist = ClampPositive(request.PathDesiredDistance.Value); }
                if (request.TargetDesiredDistance.HasValue) { agent3d.TargetDesiredDistance = appliedTargetDist = ClampPositive(request.TargetDesiredDistance.Value); }
                if (request.AvoidanceEnabled.HasValue) { agent3d.AvoidanceEnabled = appliedAvoidance = request.AvoidanceEnabled.Value; }
            }
            else
            {
                return ToolDispatchResult.Fail(
                    "wrong_node_type",
                    $"Node at '{request.NodePath}' is a '{node.GetClass()}', not a NavigationAgent2D or NavigationAgent3D.");
            }

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(node.GetPath().ToString())).Append(',');
            sb.Append("\"applied\":{");
            bool first = true;
            first = AppendFloatIfHas(sb, "radius", appliedRadius, first);
            first = AppendFloatIfHas(sb, "height", appliedHeight, first);
            first = AppendFloatIfHas(sb, "maxSpeed", appliedMaxSpeed, first);
            first = AppendFloatIfHas(sb, "pathDesiredDistance", appliedPathDist, first);
            first = AppendFloatIfHas(sb, "targetDesiredDistance", appliedTargetDist, first);
            if (appliedAvoidance.HasValue)
            {
                if (!first) sb.Append(',');
                sb.Append("\"avoidanceEnabled\":").Append(appliedAvoidance.Value ? "true" : "false");
            }
            sb.Append("}}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 6. godot_open_mcp_navigation_link_create
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_navigation_link_create</c>. Creates a
        /// <c>NavigationLink2D</c> or <c>NavigationLink3D</c> node — an off-mesh connection
        /// between two points (e.g. a jump pad, a ladder, a teleport). Start/end positions are
        /// local to the link node (Godot exposes them as local Position* properties); the
        /// <c>bidirectional</c> flag controls whether the link can be traversed both ways. The new
        /// node's owner is the edited scene root; the scene is marked unsaved.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>invalid_parameter</c> (bad
        /// dimension or malformed start/end position), <c>missing_parameter</c>,
        /// <c>no_edited_scene</c>, <c>parent_not_found</c>, <c>create_failed</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult LinkCreate(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "navigation_link_create is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = NavigationLinkCreateBody.Parse(body);
            if (request.Dimension == NavDimension.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "navigation_link_create requires 'dimension' to be one of: \"2d\", \"3d\".");
            if (!request.HasStartPosition || !request.HasEndPosition)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "navigation_link_create requires 'start_position' and 'end_position' (as 'x,y' for 2D or 'x,y,z' for 3D).");

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling navigation_link_create.");

            Node parent = root;
            if (!string.IsNullOrEmpty(request.ParentNodePath))
            {
                parent = NodeTools.ResolvePath(root, request.ParentNodePath!);
                if (parent == null)
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Parent node not found at path '{request.ParentNodePath}'.");
            }

            Node link;
            try
            {
                link = request.Dimension == NavDimension.TwoD ? new NavigationLink2D() : new NavigationLink3D();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate navigation link: {e.Message}");
            }

            if (!string.IsNullOrEmpty(request.Name))
                link.Name = request.Name;

            try
            {
                parent.AddChild(link);
            }
            catch (System.Exception e)
            {
                link.QueueFree();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add navigation link to parent: {e.Message}");
            }

            link.Owner = root;
            ApplyPosition(link, request.Position);

            // Apply start/end + bidirectional. Parse errors after AddChild still free the node is
            // not worth the complexity — the node is created; only the position fields are
            // rejected, surfaced as invalid_parameter. The start/end are LOCAL to the link node
            // (Godot's NavigationLink.StartPosition / EndPosition are local-space).
            if (link is NavigationLink2D link2d)
            {
                if (TryParseVector2(request.StartPosition, out var start2))
                    link2d.StartPosition = start2;
                else
                    return ToolDispatchResult.Fail(
                        "invalid_parameter",
                        "start_position for a 2D link must be 'x,y'.");
                if (TryParseVector2(request.EndPosition, out var end2))
                    link2d.EndPosition = end2;
                else
                    return ToolDispatchResult.Fail(
                        "invalid_parameter",
                        "end_position for a 2D link must be 'x,y'.");
                if (request.Bidirectional.HasValue) link2d.Bidirectional = request.Bidirectional.Value;
            }
            else if (link is NavigationLink3D link3d)
            {
                if (TryParseVector3(request.StartPosition, out var start3))
                    link3d.StartPosition = start3;
                else
                    return ToolDispatchResult.Fail(
                        "invalid_parameter",
                        "start_position for a 3D link must be 'x,y,z'.");
                if (TryParseVector3(request.EndPosition, out var end3))
                    link3d.EndPosition = end3;
                else
                    return ToolDispatchResult.Fail(
                        "invalid_parameter",
                        "end_position for a 3D link must be 'x,y,z'.");
                if (request.Bidirectional.HasValue) link3d.Bidirectional = request.Bidirectional.Value;
            }

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();
            EditorInterface.Singleton.EditNode(link);

            return ToolDispatchResult.Ok(NodeTools.ToNodeData(link).ToJsonString());
        }

        // ===========================================================================
        // 7. godot_open_mcp_navigation_get (read-only)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_navigation_get</c>. Reads the scalar configuration of any
        /// navigation node (region / agent / link, 2D or 3D) — enough for an agent to reconfigure
        /// it via the configure/create tools. The result includes the resolved <c>type</c> and
        /// <c>dimension</c> so an agent does not need a second probe to know which property set
        /// applies. No mesh vertex arrays in v1 — regions report their navigation resource path
        /// only (or <c>null</c> when unassigned).
        ///
        /// <para>
        /// Structured failures: <c>missing_parameter</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Get(string body)
        {
            var request = NavigationGetBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "navigation_get requires 'node_path' (the navigation node to read).");

            if (!TryResolveAnyNode(request.NodePath!, out var node, out var resolveError))
                return resolveError;

            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(node.GetPath().ToString())).Append(',');
            sb.Append("\"type\":").Append(BridgeJson.EscapeString(node.GetClass())).Append(',');

            switch (node)
            {
                case NavigationRegion2D r2:
                    sb.Append("\"dimension\":\"2d\",");
                    sb.Append("\"kind\":\"region\",");
                    sb.Append("\"meshPath\":").Append(BridgeJson.EscapeString(
                        r2.NavigationPolygon?.ResourcePath ?? null));
                    break;
                case NavigationRegion3D r3:
                    sb.Append("\"dimension\":\"3d\",");
                    sb.Append("\"kind\":\"region\",");
                    sb.Append("\"meshPath\":").Append(BridgeJson.EscapeString(
                        r3.NavigationMesh?.ResourcePath ?? null));
                    break;
                case NavigationAgent2D a2:
                    sb.Append("\"dimension\":\"2d\",");
                    sb.Append("\"kind\":\"agent\",");
                    sb.Append("\"properties\":{");
                    sb.Append("\"radius\":").Append(Float(a2.Radius)).Append(',');
                    sb.Append("\"height\":").Append(Float(a2.Height)).Append(',');
                    sb.Append("\"maxSpeed\":").Append(Float(a2.MaxSpeed)).Append(',');
                    sb.Append("\"pathDesiredDistance\":").Append(Float(a2.PathDesiredDistance)).Append(',');
                    sb.Append("\"targetDesiredDistance\":").Append(Float(a2.TargetDesiredDistance)).Append(',');
                    sb.Append("\"avoidanceEnabled\":").Append(a2.AvoidanceEnabled ? "true" : "false");
                    sb.Append('}');
                    break;
                case NavigationAgent3D a3:
                    sb.Append("\"dimension\":\"3d\",");
                    sb.Append("\"kind\":\"agent\",");
                    sb.Append("\"properties\":{");
                    sb.Append("\"radius\":").Append(Float(a3.Radius)).Append(',');
                    sb.Append("\"height\":").Append(Float(a3.Height)).Append(',');
                    sb.Append("\"maxSpeed\":").Append(Float(a3.MaxSpeed)).Append(',');
                    sb.Append("\"pathDesiredDistance\":").Append(Float(a3.PathDesiredDistance)).Append(',');
                    sb.Append("\"targetDesiredDistance\":").Append(Float(a3.TargetDesiredDistance)).Append(',');
                    sb.Append("\"avoidanceEnabled\":").Append(a3.AvoidanceEnabled ? "true" : "false");
                    sb.Append('}');
                    break;
                case NavigationLink2D l2:
                    sb.Append("\"dimension\":\"2d\",");
                    sb.Append("\"kind\":\"link\",");
                    sb.Append("\"properties\":{");
                    sb.Append("\"startPosition\":\"").Append(l2.StartPosition.X).Append(',').Append(l2.StartPosition.Y).Append("\",");
                    sb.Append("\"endPosition\":\"").Append(l2.EndPosition.X).Append(',').Append(l2.EndPosition.Y).Append("\",");
                    sb.Append("\"bidirectional\":").Append(l2.Bidirectional ? "true" : "false");
                    sb.Append('}');
                    break;
                case NavigationLink3D l3:
                    sb.Append("\"dimension\":\"3d\",");
                    sb.Append("\"kind\":\"link\",");
                    sb.Append("\"properties\":{");
                    sb.Append("\"startPosition\":\"")
                        .Append(l3.StartPosition.X).Append(',').Append(l3.StartPosition.Y).Append(',').Append(l3.StartPosition.Z).Append("\",");
                    sb.Append("\"endPosition\":\"")
                        .Append(l3.EndPosition.X).Append(',').Append(l3.EndPosition.Y).Append(',').Append(l3.EndPosition.Z).Append("\",");
                    sb.Append("\"bidirectional\":").Append(l3.Bidirectional ? "true" : "false");
                    sb.Append('}');
                    break;
                default:
                    return ToolDispatchResult.Fail(
                        "wrong_node_type",
                        $"Node at '{request.NodePath}' is a '{node.GetClass()}', not a navigation node " +
                        "(NavigationRegion2D/3D, NavigationAgent2D/3D, NavigationLink2D/3D).");
            }
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // Shared helpers
        // ===========================================================================

        /// <summary>
        /// Resolve <paramref name="nodePath"/> to any live node in the edited scene. Used by the
        /// set_mesh / configure / get handlers, which then type-check against the specific
        /// navigation class they accept. Fails with <c>no_edited_scene</c> /
        /// <c>node_not_found</c> via <paramref name="error"/>.
        /// </summary>
        static bool TryResolveAnyNode(string nodePath, out Node node, out ToolDispatchResult error)
        {
            node = null!;
            error = null!;

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
            {
                error = ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn first.");
                return false;
            }

            var resolved = NodeTools.ResolvePath(root, nodePath);
            if (resolved == null)
            {
                error = ToolDispatchResult.Fail(
                    "node_not_found",
                    $"Node not found at path '{nodePath}'.");
                return false;
            }

            node = resolved;
            return true;
        }

        /// <summary>
        /// Apply a position string to a node that derives from Node2D or Node3D. Best-effort: a
        /// malformed vector is ignored (the node keeps the origin). 2D nodes take 'x,y'; 3D nodes
        /// take 'x,y,z' (a 2-component string applied to a 3D node is rejected silently — z stays
        /// 0). Centralized here so the three create handlers share one implementation.
        /// </summary>
        static void ApplyPosition(Node node, string? position)
        {
            if (string.IsNullOrWhiteSpace(position)) return;
            switch (node)
            {
                case Node2D n2d when TryParseVector2(position, out var p2):
                    n2d.Position = p2;
                    break;
                case Node3D n3d when TryParseVector3(position, out var p3):
                    n3d.Position = p3;
                    break;
            }
        }

        /// <summary>Clamp to (0, +inf) — radius / distances must be strictly positive.</summary>
        static float ClampPositive(float v) => v <= 0f ? 0.0001f : v;

        /// <summary>Clamp to [0, +inf) — height / max_speed may be zero.</summary>
        static float ClampNonNegative(float v) => v < 0f ? 0f : v;

        /// <summary>Render a float with the invariant culture so a comma-decimal locale cannot corrupt the JSON.</summary>
        static string Float(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Append a <c>"key":value</c> float pair to <paramref name="sb"/> when
        /// <paramref name="value"/> has a value, handling the leading-comma rule. Returns the new
        /// "first" flag (false once one pair has been appended).
        /// </summary>
        static bool AppendFloatIfHas(StringBuilder sb, string key, float? value, bool first)
        {
            if (!value.HasValue) return first;
            if (!first) sb.Append(',');
            sb.Append('"').Append(key).Append("\":").Append(Float(value.Value));
            return false;
        }

        /// <summary>
        /// Parse a <c>"x,y"</c> string into a <see cref="Vector2"/>. Verbatim from TilemapTools /
        /// NodeTools.TryParseVector2 — duplicated rather than widening another type's surface for
        /// one helper. Returns false on a malformed string (the caller treats that as "no value
        /// applied").
        /// </summary>
        static bool TryParseVector2(string? text, out Vector2 v)
        {
            v = Vector2.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text!.Trim().Split(',');
            if (parts.Length < 2) return false;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return false;
            v = new Vector2(x, y);
            return true;
        }

        /// <summary>
        /// Parse a <c>"x,y,z"</c> string into a <see cref="Vector3"/>. Same shape as
        /// <see cref="TryParseVector2"/> for the 3D create/link paths.
        /// </summary>
        static bool TryParseVector3(string? text, out Vector3 v)
        {
            v = Vector3.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text!.Trim().Split(',');
            if (parts.Length < 3) return false;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return false;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }
    }
}
#endif
