#if TOOLS
#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Audio domain pack (P16.4) — three typed tools for Godot 4.3+ audio
    /// players and the project audio bus layout:
    /// <c>godot_open_mcp_audio_stream_player_create</c> (create an
    /// <c>AudioStreamPlayer</c> / <c>AudioStreamPlayer2D</c> /
    /// <c>AudioStreamPlayer3D</c> node with optional stream + bus + starter
    /// scalars), <c>godot_open_mcp_audio_stream_player_set_stream</c> (assign an
    /// <c>AudioStream</c> resource to an existing player), and
    /// <c>godot_open_mcp_audio_bus_set_volume</c> (set an audio bus's volume via
    /// <c>AudioServer</c>, native dB with optional linear→dB conversion). The
    /// fourth Phase 16 typed-editor-breadth family; mirrors the P16.3 lighting
    /// pack's folder layout, registration shape, gate policy, and group
    /// assignment.
    ///
    /// <para>
    /// <b>Fidelity:</b> adapt — Unity Open MCP's <c>AudioTools</c>
    /// (TypedTools/Extensions/Audio/AudioTools.cs — audio_source_add /
    /// audio_source_modify / audio_mixer_set_parameter) supplies the
    /// create-with-starter-scalars + bus-volume pattern. The deltas from the
    /// Unity pattern are:
    /// (1) Godot audio players are <c>Node</c> / <c>Node2D</c> / <c>Node3D</c>
    /// subclasses (<c>AudioStreamPlayer</c> / <c>AudioStreamPlayer2D</c> /
    /// <c>AudioStreamPlayer3D</c>), not Unity AudioSource components attached to
    /// a GameObject — create makes a node, not a component add;
    /// (2) Godot's <c>AudioServer</c> bus model replaces Unity's
    /// <c>AudioMixer</c> exposed-float parameter surface —
    /// <c>audio_bus_set_volume</c> writes a bus volume directly via
    /// <c>AudioServer.SetBusVolumeDb</c> (no exposed-parameter indirection, no
    /// <c>.mix</c> asset);
    /// (3) Unity <c>spatial_blend</c> / <c>spatialize</c> / <c>min_distance</c>
    /// / <c>max_distance</c> / <c>doppler_level</c> / <c>spread</c> are NOT
    /// ported in the typed surface (Godot's <c>AudioStreamPlayer3D</c> has an
    /// <c>AttenuationModel</c> + <c>max_db</c> + emission angle surface,
    /// settable via <c>node_modify</c> for the niche case);
    /// (4) Unity <c>AudioListener</c> is NOT ported (Godot has a single implicit
    /// listener — no listener node to create/inspect);
    /// (5) Unity <c>mixer_group_path</c> routing is NOT ported — Godot's
    /// per-player <c>Bus</c> property is a string bus name (set at create via the
    /// <c>bus</c> arg, or via <c>node_modify</c> afterwards).
    /// </para>
    ///
    /// <para>
    /// <b>Create path.</b> <c>audio_stream_player_create</c> resolves the parent
    /// (edited scene root by default), instantiates the player node via
    /// <c>new T()</c>, sets the name, parents it, assigns the owner (so it
    /// persists on save), applies the position (positional players only),
    /// optionally loads + assigns the stream, applies the bus, then applies the
    /// starter scalars through the same allow-listed + clamped path, and marks
    /// the scene unsaved.
    /// </para>
    ///
    /// <para>
    /// <b>Set-stream path.</b> <c>audio_stream_player_set_stream</c> resolves the
    /// node, type-checks it against the three audio player families, loads the
    /// <c>AudioStream</c> resource at <c>stream_path</c>, assigns it to the
    /// player's <c>Stream</c> property, and marks the scene unsaved.
    /// </para>
    ///
    /// <para>
    /// <b>Bus-volume path.</b> <c>audio_bus_set_volume</c> resolves the bus index
    /// via <c>AudioServer.GetBusIndex</c> (surfacing <c>bus_not_found</c> on a
    /// typo), resolves which volume input to apply (<c>volume_db</c> wins over
    /// <c>volume_linear</c>; linear is converted via Godot's
    /// <c>Mathf.LinearToDb</c>), writes it via
    /// <c>AudioServer.SetBusVolumeDb</c>, and marks the project unsaved. The bus
    /// layout is project-level state — <c>paths_hint</c> is
    /// <c>res://project.godot</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Gate contract.</b> All three handlers register with
    /// <see cref="BridgeToolEntry.DefaultGate"/> <c>"enforce"</c> and validate
    /// <c>paths_hint</c> themselves (mirrors the P4.x resource mutators and the
    /// P12.x / P16.x domain mutators). The dispatch layer rejects an empty hint
    /// when the effective gate is not <c>off</c>; the handler-level guard ALSO
    /// fires when an agent overrides with <c>gate:"off"</c>, so
    /// <c>paths_hint</c> is always required for these tools. <c>paths_hint</c>
    /// for the two player tools is the edited scene path (the <c>.tscn</c>);
    /// for the bus tool it is <c>res://project.godot</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Owner / persistence.</b> <c>audio_stream_player_create</c> sets the
    /// new node's <c>Owner</c> to the edited scene root so it persists in the
    /// <c>.tscn</c> on save — same step every node creator performs. Every
    /// mutator calls <see cref="EditorInterface.MarkSceneAsUnsaved"/> +
    /// <see cref="SceneTools.MarkEditedSceneDirty"/> so the bridge-tracked dirty
    /// flag the <c>scene_open</c> guard consults stays honest. The bus-volume
    /// mutator marks the project unsaved via
    /// <c>ProjectSettings.Save</c> is NOT called automatically — the change
    /// lives in the running AudioServer until the operator saves the bus layout
    /// (the handler reports the written value + a hint to persist via the Audio
    /// panel).
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): every handler touches
    /// <see cref="EditorInterface"/> and live <c>AudioStreamPlayer*</c> /
    /// <c>AudioStream</c> / <c>AudioServer</c> objects. The pure-managed pieces
    /// (<see cref="AudioStreamPlayerCreateBody"/> /
    /// <see cref="AudioStreamPlayerSetStreamBody"/> /
    /// <see cref="AudioBusSetVolumeBody"/> / <see cref="AudioPlayerDimension"/> /
    /// <see cref="AudioPlayerDimensionParser"/> / <see cref="AudioPropertyClamp"/>)
    /// live outside this guard and are unit-tested.
    /// </summary>
    internal static class AudioTools
    {
        internal const string PlayerCreateToolName = "godot_open_mcp_audio_stream_player_create";
        internal const string PlayerSetStreamToolName = "godot_open_mcp_audio_stream_player_set_stream";
        internal const string BusSetVolumeToolName = "godot_open_mcp_audio_bus_set_volume";

        /// <summary>
        /// Register the audio tool family. All three handlers are mutating and
        /// declare <c>defaultGate:"enforce"</c> + <c>isMutating:true</c>, and all
        /// three belong to the <c>audio</c> group. Registered once at plugin
        /// enable; idempotent (the registry replaces on re-register).
        /// </summary>
        internal static void RegisterAudioTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: PlayerCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "audio",
                handler: AudioStreamPlayerCreate));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: PlayerSetStreamToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "audio",
                handler: AudioStreamPlayerSetStream));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: BusSetVolumeToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "audio",
                handler: AudioBusSetVolume));
        }

        // ===========================================================================
        // 1. godot_open_mcp_audio_stream_player_create (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_audio_stream_player_create</c>. Resolves
        /// the parent (edited scene root by default), instantiates the player
        /// node (<c>AudioStreamPlayer</c> / <c>AudioStreamPlayer2D</c> /
        /// <c>AudioStreamPlayer3D</c>) via <c>new T()</c>, parents it, assigns
        /// the owner, applies the position (positional players only), optionally
        /// loads + assigns the stream, applies the bus, then applies the optional
        /// starter scalars (<c>volume_db</c> / <c>pitch_scale</c> / <c>autoplay</c>)
        /// through the same allow-listed + clamped path, and marks the scene
        /// unsaved. Returns the new node's NodeData so an agent can chain into
        /// <c>audio_stream_player_set_stream</c> / <c>node_modify</c>.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>invalid_parameter</c> (unknown dimension), <c>no_edited_scene</c>,
        /// <c>parent_not_found</c>, <c>invalid_path</c> (stream),
        /// <c>resource_load_failed</c>, <c>wrong_resource_type</c>,
        /// <c>bus_not_found</c>, <c>create_failed</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult AudioStreamPlayerCreate(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "audio_stream_player_create is mutating; pass a non-empty paths_hint scoped to the edited scene path (res://...tscn).");

            var request = AudioStreamPlayerCreateBody.Parse(body);

            if (request.Dimension == AudioPlayerDimension.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "audio_stream_player_create requires 'dimension' to be one of: \"nonpositional\", \"2d\", \"3d\".");

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling audio_stream_player_create.");

            Node parent = root;
            if (!string.IsNullOrEmpty(request.ParentNodePath))
            {
                parent = NodeTools.ResolvePath(root, request.ParentNodePath!);
                if (parent == null)
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Parent node not found at path '{request.ParentNodePath}'.");
            }

            // Instantiate the right player node class for the dimension.
            Node node;
            try
            {
                node = CreatePlayerNode(request.Dimension);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate player of dimension '{AudioPlayerDimensionParser.ToSchemaString(request.Dimension)}': {e.Message}");
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
                    $"Failed to add player node to parent: {e.Message}");
            }

            node.Owner = root;

            var applied = new List<string>();
            var warnings = new List<string>();

            // Apply position best-effort (2D + 3D players carry spatial transforms).
            if (AudioPlayerDimensionParser.IsPositional(request.Dimension))
                ApplyPosition(node, request.Position);

            // Load + assign the optional stream.
            if (!string.IsNullOrEmpty(request.StreamPath))
            {
                string streamPath;
                if (!ResourcePathNormalizer.TryRequireResFilePath(request.StreamPath!, out streamPath, out var streamNormError))
                {
                    warnings.Add($"stream_path: invalid_path ({streamNormError})");
                }
                else if (!ResourceLoader.Exists(streamPath))
                {
                    warnings.Add($"stream_path: resource_not_found (no AudioStream at '{streamPath}')");
                }
                else
                {
                    AudioStream? stream = null;
                    try
                    {
                        stream = ResourceLoader.Load<AudioStream>(streamPath);
                    }
                    catch (System.Exception e)
                    {
                        warnings.Add($"stream_path: resource_load_failed ({e.Message})");
                    }
                    if (stream != null)
                    {
                        SetStreamProperty(node, stream);
                        applied.Add("stream_path");
                    }
                    else if (warnings.Count == 0)
                    {
                        warnings.Add($"stream_path: wrong_resource_type ('{streamPath}' loaded but is not an AudioStream).");
                    }
                }
            }

            // Apply the optional bus (validate against the live AudioServer layout).
            if (!string.IsNullOrEmpty(request.Bus))
            {
                var busIdx = AudioServer.GetBusIndex(request.Bus!);
                if (busIdx < 0)
                {
                    warnings.Add($"bus: bus_not_found (no audio bus named '{request.Bus}'; the Master bus always exists).");
                }
                else
                {
                    SetBusProperty(node, request.Bus!);
                    applied.Add("bus");
                }
            }

            // Apply starter scalars through the shared allow-listed + clamped path.
            if (request.VolumeDb.HasValue)
                ApplyPlayerScalar(node, "volume_db", FloatStr(request.VolumeDb.Value), applied, warnings);
            if (request.PitchScale.HasValue)
                ApplyPlayerScalar(node, "pitch_scale", FloatStr(request.PitchScale.Value), applied, warnings);
            if (request.Autoplay.HasValue)
                ApplyPlayerScalar(node, "autoplay", request.Autoplay.Value ? "true" : "false", applied, warnings);

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
        // 2. godot_open_mcp_audio_stream_player_set_stream (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_audio_stream_player_set_stream</c>.
        /// Resolves the player node, type-checks it against the three audio player
        /// families, loads the <c>AudioStream</c> resource at <c>stream_path</c>,
        /// assigns it to the player's <c>Stream</c> property, and marks the scene
        /// unsaved.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>missing_parameter</c>, <c>invalid_path</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>, <c>wrong_node_type</c>,
        /// <c>resource_not_found</c>, <c>resource_load_failed</c>,
        /// <c>wrong_resource_type</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult AudioStreamPlayerSetStream(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "audio_stream_player_set_stream is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = AudioStreamPlayerSetStreamBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "audio_stream_player_set_stream requires 'node_path' (the player node to mutate).");
            if (!request.HasStreamPath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "audio_stream_player_set_stream requires 'stream_path' (a res:// AudioStream to assign).");

            string streamPath;
            if (!ResourcePathNormalizer.TryRequireResFilePath(request.StreamPath!, out streamPath, out var normError))
                return ToolDispatchResult.Fail("invalid_path", normError);

            if (!TryResolvePlayer(request.NodePath!, out var node, out var resolveError))
                return resolveError;

            if (!ResourceLoader.Exists(streamPath))
                return ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"No AudioStream resource exists at '{streamPath}'.");

            AudioStream? stream;
            try
            {
                stream = ResourceLoader.Load<AudioStream>(streamPath);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "resource_load_failed",
                    $"Failed to load AudioStream at '{streamPath}': {e.Message}");
            }

            if (stream == null)
                return ToolDispatchResult.Fail(
                    "wrong_resource_type",
                    $"'{streamPath}' loaded but is not an AudioStream resource.");

            SetStreamProperty(node, stream);

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(node.GetPath().ToString())).Append(',');
            sb.Append("\"streamPath\":").Append(BridgeJson.EscapeString(streamPath)).Append(',');
            sb.Append("\"stream\":").Append(BridgeJson.EscapeString(stream.ResourceName ?? streamPath)).Append(',');
            sb.Append("\"assigned\":true}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 3. godot_open_mcp_audio_bus_set_volume (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_audio_bus_set_volume</c>. Resolves the
        /// bus index via <c>AudioServer.GetBusIndex</c> (surfacing
        /// <c>bus_not_found</c> on a typo), resolves which volume input to apply
        /// (<c>volume_db</c> wins over <c>volume_linear</c>; linear is converted
        /// via Godot's <c>Mathf.LinearToDb</c>), writes it via
        /// <c>AudioServer.SetBusVolumeDb</c>, and reads it back. The bus layout is
        /// project-level state — <c>paths_hint</c> is <c>res://project.godot</c>.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>missing_parameter</c> (bus or both volumes absent),
        /// <c>bus_not_found</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult AudioBusSetVolume(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "audio_bus_set_volume is mutating; pass a non-empty paths_hint scoped to res://project.godot.");

            var request = AudioBusSetVolumeBody.Parse(body);

            if (!request.HasBus)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "audio_bus_set_volume requires 'bus' (the audio bus name).");
            if (!request.HasVolumeDb && !request.HasVolumeLinear)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "audio_bus_set_volume requires 'volume_db' or 'volume_linear'.");

            var busIdx = AudioServer.GetBusIndex(request.Bus!);
            if (busIdx < 0)
                return ToolDispatchResult.Fail(
                    "bus_not_found",
                    $"No audio bus named '{request.Bus}'. The default layout has 'Master' only until more buses are added in the Audio panel.");

            // Resolve which volume input to apply. volume_db wins; volume_linear
            // is converted via Godot's linear_to_db when volume_db is absent.
            float resolvedDb;
            string appliedUnit;
            var warnings = new List<string>();
            if (request.HasVolumeDb)
            {
                resolvedDb = AudioPropertyClamp.ClampVolumeDb(request.VolumeDb!.Value);
                appliedUnit = "db";
                if (request.HasVolumeLinear)
                    warnings.Add("volume_linear ignored (volume_db wins when both are present).");
            }
            else
            {
                // Mathf.LinearToDb: 0 → -80 dB (silent floor); clamped to ≥ 0 to
                // avoid the log of a negative number.
                var linear = request.VolumeLinear!.Value;
                if (linear <= 0f)
                {
                    resolvedDb = -80f;
                    warnings.Add("volume_linear clamped to the silent floor (-80 dB).");
                }
                else
                {
                    resolvedDb = Mathf.LinearToDb(linear);
                }
                appliedUnit = "linear";
            }

            AudioServer.SetBusVolumeDb(busIdx, resolvedDb);

            // Read back the stored value so the caller can confirm.
            var storedDb = AudioServer.GetBusVolumeDb(busIdx);

            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"bus\":").Append(BridgeJson.EscapeString(request.Bus!)).Append(',');
            sb.Append("\"busIndex\":").Append(busIdx).Append(',');
            sb.Append("\"volumeDb\":").Append(FloatStr(storedDb)).Append(',');
            sb.Append("\"appliedUnit\":").Append(BridgeJson.EscapeString(appliedUnit));
            if (warnings.Count > 0)
            {
                sb.Append(",\"warnings\":");
                sb.Append(JsonStringArray(warnings));
            }
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // Shared helpers — node creation + resolution
        // ===========================================================================

        /// <summary>
        /// Instantiate the concrete audio player node for a dimension. The player
        /// node classes are concrete engine nodes — no ClassExists guard needed
        /// (mirrors the lighting pack's <c>new T()</c> path).
        /// </summary>
        static Node CreatePlayerNode(AudioPlayerDimension dimension)
        {
            switch (dimension)
            {
                case AudioPlayerDimension.NonPositional: return new AudioStreamPlayer();
                case AudioPlayerDimension.TwoD: return new AudioStreamPlayer2D();
                case AudioPlayerDimension.ThreeD: return new AudioStreamPlayer3D();
                default:
                    throw new System.ArgumentException(
                        $"Unknown audio player dimension '{dimension}' (cannot instantiate).");
            }
        }

        /// <summary>
        /// Resolve <paramref name="nodePath"/> to an audio player node in the
        /// edited scene. Fails with <c>no_edited_scene</c> / <c>node_not_found</c>
        /// / <c>wrong_node_type</c> (the node is not an
        /// <c>AudioStreamPlayer</c> / <c>AudioStreamPlayer2D</c> /
        /// <c>AudioStreamPlayer3D</c>) via <paramref name="error"/>.
        /// </summary>
        static bool TryResolvePlayer(string nodePath, out Node node, out ToolDispatchResult error)
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

            if (!(resolved is AudioStreamPlayer) && !(resolved is AudioStreamPlayer2D) && !(resolved is AudioStreamPlayer3D))
            {
                error = ToolDispatchResult.Fail(
                    "wrong_node_type",
                    $"Node at '{nodePath}' is a '{resolved.GetClass()}', not an AudioStreamPlayer / AudioStreamPlayer2D / AudioStreamPlayer3D.");
                return false;
            }

            node = resolved;
            return true;
        }

        // ===========================================================================
        // Shared helpers — stream + bus assignment
        // ===========================================================================

        /// <summary>
        /// Assign an <c>AudioStream</c> to a player node's <c>Stream</c> property,
        /// dispatching by the concrete player type. All three player families
        /// expose the same <c>Stream</c> property but no shared
        /// <c>AudioStreamPlayer base</c> in the C# binding, so pattern-match.
        /// </summary>
        static void SetStreamProperty(Node node, AudioStream stream)
        {
            if (node is AudioStreamPlayer p) p.Stream = stream;
            else if (node is AudioStreamPlayer2D p2) p2.Stream = stream;
            else if (node is AudioStreamPlayer3D p3) p3.Stream = stream;
        }

        /// <summary>
        /// Assign a bus name to a player node's <c>Bus</c> property, dispatching
        /// by the concrete player type. All three player families expose the same
        /// <c>Bus</c> property (string bus name).
        /// </summary>
        static void SetBusProperty(Node node, string bus)
        {
            if (node is AudioStreamPlayer p) p.Bus = bus;
            else if (node is AudioStreamPlayer2D p2) p2.Bus = bus;
            else if (node is AudioStreamPlayer3D p3) p3.Bus = bus;
        }

        // ===========================================================================
        // Shared helpers — scalar field application (the heart of the allow-list)
        // ===========================================================================

        /// <summary>
        /// Apply one allow-listed audio player scalar to a player node, appending
        /// the field name to <paramref name="applied"/> on success or a message
        /// to <paramref name="warnings"/> on a parse/type failure. Non-aborting.
        /// Centralized so <c>audio_stream_player_create</c> shares one validation
        /// + clamping path.
        ///
        /// <para>
        /// Allow-listed fields (each validated against the node's type):
        /// <list type="bullet">
        /// <item><description><c>volume_db</c> (float, passed through) — all
        /// players.</description></item>
        /// <item><description><c>pitch_scale</c> (float, clamped strictly
        /// positive) — all players.</description></item>
        /// <item><description><c>autoplay</c> (bool) — all
        /// players.</description></item>
        /// </list>
        /// </para>
        /// </summary>
        static void ApplyPlayerScalar(Node node, string field, string? valueRaw,
            List<string> applied, List<string> warnings)
        {
            if (valueRaw == null)
            {
                warnings.Add($"{field}: value is null/absent (skipped).");
                return;
            }

            switch (field)
            {
                case "volume_db":
                {
                    if (TryParseFloat(StripQuotes(valueRaw), out var rawVol))
                    {
                        var vol = AudioPropertyClamp.ClampVolumeDb(rawVol);
                        if (node is AudioStreamPlayer p) { p.VolumeDb = vol; applied.Add(field); }
                        else if (node is AudioStreamPlayer2D p2) { p2.VolumeDb = vol; applied.Add(field); }
                        else if (node is AudioStreamPlayer3D p3) { p3.VolumeDb = vol; applied.Add(field); }
                        else warnings.Add($"{field}: node '{node.GetClass()}' has no volume_db property.");
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as float.");
                    break;
                }
                case "pitch_scale":
                {
                    if (TryParseFloat(StripQuotes(valueRaw), out var rawPitch))
                    {
                        var pitch = AudioPropertyClamp.ClampPitchScale(rawPitch);
                        if (node is AudioStreamPlayer p) { p.PitchScale = pitch; applied.Add(field); }
                        else if (node is AudioStreamPlayer2D p2) { p2.PitchScale = pitch; applied.Add(field); }
                        else if (node is AudioStreamPlayer3D p3) { p3.PitchScale = pitch; applied.Add(field); }
                        else warnings.Add($"{field}: node '{node.GetClass()}' has no pitch_scale property.");
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as float.");
                    break;
                }
                case "autoplay":
                {
                    if (TryParseBool(StripQuotes(valueRaw), out var autoplay))
                    {
                        if (node is AudioStreamPlayer p) { p.Autoplay = autoplay; applied.Add(field); }
                        else if (node is AudioStreamPlayer2D p2) { p2.Autoplay = autoplay; applied.Add(field); }
                        else if (node is AudioStreamPlayer3D p3) { p3.Autoplay = autoplay; applied.Add(field); }
                        else warnings.Add($"{field}: node '{node.GetClass()}' has no autoplay property.");
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as bool.");
                    break;
                }
                default:
                    warnings.Add($"{field}: unsupported_field (not in the audio player allow-list).");
                    break;
            }
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
