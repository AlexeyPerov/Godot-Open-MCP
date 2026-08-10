#if TOOLS
#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Lighting domain pack (P16.3) — four typed tools for Godot 4.3+ lights
    /// and scene environment:
    /// <c>godot_open_mcp_light_create</c> (create a directional / omni / spot
    /// light in 2D or 3D), <c>godot_open_mcp_light_set</c> (patch one light
    /// scalar), <c>godot_open_mcp_light_modify</c> (bulk patch multiple light
    /// scalars), and <c>godot_open_mcp_environment_set</c> (create or replace
    /// the <c>WorldEnvironment</c> node's <c>Environment</c> resource). The
    /// third Phase 16 typed-editor-breadth family; mirrors the P16.2 materials
    /// pack's folder layout, registration shape, gate policy, and group
    /// assignment.
    ///
    /// <para>
    /// <b>Fidelity:</b> adapt — Unity Open MCP's <c>LightingTools</c>
    /// (TypedTools/Extensions/Lighting/LightingTools.cs) supplies the
    /// light-create / set / modify shape. The deltas from the Unity pattern are:
    /// (1) Godot lights are <c>Node3D</c> / <c>Node2D</c> subclasses
    /// (<c>DirectionalLight3D</c> / <c>OmniLight3D</c> / <c>SpotLight3D</c> /
    /// <c>DirectionalLight2D</c> / <c>PointLight2D</c>), not Unity Light
    /// components attached to a GameObject — create makes a node, not a
    /// component add;
    /// (2) Godot's <c>Light3D</c> scalar surface
    /// (<c>LightEnergy</c> / <c>LightSize</c> (range) / <c>Param.spot_angle</c>
    /// / <c>Param.attenuation</c> / <c>ShadowEnabled</c>) replaces Unity's flat
    /// <c>Light.range</c> / <c>intensity</c> / <c>spotAngle</c> — clamping uses
    /// Godot's documented bounds;
    /// (3) Unity <c>renderMode</c> / <c>cullingMask</c> are NOT ported in the
    /// typed surface (Godot lights have no per-light render-mode; the
    /// <c>light_cull_mask</c> property on Light3D is settable via
    /// <c>node_modify</c> for the niche case);
    /// (4) <c>WorldEnvironment</c> is a Godot-specific resource (no Unity
    /// equivalent — Unity's <c>RenderSettings.skybox</c> / ambient maps to
    /// Godot's <c>Environment.sky</c> / <c>Environment.ambient_light</c>, but
    /// the resource model is Godot's own);
    /// (5) Unity baked-lighting / lightmap / <c>ReflectionProbe</c> APIs are
    /// intentionally NOT ported (the plan's skip fidelity tag — Godot's lighting
    /// model differs; reflection probes are deferred to a later pack).
    /// </para>
    ///
    /// <para>
    /// <b>Create path.</b> <c>light_create</c> resolves the parent (edited
    /// scene root by default), instantiates the light node via <c>new T()</c>,
    /// sets the name, parents it, assigns the owner (so it persists on save),
    /// applies the position, then applies the starter scalars through the same
    /// allow-listed + clamped path <c>light_set</c> uses, and marks the scene
    /// unsaved.
    /// </para>
    ///
    /// <para>
    /// <b>Set / modify path.</b> <c>light_set</c> resolves the node,
    /// type-checks against <c>Light3D</c> / <c>Light2D</c>, validates the field
    /// is one of the allow-listed light scalars, clamps it via
    /// <see cref="LightPropertyClamp"/>, writes it, and marks the scene unsaved.
    /// <c>light_modify</c> walks the <c>fields</c> map top-level, applying each
    /// field through the same validation + clamping path, accumulating
    /// per-field results (applied + errors) so a single bad entry does not
    /// abort the batch.
    /// </para>
    ///
    /// <para>
    /// <b>Environment path.</b> <c>environment_set</c> resolves the
    /// <c>WorldEnvironment</c> node (explicit <c>node_path</c>, or the first one
    /// in the edited scene, or creates one under the root when
    /// <c>create_if_missing:true</c>), loads the Environment <c>.tres</c> at
    /// <c>environment_path</c>, assigns it to the node's <c>Environment</c>
    /// property, and marks the scene unsaved.
    /// </para>
    ///
    /// <para>
    /// <b>Gate contract.</b> All four handlers register with
    /// <see cref="BridgeToolEntry.DefaultGate"/> <c>"enforce"</c> and validate
    /// <c>paths_hint</c> themselves (mirrors the P4.x resource mutators and the
    /// P12.x / P16.x domain mutators). The dispatch layer rejects an empty hint
    /// when the effective gate is not <c>off</c>; the handler-level guard ALSO
    /// fires when an agent overrides with <c>gate:"off"</c>, so
    /// <c>paths_hint</c> is always required for these tools. <c>paths_hint</c>
    /// for the four mutators is the edited scene path (the <c>.tscn</c>).
    /// </para>
    ///
    /// <para>
    /// <b>Owner / persistence.</b> <c>light_create</c> and
    /// <c>environment_set</c> (when it creates a WorldEnvironment) set the new
    /// node's <c>Owner</c> to the edited scene root so it persists in the
    /// <c>.tscn</c> on save — same step every node creator performs. Every
    /// mutator calls <see cref="EditorInterface.MarkSceneAsUnsaved"/> +
    /// <see cref="SceneTools.MarkEditedSceneDirty"/> so the bridge-tracked dirty
    /// flag the <c>scene_open</c> guard consults stays honest.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): every handler touches
    /// <see cref="EditorInterface"/> and live <c>Light3D</c> / <c>Light2D</c> /
    /// <c>WorldEnvironment</c> / <c>Environment</c> node objects. The
    /// pure-managed pieces (<see cref="LightCreateBody"/> /
    /// <see cref="LightSetBody"/> / <see cref="LightModifyBody"/> /
    /// <see cref="EnvironmentSetBody"/> / <see cref="LightKind"/> /
    /// <see cref="LightKindParser"/> / <see cref="LightPropertyClamp"/>) live
    /// outside this guard and are unit-tested.
    /// </summary>
    internal static class LightingTools
    {
        internal const string LightCreateToolName = "godot_open_mcp_light_create";
        internal const string LightSetToolName = "godot_open_mcp_light_set";
        internal const string LightModifyToolName = "godot_open_mcp_light_modify";
        internal const string EnvironmentSetToolName = "godot_open_mcp_environment_set";

        /// <summary>
        /// Register the lighting tool family. All four handlers are mutating and
        /// declare <c>defaultGate:"enforce"</c> + <c>isMutating:true</c>, and all
        /// four belong to the <c>lighting</c> group. Registered once at plugin
        /// enable; idempotent (the registry replaces on re-register).
        /// </summary>
        internal static void RegisterLightingTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: LightCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "lighting",
                handler: LightCreate));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: LightSetToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "lighting",
                handler: LightSet));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: LightModifyToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "lighting",
                handler: LightModify));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: EnvironmentSetToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "lighting",
                handler: EnvironmentSet));
        }

        // ===========================================================================
        // 1. godot_open_mcp_light_create (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_light_create</c>. Resolves the parent
        /// (edited scene root by default), instantiates the light node
        /// (<c>DirectionalLight3D</c> / <c>OmniLight3D</c> / <c>SpotLight3D</c> /
        /// <c>DirectionalLight2D</c> / <c>PointLight2D</c>) via <c>new T()</c>,
        /// parents it, assigns the owner, applies the position, then applies the
        /// optional starter scalars (color / energy / range / spot_angle /
        /// attenuation / shadow_enabled) through the same allow-listed + clamped
        /// path <c>light_set</c> uses, and marks the scene unsaved. Returns the
        /// new node's NodeData so an agent can chain into <c>light_set</c> /
        /// <c>node_modify</c>.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>invalid_parameter</c> (unknown kind), <c>no_edited_scene</c>,
        /// <c>parent_not_found</c>, <c>create_failed</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult LightCreate(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "light_create is mutating; pass a non-empty paths_hint scoped to the edited scene path (res://...tscn).");

            var request = LightCreateBody.Parse(body);

            if (request.Kind == LightKind.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "light_create requires 'kind' to be one of: \"directional3d\", \"omni3d\", \"spot3d\", \"directional2d\", \"point2d\".");

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling light_create.");

            Node parent = root;
            if (!string.IsNullOrEmpty(request.ParentNodePath))
            {
                parent = NodeTools.ResolvePath(root, request.ParentNodePath!);
                if (parent == null)
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Parent node not found at path '{request.ParentNodePath}'.");
            }

            // Instantiate the right light node class for the kind.
            Node node;
            try
            {
                node = CreateLightNode(request.Kind);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate light of kind '{LightKindParser.ToSchemaString(request.Kind)}': {e.Message}");
            }

            if (!string.IsNullOrEmpty(request.Name))
                node.Name = request.Name;

            try
            {
                parent.AddChild(node);
            }
            catch (System.Exception e)
            {
                node.QueueFree();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add light node to parent: {e.Message}");
            }

            node.Owner = root;

            // Apply position best-effort (3D and 2D lights carry spatial transforms).
            ApplyPosition(node, request.Position);

            // Apply starter scalars through the shared allow-listed + clamped path.
            var applied = new List<string>();
            var warnings = new List<string>();
            ApplyLightField(node, "color", request.Color, applied, warnings);
            if (request.Energy.HasValue)
                ApplyLightField(node, "energy", FloatStr(request.Energy.Value), applied, warnings);
            if (request.Range.HasValue)
                ApplyLightField(node, "range", FloatStr(request.Range.Value), applied, warnings);
            if (request.SpotAngle.HasValue)
                ApplyLightField(node, "spot_angle", FloatStr(request.SpotAngle.Value), applied, warnings);
            if (request.Attenuation.HasValue)
                ApplyLightField(node, "attenuation", FloatStr(request.Attenuation.Value), applied, warnings);
            if (request.ShadowEnabled.HasValue)
                ApplyLightField(node, "shadow_enabled", request.ShadowEnabled.Value ? "true" : "false", applied, warnings);

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();
            EditorInterface.Singleton.EditNode(node);

            // NodeData + the applied-scalar echo so an agent sees what landed.
            var nodeData = NodeTools.ToNodeData(node);
            var sb = new StringBuilder(256);
            nodeData.AppendJsonTo(sb);
            // Splice the applied + warnings into the NodeData JSON object.
            var json = sb.ToString();
            json = json.Substring(0, json.Length - 1); // strip trailing '}'
            json += ",\"applied\":";
            json += JsonStringArray(applied);
            json += ",\"warnings\":";
            json += JsonStringArray(warnings);
            json += "}";
            return ToolDispatchResult.Ok(json);
        }

        // ===========================================================================
        // 2. godot_open_mcp_light_set (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_light_set</c>. Resolves the light node,
        /// type-checks it against <c>Light3D</c> / <c>Light2D</c>, validates the
        /// <c>field</c> is one of the allow-listed light scalars, clamps it via
        /// <see cref="LightPropertyClamp"/>, writes it, and marks the scene
        /// unsaved.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>missing_parameter</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>,
        /// <c>unsupported_field</c> (field not in the light allow-list),
        /// <c>invalid_property_value</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult LightSet(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "light_set is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = LightSetBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "light_set requires 'node_path' (the light node to mutate).");
            if (!request.HasField)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "light_set requires 'field' (one of: color / energy / range / spot_angle / attenuation / shadow_enabled).");

            if (!TryResolveLight(request.NodePath!, out var node, out var lightBase, out var resolveError))
                return resolveError;

            var field = request.Field!;
            if (!IsAllowedLightField(field, lightBase))
                return ToolDispatchResult.Fail(
                    "unsupported_field",
                    $"'{field}' is not a recognized light field for a '{node.GetClass()}'. Allowed: " +
                    "color / energy / range / spot_angle / attenuation / shadow_enabled (some fields apply only to 3D or omni/spot lights).");

            var applied = new List<string>();
            var warnings = new List<string>();
            ApplyLightField(node, field, request.ValueRaw, applied, warnings);

            if (applied.Count == 0)
                return ToolDispatchResult.Fail(
                    "invalid_property_value",
                    warnings.Count > 0
                        ? warnings[0]
                        : $"Could not apply field '{field}' to '{node.GetClass()}'.");

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(node.GetPath().ToString())).Append(',');
            sb.Append("\"field\":").Append(BridgeJson.EscapeString(field)).Append(',');
            sb.Append("\"applied\":true");
            if (warnings.Count > 0)
            {
                sb.Append(",\"warnings\":");
                sb.Append(JsonStringArray(warnings));
            }
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 3. godot_open_mcp_light_modify (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_light_modify</c>. Bulk patch multiple
        /// light scalars in one call. Resolves the light node, type-checks it,
        /// then walks the <c>fields</c> map top-level, applying each field
        /// through the same validation + clamping path <c>light_set</c> uses,
        /// accumulating per-field results (applied + errors) so a single bad
        /// entry does not abort the batch.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>missing_parameter</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult LightModify(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "light_modify is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = LightModifyBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "light_modify requires 'node_path' (the light node to mutate).");
            if (!request.HasFields)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "light_modify requires 'fields' (a JSON object of {field: value} entries).");

            if (!TryResolveLight(request.NodePath!, out var node, out var lightBase, out var resolveError))
                return resolveError;

            // Walk the fields map top-level, extracting each {field → verbatim value} pair.
            var entries = ExtractFieldsMapEntries(request.FieldsRaw!);

            var applied = new List<string>();
            var errors = new List<string>();
            foreach (var (field, valueRaw) in entries)
            {
                if (!IsAllowedLightField(field, lightBase))
                {
                    errors.Add($"{field}: unsupported_field (not a recognized light field for a '{node.GetClass()}')");
                    continue;
                }
                var beforeCount = applied.Count;
                var localWarnings = new List<string>();
                ApplyLightField(node, field, valueRaw, applied, localWarnings);
                if (applied.Count == beforeCount)
                {
                    // The field was recognized but the value could not be applied.
                    errors.Add($"{field}: invalid_property_value" +
                        (localWarnings.Count > 0 ? $" ({localWarnings[0]})" : ""));
                }
            }

            if (applied.Count > 0)
            {
                EditorInterface.Singleton.MarkSceneAsUnsaved();
                SceneTools.MarkEditedSceneDirty();
            }

            var sb = new StringBuilder(192);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(node.GetPath().ToString())).Append(',');
            sb.Append("\"applied\":").Append(JsonStringArray(applied));
            if (errors.Count > 0)
            {
                sb.Append(",\"errors\":").Append(JsonStringArray(errors));
            }
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 4. godot_open_mcp_environment_set (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_environment_set</c>. Resolves the
        /// <c>WorldEnvironment</c> node in the edited scene — explicit
        /// <c>node_path</c>, or the first <c>WorldEnvironment</c> found in the
        /// tree, or (when <c>create_if_missing:true</c>) a fresh
        /// <c>WorldEnvironment</c> under the scene root. Loads the
        /// <c>Environment</c> resource at <c>environment_path</c> (a res://
        /// <c>.tres</c>), assigns it to the node's <c>Environment</c> property,
        /// and marks the scene unsaved. This is the Godot analog of Unity's
        /// <c>skybox_set</c> + <c>RenderSettings.ambientLight</c> (greenfield —
        /// the resource model is Godot's own).
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>missing_parameter</c>, <c>invalid_path</c>,
        /// <c>no_edited_scene</c>, <c>environment_node_not_found</c>,
        /// <c>resource_not_found</c>, <c>resource_load_failed</c>,
        /// <c>wrong_resource_type</c>, <c>execution_error</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult EnvironmentSet(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "environment_set is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = EnvironmentSetBody.Parse(body);

            if (!request.HasEnvironmentPath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "environment_set requires 'environment_path' (a res:// Environment .tres to assign).");

            string envPath;
            if (!ResourcePathNormalizer.TryRequireResFilePath(request.EnvironmentPath!, out envPath, out var normError))
                return ToolDispatchResult.Fail("invalid_path", normError);

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling environment_set.");

            // Resolve the WorldEnvironment node.
            WorldEnvironment? worldEnv = null;
            bool created = false;

            if (request.HasNodePath)
            {
                var resolved = NodeTools.ResolvePath(root, request.NodePath!);
                if (resolved == null)
                    return ToolDispatchResult.Fail(
                        "node_not_found",
                        $"WorldEnvironment node not found at path '{request.NodePath}'.");
                if (!(resolved is WorldEnvironment we))
                    return ToolDispatchResult.Fail(
                        "wrong_node_type",
                        $"Node at '{request.NodePath}' is a '{resolved.GetClass()}', not a WorldEnvironment.");
                worldEnv = we;
            }
            else
            {
                // Find the first WorldEnvironment in the edited scene.
                worldEnv = FindWorldEnvironment(root);
                if (worldEnv == null)
                {
                    if (request.CreateIfMissing == true)
                    {
                        worldEnv = new WorldEnvironment();
                        worldEnv.Name = "WorldEnvironment";
                        try
                        {
                            root.AddChild(worldEnv);
                        }
                        catch (System.Exception e)
                        {
                            worldEnv.QueueFree();
                            return ToolDispatchResult.Fail(
                                "execution_error",
                                $"Failed to add WorldEnvironment node to scene root: {e.Message}");
                        }
                        worldEnv.Owner = root;
                        created = true;
                    }
                    else
                    {
                        return ToolDispatchResult.Fail(
                            "environment_node_not_found",
                            "No WorldEnvironment node exists in the edited scene. Pass create_if_missing:true to create one, or node_path to target an existing one.");
                    }
                }
            }

            // Load the Environment resource.
            if (!ResourceLoader.Exists(envPath))
                return ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"No Environment resource exists at '{envPath}'.");

            Environment? env;
            try
            {
                env = ResourceLoader.Load<Environment>(envPath);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "resource_load_failed",
                    $"Failed to load Environment at '{envPath}': {e.Message}");
            }

            if (env == null)
                return ToolDispatchResult.Fail(
                    "wrong_resource_type",
                    $"'{envPath}' loaded but is not an Environment resource.");

            worldEnv.Environment = env;

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(worldEnv.GetPath().ToString())).Append(',');
            sb.Append("\"environmentPath\":").Append(BridgeJson.EscapeString(envPath)).Append(',');
            sb.Append("\"created\":").Append(created ? "true" : "false").Append(',');
            sb.Append("\"assigned\":true}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // Shared helpers — node creation + resolution
        // ===========================================================================

        /// <summary>
        /// Instantiate the concrete light node for a kind. The light node classes
        /// are concrete engine nodes — no ClassExists guard needed (mirrors the
        /// CSG pack's <c>new T()</c> path).
        /// </summary>
        static Node CreateLightNode(LightKind kind)
        {
            switch (kind)
            {
                case LightKind.Directional3D: return new DirectionalLight3D();
                case LightKind.Omni3D: return new OmniLight3D();
                case LightKind.Spot3D: return new SpotLight3D();
                case LightKind.Directional2D: return new DirectionalLight2D();
                case LightKind.Point2D: return new PointLight2D();
                default:
                    throw new System.ArgumentException(
                        $"Unknown light kind '{kind}' (cannot instantiate).");
            }
        }

        /// <summary>
        /// Resolve <paramref name="nodePath"/> to a light node in the edited
        /// scene. Fails with <c>no_edited_scene</c> / <c>node_not_found</c> /
        /// <c>wrong_node_type</c> (the node is not a <c>Light3D</c> or
        /// <c>Light2D</c>) via <paramref name="error"/>. The resolved
        /// <paramref name="lightBase"/> is the node typed as <c>Node</c> (the
        /// caller uses <c>is</c> pattern matching to pick the Light3D / Light2D
        /// surface).
        /// </summary>
        static bool TryResolveLight(string nodePath, out Node node, out Node lightBase, out ToolDispatchResult error)
        {
            node = null!;
            lightBase = null!;
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

            if (!(resolved is Light3D) && !(resolved is Light2D))
            {
                error = ToolDispatchResult.Fail(
                    "wrong_node_type",
                    $"Node at '{nodePath}' is a '{resolved.GetClass()}', not a Light3D / Light2D.");
                return false;
            }

            node = resolved;
            lightBase = resolved;
            return true;
        }

        /// <summary>
        /// Depth-first search for the first <c>WorldEnvironment</c> in the edited
        /// scene. Excludes internal children (editor-only helpers).
        /// </summary>
        static WorldEnvironment? FindWorldEnvironment(Node root)
        {
            if (root is WorldEnvironment we) return we;
            int childCount = root.GetChildCount(includeInternal: false);
            for (int i = 0; i < childCount; i++)
            {
                var child = root.GetChild(i, includeInternal: false);
                if (child == null) continue;
                var found = FindWorldEnvironment(child);
                if (found != null) return found;
            }
            return null;
        }

        // ===========================================================================
        // Shared helpers — scalar field application (the heart of the allow-list)
        // ===========================================================================

        /// <summary>
        /// Apply one allow-listed light scalar field to a light node, appending
        /// the field name to <paramref name="applied"/> on success or a message
        /// to <paramref name="warnings"/> on a parse/type failure. Non-aborting.
        /// Centralized so <c>light_create</c>, <c>light_set</c>, and
        /// <c>light_modify</c> share one validation + clamping path.
        ///
        /// <para>
        /// Allow-listed fields (each validated against the node's type):
        /// <list type="bullet">
        /// <item><description><c>color</c> (Color string "r,g,b[,a]") — Light3D /
        /// Light2D.</description></item>
        /// <item><description><c>energy</c> (float, clamped ≥ 0) — Light3D /
        /// Light2D.</description></item>
        /// <item><description><c>range</c> (float, clamped strictly positive) —
        /// Omni/Spot3D (LightSize), PointLight2D (texture_height when applicable;
        /// here maps to the 2D range analog where present).</description></item>
        /// <item><description><c>spot_angle</c> (float degrees, clamped
        /// [0.01,180]) — SpotLight3D only.</description></item>
        /// <item><description><c>attenuation</c> (float ≥ 0) — Omni/Spot3D
        /// (Param.Attenuation).</description></item>
        /// <item><description><c>shadow_enabled</c> (bool) — Light3D /
        /// Light2D.</description></item>
        /// </list>
        /// </para>
        /// </summary>
        static void ApplyLightField(Node node, string field, string? valueRaw,
            List<string> applied, List<string> warnings)
        {
            if (valueRaw == null)
            {
                warnings.Add($"{field}: value is null/absent (skipped).");
                return;
            }

            switch (field)
            {
                case "color":
                {
                    if (TryParseColor(StripQuotes(valueRaw), out var color))
                    {
                        if (node is Light3D l3d) { l3d.LightColor = color; applied.Add(field); }
                        else if (node is Light2D l2d) { l2d.Color = color; applied.Add(field); }
                        else warnings.Add($"{field}: node '{node.GetClass()}' has no color property.");
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as 'r,g,b[,a]'.");
                    break;
                }
                case "energy":
                {
                    if (TryParseFloat(StripQuotes(valueRaw), out var rawEnergy))
                    {
                        var energy = LightPropertyClamp.ClampEnergy(rawEnergy);
                        if (node is Light3D l3d) { l3d.LightEnergy = energy; applied.Add(field); }
                        else if (node is Light2D l2d) { l2d.Energy = energy; applied.Add(field); }
                        else warnings.Add($"{field}: node '{node.GetClass()}' has no energy property.");
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as float.");
                    break;
                }
                case "range":
                {
                    if (TryParseFloat(StripQuotes(valueRaw), out var rawRange))
                    {
                        var range = LightPropertyClamp.ClampRange(rawRange);
                        if (node is OmniLight3D omni) { omni.OmniRange = range; applied.Add(field); }
                        else if (node is SpotLight3D spot) { spot.SpotRange = range; applied.Add(field); }
                        else warnings.Add($"{field}: node '{node.GetClass()}' has no range property (only Omni/Spot3D).");
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as float.");
                    break;
                }
                case "spot_angle":
                {
                    if (TryParseFloat(StripQuotes(valueRaw), out var rawAngle))
                    {
                        var angle = LightPropertyClamp.ClampSpotAngle(rawAngle);
                        if (node is SpotLight3D spot) { spot.SpotAngle = angle; applied.Add(field); }
                        else warnings.Add($"{field}: node '{node.GetClass()}' has no spot_angle property (SpotLight3D only).");
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as float.");
                    break;
                }
                case "attenuation":
                {
                    if (TryParseFloat(StripQuotes(valueRaw), out var rawAtt))
                    {
                        var att = LightPropertyClamp.ClampEnergy(rawAtt);
                        if (node is OmniLight3D omni) { omni.OmniAttenuation = att; applied.Add(field); }
                        else if (node is SpotLight3D spot) { spot.SpotAttenuation = att; applied.Add(field); }
                        else warnings.Add($"{field}: node '{node.GetClass()}' has no attenuation property (Omni/Spot3D only).");
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as float.");
                    break;
                }
                case "shadow_enabled":
                {
                    if (TryParseBool(StripQuotes(valueRaw), out var shadow))
                    {
                        if (node is Light3D l3d) { l3d.ShadowEnabled = shadow; applied.Add(field); }
                        else if (node is Light2D l2d) { l2d.ShadowEnabled = shadow; applied.Add(field); }
                        else warnings.Add($"{field}: node '{node.GetClass()}' has no shadow_enabled property.");
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as bool.");
                    break;
                }
                default:
                    warnings.Add($"{field}: unsupported_field (not in the light allow-list).");
                    break;
            }
        }

        /// <summary>
        /// True when <paramref name="field"/> is in the light allow-list AND
        /// applies to the resolved light node's type. The handler uses this to
        /// surface <c>unsupported_field</c> from <c>light_set</c> /
        /// <c>light_modify</c> before the per-field apply path (which itself
        /// warns on a type mismatch). Centralized so the field vocabulary is one
        /// source of truth.
        /// </summary>
        static bool IsAllowedLightField(string field, Node lightBase)
        {
            switch (field)
            {
                case "color":
                case "energy":
                case "shadow_enabled":
                    // Common to every light (3D + 2D).
                    return lightBase is Light3D || lightBase is Light2D;
                case "range":
                    return lightBase is OmniLight3D || lightBase is SpotLight3D;
                case "spot_angle":
                    return lightBase is SpotLight3D;
                case "attenuation":
                    return lightBase is OmniLight3D || lightBase is SpotLight3D;
                default:
                    return false;
            }
        }

        // ===========================================================================
        // Shared helpers — fields-map walking for light_modify
        // ===========================================================================

        /// <summary>
        /// Walk a raw JSON object slice (the inner text between the outer braces
        /// of a <c>fields</c> map) and yield each top-level {field → verbatim
        /// value} pair. The slice comes from <see cref="LightModifyBody.FieldsRaw"/>.
        /// Each pair's value is the verbatim JSON token (quotes preserved for
        /// strings, braces for objects) the per-field apply path re-parses.
        /// Honors backslash escapes + nested objects/arrays so a comma inside a
        /// nested string or object does not split the entry early.
        /// </summary>
        static List<(string field, string? valueRaw)> ExtractFieldsMapEntries(string slice)
        {
            var entries = new List<(string, string?)>();
            int i = 0;
            while (i < slice.Length)
            {
                // Skip whitespace + commas between entries.
                while (i < slice.Length && (char.IsWhiteSpace(slice[i]) || slice[i] == ',')) i++;
                if (i >= slice.Length) break;

                // Expect a quoted key.
                if (slice[i] != '"') { i++; continue; }
                var key = SliceQuotedString(slice, i, out var keyEnd);
                i = keyEnd;

                // Skip whitespace + the colon.
                while (i < slice.Length && (char.IsWhiteSpace(slice[i]) || slice[i] == ':')) i++;
                if (i >= slice.Length) break;

                // Slice the value (verbatim token).
                var value = SliceValueToken(slice, i, out var valueEnd);
                i = valueEnd;

                if (!string.IsNullOrEmpty(key))
                    entries.Add((key!, value));
            }
            return entries;
        }

        /// <summary>
        /// Slice a quoted JSON string starting at <paramref name="start"/>
        /// (the opening quote), returning the unescaped content + the index just
        /// past the closing quote (in <paramref name="end"/>).
        /// </summary>
        static string SliceQuotedString(string s, int start, out int end)
        {
            end = start + 1;
            var sb = new StringBuilder();
            while (end < s.Length)
            {
                var c = s[end];
                if (c == '\\' && end + 1 < s.Length)
                {
                    var nxt = s[end + 1];
                    switch (nxt)
                    {
                        case '"': sb.Append('"'); end += 2; continue;
                        case '\\': sb.Append('\\'); end += 2; continue;
                        case '/': sb.Append('/'); end += 2; continue;
                        case 'n': sb.Append('\n'); end += 2; continue;
                        case 'r': sb.Append('\r'); end += 2; continue;
                        case 't': sb.Append('\t'); end += 2; continue;
                        default: sb.Append(nxt); end += 2; continue;
                    }
                }
                if (c == '"') { end++; return sb.ToString(); }
                sb.Append(c);
                end++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Slice a verbatim JSON value token starting at
        /// <paramref name="start"/>: a quoted string (content returned with
        /// surrounding quotes stripped via the caller's StripQuotes), a balanced
        /// object/array, or a bare primitive up to the next comma/close.
        /// Returns the verbatim token + the index just past it (in
        /// <paramref name="end"/>).
        /// </summary>
        static string? SliceValueToken(string s, int start, out int end)
        {
            end = start;
            if (start >= s.Length) return null;

            // Quoted string.
            if (s[start] == '"')
            {
                var i = start + 1;
                while (i < s.Length)
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { i += 2; continue; }
                    if (s[i] == '"') { i++; break; }
                    i++;
                }
                end = i;
                return s.Substring(start, i - start);
            }

            // Object / array — balanced slice.
            if (s[start] == '{' || s[start] == '[')
            {
                char open = s[start];
                char close = open == '{' ? '}' : ']';
                int depth = 0;
                int i = start;
                bool inString = false;
                while (i < s.Length)
                {
                    var c = s[i];
                    if (inString)
                    {
                        if (c == '\\' && i + 1 < s.Length) { i += 2; continue; }
                        if (c == '"') inString = false;
                        i++;
                        continue;
                    }
                    if (c == '"') { inString = true; i++; continue; }
                    if (c == open) depth++;
                    else if (c == close)
                    {
                        depth--;
                        if (depth == 0) { i++; break; }
                    }
                    i++;
                }
                end = i;
                return s.Substring(start, i - start);
            }

            // Bare primitive — up to the next comma or close brace/bracket.
            var pe = start;
            while (pe < s.Length && s[pe] != ',' && s[pe] != '}' && s[pe] != ']') pe++;
            end = pe;
            return s.Substring(start, pe - start).Trim();
        }

        // ===========================================================================
        // Shared helpers — parsing + formatting primitives
        // ===========================================================================

        /// <summary>Apply a position string to a Node3D / Node2D. Best-effort: a
        /// malformed vector is ignored (the node keeps the origin). Mirrors the
        /// helper in the other Phase 12 / Phase 16 packs.</summary>
        static void ApplyPosition(Node node, string? position)
        {
            if (string.IsNullOrWhiteSpace(position)) return;
            if (node is Node3D n3d && TryParseVector3(position, out var p3))
                n3d.Position = p3;
            else if (node is Node2D n2d && TryParseVector2(position, out var p2))
                n2d.Position = p2;
        }

        /// <summary>Parse a "x,y,z" string into a Vector3. Verbatim from the CSG
        /// pack. Returns false on a malformed string.</summary>
        static bool TryParseVector3(string? text, out Vector3 v)
        {
            v = Vector3.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text!.Trim().Split(',');
            if (parts.Length < 3) return false;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var y)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        /// <summary>Parse a "x,y" string into a Vector2. Verbatim from NodeTools.
        /// Returns false on a malformed string.</summary>
        static bool TryParseVector2(string? text, out Vector2 v)
        {
            v = Vector2.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text!.Trim().Split(',');
            if (parts.Length < 2) return false;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var y)) return false;
            v = new Vector2(x, y);
            return true;
        }

        /// <summary>Parse a "r,g,b[,a]" string (0–1 floats) into a Color. Verbatim
        /// from NodeTools.TryParseColor. Returns false on a malformed string.
        /// Accepts 3-component input by filling alpha with 1.</summary>
        static bool TryParseColor(string? text, out Color c)
        {
            c = Colors.White;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text!.Trim().Split(',');
            if (parts.Length < 3) return false;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var r)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var g)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var b)) return false;
            float a = 1f;
            if (parts.Length >= 4 &&
                float.TryParse(parts[3].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var parsedA))
                a = parsedA;
            c = new Color(r, g, b, a);
            return true;
        }

        /// <summary>Parse a float with the invariant culture.</summary>
        static bool TryParseFloat(string? text, out float v)
        {
            v = 0f;
            if (string.IsNullOrWhiteSpace(text)) return false;
            return float.TryParse(text!.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out v);
        }

        /// <summary>Parse a bool from a raw string value. Accepts JSON bare
        /// tokens true/false (case-insensitive to tolerate "True").</summary>
        static bool TryParseBool(string raw, out bool value)
        {
            var t = raw.Trim().Trim('"');
            if (string.Equals(t, "true", System.StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
            if (string.Equals(t, "false", System.StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
            value = false;
            return false;
        }

        /// <summary>Strip surrounding quotes from a verbatim JSON string token
        /// (if present). Non-string tokens pass through unchanged.</summary>
        static string StripQuotes(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw!.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
                return raw.Substring(1, raw.Length - 2);
            return raw;
        }

        /// <summary>Render a float with the invariant culture.</summary>
        static string FloatStr(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>Render a string list as a JSON array of escaped strings.</summary>
        static string JsonStringArray(List<string> items)
        {
            var sb = new StringBuilder(64);
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(items[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
#endif
