#if TOOLS
#nullable enable
using System.Text;
using Godot;
// BridgeRequestBody + BridgeJson live in this same namespace (GodotOpenMcp.Bridge.Editor);
// no extra using is needed — they are internal siblings of the tool family.

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// PhantomCamera domain pack — six typed tools for the third-party
    /// <b>phantom-camera</b> GDScript addon (Cinemachine-style virtual cameras
    /// by ramokz). Mirrors the Phase 12 domain packs' folder layout,
    /// registration shape, gate policy, group assignment, and docs convention.
    ///
    /// <para>
    /// <b>Fidelity:</b> greenfield — there is no Unity twin inside this repo's
    /// porting scope, and the addon is GDScript (not statically available to the
    /// C# bridge). The architecture (embedded domain handlers under
    /// <c>Tools/Extensions/PhantomCamera/</c>, one
    /// <c>RegisterPhantomCameraTools()</c> called from
    /// <see cref="GodotOpenMcpPlugin"/>, every mutator declaring
    /// <c>isMutating:true</c> / <c>defaultGate:"enforce"</c> /
    /// <c>group:"phantom_camera"</c> and validating <c>paths_hint</c> at the
    /// handler level) is copied from the P12.1 tilemap pack. The deltas are:
    /// (1) the classes (<c>PhantomCamera2D</c> / <c>PhantomCamera3D</c>) are
    /// detected at runtime via <c>ClassDB.ClassExists</c> — they are NOT
    /// statically available to the C# bridge (the addon is GDScript); (2)
    /// properties (priority / follow_mode / follow_target / look_at_mode /
    /// look_at_target) are read + written via duck-typed
    /// <see cref="GodotObject.Set(string, Variant)"/> /
    /// <see cref="GodotObject.Get(StringName)"/> rather than typed accessors, so
    /// the pack tracks the addon without a compile-time dependency; (3) if the
    /// addon is not enabled, every tool surfaces <c>addon_not_found</c> instead
    /// of failing to compile — Godot Open MCP has no per-pack compile inventory,
    /// so this is a runtime gate (the faithful equivalent of a Unity
    /// <c>versionDefines</c> gate given the architecture).
    /// </para>
    ///
    /// <para>
    /// <b>Addon API drift.</b> The addon's property names + enum ordinals are
    /// not pinned by a compile-time contract (the spec risk: "PhantomCamera API
    /// changes (third-party)"). Every duck-typed Set/Get is wrapped so an addon
    /// version that renamed a property surfaces <c>execution_error</c> with the
    /// addon's own message rather than crashing the dispatch. The documented
    /// ordinals (follow_mode 0–6, look_at_mode 0–3) match the addon's published
    /// enums; an out-of-range ordinal is passed straight through and surfaces as
    /// an addon-side error.
    /// </para>
    ///
    /// <para>
    /// <b>Gate contract.</b> The five mutating handlers (<c>create</c> /
    /// <c>set_target</c> / <c>set_priority</c> / <c>set_follow</c> /
    /// <c>set_look_at</c>) register with
    /// <see cref="BridgeToolEntry.DefaultGate"/> <c>"enforce"</c> and validate
    /// <c>paths_hint</c> themselves (mirrors the P4.x resource/filesystem/editor
    /// mutators + P12 domain mutators). The dispatch layer rejects an empty hint
    /// when the effective gate is not <c>off</c>; the handler-level guard ALSO
    /// fires when an agent overrides with <c>gate:"off"</c>, so
    /// <c>paths_hint</c> is always required for these tools. The read-only
    /// <c>get</c> has no gate surface.
    /// </para>
    ///
    /// <para>
    /// <b>Owner / persistence.</b> <c>phantom_camera_create</c> sets the new
    /// node's <c>Owner</c> to the edited scene root so it persists in the
    /// <c>.tscn</c> on save. Every mutator that changes scene state calls
    /// <see cref="EditorInterface.MarkSceneAsUnsaved"/> +
    /// <see cref="SceneTools.MarkEditedSceneDirty"/>.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): every handler touches
    /// <see cref="EditorInterface"/> and live <see cref="GodotObject"/>s. The
    /// pure-managed body parsers live outside this guard and are unit-tested.
    /// </summary>
    internal static class PhantomCameraTools
    {
        internal const string PhantomCameraCreateToolName = "godot_open_mcp_phantom_camera_create";
        internal const string PhantomCameraSetTargetToolName = "godot_open_mcp_phantom_camera_set_target";
        internal const string PhantomCameraSetPriorityToolName = "godot_open_mcp_phantom_camera_set_priority";
        internal const string PhantomCameraSetFollowToolName = "godot_open_mcp_phantom_camera_set_follow";
        internal const string PhantomCameraSetLookAtToolName = "godot_open_mcp_phantom_camera_set_look_at";
        internal const string PhantomCameraGetToolName = "godot_open_mcp_phantom_camera_get";

        // The phantom-camera addon registers these class_names into ClassDB at runtime
        // (when the addon is enabled). They are GDScript classes — NOT statically
        // available to the C# bridge — so every handler detects them via ClassDB.
        internal const string PhantomCamera3DClass = "PhantomCamera3D";
        internal const string PhantomCamera2DClass = "PhantomCamera2D";

        // Addon property names (duck-typed via GodotObject.Set/Get). Match the
        // phantom-camera addon's published @export names.
        internal const string PropPriority = "priority";
        internal const string PropFollowMode = "follow_mode";
        internal const string PropFollowTarget = "follow_target";
        internal const string PropLookAtMode = "look_at_mode";
        internal const string PropLookAtTarget = "look_at_target";

        /// <summary>
        /// Register the phantom_camera tool family. The five mutators declare
        /// <c>defaultGate:"enforce"</c> and <c>isMutating:true</c>; the
        /// read-only <c>get</c> is <c>off</c>. All six belong to the
        /// <c>phantom_camera</c> group. Registered once at plugin enable;
        /// idempotent (the registry replaces on re-register).
        /// </summary>
        internal static void RegisterPhantomCameraTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: PhantomCameraCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "phantom_camera",
                handler: Create));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: PhantomCameraSetTargetToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "phantom_camera",
                handler: SetTarget));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: PhantomCameraSetPriorityToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "phantom_camera",
                handler: SetPriority));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: PhantomCameraSetFollowToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "phantom_camera",
                handler: SetFollow));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: PhantomCameraSetLookAtToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "phantom_camera",
                handler: SetLookAt));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: PhantomCameraGetToolName,
                isMutating: false,
                defaultGate: "off",
                group: "phantom_camera",
                handler: Get));
        }

        // ===========================================================================
        // 1. godot_open_mcp_phantom_camera_create
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_phantom_camera_create</c>. Creates a
        /// <c>PhantomCamera3D</c> (default) or <c>PhantomCamera2D</c> node in the
        /// currently edited scene via <c>ClassDB.Instantiate</c> and returns its
        /// NodeData (same shape as <c>node_create</c>). The new node's owner is
        /// the edited scene root; the scene is marked unsaved.
        ///
        /// <para>
        /// A PhantomCamera is inert until a <c>PhantomCameraHost</c> exists
        /// under a <c>Camera3D</c>/<c>Camera2D</c> in the scene (the addon
        /// requirement) and the camera has a priority — surface those steps with
        /// <c>set_priority</c> after create. This tool does NOT auto-create a
        /// host (out of scope for the frozen 6-tool roster).
        /// </para>
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>addon_not_found</c>, <c>no_edited_scene</c>,
        /// <c>parent_not_found</c>, <c>create_failed</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Create(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "phantom_camera_create is mutating; pass a non-empty paths_hint scoped to the edited scene path (res://...tscn).");

            var request = PhantomCameraCreateBody.Parse(body);

            // Runtime addon gate: the GDScript classes only exist in ClassDB when the
            // addon is enabled. This is the architecture-faithful equivalent of a
            // compile gate (Godot Open MCP has no per-pack compile inventory).
            var dimension = request.EffectiveDimension;
            var className = dimension == "2d" ? PhantomCamera2DClass : PhantomCamera3DClass;
            if (!IsAddonInstalled())
                return ToolDispatchResult.Fail(
                    "addon_not_found",
                    $"The phantom-camera addon is not enabled in this project (no '{PhantomCamera3DClass}'/" +
                    $"'{PhantomCamera2DClass}' class in ClassDB). Enable the addon in Project Settings → " +
                    "Plugins, then retry.");

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling phantom_camera_create.");

            Node parent = root;
            if (!string.IsNullOrEmpty(request.ParentNodePath))
            {
                parent = NodeTools.ResolvePath(root, request.ParentNodePath!);
                if (parent == null)
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Parent node not found at path '{request.ParentNodePath}'.");
            }

            Node phantomCam;
            try
            {
                // ClassDB.Instantiate returns a Variant; unwrap with Variant.As<Node>()
                // (same pattern as node_create's generic class path).
                var variant = ClassDB.Instantiate(className);
                phantomCam = variant.As<Node>();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate {className}: {e.Message}");
            }

            if (phantomCam == null)
                return ToolDispatchResult.Fail("create_failed",
                    $"Instantiation of {className} succeeded but the result was not a Node.");

            if (!string.IsNullOrEmpty(request.Name))
                phantomCam.Name = request.Name;

            try
            {
                parent.AddChild(phantomCam);
            }
            catch (System.Exception e)
            {
                phantomCam.QueueFree();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add {className} to parent: {e.Message}");
            }

            // Owner = edited scene root so the node persists in the .tscn on save.
            phantomCam.Owner = root;

            // Position: 2D → "x,y" (Node2D), 3D → "x,y,z" (Node3D). Best-effort; a malformed
            // vector is silently ignored (the camera is still created at the origin).
            if (dimension == "2d")
            {
                if (phantomCam is Node2D n2d && TryParseVector2(request.Position, out var pos2))
                    n2d.Position = pos2;
            }
            else
            {
                if (phantomCam is Node3D n3d && TryParseVector3(request.Position, out var pos3))
                    n3d.Position = pos3;
            }

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();
            EditorInterface.Singleton.EditNode(phantomCam);

            return ToolDispatchResult.Ok(NodeTools.ToNodeData(phantomCam).ToJsonString());
        }

        // ===========================================================================
        // 2. godot_open_mcp_phantom_camera_set_target
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_phantom_camera_set_target</c>. Sets the
        /// addon's <c>follow_target</c> property to a resolved scene node — the
        /// "which node should this camera track" knob. (Use
        /// <c>set_follow</c> for the follow *mode* and
        /// <c>set_look_at</c> for the look-at target.)
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>addon_not_found</c>, <c>missing_parameter</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>,
        /// <c>wrong_node_type</c>, <c>execution_error</c> (addon-side property
        /// failure). Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult SetTarget(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "phantom_camera_set_target is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var addonErr = EnsureAddon();
            if (addonErr != null) return addonErr;

            var request = PhantomCameraSetTargetBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "phantom_camera_set_target requires 'node_path' (the PhantomCamera to mutate).");
            if (!request.HasTargetNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "phantom_camera_set_target requires 'target_node_path' (the node to follow).");

            if (!TryResolvePhantomCamera(request.NodePath!, out var phantomCam, out var resolveError))
                return resolveError;

            if (!TryResolveTargetNode(request.TargetNodePath!, out var target, out var targetError))
                return targetError;

            try
            {
                phantomCam.Set(PropFollowTarget, target);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error",
                    $"Failed to set {PropFollowTarget} on '{request.NodePath}': {e.Message}");
            }

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            return ToolDispatchResult.Ok(EchoNodePath(phantomCam));
        }

        // ===========================================================================
        // 3. godot_open_mcp_phantom_camera_set_priority
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_phantom_camera_set_priority</c>. Sets
        /// the addon's <c>priority</c> integer — higher wins, so raising it
        /// switches the active camera.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>addon_not_found</c>, <c>missing_parameter</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>,
        /// <c>wrong_node_type</c>, <c>execution_error</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult SetPriority(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "phantom_camera_set_priority is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var addonErr = EnsureAddon();
            if (addonErr != null) return addonErr;

            var request = PhantomCameraSetPriorityBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "phantom_camera_set_priority requires 'node_path' (the PhantomCamera to mutate).");

            if (!TryResolvePhantomCamera(request.NodePath!, out var phantomCam, out var resolveError))
                return resolveError;

            try
            {
                phantomCam.Set(PropPriority, request.Priority);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error",
                    $"Failed to set {PropPriority} on '{request.NodePath}': {e.Message}");
            }

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(phantomCam.GetPath().ToString())).Append(',');
            sb.Append("\"priority\":").Append(request.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 4. godot_open_mcp_phantom_camera_set_follow
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_phantom_camera_set_follow</c>. Sets the
        /// addon's <c>follow_mode</c> ordinal (required) and, when supplied, the
        /// follow target (target_node_path).
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>addon_not_found</c>, <c>missing_parameter</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>,
        /// <c>wrong_node_type</c>, <c>execution_error</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult SetFollow(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "phantom_camera_set_follow is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var addonErr = EnsureAddon();
            if (addonErr != null) return addonErr;

            var request = PhantomCameraSetFollowBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "phantom_camera_set_follow requires 'node_path' (the PhantomCamera to mutate).");

            if (!TryResolvePhantomCamera(request.NodePath!, out var phantomCam, out var resolveError))
                return resolveError;

            // Optional follow target — resolve + set before the mode so the addon does not
            // reject a follow_mode that requires a target.
            if (request.HasTargetNodePath)
            {
                if (!TryResolveTargetNode(request.TargetNodePath!, out var target, out var targetError))
                    return targetError;
                try
                {
                    phantomCam.Set(PropFollowTarget, target);
                }
                catch (System.Exception e)
                {
                    return ToolDispatchResult.Fail("execution_error",
                        $"Failed to set {PropFollowTarget} on '{request.NodePath}': {e.Message}");
                }
            }

            try
            {
                phantomCam.Set(PropFollowMode, request.FollowMode);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error",
                    $"Failed to set {PropFollowMode} on '{request.NodePath}': {e.Message}");
            }

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(phantomCam.GetPath().ToString())).Append(',');
            sb.Append("\"followMode\":").Append(request.FollowMode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (request.HasTargetNodePath)
                sb.Append(",\"targetNodePath\":").Append(BridgeJson.EscapeString(request.TargetNodePath));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 5. godot_open_mcp_phantom_camera_set_look_at
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_phantom_camera_set_look_at</c>. Sets the
        /// addon's <c>look_at_target</c> (required) and, when supplied, the
        /// <c>look_at_mode</c> ordinal.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>addon_not_found</c>, <c>missing_parameter</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>,
        /// <c>wrong_node_type</c>, <c>execution_error</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult SetLookAt(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "phantom_camera_set_look_at is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var addonErr = EnsureAddon();
            if (addonErr != null) return addonErr;

            var request = PhantomCameraSetLookAtBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "phantom_camera_set_look_at requires 'node_path' (the PhantomCamera to mutate).");
            if (!request.HasTargetNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "phantom_camera_set_look_at requires 'target_node_path' (the node to look at).");

            if (!TryResolvePhantomCamera(request.NodePath!, out var phantomCam, out var resolveError))
                return resolveError;

            if (!TryResolveTargetNode(request.TargetNodePath!, out var target, out var targetError))
                return targetError;

            try
            {
                phantomCam.Set(PropLookAtTarget, target);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error",
                    $"Failed to set {PropLookAtTarget} on '{request.NodePath}': {e.Message}");
            }

            // Optional look_at_mode — only set when the agent supplied it (null = leave
            // the current mode unchanged). ExtractIntOrNull distinguishes absent from 0.
            if (request.LookAtMode.HasValue)
            {
                try
                {
                    phantomCam.Set(PropLookAtMode, request.LookAtMode.Value);
                }
                catch (System.Exception e)
                {
                    return ToolDispatchResult.Fail("execution_error",
                        $"Failed to set {PropLookAtMode} on '{request.NodePath}': {e.Message}");
                }
            }

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(phantomCam.GetPath().ToString())).Append(',');
            sb.Append("\"targetNodePath\":").Append(BridgeJson.EscapeString(request.TargetNodePath));
            if (request.LookAtMode.HasValue)
                sb.Append(",\"lookAtMode\":").Append(request.LookAtMode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 6. godot_open_mcp_phantom_camera_get (read-only)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_phantom_camera_get</c>. Reads the
        /// scalar configuration of a PhantomCamera — priority, follow_mode,
        /// follow_target (as a scene path), look_at_mode, look_at_target (as a
        /// scene path). The resolved <c>type</c> is also reported.
        ///
        /// <para>
        /// Structured failures: <c>addon_not_found</c>,
        /// <c>missing_parameter</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>,
        /// <c>execution_error</c> (addon-side property read failure). Must not
        /// throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Get(string body)
        {
            var addonErr = EnsureAddon();
            if (addonErr != null) return addonErr;

            var request = PhantomCameraGetBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "phantom_camera_get requires 'node_path' (the PhantomCamera to read).");

            if (!TryResolvePhantomCamera(request.NodePath!, out var phantomCam, out var resolveError))
                return resolveError;

            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(phantomCam.GetPath().ToString())).Append(',');
            sb.Append("\"type\":").Append(BridgeJson.EscapeString(phantomCam.GetClass())).Append(',');
            sb.Append("\"priority\":").Append(ReadInt(phantomCam, PropPriority)).Append(',');
            sb.Append("\"followMode\":").Append(ReadInt(phantomCam, PropFollowMode)).Append(',');
            sb.Append("\"followTargetPath\":").Append(BridgeJson.EscapeString(ReadTargetPath(phantomCam, PropFollowTarget))).Append(',');
            sb.Append("\"lookAtMode\":").Append(ReadInt(phantomCam, PropLookAtMode)).Append(',');
            sb.Append("\"lookAtTargetPath\":").Append(BridgeJson.EscapeString(ReadTargetPath(phantomCam, PropLookAtTarget)));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // Shared helpers
        // ===========================================================================

        /// <summary>True when either PhantomCamera class is registered in ClassDB
        /// (i.e. the phantom-camera addon is enabled in this project).</summary>
        static bool IsAddonInstalled()
            => ClassDB.ClassExists(PhantomCamera3DClass) || ClassDB.ClassExists(PhantomCamera2DClass);

        /// <summary>Return the <c>addon_not_found</c> failure when the addon is
        /// absent, or null when it is installed. Read-only tool callers use this
        /// before resolving the body so the detection message is uniform.</summary>
        static ToolDispatchResult? EnsureAddon()
        {
            if (IsAddonInstalled()) return null;
            return ToolDispatchResult.Fail(
                "addon_not_found",
                $"The phantom-camera addon is not enabled in this project (no '{PhantomCamera3DClass}'/" +
                $"'{PhantomCamera2DClass}' class in ClassDB). Enable the addon in Project Settings → " +
                "Plugins, then retry.");
        }

        /// <summary>
        /// Resolve <paramref name="nodePath"/> to a live PhantomCamera node in
        /// the edited scene. Fails with <c>no_edited_scene</c> /
        /// <c>node_not_found</c> / <c>wrong_node_type</c> via
        /// <paramref name="error"/>. The class check is duck-typed against the
        /// runtime <c>GetClass()</c> name (the addon classes are GDScript, not
        /// statically available) — a node whose class is neither
        /// PhantomCamera2D nor PhantomCamera3D is rejected.
        /// </summary>
        static bool TryResolvePhantomCamera(string nodePath, out Node phantomCam, out ToolDispatchResult error)
        {
            phantomCam = null!;
            error = null!;

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
            {
                error = ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn first.");
                return false;
            }

            var node = NodeTools.ResolvePath(root, nodePath);
            if (node == null)
            {
                error = ToolDispatchResult.Fail(
                    "node_not_found",
                    $"PhantomCamera not found at path '{nodePath}'.");
                return false;
            }

            var cls = node.GetClass();
            if (cls == PhantomCamera3DClass || cls == PhantomCamera2DClass || node.IsClass(PhantomCamera3DClass) || node.IsClass(PhantomCamera2DClass))
            {
                phantomCam = node;
                return true;
            }

            error = ToolDispatchResult.Fail(
                "wrong_node_type",
                $"Node at '{nodePath}' is a '{cls}', not a PhantomCamera2D / PhantomCamera3D. " +
                "Enable the phantom-camera addon if it is missing.");
            return false;
        }

        /// <summary>
        /// Resolve a target node path (the node a PhantomCamera should follow /
        /// look at) relative to the edited scene root. Reuses
        /// <see cref="NodeTools.ResolvePath"/> so the same path vocabulary as
        /// <c>node_find</c> is accepted.
        /// </summary>
        static bool TryResolveTargetNode(string targetNodePath, out Node target, out ToolDispatchResult error)
        {
            target = null!;
            error = null!;

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
            {
                error = ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn first.");
                return false;
            }

            var node = NodeTools.ResolvePath(root, targetNodePath);
            if (node == null)
            {
                error = ToolDispatchResult.Fail(
                    "node_not_found",
                    $"Target node not found at path '{targetNodePath}'.");
                return false;
            }

            target = node;
            return true;
        }

        /// <summary>Read an integer addon property, rendered as an invariant-
        /// culture JSON number. Returns <c>"0"</c> on any failure (a missing
        /// property on an older addon version degrades to 0 rather than crashing
        /// the read).</summary>
        static string ReadInt(Node node, string prop)
        {
            try
            {
                var v = node.Get(prop);
                int value = (int)v;
                return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return "0";
            }
        }

        /// <summary>Read a Node-typed addon property and return its scene path
        /// (or null when unassigned / not a valid Node).</summary>
        static string? ReadTargetPath(Node node, string prop)
        {
            try
            {
                var v = node.Get(prop);
                if (v.VariantType == Variant.Type.Object)
                {
                    if (v.As<Node>() is Node target && GodotObject.IsInstanceValid(target))
                        return target.GetPath().ToString();
                }
            }
            catch
            {
                // Degrade to null on addon-side read failure.
            }
            return null;
        }

        /// <summary>Build the minimal JSON echo <c>{"nodePath": "..."}</c> used
        /// by the target-setting mutators whose result only needs to confirm the
        /// node touched.</summary>
        static ToolDispatchResult EchoNodePath(Node phantomCam)
        {
            var sb = new StringBuilder(64);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(phantomCam.GetPath().ToString()));
            sb.Append(",\"targetSet\":true");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        /// <summary>
        /// Parse an <c>"x,y,z"</c> string into a <see cref="Vector3"/>. Returns
        /// false on a malformed string (the caller treats that as "no position
        /// applied"). Verbatim from the CSG/Navigation packs.
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

        /// <summary>
        /// Parse an <c>"x,y"</c> string into a <see cref="Vector2"/>. Verbatim
        /// from the tilemap pack.
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
    }
}
#endif
