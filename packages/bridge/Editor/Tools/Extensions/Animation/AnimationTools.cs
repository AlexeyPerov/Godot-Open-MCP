#if TOOLS
#nullable enable
using System.Globalization;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Animation domain pack (P12.4) — seven typed tools for Godot 4.3+
    /// <c>AnimationPlayer</c> authoring (player → library → animation → track → key). The
    /// fourth Phase 12 domain pack; mirrors the P12.1–P12.3 packs' folder layout,
    /// registration shape, gate policy, and group assignment.
    ///
    /// <para>
    /// <b>Fidelity:</b> adapt — architecture (embedded domain handlers under
    /// <c>Tools/Extensions/&lt;Domain&gt;/</c>, one <c>Register*Tools()</c> called from
    /// <see cref="GodotOpenMcpPlugin"/>, every mutator declaring <c>isMutating:true</c> /
    /// <c>defaultGate:"enforce"</c> / <c>group:"animation"</c> and validating
    /// <c>paths_hint</c> at the handler level) is copied from Unity Open MCP's
    /// <c>TypedTools/Extensions/Animation/AnimationClipTools.cs</c>. Unity ships a
    /// clip-centric surface (AnimationClip create/get/modify + AnimatorController); the Godot
    /// catalog expands it to a 7-tool player-centric roster (defaults / player_create /
    /// library_add / animation_create / add_track / insert_key / get). The deltas are:
    /// (1) Godot animations live as <c>Animation</c> resources inside named
    /// <c>AnimationLibrary</c> resources registered on an <c>AnimationPlayer</c> node — not
    /// as standalone AnimationClip assets + AnimatorController state machines. The pack skips
    /// AnimatorController entirely (spec design decision §"skip"). No AnimationTree, no
    /// Tween, no .glb retargeting.
    /// (2) explicit <c>add_track</c> / <c>insert_key</c> tools rather than Unity's
    /// single-clip-modify envelope — Godot's Animation API is track-then-key, and surfacing
    /// the two steps lets an agent recover from a bad track_index without rebuilding a clip.
    /// (3) track types in v1 are value + position_3d + rotation_3d + scale_3d (the four
    /// Godot exposes cleanly for create). Blend-shape / method / bezier / audio / animation
    /// tracks are NOT claimed in v1 — an agent requesting one gets
    /// <c>unsupported_track_type</c> (spec design decision §4).
    /// (4) the optional <c>save_path</c> persistence model is deferred — animations are
    /// owned by the AnimationPlayer in the edited scene and persist on scene save (spec
    /// design decision §1).
    /// (5) no package compile gate — AnimationPlayer is an engine module present in every
    /// 4.3+ build.
    /// </para>
    ///
    /// <para>
    /// <b>Gate contract.</b> The five mutating handlers (<c>player_create</c> /
    /// <c>library_add</c> / <c>animation_create</c> / <c>add_track</c> / <c>insert_key</c>)
    /// register with <see cref="BridgeToolEntry.DefaultGate"/> <c>"enforce"</c> and validate
    /// <c>paths_hint</c> themselves (mirrors the P4.x resource/filesystem/editor mutators and
    /// the P12.1–P12.3 domain mutators). The dispatch layer rejects an empty hint when the
    /// effective gate is not <c>off</c>; the handler-level guard ALSO fires when an agent
    /// overrides with <c>gate:"off"</c>, so <c>paths_hint</c> is always required for these
    /// tools. The read-only <c>defaults</c> / <c>get</c> have no gate surface.
    /// </para>
    ///
    /// <para>
    /// <b>Owner / persistence.</b> <c>player_create</c> sets the new node's <c>Owner</c> to
    /// the edited scene root so it persists in the <c>.tscn</c> on save. AnimationLibrary and
    /// Animation resources created by <c>library_add</c> / <c>animation_create</c> are
    /// parented to the AnimationPlayer's library dictionary (via
    /// <c>AnimationMixer.AddAnimationLibrary</c> / <c>AnimationLibrary.AddAnimation</c>);
    /// their ownership is tracked by the player's serialized state, so they persist when the
    /// scene is saved. Every mutator calls <see cref="EditorInterface.MarkSceneAsUnsaved"/> +
    /// <see cref="SceneTools.MarkEditedSceneDirty"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Track path format (the #1 failure mode).</b> Godot resolves track paths relative to
    /// the AnimationPlayer's <c>root_node</c> (an AnimationMixer property; default is the
    /// player's parent). The path is a NodePath with an optional sub-path to the animated
    /// property, e.g. <c>"Sprite2D:position"</c> animates the <c>position</c> property of a
    /// sibling/friend node named Sprite2D. A path that does not resolve (wrong node name, or
    /// animating a property the node does not have) is accepted by Godot at authoring time
    /// but produces no effect at playback — this pack surfaces the resolved path back in the
    /// result so an agent can verify, but does not validate the path against the scene tree
    /// (Godot itself does not, and the player may animate nodes added later). See
    /// <c>README.md</c> for the working API sequence.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): every handler touches <see cref="EditorInterface"/>
    /// and live AnimationPlayer / AnimationLibrary / Animation objects. The pure-managed
    /// pieces (<see cref="AnimationCreateBody"/> / <see cref="AnimationAddTrackBody"/> / … /
    /// <see cref="AnimationLoopMode"/> / <see cref="AnimationTrackType"/> /
    /// <see cref="AnimationKeyValue"/>) live outside this guard and are unit-tested.
    /// </summary>
    internal static class AnimationTools
    {
        internal const string AnimationDefaultsToolName = "godot_open_mcp_animation_defaults";
        internal const string AnimationPlayerCreateToolName = "godot_open_mcp_animation_player_create";
        internal const string AnimationLibraryAddToolName = "godot_open_mcp_animation_library_add";
        internal const string AnimationCreateToolName = "godot_open_mcp_animation_create";
        internal const string AnimationAddTrackToolName = "godot_open_mcp_animation_add_track";
        internal const string AnimationInsertKeyToolName = "godot_open_mcp_animation_insert_key";
        internal const string AnimationGetToolName = "godot_open_mcp_animation_get";

        /// <summary>
        /// Register the animation tool family. The five mutators declare
        /// <c>defaultGate:"enforce"</c> and <c>isMutating:true</c>; the read-only
        /// <c>defaults</c> + <c>get</c> are <c>off</c>. All seven belong to the
        /// <c>animation</c> group (the P8 stub now filled). Registered once at plugin enable;
        /// idempotent (the registry replaces on re-register).
        /// </summary>
        internal static void RegisterAnimationTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: AnimationDefaultsToolName,
                isMutating: false,
                defaultGate: "off",
                group: "animation",
                handler: Defaults));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: AnimationPlayerCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "animation",
                handler: PlayerCreate));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: AnimationLibraryAddToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "animation",
                handler: LibraryAdd));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: AnimationCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "animation",
                handler: AnimationCreate));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: AnimationAddTrackToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "animation",
                handler: AddTrack));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: AnimationInsertKeyToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "animation",
                handler: InsertKey));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: AnimationGetToolName,
                isMutating: false,
                defaultGate: "off",
                group: "animation",
                handler: Get));
        }

        // ===========================================================================
        // 1. godot_open_mcp_animation_defaults (read-only helper)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_animation_defaults</c>. Pure helper — returns the
        /// recommended starter Animation length + loop mode as a JSON object an agent can
        /// spread into <c>animation_create</c>. No scene required; no gate surface. Dimension-
        /// agnostic in v1 (Animation is one class).
        /// </summary>
        internal static ToolDispatchResult Defaults(string body)
        {
            _ = AnimationDefaultsBody.Parse(body);

            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"length\":1.0,");
            sb.Append("\"loopMode\":").Append(BridgeJson.EscapeString("none"));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 2. godot_open_mcp_animation_player_create
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_animation_player_create</c>. Creates an
        /// <c>AnimationPlayer</c> node in the edited scene and returns its NodeData (same
        /// shape as <c>node_create</c>). The new node's owner is the edited scene root; the
        /// scene is marked unsaved. No initial libraries/clips — use <c>library_add</c> and
        /// <c>animation_create</c> afterwards.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>no_edited_scene</c>,
        /// <c>parent_not_found</c>, <c>create_failed</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult PlayerCreate(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "animation_player_create is mutating; pass a non-empty paths_hint scoped to the edited scene path (res://...tscn).");

            var request = AnimationPlayerCreateBody.Parse(body);

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling animation_player_create.");

            Node parent = root;
            if (!string.IsNullOrEmpty(request.ParentNodePath))
            {
                parent = NodeTools.ResolvePath(root, request.ParentNodePath!);
                if (parent == null)
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Parent node not found at path '{request.ParentNodePath}'.");
            }

            Node player;
            try
            {
                // AnimationPlayer is a concrete engine node — no ClassExists guard needed.
                player = new AnimationPlayer();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate AnimationPlayer: {e.Message}");
            }

            if (!string.IsNullOrEmpty(request.Name))
                player.Name = request.Name;

            try
            {
                parent.AddChild(player);
            }
            catch (System.Exception e)
            {
                player.QueueFree();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add AnimationPlayer to parent: {e.Message}");
            }

            player.Owner = root;

            // AnimationPlayer derives from Node (not Node2D/Node3D), so there is no Position
            // to apply. The request body carries position for forward-compat only; it is
            // ignored here (an AnimationPlayer is not a spatial node).

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();
            EditorInterface.Singleton.EditNode(player);

            return ToolDispatchResult.Ok(NodeTools.ToNodeData(player).ToJsonString());
        }

        // ===========================================================================
        // 3. godot_open_mcp_animation_library_add
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_animation_library_add</c>. Adds an empty
        /// <c>AnimationLibrary</c> registered under the given name on the target
        /// <c>AnimationPlayer</c>. The library name defaults to "default" when absent. A
        /// library already registered under that name returns <c>already_exists</c> (no
        /// silent overwrite — spec design decision §3).
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>, <c>wrong_node_type</c>,
        /// <c>already_exists</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult LibraryAdd(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "animation_library_add is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = AnimationLibraryAddBody.Parse(body);
            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "animation_library_add requires 'node_path' (the AnimationPlayer to add the library to).");

            if (!TryResolvePlayer(request.NodePath!, out var player, out var resolveError))
                return resolveError;

            var libraryName = request.EffectiveLibrary;
            if (player.HasAnimationLibrary(libraryName))
                return ToolDispatchResult.Fail(
                    "already_exists",
                    $"AnimationPlayer at '{request.NodePath}' already has a library named '{libraryName}'.");

            AnimationLibrary library;
            try
            {
                library = new AnimationLibrary();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate AnimationLibrary: {e.Message}");
            }

            // AddAnimationLibrary registers the library under the given key on the player.
            // Godot raises if the key is invalid; surface as create_failed.
            try
            {
                player.AddAnimationLibrary(libraryName, library);
            }
            catch (System.Exception e)
            {
                library.Free();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to register AnimationLibrary '{libraryName}' on player: {e.Message}");
            }

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(player.GetPath().ToString())).Append(',');
            sb.Append("\"library\":").Append(BridgeJson.EscapeString(libraryName)).Append(',');
            sb.Append("\"animationCount\":0");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 4. godot_open_mcp_animation_create
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_animation_create</c>. Creates an <c>Animation</c>
        /// resource in a named library on the target <c>AnimationPlayer</c>, and returns the
        /// clip's name + length + loop mode. The library defaults to "default" and is
        /// auto-created when missing (spec design decision §3 — the catalog says "auto-created
        /// when missing"). An animation already registered under that name returns
        /// <c>already_exists</c>. An explicit-but-unrecognized loop_mode token returns
        /// <c>invalid_parameter</c>; an absent loop_mode leaves the Godot default (LoopNone).
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>invalid_parameter</c> (bad loop_mode), <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>, <c>already_exists</c>,
        /// <c>create_failed</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult AnimationCreate(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "animation_create is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = AnimationCreateBody.Parse(body);
            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "animation_create requires 'node_path' (the AnimationPlayer that will own the clip).");
            if (!request.HasAnimation)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "animation_create requires 'animation' (the clip name).");
            if (request.HasLoopMode && request.LoopMode == AnimationLoopMode.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "animation_create 'loop_mode' must be one of: \"none\", \"linear\", \"pingpong\".");

            if (!TryResolvePlayer(request.NodePath!, out var player, out var resolveError))
                return resolveError;

            var libraryName = request.EffectiveLibrary;

            // Auto-create library when missing (spec design decision §3).
            AnimationLibrary library;
            if (player.HasAnimationLibrary(libraryName))
            {
                var existing = player.GetAnimationLibrary(libraryName);
                if (existing == null)
                    return ToolDispatchResult.Fail("create_failed",
                        $"AnimationPlayer at '{request.NodePath}' reports library '{libraryName}' but GetAnimationLibrary returned null.");
                library = existing;
            }
            else
            {
                try
                {
                    library = new AnimationLibrary();
                    player.AddAnimationLibrary(libraryName, library);
                }
                catch (System.Exception e)
                {
                    return ToolDispatchResult.Fail("create_failed",
                        $"Failed to auto-create AnimationLibrary '{libraryName}': {e.Message}");
                }
            }

            var animName = request.Animation!;
            if (library.HasAnimation(animName))
                return ToolDispatchResult.Fail(
                    "already_exists",
                    $"Animation '{animName}' already exists in library '{libraryName}' on player '{request.NodePath}'.");

            Animation anim;
            try
            {
                anim = new Animation();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate Animation: {e.Message}");
            }

            // Length: default 1.0 (Godot's default); clamp to strictly positive so a 0/negative
            // length does not produce a clip that ends at its first key.
            var length = request.Length ?? 1.0f;
            if (length < 0.0001f) length = 0.0001f;
            anim.Length = length;

            // Loop mode: only set when the agent sent an explicit token. Absent leaves the
            // Godot default (LoopNone). The parser already validated the token.
            if (request.HasLoopMode)
                anim.LoopMode = LoopModeToGodot(request.LoopMode);

            try
            {
                library.AddAnimation(animName, anim);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add Animation '{animName}' to library '{libraryName}': {e.Message}");
            }

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(player.GetPath().ToString())).Append(',');
            sb.Append("\"library\":").Append(BridgeJson.EscapeString(libraryName)).Append(',');
            sb.Append("\"animation\":").Append(BridgeJson.EscapeString(animName)).Append(',');
            sb.Append("\"length\":").Append(Float(length)).Append(',');
            sb.Append("\"loopMode\":").Append(BridgeJson.EscapeString(LoopModeToCatalog(anim.LoopMode)));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 5. godot_open_mcp_animation_add_track
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_animation_add_track</c>. Adds a track to an existing
        /// Animation in a named library on the target AnimationPlayer, and returns the new
        /// track's index. v1 supports four track types — value / position_3d / rotation_3d /
        /// scale_3d (spec design decision §4). Other Godot track types (blend_shape / method
        /// / bezier / audio / animation) return <c>unsupported_track_type</c>. For value
        /// tracks an optional <c>update_mode</c> sets the Godot UpdateMode (continuous is the
        /// default).
        ///
        /// <para>
        /// <b>Track path.</b> Godot resolves the path relative to the player's root_node;
        /// the format is a NodePath with a sub-path to the animated property, e.g.
        /// <c>"Sprite2D:position"</c>. A wrong path is accepted at authoring time but
        /// produces no playback effect; the result echoes the resolved path so an agent can
        /// verify.
        /// </para>
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>unsupported_track_type</c>, <c>library_not_found</c>, <c>animation_not_found</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>, <c>wrong_node_type</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult AddTrack(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "animation_add_track is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = AnimationAddTrackBody.Parse(body);
            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "animation_add_track requires 'node_path' (the AnimationPlayer owning the clip).");
            if (!request.HasAnimation)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "animation_add_track requires 'animation' (the clip name).");
            if (!request.HasTrackType)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "animation_add_track requires 'track_type' (one of: value, position_3d, rotation_3d, scale_3d).");
            if (request.TrackType == AnimationTrackType.Unknown)
                return ToolDispatchResult.Fail(
                    "unsupported_track_type",
                    "animation_add_track 'track_type' must be one of: value, position_3d, rotation_3d, scale_3d. " +
                    "Blend-shape / method / bezier / audio / animation tracks are not supported in v1.");
            if (!request.HasTrackPath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "animation_add_track requires 'track_path' (a NodePath relative to the AnimationPlayer's root_node, e.g. \"Sprite2D:position\").");

            if (!TryResolveAnimationClip(request.NodePath!, request.EffectiveLibrary,
                    request.Animation!, out var player, out var anim, out var clipError))
                return clipError;

            int trackIndex;
            try
            {
                trackIndex = anim.AddTrack(TrackTypeToGodot(request.TrackType));
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add {request.TrackType} track: {e.Message}");
            }

            // Apply the path + (for value tracks) the update mode. Godot's TrackSetPath takes
            // a NodePath; an invalid string still becomes a NodePath (Godot does not validate
            // the path against the scene tree at authoring time — see the class doc).
            try
            {
                anim.TrackSetPath(trackIndex, new NodePath(request.TrackPath!));
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to set track path '{request.TrackPath}': {e.Message}");
            }

            // Value tracks expose an UpdateMode (continuous/discrete/capture). Other track
            // types do not (Godot ignores the property). Apply only for value tracks, only
            // when the agent sent an explicit token.
            string appliedUpdateMode = "continuous";
            if (request.TrackType == AnimationTrackType.Value)
            {
                var mode = request.HasUpdateMode
                    ? UpdateModeToGodot(request.UpdateMode)
                    : Animation.UpdateMode.Continuous;
                anim.ValueTrackSetUpdateMode(trackIndex, mode);
                appliedUpdateMode = UpdateModeToCatalog(mode);
            }

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(player.GetPath().ToString())).Append(',');
            sb.Append("\"library\":").Append(BridgeJson.EscapeString(request.EffectiveLibrary)).Append(',');
            sb.Append("\"animation\":").Append(BridgeJson.EscapeString(request.Animation!)).Append(',');
            sb.Append("\"trackIndex\":").Append(trackIndex).Append(',');
            sb.Append("\"trackType\":").Append(BridgeJson.EscapeString(TrackTypeToCatalog(request.TrackType))).Append(',');
            sb.Append("\"trackPath\":").Append(BridgeJson.EscapeString(request.TrackPath!)).Append(',');
            if (request.TrackType == AnimationTrackType.Value)
                sb.Append("\"updateMode\":").Append(BridgeJson.EscapeString(appliedUpdateMode)).Append(',');
            sb.Append("\"keyCount\":0");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 6. godot_open_mcp_animation_insert_key
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_animation_insert_key</c>. Inserts a keyframe at the
        /// given time on a track in an Animation, and returns the key index Godot assigned.
        /// The value is parsed into a typed <see cref="AnimationKeyValue"/> by the body parser
        /// and converted to a Godot Variant here. An invalid value returns
        /// <c>invalid_parameter</c>; a track_index out of range returns
        /// <c>track_not_found</c>.
        ///
        /// <para>
        /// <b>Value/track-type pairing.</b> Godot checks the Variant type against the track
        /// type at insertion: a value track accepts any scalar the property supports, a
        /// position_3d / rotation_3d / scale_3d track requires a Vector3. This pack does not
        /// pre-validate the pairing (Godot itself raises a runtime error in some cases); a
        /// type mismatch is surfaced as <c>invalid_parameter</c> from the catch block.
        /// </para>
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>invalid_parameter</c> (bad value or interpolation), <c>library_not_found</c>,
        /// <c>animation_not_found</c>, <c>track_not_found</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult InsertKey(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "animation_insert_key is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = AnimationInsertKeyBody.Parse(body);
            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "animation_insert_key requires 'node_path' (the AnimationPlayer owning the clip).");
            if (!request.HasAnimation)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "animation_insert_key requires 'animation' (the clip name).");
            if (!request.TrackIndex.HasValue)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "animation_insert_key requires 'track_index' (the track to key, returned by animation_add_track).");
            if (!request.HasTime)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "animation_insert_key requires 'time' (the keyframe time in seconds).");
            if (!request.Value.IsValid)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "animation_insert_key requires a 'value' the parser recognizes (number / bool / string / {x,y} / {x,y,z} / {r,g,b[,a]}).");
            if (request.HasInterpolation && request.Interpolation == AnimationInterpolation.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "animation_insert_key 'interpolation' must be one of: nearest, linear, cubic.");

            if (!TryResolveAnimationClip(request.NodePath!, request.EffectiveLibrary,
                    request.Animation!, out var player, out var anim, out var clipError))
                return clipError;

            int trackIndex = request.TrackIndex.Value;
            if (trackIndex < 0 || trackIndex >= anim.GetTrackCount())
                return ToolDispatchResult.Fail(
                    "track_not_found",
                    $"Track index {trackIndex} is out of range — clip '{request.Animation}' has {anim.GetTrackCount()} track(s) (0..{anim.GetTrackCount() - 1}).");

            Variant variant;
            try
            {
                variant = ValueToVariant(request.Value);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("invalid_parameter",
                    $"Failed to convert value to Variant: {e.Message}");
            }

            float time = request.Time!.Value;
            // Transition defaults to 1.0 (Godot's default — a linear easing curve).
            float transition = request.Transition ?? 1.0f;

            int keyIndex;
            try
            {
                keyIndex = anim.TrackInsertKey(trackIndex, time, variant, transition);
            }
            catch (System.Exception e)
            {
                // Godot raises on a type mismatch between the Variant and the track type
                // (e.g. a string value on a position_3d track). Surface as invalid_parameter.
                return ToolDispatchResult.Fail("invalid_parameter",
                    $"Failed to insert key on track {trackIndex}: {e.Message}");
            }

            // Optional interpolation override per key. Godot exposes TrackSetKeyInterpolation
            // (via the InterpolationType enum); absent leaves the Godot default (linear).
            if (request.HasInterpolation)
            {
                try
                {
                    anim.TrackSetKeyInterpolation(trackIndex, keyIndex, InterpolationToGodot(request.Interpolation));
                }
                catch (System.Exception)
                {
                    // Non-fatal: the key landed; an unsupported interpolation on this Godot
                    // version is best-effort. Do not abort the insert.
                }
            }

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(player.GetPath().ToString())).Append(',');
            sb.Append("\"library\":").Append(BridgeJson.EscapeString(request.EffectiveLibrary)).Append(',');
            sb.Append("\"animation\":").Append(BridgeJson.EscapeString(request.Animation!)).Append(',');
            sb.Append("\"trackIndex\":").Append(trackIndex).Append(',');
            sb.Append("\"keyIndex\":").Append(keyIndex).Append(',');
            sb.Append("\"time\":").Append(Float(time)).Append(',');
            sb.Append("\"keyCount\":").Append(anim.TrackGetKeyCount(trackIndex));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 7. godot_open_mcp_animation_get (read-only)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_animation_get</c>. Reads the libraries / animations
        /// / tracks of an AnimationPlayer in a bounded JSON envelope (spec design decision
        /// §6). Does NOT dump every key by default; pass <c>include_keys: true</c> + an
        /// optional <c>animation</c> filter + <c>max_keys</c> (default 32, hard max 256) to
        /// dump keys for one clip.
        ///
        /// <para>
        /// Structured failures: <c>missing_parameter</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>, <c>library_not_found</c>,
        /// <c>animation_not_found</c> (when a filter does not resolve).
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Get(string body)
        {
            var request = AnimationGetBody.Parse(body);
            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "animation_get requires 'node_path' (the AnimationPlayer to read).");

            if (!TryResolvePlayer(request.NodePath!, out var player, out var resolveError))
                return resolveError;

            // Cap max_keys to [0, 256] regardless of what the agent sent.
            int maxKeys = request.MaxKeys;
            if (maxKeys < 0) maxKeys = 0;
            if (maxKeys > 256) maxKeys = 256;
            bool includeKeys = request.IncludeKeys;

            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(player.GetPath().ToString())).Append(',');

            // Library list — Godot returns StringName keys. When a library filter is set,
            // restrict to that one; when absent, list all.
            var libraryKeys = player.GetAnimationLibraryList();
            bool filterLib = request.HasLibraryFilter && request.Library != null;
            bool filterAnim = request.HasAnimationFilter && request.Animation != null;

            // If a library filter is set and the library does not exist, fail.
            if (filterLib && !player.HasAnimationLibrary(request.Library!))
                return ToolDispatchResult.Fail(
                    "library_not_found",
                    $"AnimationPlayer at '{request.NodePath}' has no library named '{request.Library}'.");

            // If an animation filter is set, resolve it against the (filtered) library.
            // library_not_found / animation_not_found are surfaced here so an agent gets a
            // precise error rather than an empty result.
            if (filterAnim)
            {
                var libName = filterLib ? request.Library! : "default";
                if (!player.HasAnimationLibrary(libName))
                    return ToolDispatchResult.Fail(
                        "library_not_found",
                        $"AnimationPlayer at '{request.NodePath}' has no library named '{libName}' (looked for animation '{request.Animation}').");
                var lib = player.GetAnimationLibrary(libName);
                if (lib == null || !lib.HasAnimation(request.Animation!))
                    return ToolDispatchResult.Fail(
                        "animation_not_found",
                        $"Animation '{request.Animation}' not found in library '{libName}' on player '{request.NodePath}'.");
            }

            sb.Append("\"libraries\":[");
            bool firstLib = true;
            foreach (var libKey in libraryKeys)
            {
                var libNameStr = libKey.ToString();
                if (filterLib && libNameStr != request.Library) continue;

                var lib = player.GetAnimationLibrary(libKey);
                if (lib == null) continue;

                if (!firstLib) sb.Append(',');
                firstLib = false;
                sb.Append('{');
                sb.Append("\"name\":").Append(BridgeJson.EscapeString(libNameStr)).Append(',');
                sb.Append("\"animations\":[");
                var animKeys = lib.GetAnimationList();
                bool firstAnim = true;
                foreach (var animKey in animKeys)
                {
                    var animNameStr = animKey.ToString();
                    if (filterAnim && animNameStr != request.Animation) continue;

                    var anim = lib.GetAnimation(animKey);
                    if (anim == null) continue;

                    if (!firstAnim) sb.Append(',');
                    firstAnim = false;
                    AppendAnimationJson(sb, animNameStr, anim, includeKeys, maxKeys);
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // Shared helpers
        // ===========================================================================

        /// <summary>
        /// Resolve <paramref name="nodePath"/> to an <c>AnimationPlayer</c> in the edited
        /// scene. Used by every handler that targets a player. Fails with
        /// <c>no_edited_scene</c> / <c>node_not_found</c> / <c>wrong_node_type</c>.
        /// </summary>
        static bool TryResolvePlayer(string nodePath, out AnimationPlayer player, out ToolDispatchResult error)
        {
            player = null!;
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

            if (resolved is AnimationPlayer ap)
            {
                player = ap;
                return true;
            }

            error = ToolDispatchResult.Fail(
                "wrong_node_type",
                $"Node at '{nodePath}' is a '{resolved.GetClass()}', not an AnimationPlayer.");
            return false;
        }

        /// <summary>
        /// Resolve a (player, library, animation) triple to the Animation clip and the owning
        /// player. Fails with <c>library_not_found</c> / <c>animation_not_found</c> (and the
        /// player-resolution errors from <see cref="TryResolvePlayer"/>).
        /// </summary>
        static bool TryResolveAnimationClip(string nodePath, string libraryName, string animationName,
            out AnimationPlayer player, out Animation anim, out ToolDispatchResult error)
        {
            player = null!;
            anim = null!;
            error = null!;

            if (!TryResolvePlayer(nodePath, out player, out error))
                return false;

            if (!player.HasAnimationLibrary(libraryName))
            {
                error = ToolDispatchResult.Fail(
                    "library_not_found",
                    $"AnimationPlayer at '{nodePath}' has no library named '{libraryName}'.");
                return false;
            }

            var lib = player.GetAnimationLibrary(libraryName);
            if (lib == null)
            {
                error = ToolDispatchResult.Fail(
                    "library_not_found",
                    $"AnimationPlayer at '{nodePath}' reports library '{libraryName}' but GetAnimationLibrary returned null.");
                return false;
            }

            if (!lib.HasAnimation(animationName))
            {
                error = ToolDispatchResult.Fail(
                    "animation_not_found",
                    $"Animation '{animationName}' not found in library '{libraryName}' on player '{nodePath}'.");
                return false;
            }

            var resolved = lib.GetAnimation(animationName);
            if (resolved == null)
            {
                error = ToolDispatchResult.Fail(
                    "animation_not_found",
                    $"Library '{libraryName}' reports animation '{animationName}' but GetAnimation returned null.");
                return false;
            }

            anim = resolved;
            return true;
        }

        /// <summary>Append one Animation's JSON (name + length + loopMode + tracks[]). When
        /// <paramref name="includeKeys"/> is true, each track also carries a bounded
        /// <c>keys</c> array (capped at <paramref name="maxKeys"/>).</summary>
        static void AppendAnimationJson(StringBuilder sb, string animName, Animation anim,
            bool includeKeys, int maxKeys)
        {
            sb.Append('{');
            sb.Append("\"name\":").Append(BridgeJson.EscapeString(animName)).Append(',');
            sb.Append("\"length\":").Append(Float(anim.Length)).Append(',');
            sb.Append("\"loopMode\":").Append(BridgeJson.EscapeString(LoopModeToCatalog(anim.LoopMode))).Append(',');
            sb.Append("\"tracks\":[");
            int trackCount = anim.GetTrackCount();
            for (int i = 0; i < trackCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{');
                sb.Append("\"index\":").Append(i).Append(',');
                sb.Append("\"type\":").Append(BridgeJson.EscapeString(TrackTypeToCatalog(anim.TrackGetType(i)))).Append(',');
                sb.Append("\"path\":").Append(BridgeJson.EscapeString(anim.TrackGetPath(i).ToString())).Append(',');
                int keyCount = anim.TrackGetKeyCount(i);
                sb.Append("\"keyCount\":").Append(keyCount);
                if (includeKeys && keyCount > 0)
                {
                    sb.Append(",\"keys\":[");
                    int cap = keyCount < maxKeys ? keyCount : maxKeys;
                    for (int k = 0; k < cap; k++)
                    {
                        if (k > 0) sb.Append(',');
                        float time = anim.TrackGetKeyTime(i, k);
                        float transition = anim.TrackGetKeyTransition(i, k);
                        Variant value = anim.TrackGetKeyValue(i, k);
                        sb.Append('{');
                        sb.Append("\"time\":").Append(Float(time)).Append(',');
                        sb.Append("\"transition\":").Append(Float(transition)).Append(',');
                        sb.Append("\"value\":").Append(VariantToJson(value));
                        sb.Append('}');
                    }
                    if (cap < keyCount)
                        sb.Append(",\"truncated\":").Append(keyCount - cap);
                    sb.Append(']');
                }
                sb.Append('}');
            }
            sb.Append("]}");
        }

        // --- Enum mapping (catalog ↔ Godot) -------------------------------------

        static Animation.LoopMode LoopModeToGodot(AnimationLoopMode mode)
        {
            switch (mode)
            {
                case AnimationLoopMode.Linear: return Animation.LoopMode.Linear;
                case AnimationLoopMode.Pingpong: return Animation.LoopMode.Pingpong;
                default: return Animation.LoopMode.None;
            }
        }

        static string LoopModeToCatalog(Animation.LoopMode mode)
        {
            switch (mode)
            {
                case Animation.LoopMode.Linear: return "linear";
                case Animation.LoopMode.Pingpong: return "pingpong";
                case Animation.LoopMode.None: return "none";
                default: return "none";
            }
        }

        static Animation.TrackType TrackTypeToGodot(AnimationTrackType type)
        {
            switch (type)
            {
                case AnimationTrackType.Position3D: return Animation.TrackType.Position3D;
                case AnimationTrackType.Rotation3D: return Animation.TrackType.Rotation3D;
                case AnimationTrackType.Scale3D: return Animation.TrackType.Scale3D;
                default: return Animation.TrackType.Value;
            }
        }

        static string TrackTypeToCatalog(Animation.TrackType type)
        {
            switch (type)
            {
                case Animation.TrackType.Position3D: return "position_3d";
                case Animation.TrackType.Rotation3D: return "rotation_3d";
                case Animation.TrackType.Scale3D: return "scale_3d";
                case Animation.TrackType.Value: return "value";
                // Blend-shape / method / bezier / audio / animation are out of v1 scope; surface
                // them as their Godot enum name so an agent reading a pre-existing clip sees the
                // real type rather than a misleading "value".
                default: return type.ToString().ToLowerInvariant();
            }
        }

        static string TrackTypeToCatalog(AnimationTrackType type)
        {
            switch (type)
            {
                case AnimationTrackType.Position3D: return "position_3d";
                case AnimationTrackType.Rotation3D: return "rotation_3d";
                case AnimationTrackType.Scale3D: return "scale_3d";
                default: return "value";
            }
        }

        static Animation.UpdateMode UpdateModeToGodot(AnimationUpdateMode mode)
        {
            switch (mode)
            {
                case AnimationUpdateMode.Discrete: return Animation.UpdateMode.Discrete;
                case AnimationUpdateMode.Capture: return Animation.UpdateMode.Capture;
                default: return Animation.UpdateMode.Continuous;
            }
        }

        static string UpdateModeToCatalog(Animation.UpdateMode mode)
        {
            switch (mode)
            {
                case Animation.UpdateMode.Discrete: return "discrete";
                case Animation.UpdateMode.Capture: return "capture";
                case Animation.UpdateMode.Continuous: return "continuous";
                default: return "continuous";
            }
        }

        static Animation.InterpolationType InterpolationToGodot(AnimationInterpolation interp)
        {
            switch (interp)
            {
                case AnimationInterpolation.Nearest: return Animation.InterpolationType.Nearest;
                case AnimationInterpolation.Cubic: return Animation.InterpolationType.Cubic;
                default: return Animation.InterpolationType.Linear;
            }
        }

        // --- Variant conversion -------------------------------------------------

        /// <summary>
        /// Convert a typed <see cref="AnimationKeyValue"/> to a Godot Variant for
        /// <c>Animation.TrackInsertKey</c>. C# implicit conversion handles int/float/bool/
        /// string; Vector2/Vector3/Color are constructed explicitly. Throws on an invalid
        /// value (the caller surfaces invalid_parameter).
        /// </summary>
        static Variant ValueToVariant(AnimationKeyValue value)
        {
            switch (value.Kind)
            {
                case AnimationValueKind.Number:
                    // Use Variant.Of(double) so a whole number is not narrowed to int (Godot
                    // value tracks treat numbers as floats). Implicit conversion handles this
                    // but being explicit avoids ambiguity on some toolchains.
                    return Variant.From(value.Number);
                case AnimationValueKind.Bool:
                    return Variant.From(value.Bool);
                case AnimationValueKind.String:
                    return Variant.From(value.StringValue ?? "");
                case AnimationValueKind.Vector2:
                    return Variant.From(new Vector2((float)value.X, (float)value.Y));
                case AnimationValueKind.Vector3:
                    return Variant.From(new Vector3((float)value.X, (float)value.Y, (float)value.Z));
                case AnimationValueKind.Color:
                    return Variant.From(new Color((float)value.X, (float)value.Y, (float)value.Z, (float)value.W));
                default:
                    throw new System.InvalidOperationException("AnimationKeyValue kind is Invalid.");
            }
        }

        /// <summary>
        /// Render a Godot Variant back to a JSON value token for the get-with-keys path.
        /// Handles the same shapes the insert path accepts (number / bool / string /
        /// Vector2 / Vector3 / Color); anything else is stringified via <c>ToString</c>.
        /// </summary>
        static string VariantToJson(Variant v)
        {
            var vt = v.VariantType;
            switch (vt)
            {
                case Variant.Type.Bool:
                    return (bool)v ? "true" : "false";
                case Variant.Type.Int:
                case Variant.Type.Float:
                    return Float((float)(double)v);
                case Variant.Type.String:
                    return BridgeJson.EscapeString((string)v);
                case Variant.Type.Vector2:
                    {
                        var vec = (Vector2)v;
                        var o = new StringBuilder(48);
                        o.Append("{\"x\":").Append(Float(vec.X)).Append(",\"y\":").Append(Float(vec.Y)).Append('}');
                        return o.ToString();
                    }
                case Variant.Type.Vector3:
                    {
                        var vec = (Vector3)v;
                        var o = new StringBuilder(64);
                        o.Append("{\"x\":").Append(Float(vec.X))
                            .Append(",\"y\":").Append(Float(vec.Y))
                            .Append(",\"z\":").Append(Float(vec.Z)).Append('}');
                        return o.ToString();
                    }
                case Variant.Type.Color:
                    {
                        var col = (Color)v;
                        var o = new StringBuilder(64);
                        o.Append("{\"r\":").Append(Float(col.R))
                            .Append(",\"g\":").Append(Float(col.G))
                            .Append(",\"b\":").Append(Float(col.B))
                            .Append(",\"a\":").Append(Float(col.A)).Append('}');
                        return o.ToString();
                    }
                default:
                    // Fallback: stringify. Vector2i / Vector3i / Rect2 / etc. are not first-
                    // class in the v1 value surface; surface them as their ToString so the
                    // JSON stays valid.
                    return BridgeJson.EscapeString(v.ToString());
            }
        }

        /// <summary>Render a float with the invariant culture so a comma-decimal locale cannot corrupt the JSON.</summary>
        static string Float(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
#endif
