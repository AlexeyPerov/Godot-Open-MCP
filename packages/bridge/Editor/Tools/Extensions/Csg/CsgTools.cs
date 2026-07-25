#if TOOLS
#nullable enable
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// CSG domain pack (P12.5) — seven typed tools for Godot 4.3+ constructive-solid
    /// geometry primitives (<c>CsgBox3D</c>, <c>CsgSphere3D</c>, <c>CsgCylinder3D</c>,
    /// <c>CsgCombiner3D</c>) and their boolean operations. The fifth and final Phase
    /// 12 domain pack; mirrors the P12.1–P12.4 packs' folder layout, registration
    /// shape, gate policy, and group assignment.
    ///
    /// <para>
    /// <b>Fidelity:</b> greenfield — there is no Unity CSG twin. Unity ships ProBuilder
    /// (a face-editing mesh primitive authoring addon), which the plan consults as a
    /// <b>pattern</b> reference only (create-shape args style, get-info summary,
    /// mutating gate). The deltas from the ProBuilder pattern are:
    /// (1) the Godot surface is node-based (Csg*3D are Node3D subclasses, not a Unity
    /// ProBuilderMesh component attached to a GameObject) — create makes a node, not a
    /// component add;
    /// (2) four separate create tools (box / sphere / cylinder / combiner) vs
    /// ProBuilder's single <c>create_shape</c> with a shape enum — follows the catalog's
    /// split-tool style (clearer for agents picking a primitive);
    /// (3) boolean operations are first-class via <c>set_operation</c> on any CSG shape
    /// — ProBuilder has no direct equivalent (face extrude / delete are the ProBuilder
    /// verbs; intentionally NOT ported — Godot's CSG has no face API);
    /// (4) <c>csg_get</c> reads only the scalar config (size / radius / segments /
    /// operation) — no full mesh vertex dump (a CSG mesh's vertex array is large and not
    /// what an agent tunes);
    /// (5) no package compile gate — CSG is an engine module present in every 4.3+ build
    /// (no equivalent of ProBuilder's <c>UNITY_OPEN_MCP_EXT_PROBUILDER</c> version-define).
    /// </para>
    ///
    /// <para>
    /// <b>Boolean workflow.</b> Godot's CSG combiner is implicit: a <c>CsgCombiner3D</c>
    /// parent collects its child <c>CsgShape3D</c> children, and each child's
    /// <c>Operation</c> property (Union / Intersection / Subtraction) defines how it
    /// combines with the sibling-before-it. The pack surfaces this with three steps:
    /// <c>csg_combiner_create</c> for the parent, the primitive create tools with
    /// <c>parent_node_path</c> = the combiner, and <c>csg_set_operation</c> on each child
    /// (e.g. subtraction on a cutter). Godot rebuilds the mesh when the scene updates;
    /// the pack does not force a manual rebuild beyond marking the scene dirty.
    /// </para>
    ///
    /// <para>
    /// <b>Gate contract.</b> The five mutating handlers (four creates + set_operation)
    /// register with <see cref="BridgeToolEntry.DefaultGate"/> <c>"enforce"</c> and
    /// validate <c>paths_hint</c> themselves (mirrors the P4.x resource/filesystem/editor
    /// mutators and the P12.1–P12.4 domain mutators). The dispatch layer rejects an empty
    /// hint when the effective gate is not <c>off</c>; the handler-level guard ALSO fires
    /// when an agent overrides with <c>gate:"off"</c>, so <c>paths_hint</c> is always
    /// required for these tools. The read-only <c>defaults</c> / <c>get</c> have no gate
    /// surface.
    /// </para>
    ///
    /// <para>
    /// <b>Owner / persistence.</b> Each create handler sets the new node's <c>Owner</c>
    /// to the edited scene root so it persists in the <c>.tscn</c> on save — same step
    /// every node creator performs. Every mutator calls
    /// <see cref="EditorInterface.MarkSceneAsUnsaved"/> +
    /// <see cref="SceneTools.MarkEditedSceneDirty"/> so the bridge-tracked dirty flag the
    /// <c>scene_open</c> guard consults stays honest.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): every handler touches <see cref="EditorInterface"/>
    /// and live Csg*3D node objects. The pure-managed pieces (<see cref="CsgCreateBody"/>
    /// / <see cref="CsgSetOperationBody"/> / <see cref="CsgGetBody"/> /
    /// <see cref="CsgKind"/> / <see cref="CsgOperation"/> /
    /// <see cref="CsgPropertyClamp"/>) live outside this guard and are unit-tested.
    /// </summary>
    internal static class CsgTools
    {
        internal const string CsgDefaultsToolName = "godot_open_mcp_csg_defaults";
        internal const string CsgBoxCreateToolName = "godot_open_mcp_csg_box_create";
        internal const string CsgSphereCreateToolName = "godot_open_mcp_csg_sphere_create";
        internal const string CsgCylinderCreateToolName = "godot_open_mcp_csg_cylinder_create";
        internal const string CsgCombinerCreateToolName = "godot_open_mcp_csg_combiner_create";
        internal const string CsgSetOperationToolName = "godot_open_mcp_csg_set_operation";
        internal const string CsgGetToolName = "godot_open_mcp_csg_get";

        /// <summary>
        /// Register the CSG tool family. The five mutators (four creates + set_operation)
        /// declare <c>defaultGate:"enforce"</c> and <c>isMutating:true</c>; the read-only
        /// <c>defaults</c> + <c>get</c> are <c>off</c>. All seven belong to the <c>csg</c>
        /// group (the P8 stub now filled — the last Phase 12 stub). Registered once at
        /// plugin enable; idempotent (the registry replaces on re-register).
        /// </summary>
        internal static void RegisterCsgTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: CsgDefaultsToolName,
                isMutating: false,
                defaultGate: "off",
                group: "csg",
                handler: Defaults));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: CsgBoxCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "csg",
                handler: BoxCreate));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: CsgSphereCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "csg",
                handler: SphereCreate));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: CsgCylinderCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "csg",
                handler: CylinderCreate));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: CsgCombinerCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "csg",
                handler: CombinerCreate));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: CsgSetOperationToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "csg",
                handler: SetOperation));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: CsgGetToolName,
                isMutating: false,
                defaultGate: "off",
                group: "csg",
                handler: Get));
        }

        // ===========================================================================
        // 1. godot_open_mcp_csg_defaults (read-only helper)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_csg_defaults</c>. Pure helper — returns
        /// recommended starter scalars for the requested kind (box / sphere / cylinder /
        /// combiner) as a JSON object an agent can spread into the matching create tool.
        /// No scene required; no gate surface. A kind other than the four valid tokens
        /// returns <c>invalid_parameter</c>.
        ///
        /// <para>
        /// The scalar keys match the Godot property names the agent will reuse in the
        /// create tool's args (snake_case in the MCP schema; the result uses the same
        /// keys so a spread-into-create round-trips). The defaults are conservative
        /// mid-range values inside the clamp ranges, not necessarily the engine defaults —
        /// they are a sensible starting point an agent can tune from.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Defaults(string body)
        {
            var request = CsgDefaultsBody.Parse(body);
            if (request.Kind == CsgKind.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "csg_defaults requires 'kind' to be one of: \"box\", \"sphere\", \"cylinder\", \"combiner\".");

            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"kind\":").Append(BridgeJson.EscapeString(KindToSchemaString(request.Kind))).Append(',');
            // operation is shared by every kind — Godot's default Operation is Union.
            sb.Append("\"operation\":\"union\",");

            switch (request.Kind)
            {
                case CsgKind.Box:
                    // The engine default box is (1, 1, 1). Use the same so defaults round-trip
                    // against what an agent sees in the inspector on a fresh CsgBox3D.
                    sb.Append("\"size\":{\"x\":1.0,\"y\":1.0,\"z\":1.0}");
                    break;
                case CsgKind.Sphere:
                    // Engine defaults: radius 0.5, radial_segments 12, rings 6, smooth_faces true.
                    sb.Append("\"radius\":0.5,").Append("\"radial_segments\":12,").Append("\"rings\":6,")
                        .Append("\"smooth_faces\":true");
                    break;
                case CsgKind.Cylinder:
                    // Engine defaults: radius 0.5, height 2.0, sides 8, cone false, smooth_faces true.
                    sb.Append("\"radius\":0.5,").Append("\"height\":2.0,").Append("\"sides\":8,")
                        .Append("\"cone\":false,").Append("\"smooth_faces\":true");
                    break;
                case CsgKind.Combiner:
                    // CsgCombiner3D exposes no primitive-specific scalars — operation is the only
                    // knob, already emitted above. Emit an empty "properties" object so the shape
                    // matches the other kinds (an agent can destructure uniformly).
                    sb.Append("\"properties\":{}");
                    break;
            }
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 2. godot_open_mcp_csg_box_create
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_csg_box_create</c>. Creates a <c>CsgBox3D</c>
        /// node in the edited scene and returns its NodeData. The new node's owner is
        /// the edited scene root; the scene is marked unsaved. The optional
        /// <c>size</c> ("x,y,z") clamps each component to strictly positive; an
        /// optional <c>operation</c> applies Godot's <c>OperationEnum</c> at create
        /// time.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>no_edited_scene</c>,
        /// <c>parent_not_found</c>, <c>create_failed</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult BoxCreate(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "csg_box_create is mutating; pass a non-empty paths_hint scoped to the edited scene path (res://...tscn).");

            var request = CsgCreateBody.Parse(body);

            CsgBox3D box;
            var createErr = TryCreatePrimitive<CsgBox3D>(request, out box, out var nodeData);
            if (createErr != null) return createErr;

            // Apply kind-specific scalars. Each is independent (non-aborting) — a malformed
            // size string is ignored and the box keeps the engine default (1, 1, 1).
            if (!string.IsNullOrEmpty(request.Size) && TryParseVector3(request.Size, out var size))
            {
                box.Size = new Vector3(
                    CsgPropertyClamp.ClampPositive(size.X),
                    CsgPropertyClamp.ClampPositive(size.Y),
                    CsgPropertyClamp.ClampPositive(size.Z));
            }

            FinalizeShape(box, request);
            return ToolDispatchResult.Ok(nodeData.ToJsonString());
        }

        // ===========================================================================
        // 3. godot_open_mcp_csg_sphere_create
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_csg_sphere_create</c>. Creates a
        /// <c>CsgSphere3D</c> node in the edited scene and returns its NodeData.
        /// Optional scalars: <c>radius</c> (strictly positive), <c>radial_segments</c>
        /// (clamped to [3, 1000]), <c>rings</c> (clamped to [3, 1000]),
        /// <c>smooth_faces</c> (bool). An optional <c>operation</c> applies at create
        /// time.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>no_edited_scene</c>,
        /// <c>parent_not_found</c>, <c>create_failed</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult SphereCreate(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "csg_sphere_create is mutating; pass a non-empty paths_hint scoped to the edited scene path (res://...tscn).");

            var request = CsgCreateBody.Parse(body);

            CsgSphere3D sphere;
            var createErr = TryCreatePrimitive<CsgSphere3D>(request, out sphere, out var nodeData);
            if (createErr != null) return createErr;

            if (request.Radius.HasValue)
                sphere.Radius = CsgPropertyClamp.ClampPositive(request.Radius.Value);
            if (request.RadialSegments.HasValue)
                sphere.RadialSegments = CsgPropertyClamp.ClampSegmentCount(request.RadialSegments.Value);
            if (request.Rings.HasValue)
                sphere.Rings = CsgPropertyClamp.ClampSegmentCount(request.Rings.Value);
            if (request.SmoothFaces.HasValue)
                sphere.SmoothFaces = request.SmoothFaces.Value;

            FinalizeShape(sphere, request);
            return ToolDispatchResult.Ok(nodeData.ToJsonString());
        }

        // ===========================================================================
        // 4. godot_open_mcp_csg_cylinder_create
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_csg_cylinder_create</c>. Creates a
        /// <c>CsgCylinder3D</c> node in the edited scene and returns its NodeData.
        /// Optional scalars: <c>radius</c> (strictly positive), <c>height</c> (strictly
        /// positive), <c>sides</c> (clamped to [3, 1000]), <c>cone</c> (bool),
        /// <c>smooth_faces</c> (bool). An optional <c>operation</c> applies at create
        /// time.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>no_edited_scene</c>,
        /// <c>parent_not_found</c>, <c>create_failed</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult CylinderCreate(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "csg_cylinder_create is mutating; pass a non-empty paths_hint scoped to the edited scene path (res://...tscn).");

            var request = CsgCreateBody.Parse(body);

            CsgCylinder3D cylinder;
            var createErr = TryCreatePrimitive<CsgCylinder3D>(request, out cylinder, out var nodeData);
            if (createErr != null) return createErr;

            if (request.Radius.HasValue)
                cylinder.Radius = CsgPropertyClamp.ClampPositive(request.Radius.Value);
            if (request.Height.HasValue)
                cylinder.Height = CsgPropertyClamp.ClampPositive(request.Height.Value);
            if (request.Sides.HasValue)
                cylinder.Sides = CsgPropertyClamp.ClampSegmentCount(request.Sides.Value);
            if (request.Cone.HasValue)
                cylinder.Cone = request.Cone.Value;
            if (request.SmoothFaces.HasValue)
                cylinder.SmoothFaces = request.SmoothFaces.Value;

            FinalizeShape(cylinder, request);
            return ToolDispatchResult.Ok(nodeData.ToJsonString());
        }

        // ===========================================================================
        // 5. godot_open_mcp_csg_combiner_create
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_csg_combiner_create</c>. Creates a
        /// <c>CsgCombiner3D</c> node in the edited scene and returns its NodeData. The
        /// combiner has no primitive-specific scalars — its job is to be a parent for
        /// child <c>CsgShape3D</c> nodes whose <c>Operation</c> defines the boolean
        /// combination. An optional <c>operation</c> applies at create time (rarely
        /// needed — a combiner's own operation only matters when it is itself a child
        /// of another combiner).
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>no_edited_scene</c>,
        /// <c>parent_not_found</c>, <c>create_failed</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult CombinerCreate(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "csg_combiner_create is mutating; pass a non-empty paths_hint scoped to the edited scene path (res://...tscn).");

            var request = CsgCreateBody.Parse(body);

            CsgCombiner3D combiner;
            var createErr = TryCreatePrimitive<CsgCombiner3D>(request, out combiner, out var nodeData);
            if (createErr != null) return createErr;

            FinalizeShape(combiner, request);
            return ToolDispatchResult.Ok(nodeData.ToJsonString());
        }

        // ===========================================================================
        // 6. godot_open_mcp_csg_set_operation
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_csg_set_operation</c>. Sets the boolean
        /// <c>Operation</c> (union / intersection / subtraction) on any CSG shape —
        /// primitives (<c>CsgBox3D</c> / <c>CsgSphere3D</c> / <c>CsgCylinder3D</c>) and
        /// <c>CsgCombiner3D</c> alike (the operation is defined on the shared
        /// <c>CsgShape3D</c> base). This is the second half of the boolean workflow:
        /// create a combiner parent, add primitive children, then call
        /// <c>set_operation</c> on each child to define how it combines with the
        /// sibling-before-it (e.g. subtraction on a cutter carves out the cutter's
        /// shape). Godot rebuilds the mesh when the scene updates; the pack does not
        /// force a manual rebuild.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>
        /// (node_path or operation absent / invalid), <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult SetOperation(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "csg_set_operation is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = CsgSetOperationBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "csg_set_operation requires 'node_path' (the CSG shape to mutate).");
            if (!request.HasOperation)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "csg_set_operation requires 'operation' (one of: \"union\", \"intersection\", \"subtraction\").");

            if (!TryResolveAnyNode(request.NodePath!, out var node, out var resolveError))
                return resolveError;

            // The Operation property lives on the shared CsgShape3D base — primitives and
            // combiner all inherit it. A non-CSG node returns wrong_node_type.
            if (!(node is CsgShape3D shape))
                return ToolDispatchResult.Fail(
                    "wrong_node_type",
                    $"Node at '{request.NodePath}' is a '{node.GetClass()}', not a CSG shape (CsgShape3D).");

            var godotOp = ToGodotOperation(request.Operation);
            shape.Operation = godotOp;

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(node.GetPath().ToString())).Append(',');
            sb.Append("\"operation\":").Append(BridgeJson.EscapeString(
                CsgOperationParser.ToSchemaString(request.Operation)));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 7. godot_open_mcp_csg_get (read-only)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_csg_get</c>. Reads the scalar configuration of
        /// any CSG shape — the operation (shared) plus the kind-specific scalars (size
        /// for box; radius / radial_segments / rings / smooth_faces for sphere; radius /
        /// height / sides / cone / smooth_faces for cylinder; none for combiner). The
        /// resolved <c>type</c> and <c>kind</c> are also reported. No mesh vertex dump
        /// in v1 — a CSG mesh's vertex array is large and not what an agent tunes.
        ///
        /// <para>
        /// Structured failures: <c>missing_parameter</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Get(string body)
        {
            var request = CsgGetBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "csg_get requires 'node_path' (the CSG shape to read).");

            if (!TryResolveAnyNode(request.NodePath!, out var node, out var resolveError))
                return resolveError;

            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(node.GetPath().ToString())).Append(',');
            sb.Append("\"type\":").Append(BridgeJson.EscapeString(node.GetClass())).Append(',');
            sb.Append("\"operation\":").Append(BridgeJson.EscapeString(
                CsgOperationParser.ToSchemaString(FromGodotOperation((node as CsgShape3D)?.Operation)))).Append(',');

            switch (node)
            {
                case CsgBox3D box:
                    sb.Append("\"kind\":\"box\",");
                    sb.Append("\"size\":{");
                    sb.Append("\"x\":").Append(Float(box.Size.X)).Append(',');
                    sb.Append("\"y\":").Append(Float(box.Size.Y)).Append(',');
                    sb.Append("\"z\":").Append(Float(box.Size.Z));
                    sb.Append('}');
                    break;
                case CsgSphere3D sphere:
                    sb.Append("\"kind\":\"sphere\",");
                    sb.Append("\"radius\":").Append(Float(sphere.Radius)).Append(',');
                    sb.Append("\"radial_segments\":").Append(sphere.RadialSegments).Append(',');
                    sb.Append("\"rings\":").Append(sphere.Rings).Append(',');
                    sb.Append("\"smooth_faces\":").Append(sphere.SmoothFaces ? "true" : "false");
                    break;
                case CsgCylinder3D cylinder:
                    sb.Append("\"kind\":\"cylinder\",");
                    sb.Append("\"radius\":").Append(Float(cylinder.Radius)).Append(',');
                    sb.Append("\"height\":").Append(Float(cylinder.Height)).Append(',');
                    sb.Append("\"sides\":").Append(cylinder.Sides).Append(',');
                    sb.Append("\"cone\":").Append(cylinder.Cone ? "true" : "false").Append(',');
                    sb.Append("\"smooth_faces\":").Append(cylinder.SmoothFaces ? "true" : "false");
                    break;
                case CsgCombiner3D _:
                    // CsgCombiner3D exposes no primitive scalars — only operation (already emitted).
                    sb.Append("\"kind\":\"combiner\"");
                    break;
                default:
                    return ToolDispatchResult.Fail(
                        "wrong_node_type",
                        $"Node at '{request.NodePath}' is a '{node.GetClass()}', not a CSG shape (CsgShape3D).");
            }
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // Shared helpers
        // ===========================================================================

        /// <summary>
        /// Resolve <paramref name="nodePath"/> to any live node in the edited scene. Used by
        /// <c>set_operation</c> / <c>get</c>, which then type-check against
        /// <c>CsgShape3D</c>. Fails with <c>no_edited_scene</c> / <c>node_not_found</c>
        /// via <paramref name="error"/>.
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
        /// Shared create path for the four primitive kinds. Resolves the parent
        /// (edited scene root by default), instantiates <typeparamref name="T"/>, sets
        /// the name, parents it, assigns the owner, applies the position, and returns
        /// the new node's NodeData via <paramref name="nodeData"/>. Returns null on
        /// success or a structured failure (<c>no_edited_scene</c> /
        /// <c>parent_not_found</c> / <c>create_failed</c>); the caller applies
        /// kind-specific scalars and calls <see cref="FinalizeShape"/> on success.
        /// </summary>
        static ToolDispatchResult? TryCreatePrimitive<T>(CsgCreateBody request, out T node, out NodeData nodeData)
            where T : Node, new()
        {
            node = null!;
            nodeData = null!;

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling a csg_*_create tool.");

            Node parent = root;
            if (!string.IsNullOrEmpty(request.ParentNodePath))
            {
                parent = NodeTools.ResolvePath(root, request.ParentNodePath!);
                if (parent == null)
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Parent node not found at path '{request.ParentNodePath}'.");
            }

            T shape;
            try
            {
                // Csg*3D are concrete engine nodes — no ClassExists guard needed.
                shape = new T();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate CSG shape: {e.Message}");
            }

            if (!string.IsNullOrEmpty(request.Name))
                shape.Name = request.Name;

            try
            {
                parent.AddChild(shape);
            }
            catch (System.Exception e)
            {
                shape.QueueFree();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add CSG shape to parent: {e.Message}");
            }

            shape.Owner = root;

            // All Csg*3D derive from Node3D — apply best-effort; a malformed vector is ignored.
            ApplyPosition(shape, request.Position);

            node = shape;
            nodeData = NodeTools.ToNodeData(shape);
            return null;
        }

        /// <summary>
        /// Apply the optional <c>operation</c> from the create body (only when the agent
        /// sent one — Godot's default Operation on a fresh shape is Union, so a missing
        /// key means "leave the engine default"), then mark the scene unsaved + dirty and
        /// select the new node. Called by every create handler after its kind-specific
        /// scalars land.
        /// </summary>
        static void FinalizeShape(CsgShape3D shape, CsgCreateBody request)
        {
            if (request.HasOperation)
                shape.Operation = ToGodotOperation(request.Operation);

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();
            EditorInterface.Singleton.EditNode(shape);
        }

        /// <summary>Apply a position string to a Node3D. Best-effort: a malformed vector
        /// is ignored (the node keeps the origin). Mirrors the helper in the other Phase
        /// 12 packs.</summary>
        static void ApplyPosition(Node node, string? position)
        {
            if (string.IsNullOrWhiteSpace(position)) return;
            if (node is Node3D n3d && TryParseVector3(position, out var p3))
                n3d.Position = p3;
        }

        /// <summary>Map the pack's <see cref="CsgOperation"/> token to Godot's
        /// <c>CsgShape3D.OperationEnum</c>. Callers have already validated the token is
        /// not Unknown.</summary>
        static CsgShape3D.OperationEnum ToGodotOperation(CsgOperation op)
        {
            switch (op)
            {
                case CsgOperation.Intersection: return CsgShape3D.OperationEnum.Intersection;
                case CsgOperation.Subtraction: return CsgShape3D.OperationEnum.Subtraction;
                case CsgOperation.Union:
                default: return CsgShape3D.OperationEnum.Union;
            }
        }

        /// <summary>Map Godot's <c>CsgShape3D.OperationEnum</c> back to the pack's
        /// <see cref="CsgOperation"/> token. Accepts null (a non-CSG node cast) and
        /// returns Unknown — the caller (<c>get</c>) feeds the result through
        /// <see cref="CsgOperationParser.ToSchemaString"/>, which renders Unknown as
        /// "union" (the engine default).</summary>
        static CsgOperation FromGodotOperation(CsgShape3D.OperationEnum? op)
        {
            switch (op)
            {
                case CsgShape3D.OperationEnum.Intersection: return CsgOperation.Intersection;
                case CsgShape3D.OperationEnum.Subtraction: return CsgOperation.Subtraction;
                case CsgShape3D.OperationEnum.Union: return CsgOperation.Union;
                default: return CsgOperation.Unknown;
            }
        }

        /// <summary>Render a kind enum back to its MCP schema string. Mirrors the
        /// parser's token vocabulary.</summary>
        static string KindToSchemaString(CsgKind kind)
        {
            switch (kind)
            {
                case CsgKind.Box: return "box";
                case CsgKind.Sphere: return "sphere";
                case CsgKind.Cylinder: return "cylinder";
                case CsgKind.Combiner: return "combiner";
                default: return "box";
            }
        }

        /// <summary>Render a float with the invariant culture so a comma-decimal locale cannot corrupt the JSON.</summary>
        static string Float(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Parse a <c>"x,y,z"</c> string into a <see cref="Vector3"/>. Verbatim from
        /// NavigationTools / ParticlesTools / NodeTools.TryParseVector3 — duplicated
        /// rather than widening another type's surface for one helper. Returns false on
        /// a malformed string (the caller treats that as "no value applied").
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
