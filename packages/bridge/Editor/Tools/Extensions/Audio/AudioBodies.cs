#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    // ===========================================================================
    // P16.4 audio pack request bodies + player-dimension / stream catalogs.
    //
    // Three tools land in this pack:
    //   - godot_open_mcp_audio_stream_player_create (mutating, gated) — create an
    //     AudioStreamPlayer / AudioStreamPlayer2D / AudioStreamPlayer3D node in
    //     the edited scene, with an optional stream + bus + starter scalars
    //     (volume_db / pitch_scale / autoplay) applied at create time.
    //   - godot_open_mcp_audio_stream_player_set_stream (mutating, gated) —
    //     assign an AudioStream resource (.wav / .ogg / .mp3 / .tres) to an
    //     existing audio player node.
    //   - godot_open_mcp_audio_bus_set_volume (mutating, gated) — set an audio
    //     bus's volume via Godot's AudioServer (native dB, with an optional
    //     linear→dB conversion).
    //
    // The body types mirror the hand-rolled IndexOf-substring style already used by
    // the P12.x domain packs and the P16.1 / P16.2 / P16.3 packs (see packages/
    // bridge/AGENTS.md §Transport: the bridge deliberately carries no typed JSON
    // DOM dependency on the hot path). Pure-managed (no Godot API surface, no
    // `#if TOOLS`), so the parsing logic is unit-testable in the binary-less xUnit
    // host.
    //
    // The shared extraction primitives live in the P12.1 `JsonScalar` static class
    // (GodotOpenMcp.Bridge.Editor namespace, sibling file
    // Extensions/Tilemap/TilemapBodies.cs). P16.4 reuses ExtractString /
    // ExtractFloat / ExtractBool.
    //
    // Player-dimension catalog: the three audio player families the plan names are
    // creatable — AudioStreamPlayer (non-positional, "3d" is a misnomer — it is
    // the dimensionless global player), AudioStreamPlayer2D (2D positional),
    // AudioStreamPlayer3D (3D positional). The catalog maps the dimension token to
    // the Godot class name the editor-only handler instantiates via `new T()`.
    // Centralized here so the vocabulary is unit-testable without the editor
    // (mirrors the P16.3 lighting pack's kind-catalog design decision).
    //
    // Fidelity: adapt — Unity Open MCP's AudioTools (audio_source_add /
    // audio_source_modify / audio_mixer_set_parameter shape) supplies the
    // create-with-starter-scalars + bus-volume pattern. The deltas are:
    // (1) Godot audio players are Node / Node2D / Node3D subclasses, not Unity
    // AudioSource components attached to a GameObject — create makes a node, not a
    // component add;
    // (2) Godot's AudioServer bus model replaces Unity's AudioMixer exposed-float
    // parameter surface — audio_bus_set_volume writes a bus volume directly via
    // AudioServer.SetBusVolumeDb (no exposed-parameter indirection, no .mix
    // asset);
    // (3) Unity spatial_blend / spatialize / min_distance / max_distance /
    // doppler_level / spread are NOT ported in the typed surface (Godot's
    // AudioStreamPlayer3D has an AttenuationModel + max_db + emission angle
    // surface, settable via node_modify for the niche case);
    // (4) Unity AudioListener is NOT ported (Godot has a single implicit
    // listener — no listener node to create/inspect);
    // (5) Unity mixer_group_path routing is NOT ported — Godot's per-player Bus
    // property is a string bus name (set at create via the `bus` arg).
    // ===========================================================================

    /// <summary>
    /// Normalized player-dimension token extracted from an
    /// <c>audio_stream_player_create</c> request body. <see cref="Unknown"/>
    /// covers both "absent" and "not a valid token"; the handler turns that into
    /// <c>invalid_parameter</c>. The three values map to the three audio player
    /// node families the plan names.
    /// </summary>
    internal enum AudioPlayerDimension
    {
        Unknown = 0,
        NonPositional = 1, // AudioStreamPlayer (dimensionless global player)
        TwoD = 2,          // AudioStreamPlayer2D
        ThreeD = 3,        // AudioStreamPlayer3D
    }

    /// <summary>
    /// Map a raw <c>dimension</c> string ("nonpositional" | "2d" | "3d") to an
    /// <see cref="AudioPlayerDimension"/>. Returns
    /// <see cref="AudioPlayerDimension.Unknown"/> for null / empty / unrecognized
    /// tokens so the handler can surface a single <c>invalid_parameter</c> error.
    /// Case-insensitive to tolerate an agent sending "2D" / "3D" / "NonPositional".
    /// </summary>
    internal static class AudioPlayerDimensionParser
    {
        internal static AudioPlayerDimension Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return AudioPlayerDimension.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "nonpositional":
                case "non-positional":
                case "global":
                    return AudioPlayerDimension.NonPositional;
                case "2d":
                    return AudioPlayerDimension.TwoD;
                case "3d":
                    return AudioPlayerDimension.ThreeD;
                default:
                    return AudioPlayerDimension.Unknown;
            }
        }

        /// <summary>
        /// Render an <see cref="AudioPlayerDimension"/> back to its MCP schema
        /// string. Used by the create handler so the JSON key an agent reads
        /// round-trips into a subsequent call. Returns an empty string for
        /// <see cref="AudioPlayerDimension.Unknown"/>.
        /// </summary>
        internal static string ToSchemaString(AudioPlayerDimension dimension)
        {
            switch (dimension)
            {
                case AudioPlayerDimension.NonPositional: return "nonpositional";
                case AudioPlayerDimension.TwoD: return "2d";
                case AudioPlayerDimension.ThreeD: return "3d";
                default: return "";
            }
        }

        /// <summary>
        /// The Godot class name the editor-only handler instantiates for a
        /// dimension. The handler instantiates via <c>new T()</c> (these are
        /// concrete engine nodes). Returns an empty string for
        /// <see cref="AudioPlayerDimension.Unknown"/>.
        /// </summary>
        internal static string ToClassName(AudioPlayerDimension dimension)
        {
            switch (dimension)
            {
                case AudioPlayerDimension.NonPositional: return "AudioStreamPlayer";
                case AudioPlayerDimension.TwoD: return "AudioStreamPlayer2D";
                case AudioPlayerDimension.ThreeD: return "AudioStreamPlayer3D";
                default: return "";
            }
        }

        /// <summary>
        /// True for the two positional dimensions (AudioStreamPlayer2D /
        /// AudioStreamPlayer3D). The handler uses this to decide whether the
        /// position arg applies (non-positional players carry no spatial
        /// transform).
        /// </summary>
        internal static bool IsPositional(AudioPlayerDimension dimension)
        {
            return dimension == AudioPlayerDimension.TwoD
                || dimension == AudioPlayerDimension.ThreeD;
        }
    }

    /// <summary>
    /// Centralized clamp table for the audio scalar allow-list (mirrors the
    /// P12.x / P16.x packs' design decision §2). Every scalar the create handler
    /// accepts is clamped here so the table is unit-testable without the editor.
    ///
    /// <para>
    /// The valid ranges mirror Godot's documented bounds for the
    /// AudioStreamPlayer properties:
    /// <list type="bullet">
    /// <item><c>volume_db</c>: float, no hard clamp (Godot accepts the full dB
    /// range; a very negative value is effectively silent, a very positive value
    /// amplifies). Passed through unchanged.</item>
    /// <item><c>pitch_scale</c>: float, clamped strictly positive
    /// (<c>PitchScale &lt;= 0</c> is rejected by the engine and produces no
    /// sound; the floor mirrors the lighting pack's strictly-positive range
    /// floor). The engine default is 1.0.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class AudioPropertyClamp
    {
        /// <summary>Strictly-positive floor for pitch_scale. A non-positive pitch
        /// scale produces no sound and is rejected by the engine; the floor keeps
        /// the pitch audible. Matches the lighting pack's <c>MinRange</c>.</summary>
        internal const float MinPitchScale = 0.0001f;

        /// <summary>Clamp pitch_scale to strictly-positive. A non-positive pitch
        /// scale is rejected by the engine (no sound); clamp to the floor.</summary>
        internal static float ClampPitchScale(float v) => v < MinPitchScale ? MinPitchScale : v;

        /// <summary>volume_db passes through — Godot accepts the full dB range.
        /// Centralized for symmetry + a single echo point.</summary>
        internal static float ClampVolumeDb(float v) => v;
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_audio_stream_player_create</c>
    /// (P16.4, mutating, gated). Carries the <c>dimension</c> (which audio player
    /// node family to create), the standard node-creation fields (<c>name</c> /
    /// <c>parent_node_path</c> / <c>position</c>), an optional <c>stream_path</c>
    /// (a res:// AudioStream to load + assign at create time), an optional
    /// <c>bus</c> (the output bus name), and the optional starter scalars
    /// (<c>volume_db</c> / <c>pitch_scale</c> / <c>autoplay</c>) applied at create
    /// time. A null scalar means "leave the engine default" — the handler does not
    /// write the property.
    /// </summary>
    internal sealed class AudioStreamPlayerCreateBody
    {
        internal AudioPlayerDimension Dimension { get; private set; }

        internal string? Name { get; private set; }
        internal string? ParentNodePath { get; private set; }
        internal string? Position { get; private set; }

        /// <summary>Optional res:// AudioStream resource path (.wav / .ogg /
        /// .mp3 / .tres) to load and assign to the player's Stream property at
        /// create time. Null means "no stream assigned".</summary>
        internal string? StreamPath { get; private set; }

        /// <summary>Optional output bus name. Null means "Master" (the engine
        /// default).</summary>
        internal string? Bus { get; private set; }

        // Optional starter scalars. Each is nullable — null means "use the engine
        // default" (the handler does not write the property when null). The body
        // parser records the raw value; clamping is the handler's job via
        // <see cref="AudioPropertyClamp"/> so the clamped result can be echoed.
        internal float? VolumeDb { get; private set; }  // all players
        internal float? PitchScale { get; private set; } // all players
        internal bool? Autoplay { get; private set; }     // all players

        internal static AudioStreamPlayerCreateBody Parse(string? body)
        {
            var parsed = new AudioStreamPlayerCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Dimension = AudioPlayerDimensionParser.Parse(JsonScalar.ExtractString(body, "dimension"));
            parsed.Name = JsonScalar.ExtractString(body, "name");
            parsed.ParentNodePath = JsonScalar.ExtractString(body, "parent_node_path");
            parsed.Position = JsonScalar.ExtractString(body, "position");
            parsed.StreamPath = JsonScalar.ExtractString(body, "stream_path");
            parsed.Bus = JsonScalar.ExtractString(body, "bus");
            parsed.VolumeDb = JsonScalar.ExtractFloat(body, "volume_db");
            parsed.PitchScale = JsonScalar.ExtractFloat(body, "pitch_scale");
            parsed.Autoplay = JsonScalar.ExtractBool(body, "autoplay");
            return parsed;
        }

        AudioStreamPlayerCreateBody() { }
    }

    /// <summary>
    /// Parsed request body for
    /// <c>godot_open_mcp_audio_stream_player_set_stream</c> (P16.4, mutating,
    /// gated). Carries the player target (<c>node_path</c>) and the
    /// <c>stream_path</c> (a res:// AudioStream resource to load and assign). The
    /// handler resolves the node, type-checks it against the three audio player
    /// families, loads the resource, assigns it to the player's Stream property,
    /// and marks the scene unsaved.
    /// </summary>
    internal sealed class AudioStreamPlayerSetStreamBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal string? StreamPath { get; private set; }
        internal bool HasStreamPath => !string.IsNullOrEmpty(StreamPath);

        internal static AudioStreamPlayerSetStreamBody Parse(string? body)
        {
            var parsed = new AudioStreamPlayerSetStreamBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.StreamPath = JsonScalar.ExtractString(body, "stream_path");
            return parsed;
        }

        AudioStreamPlayerSetStreamBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_audio_bus_set_volume</c>
    /// (P16.4, mutating, gated). Carries the <c>bus</c> name (resolved to a bus
    /// index via AudioServer.GetBusIndex), the <c>volume_db</c> (Godot native —
    /// written directly), and an optional <c>volume_linear</c> (0–1, converted to
    /// dB via Godot's linear_to_db when <c>volume_db</c> is absent). The handler
    /// validates the bus exists, resolves which volume input to apply, writes it
    /// via AudioServer.SetBusVolumeDb, and marks the project unsaved.
    ///
    /// <para>
    /// <c>volume_db</c> wins over <c>volume_linear</c> when both are present
    /// (the handler surfaces a warning in that case). <c>paths_hint</c> for this
    /// tool is <c>res://project.godot</c> — the bus layout is project-level state
    /// (Godot saves it into <c>project.godot</c> via the Audio panel, and the
    /// AudioServer holds the in-memory copy).
    /// </para>
    /// </summary>
    internal sealed class AudioBusSetVolumeBody
    {
        internal string? Bus { get; private set; }
        internal bool HasBus => !string.IsNullOrEmpty(Bus);

        /// <summary>Godot-native dB volume. Wins over <c>volume_linear</c> when
        /// both are present.</summary>
        internal float? VolumeDb { get; private set; }
        internal bool HasVolumeDb => VolumeDb.HasValue;

        /// <summary>Linear volume (0–1). Converted to dB via Godot's
        /// linear_to_db when <c>volume_db</c> is absent.</summary>
        internal float? VolumeLinear { get; private set; }
        internal bool HasVolumeLinear => VolumeLinear.HasValue;

        internal static AudioBusSetVolumeBody Parse(string? body)
        {
            var parsed = new AudioBusSetVolumeBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Bus = JsonScalar.ExtractString(body, "bus");
            parsed.VolumeDb = JsonScalar.ExtractFloat(body, "volume_db");
            parsed.VolumeLinear = JsonScalar.ExtractFloat(body, "volume_linear");
            return parsed;
        }

        AudioBusSetVolumeBody() { }
    }
}
