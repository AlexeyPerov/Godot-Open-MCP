#if TOOLS
#nullable enable
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Particles domain pack (P12.3) — five typed tools for Godot 4.3+ GPU particle emitters
    /// (<c>GpuParticles2D</c>, <c>GpuParticles3D</c>). The third Phase 12 domain pack; mirrors the
    /// P12.1 tilemap / P12.2 navigation packs' folder layout, registration shape, gate policy, and
    /// group assignment.
    ///
    /// <para>
    /// <b>Fidelity:</b> adapt — architecture (embedded domain handlers under
    /// <c>Tools/Extensions/&lt;Domain&gt;/</c>, one <c>Register*Tools()</c> called from
    /// <see cref="GodotOpenMcpPlugin"/>, every mutator declaring <c>isMutating:true</c> /
    /// <c>defaultGate:"enforce"</c> / <c>group:"particles"</c> and validating <c>paths_hint</c> at
    /// the handler level) is copied from Unity Open MCP's
    /// <c>TypedTools/Extensions/ParticleSystem/ParticleSystemTools.cs</c>. Unity ships only a 2-tool
    /// surface (particle_system_get / modify); the Godot catalog expands it to 5
    /// (defaults / create / configure / set_emitting / get) — the deltas are:
    /// (1) the Godot surface is node-based (GpuParticles2D/3D are Node subclasses, not a Unity
    /// ParticleSystem component attached to a GameObject) — create makes a node, not a component add;
    /// (2) two parallel class hierarchies (2D vs 3D) selected by a <c>dimension</c> arg — Unity has
    /// one ParticleSystem in 3D world space;
    /// (3) an explicit scalar allow-list with centralized clamping (<see cref="ParticlesPropertyClamp"/>)
    /// rather than Unity's broader module-field patch — particles tuning has many interdependent
    /// properties and easy-to-set invalid ranges, so the pack restricts to a curated scalar surface;
    /// (4) an explicit <c>set_emitting</c> tool (start/stop emission + optional restart) instead of
    /// only modifying the <c>emitting</c> property through configure — the catalog surfaces it as a
    /// first-class verb;
    /// (5) no full <c>ParticleProcessMaterial</c> graph authoring in v1 — create/configure accept an
    /// optional <c>process_material_path</c> pointing at an existing <c>ParticleProcessMaterial</c>;
    /// deep material property editing is deferred;
    /// (6) no package compile gate — GpuParticles is an engine module present in every 4.3+ build.
    /// </para>
    ///
    /// <para>
    /// <b>Gate contract.</b> The three mutating handlers (<c>create</c> / <c>configure</c> /
    /// <c>set_emitting</c>) register with <see cref="BridgeToolEntry.DefaultGate"/> <c>"enforce"</c>
    /// and validate <c>paths_hint</c> themselves (mirrors the P4.x resource/filesystem/editor
    /// mutators and the P12.1/P12.2 domain mutators). The dispatch layer rejects an empty hint when
    /// the effective gate is not <c>off</c>; the handler-level guard ALSO fires when an agent
    /// overrides with <c>gate:"off"</c>, so <c>paths_hint</c> is always required for these tools.
    /// The read-only <c>defaults</c> / <c>get</c> have no gate surface.
    /// </para>
    ///
    /// <para>
    /// <b>Owner / persistence.</b> <c>create</c> sets the new node's <c>Owner</c> to the edited
    /// scene root so it persists in the <c>.tscn</c> on save — same step every node creator
    /// performs. Every mutator that changes scene state calls
    /// <see cref="EditorInterface.MarkSceneAsUnsaved"/> +
    /// <see cref="SceneTools.MarkEditedSceneDirty"/> so the bridge-tracked dirty flag the
    /// <c>scene_open</c> guard consults stays honest.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): every handler touches <see cref="EditorInterface"/> and live
    /// GpuParticles node objects. The pure-managed pieces (<see cref="ParticlesConfigureBody"/> /
    /// <see cref="ParticlesSetEmittingBody"/> / … / <see cref="ParticlesDimension"/> /
    /// <see cref="ParticlesPropertyClamp"/>) live outside this guard and are unit-tested.
    /// </summary>
    internal static class ParticlesTools
    {
        internal const string ParticlesDefaultsToolName = "godot_open_mcp_particles_defaults";
        internal const string ParticlesCreateToolName = "godot_open_mcp_particles_create";
        internal const string ParticlesConfigureToolName = "godot_open_mcp_particles_configure";
        internal const string ParticlesSetEmittingToolName = "godot_open_mcp_particles_set_emitting";
        internal const string ParticlesGetToolName = "godot_open_mcp_particles_get";

        /// <summary>
        /// Register the particles tool family. The three mutators declare <c>defaultGate:"enforce"</c>
        /// and <c>isMutating:true</c>; the read-only <c>defaults</c> + <c>get</c> are <c>off</c>. All
        /// five belong to the <c>particles</c> group (the P8 stub now filled). Registered once at
        /// plugin enable; idempotent (the registry replaces on re-register).
        /// </summary>
        internal static void RegisterParticlesTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ParticlesDefaultsToolName,
                isMutating: false,
                defaultGate: "off",
                group: "particles",
                handler: Defaults));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ParticlesCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "particles",
                handler: Create));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ParticlesConfigureToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "particles",
                handler: Configure));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ParticlesSetEmittingToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "particles",
                handler: SetEmitting));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ParticlesGetToolName,
                isMutating: false,
                defaultGate: "off",
                group: "particles",
                handler: Get));
        }

        // ===========================================================================
        // 1. godot_open_mcp_particles_defaults (read-only helper)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_particles_defaults</c>. Pure helper — returns recommended
        /// starter scalars for a 2D or 3D GpuParticles emitter as a JSON object an agent can spread
        /// into <c>particles_create</c> / <c>particles_configure</c>. No scene required; no gate
        /// surface. A dimension other than "2d"/"3d" returns <c>invalid_parameter</c>.
        ///
        /// <para>
        /// The scalar keys match the Godot property names the agent will reuse in
        /// <c>particles_configure</c> (snake_case in the MCP schema → camelCase in the result mirrors
        /// the NodeData serializer convention; the agent uses the snake_case schema keys when calling
        /// configure). The defaults are conservative mid-range values, not engine defaults — they are
        /// a sensible starting point an agent can tune from.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Defaults(string body)
        {
            var request = ParticlesDefaultsBody.Parse(body);
            if (request.Dimension == ParticlesDimension.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "particles_defaults requires 'dimension' to be one of: \"2d\", \"3d\".");

            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"dimension\":").Append(BridgeJson.EscapeString(
                request.Dimension == ParticlesDimension.TwoD ? "2d" : "3d")).Append(',');
            sb.Append("\"properties\":{");
            // 2D and 3D share the same scalar surface; the defaults differ only in amount (2D is
            // cheaper by default since 2D particles render as sprites, not billboards). The values
            // are inside the clamp ranges so a spread-into-configure round-trips without clamping.
            if (request.Dimension == ParticlesDimension.TwoD)
            {
                sb.Append("\"amount\":30,").Append("\"lifetime\":1.0,").Append("\"oneShot\":false,");
                sb.Append("\"preprocess\":0,").Append("\"speedScale\":1.0,").Append("\"explosiveness\":0,");
                sb.Append("\"randomness\":0,").Append("\"fixedFps\":0,").Append("\"interpolate\":true,");
                sb.Append("\"fractDelta\":true,").Append("\"localCoords\":true");
            }
            else
            {
                sb.Append("\"amount\":16,").Append("\"lifetime\":1.0,").Append("\"oneShot\":false,");
                sb.Append("\"preprocess\":0,").Append("\"speedScale\":1.0,").Append("\"explosiveness\":0,");
                sb.Append("\"randomness\":0,").Append("\"fixedFps\":0,").Append("\"interpolate\":true,");
                sb.Append("\"fractDelta\":true,").Append("\"localCoords\":true");
            }
            sb.Append("}}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 2. godot_open_mcp_particles_create
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_particles_create</c>. Creates a <c>GpuParticles2D</c> or
        /// <c>GpuParticles3D</c> node in the edited scene and returns its NodeData (same shape as
        /// <c>node_create</c>). The new node's owner is the edited scene root; the scene is marked
        /// unsaved. An optional initial <c>properties</c> object is applied post-creation (same
        /// allow-list + clamping as <c>configure</c>), and an optional <c>process_material_path</c>
        /// assigns an existing <c>ParticleProcessMaterial</c> — Godot emitters do nothing visible
        /// without one, so the tool surfaces the assignment as a first-class create arg.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>invalid_parameter</c> (bad dimension),
        /// <c>no_edited_scene</c>, <c>parent_not_found</c>, <c>create_failed</c>,
        /// <c>resource_not_found</c>, <c>resource_load_failed</c> (bad process material).
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Create(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "particles_create is mutating; pass a non-empty paths_hint scoped to the edited scene path (res://...tscn).");

            var request = ParticlesCreateBody.Parse(body);
            if (request.Dimension == ParticlesDimension.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "particles_create requires 'dimension' to be one of: \"2d\", \"3d\".");

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling particles_create.");

            Node parent = root;
            if (!string.IsNullOrEmpty(request.ParentNodePath))
            {
                parent = NodeTools.ResolvePath(root, request.ParentNodePath!);
                if (parent == null)
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Parent node not found at path '{request.ParentNodePath}'.");
            }

            Node emitter;
            try
            {
                // GpuParticles2D/3D are concrete engine nodes — no ClassExists guard needed.
                emitter = request.Dimension == ParticlesDimension.TwoD ? new GpuParticles2D() : new GpuParticles3D();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate particles emitter: {e.Message}");
            }

            if (!string.IsNullOrEmpty(request.Name))
                emitter.Name = request.Name;

            try
            {
                parent.AddChild(emitter);
            }
            catch (System.Exception e)
            {
                emitter.QueueFree();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add particles emitter to parent: {e.Message}");
            }

            emitter.Owner = root;

            // Both GpuParticles2D (Node2D) and GpuParticles3D (Node3D) have a Position, but through
            // different base classes. Apply best-effort; a malformed vector is ignored.
            ApplyPosition(emitter, request.Position);

            // Optional process material assignment. An emitter without a process material renders
            // nothing; surfacing the assignment here saves a second configure round-trip. A bad path
            // or type after AddChild still frees the node is not worth the complexity — the node is
            // created; only the material assignment is rejected, surfaced as resource_load_failed.
            if (request.HasProcessMaterialPath && !TryAssignProcessMaterial(emitter, request.ProcessMaterialPath!, out var matError))
                return matError;

            // Optional initial scalar properties (same allow-list + clamping as configure). Applied
            // after AddChild + material so the emitter is fully parented before tuning. A scalar
            // failure inside the initial batch does NOT abort create — the node exists; the result
            // still returns its NodeData. The agent can re-apply via configure.
            ApplyScalarsFromRawBody(emitter, body);

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();
            EditorInterface.Singleton.EditNode(emitter);

            return ToolDispatchResult.Ok(NodeTools.ToNodeData(emitter).ToJsonString());
        }

        // ===========================================================================
        // 3. godot_open_mcp_particles_configure
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_particles_configure</c>. Patches clamped scalar properties
        /// on a <c>GpuParticles2D</c> or <c>GpuParticles3D</c>. Each scalar is applied independently
        /// (non-aborting — a bad value on one key does not skip the rest, matching
        /// <c>node_modify</c>'s contract). Unknown keys are rejected by the schema
        /// (<c>additionalProperties:false</c>), so the body parser only extracts the fixed known
        /// allow-list. Clamps: amount in [1, 100000], lifetime &gt; 0, preprocess ≥ 0, speed_scale ≥ 0,
        /// explosiveness/randomness in [0, 1], fixed_fps ≥ 0; bools pass through. A value outside the
        /// valid range is clamped to the nearest bound and the clamped value is echoed in the result.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>, <c>wrong_node_type</c>. A scalar that is
        /// present but non-numeric is silently skipped (the body parser returns null).
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Configure(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "particles_configure is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = ParticlesConfigureBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "particles_configure requires 'node_path' (the GpuParticles emitter to configure).");

            if (!TryResolveAnyNode(request.NodePath!, out var node, out var resolveError))
                return resolveError;

            // Resolve to the 2D or 3D emitter. Both expose the same scalar property names, but
            // through different classes — handle each branch explicitly so a non-emitter node
            // returns wrong_node_type rather than a cast fault.
            int? appliedAmount = null, appliedFixedFps = null;
            float? appliedLifetime = null, appliedPreprocess = null, appliedSpeedScale = null;
            float? appliedExplosiveness = null, appliedRandomness = null;
            bool? appliedOneShot = null, appliedInterpolate = null, appliedFractDelta = null, appliedLocalCoords = null;

            if (node is GpuParticles2D p2d)
            {
                if (request.Amount.HasValue) { p2d.Amount = appliedAmount = ParticlesPropertyClamp.ClampAmount(request.Amount.Value); }
                if (request.Lifetime.HasValue) { p2d.Lifetime = appliedLifetime = ParticlesPropertyClamp.ClampLifetime(request.Lifetime.Value); }
                if (request.Preprocess.HasValue) { p2d.Preprocess = appliedPreprocess = ParticlesPropertyClamp.ClampNonNegative(request.Preprocess.Value); }
                if (request.SpeedScale.HasValue) { p2d.SpeedScale = appliedSpeedScale = ParticlesPropertyClamp.ClampNonNegative(request.SpeedScale.Value); }
                if (request.Explosiveness.HasValue) { p2d.Explosiveness = appliedExplosiveness = ParticlesPropertyClamp.ClampUnit(request.Explosiveness.Value); }
                if (request.Randomness.HasValue) { p2d.Randomness = appliedRandomness = ParticlesPropertyClamp.ClampUnit(request.Randomness.Value); }
                if (request.FixedFps.HasValue) { p2d.FixedFps = appliedFixedFps = ParticlesPropertyClamp.ClampFixedFps(request.FixedFps.Value); }
                if (request.OneShot.HasValue) { p2d.OneShot = appliedOneShot = request.OneShot.Value; }
                if (request.Interpolate.HasValue) { p2d.Interpolate = appliedInterpolate = request.Interpolate.Value; }
                if (request.FractDelta.HasValue) { p2d.FractDelta = appliedFractDelta = request.FractDelta.Value; }
                if (request.LocalCoords.HasValue) { p2d.LocalCoords = appliedLocalCoords = request.LocalCoords.Value; }
            }
            else if (node is GpuParticles3D p3d)
            {
                if (request.Amount.HasValue) { p3d.Amount = appliedAmount = ParticlesPropertyClamp.ClampAmount(request.Amount.Value); }
                if (request.Lifetime.HasValue) { p3d.Lifetime = appliedLifetime = ParticlesPropertyClamp.ClampLifetime(request.Lifetime.Value); }
                if (request.Preprocess.HasValue) { p3d.Preprocess = appliedPreprocess = ParticlesPropertyClamp.ClampNonNegative(request.Preprocess.Value); }
                if (request.SpeedScale.HasValue) { p3d.SpeedScale = appliedSpeedScale = ParticlesPropertyClamp.ClampNonNegative(request.SpeedScale.Value); }
                if (request.Explosiveness.HasValue) { p3d.Explosiveness = appliedExplosiveness = ParticlesPropertyClamp.ClampUnit(request.Explosiveness.Value); }
                if (request.Randomness.HasValue) { p3d.Randomness = appliedRandomness = ParticlesPropertyClamp.ClampUnit(request.Randomness.Value); }
                if (request.FixedFps.HasValue) { p3d.FixedFps = appliedFixedFps = ParticlesPropertyClamp.ClampFixedFps(request.FixedFps.Value); }
                if (request.OneShot.HasValue) { p3d.OneShot = appliedOneShot = request.OneShot.Value; }
                if (request.Interpolate.HasValue) { p3d.Interpolate = appliedInterpolate = request.Interpolate.Value; }
                if (request.FractDelta.HasValue) { p3d.FractDelta = appliedFractDelta = request.FractDelta.Value; }
                if (request.LocalCoords.HasValue) { p3d.LocalCoords = appliedLocalCoords = request.LocalCoords.Value; }
            }
            else
            {
                return ToolDispatchResult.Fail(
                    "wrong_node_type",
                    $"Node at '{request.NodePath}' is a '{node.GetClass()}', not a GpuParticles2D or GpuParticles3D.");
            }

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(node.GetPath().ToString())).Append(',');
            sb.Append("\"applied\":{");
            bool first = true;
            first = AppendIntIfHas(sb, "amount", appliedAmount, first);
            first = AppendFloatIfHas(sb, "lifetime", appliedLifetime, first);
            first = AppendBoolIfHas(sb, "oneShot", appliedOneShot, first);
            first = AppendFloatIfHas(sb, "preprocess", appliedPreprocess, first);
            first = AppendFloatIfHas(sb, "speedScale", appliedSpeedScale, first);
            first = AppendFloatIfHas(sb, "explosiveness", appliedExplosiveness, first);
            first = AppendFloatIfHas(sb, "randomness", appliedRandomness, first);
            first = AppendIntIfHas(sb, "fixedFps", appliedFixedFps, first);
            first = AppendBoolIfHas(sb, "interpolate", appliedInterpolate, first);
            first = AppendBoolIfHas(sb, "fractDelta", appliedFractDelta, first);
            AppendBoolIfHas(sb, "localCoords", appliedLocalCoords, first);
            sb.Append("}}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 4. godot_open_mcp_particles_set_emitting
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_particles_set_emitting</c>. Starts or stops emission on a
        /// <c>GpuParticles2D</c> / <c>GpuParticles3D</c> by flipping the <c>Emitting</c> property.
        /// When <c>restart</c> is true, Godot's <c>Restart()</c> is called first — it clears existing
        /// particles and restarts the emission cycle (useful for one-shot re-fire or resetting a
        /// continuous emitter's accumulator). The scene is marked unsaved.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c> (node_path or
        /// emitting absent / not a bool), <c>no_edited_scene</c>, <c>node_not_found</c>,
        /// <c>wrong_node_type</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult SetEmitting(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "particles_set_emitting is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = ParticlesSetEmittingBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "particles_set_emitting requires 'node_path' (the GpuParticles emitter to toggle).");
            if (!request.Emitting.HasValue)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "particles_set_emitting requires 'emitting' (a boolean: true to start, false to stop).");

            if (!TryResolveAnyNode(request.NodePath!, out var node, out var resolveError))
                return resolveError;

            bool emitting = request.Emitting.Value;

            if (node is GpuParticles2D p2d)
            {
                if (request.Restart) p2d.Restart();
                p2d.Emitting = emitting;
            }
            else if (node is GpuParticles3D p3d)
            {
                if (request.Restart) p3d.Restart();
                p3d.Emitting = emitting;
            }
            else
            {
                return ToolDispatchResult.Fail(
                    "wrong_node_type",
                    $"Node at '{request.NodePath}' is a '{node.GetClass()}', not a GpuParticles2D or GpuParticles3D.");
            }

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(node.GetPath().ToString())).Append(',');
            sb.Append("\"emitting\":").Append(emitting ? "true" : "false").Append(',');
            sb.Append("\"restarted\":").Append(request.Restart ? "true" : "false");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 5. godot_open_mcp_particles_get (read-only)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_particles_get</c>. Reads the scalar configuration of a
        /// <c>GpuParticles2D</c> or <c>GpuParticles3D</c> emitter — the full allow-listed property
        /// set an agent can re-apply via <c>configure</c>, plus the resolved <c>type</c> /
        /// <c>dimension</c> and the <c>process_material_path</c> (or <c>null</c> when unassigned) so
        /// an agent does not need a second probe. No particle-instance arrays in v1.
        ///
        /// <para>
        /// Structured failures: <c>missing_parameter</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Get(string body)
        {
            var request = ParticlesGetBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "particles_get requires 'node_path' (the GpuParticles emitter to read).");

            if (!TryResolveAnyNode(request.NodePath!, out var node, out var resolveError))
                return resolveError;

            var sb = new StringBuilder(320);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(node.GetPath().ToString())).Append(',');
            sb.Append("\"type\":").Append(BridgeJson.EscapeString(node.GetClass())).Append(',');

            // Shared scalar snapshot builder — both classes expose the same property names. The
            // process-material path is read off the ProcessMaterial resource (or null).
            if (node is GpuParticles2D p2)
            {
                sb.Append("\"dimension\":\"2d\",");
                AppendEmitterScalars(sb, p2.Amount, p2.Lifetime, p2.OneShot, p2.Preprocess, p2.SpeedScale,
                    p2.Explosiveness, p2.Randomness, p2.FixedFps, p2.Interpolate, p2.FractDelta, p2.LocalCoords,
                    p2.Emitting, p2.ProcessMaterial?.ResourcePath);
            }
            else if (node is GpuParticles3D p3)
            {
                sb.Append("\"dimension\":\"3d\",");
                AppendEmitterScalars(sb, p3.Amount, p3.Lifetime, p3.OneShot, p3.Preprocess, p3.SpeedScale,
                    p3.Explosiveness, p3.Randomness, p3.FixedFps, p3.Interpolate, p3.FractDelta, p3.LocalCoords,
                    p3.Emitting, p3.ProcessMaterial?.ResourcePath);
            }
            else
            {
                return ToolDispatchResult.Fail(
                    "wrong_node_type",
                    $"Node at '{request.NodePath}' is a '{node.GetClass()}', not a GpuParticles2D or GpuParticles3D.");
            }
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // Shared helpers
        // ===========================================================================

        /// <summary>
        /// Resolve <paramref name="nodePath"/> to any live node in the edited scene. Used by the
        /// configure / set_emitting / get handlers, which then type-check against GpuParticles2D /
        /// GpuParticles3D. Fails with <c>no_edited_scene</c> / <c>node_not_found</c> via
        /// <paramref name="error"/>.
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
        /// take 'x,y,z'. Centralized here so the create handler shares one implementation with the
        /// other packs.
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

        /// <summary>
        /// Load a <c>ParticleProcessMaterial</c> from <paramref name="materialPath"/> and assign it
        /// to the emitter's <c>ProcessMaterial</c>. Works for both GpuParticles2D and GpuParticles3D
        /// (both expose ProcessMaterial as a ProcessMaterial base). Returns false with a structured
        /// error on a missing path or type mismatch; the caller surfaces it.
        /// </summary>
        static bool TryAssignProcessMaterial(Node emitter, string materialPath, out ToolDispatchResult error)
        {
            error = null!;
            if (!ResourceLoader.Exists(materialPath))
            {
                error = ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"Process material not found at '{materialPath}'.");
                return false;
            }

            ProcessMaterial material;
            try
            {
                var loaded = ResourceLoader.Load<ProcessMaterial>(materialPath);
                if (loaded == null)
                {
                    error = ToolDispatchResult.Fail(
                        "resource_load_failed",
                        $"Resource at '{materialPath}' exists but is not a ProcessMaterial (ParticleProcessMaterial is the typical choice for GPU particles).");
                    return false;
                }
                material = loaded;
            }
            catch (System.Exception e)
            {
                error = ToolDispatchResult.Fail("resource_load_failed",
                    $"Failed to load ProcessMaterial from '{materialPath}': {e.Message}");
                return false;
            }

            if (emitter is GpuParticles2D p2d) p2d.ProcessMaterial = material;
            else if (emitter is GpuParticles3D p3d) p3d.ProcessMaterial = material;
            return true;
        }

        /// <summary>
        /// Apply the optional initial scalar <c>properties</c> from a raw create body. Reuses
        /// <see cref="ParticlesConfigureBody.Parse"/> to extract the same allow-list + clamping as
        /// configure — the JsonScalar extractors resolve nested keys inside the properties object.
        /// A bad scalar is silently skipped (non-aborting), matching the configure contract. The
        /// create handler does NOT echo these (it returns NodeData); the agent re-reads via get.
        /// </summary>
        static void ApplyScalarsFromRawBody(Node emitter, string body)
        {
            var scalars = ParticlesConfigureBody.Parse(body);
            if (emitter is GpuParticles2D p2d)
            {
                if (scalars.Amount.HasValue) p2d.Amount = ParticlesPropertyClamp.ClampAmount(scalars.Amount.Value);
                if (scalars.Lifetime.HasValue) p2d.Lifetime = ParticlesPropertyClamp.ClampLifetime(scalars.Lifetime.Value);
                if (scalars.Preprocess.HasValue) p2d.Preprocess = ParticlesPropertyClamp.ClampNonNegative(scalars.Preprocess.Value);
                if (scalars.SpeedScale.HasValue) p2d.SpeedScale = ParticlesPropertyClamp.ClampNonNegative(scalars.SpeedScale.Value);
                if (scalars.Explosiveness.HasValue) p2d.Explosiveness = ParticlesPropertyClamp.ClampUnit(scalars.Explosiveness.Value);
                if (scalars.Randomness.HasValue) p2d.Randomness = ParticlesPropertyClamp.ClampUnit(scalars.Randomness.Value);
                if (scalars.FixedFps.HasValue) p2d.FixedFps = ParticlesPropertyClamp.ClampFixedFps(scalars.FixedFps.Value);
                if (scalars.OneShot.HasValue) p2d.OneShot = scalars.OneShot.Value;
                if (scalars.Interpolate.HasValue) p2d.Interpolate = scalars.Interpolate.Value;
                if (scalars.FractDelta.HasValue) p2d.FractDelta = scalars.FractDelta.Value;
                if (scalars.LocalCoords.HasValue) p2d.LocalCoords = scalars.LocalCoords.Value;
            }
            else if (emitter is GpuParticles3D p3d)
            {
                if (scalars.Amount.HasValue) p3d.Amount = ParticlesPropertyClamp.ClampAmount(scalars.Amount.Value);
                if (scalars.Lifetime.HasValue) p3d.Lifetime = ParticlesPropertyClamp.ClampLifetime(scalars.Lifetime.Value);
                if (scalars.Preprocess.HasValue) p3d.Preprocess = ParticlesPropertyClamp.ClampNonNegative(scalars.Preprocess.Value);
                if (scalars.SpeedScale.HasValue) p3d.SpeedScale = ParticlesPropertyClamp.ClampNonNegative(scalars.SpeedScale.Value);
                if (scalars.Explosiveness.HasValue) p3d.Explosiveness = ParticlesPropertyClamp.ClampUnit(scalars.Explosiveness.Value);
                if (scalars.Randomness.HasValue) p3d.Randomness = ParticlesPropertyClamp.ClampUnit(scalars.Randomness.Value);
                if (scalars.FixedFps.HasValue) p3d.FixedFps = ParticlesPropertyClamp.ClampFixedFps(scalars.FixedFps.Value);
                if (scalars.OneShot.HasValue) p3d.OneShot = scalars.OneShot.Value;
                if (scalars.Interpolate.HasValue) p3d.Interpolate = scalars.Interpolate.Value;
                if (scalars.FractDelta.HasValue) p3d.FractDelta = scalars.FractDelta.Value;
                if (scalars.LocalCoords.HasValue) p3d.LocalCoords = scalars.LocalCoords.Value;
            }
        }

        /// <summary>
        /// Append the shared emitter scalar snapshot to <paramref name="sb"/>. Both GpuParticles2D
        /// and GpuParticles3D expose the same property names, so the read path factors the JSON
        /// building into one helper. Keys are camelCase to mirror the NodeData serializer
        /// convention; an agent re-applies them via configure's snake_case schema keys.
        /// </summary>
        static void AppendEmitterScalars(StringBuilder sb, int amount, float lifetime, bool oneShot,
            float preprocess, float speedScale, float explosiveness, float randomness, int fixedFps,
            bool interpolate, bool fractDelta, bool localCoords, bool emitting, string? processMaterialPath)
        {
            sb.Append("\"properties\":{");
            sb.Append("\"amount\":").Append(amount).Append(',');
            sb.Append("\"lifetime\":").Append(Float(lifetime)).Append(',');
            sb.Append("\"oneShot\":").Append(oneShot ? "true" : "false").Append(',');
            sb.Append("\"preprocess\":").Append(Float(preprocess)).Append(',');
            sb.Append("\"speedScale\":").Append(Float(speedScale)).Append(',');
            sb.Append("\"explosiveness\":").Append(Float(explosiveness)).Append(',');
            sb.Append("\"randomness\":").Append(Float(randomness)).Append(',');
            sb.Append("\"fixedFps\":").Append(fixedFps).Append(',');
            sb.Append("\"interpolate\":").Append(interpolate ? "true" : "false").Append(',');
            sb.Append("\"fractDelta\":").Append(fractDelta ? "true" : "false").Append(',');
            sb.Append("\"localCoords\":").Append(localCoords ? "true" : "false").Append(',');
            sb.Append("\"emitting\":").Append(emitting ? "true" : "false");
            sb.Append("},");
            sb.Append("\"processMaterialPath\":").Append(BridgeJson.EscapeString(processMaterialPath));
        }

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

        /// <summary>Int variant of <see cref="AppendFloatIfHas"/>.</summary>
        static bool AppendIntIfHas(StringBuilder sb, string key, int? value, bool first)
        {
            if (!value.HasValue) return first;
            if (!first) sb.Append(',');
            sb.Append('"').Append(key).Append("\":").Append(value.Value);
            return false;
        }

        /// <summary>Bool variant of <see cref="AppendFloatIfHas"/>.</summary>
        static bool AppendBoolIfHas(StringBuilder sb, string key, bool? value, bool first)
        {
            if (!value.HasValue) return first;
            if (!first) sb.Append(',');
            sb.Append('"').Append(key).Append("\":").Append(value.Value ? "true" : "false");
            return false;
        }

        /// <summary>
        /// Parse a <c>"x,y"</c> string into a <see cref="Vector2"/>. Verbatim from NavigationTools /
        /// TilemapTools / NodeTools.TryParseVector2 — duplicated rather than widening another type's
        /// surface for one helper. Returns false on a malformed string (the caller treats that as
        /// "no value applied").
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
        /// <see cref="TryParseVector2"/> for the 3D create path.
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
