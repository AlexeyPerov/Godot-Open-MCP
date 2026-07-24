#nullable enable
using System.Globalization;

namespace GodotOpenMcp.Bridge.Editor
{
    // ===========================================================================
    // P12.4 animation pack request bodies + value parser.
    //
    // The animation pack is the hardest P12 pack (libraries / clips / tracks /
    // keys). The bridge deliberately carries no typed JSON DOM dependency on the
    // hot path (see packages/bridge/AGENTS.md §Transport), so each tool that needs
    // structured extraction gets a tiny sealed body type mirroring the P12.1–P12.3
    // `IndexOf`-substring style. Pure-managed (no Godot API surface, no
    // `#if TOOLS`), so the parsing logic is unit-testable in the binary-less xUnit
    // host.
    //
    // The shared extraction primitives live in the P12.1 `JsonScalar` static class
    // (GodotOpenMcp.Bridge.Editor namespace, sibling of TilemapBodies.cs). P12.4 is
    // the "fifth family" — it reuses JsonScalar's ExtractString / ExtractFloat /
    // ExtractIntOrNull / ExtractBool rather than duplicating them. The one new
    // piece the animation pack needs is a JSON value parser for animation keys
    // (an animation key carries a Variant — number / bool / string / vector /
    // color — and the body parser must produce a typed structure the editor-only
    // handler can turn into a Godot Variant). That parser lives here as
    // `AnimationKeyValue` + `AnimationKeyValueParser`.
    //
    // Loop mode + track type + interpolation + update mode: every create / add /
    // insert tool accepts a string token (e.g. "linear", "value", "position_3d").
    // The body parser records the raw value; the handler normalizes via the
    // matching parser below — Unknown when absent or not a valid token, surfaced
    // as `invalid_parameter` by the handler. These small enum+parser pairs mirror
    // ParticlesDimensionParser / NavDimension from the earlier packs.
    // ===========================================================================

    /// <summary>
    /// Normalized <c>Animation.LoopMode</c> token extracted from a request body. The string
    /// schema uses Godot-catalog names (none / linear / pingpong); <see cref="Unknown"/>
    /// covers both "absent" and "not a valid token". The handler treats Unknown as "leave the
    /// Godot default (LoopNone)" on create, and as <c>invalid_parameter</c> when an agent
    /// sends an explicit bad token (the parser cannot tell the two apart — the handler
    /// distinguishes via the raw-body presence check, mirroring how ParticlesConfigureBody
    /// leaves absent scalars as null).
    /// </summary>
    internal enum AnimationLoopMode
    {
        Unknown = 0,
        None = 1,
        Linear = 2,
        Pingpong = 3,
    }

    /// <summary>
    /// Map a raw <c>loop_mode</c> string ("none" | "linear" | "pingpong") to an
    /// <see cref="AnimationLoopMode"/>. Returns <see cref="AnimationLoopMode.Unknown"/> for
    /// null / empty / unrecognized tokens. Case-insensitive to tolerate an agent sending
    /// "Linear". The names mirror Godot's <c>Animation.LoopMode</c> enum (LoopNone /
    /// LoopLinear / LoopPingpong) minus the redundant "Loop" prefix.
    /// </summary>
    internal static class AnimationLoopModeParser
    {
        internal static AnimationLoopMode Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return AnimationLoopMode.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "none": return AnimationLoopMode.None;
                case "linear": return AnimationLoopMode.Linear;
                case "pingpong": return AnimationLoopMode.Pingpong;
                default: return AnimationLoopMode.Unknown;
            }
        }
    }

    /// <summary>
    /// Normalized <c>Animation.TrackType</c> token. The pack's v1 surface is value +
    /// position_3d + rotation_3d + scale_3d (the spec's design decision §4 — start with
    /// TYPE_VALUE and the transform tracks Godot exposes cleanly). Blend-shape / method /
    /// bezier / audio / animation tracks are deliberately NOT claimed in v1; an agent
    /// requesting one gets <c>unsupported_track_type</c>.
    /// </summary>
    internal enum AnimationTrackType
    {
        Unknown = 0,
        Value = 1,
        Position3D = 2,
        Rotation3D = 3,
        Scale3D = 4,
    }

    /// <summary>
    /// Map a raw <c>track_type</c> string ("value" | "position_3d" | "rotation_3d" |
    /// "scale_3d") to an <see cref="AnimationTrackType"/>. Returns
    /// <see cref="AnimationTrackType.Unknown"/> for null / empty / unrecognized tokens. The
    /// handler turns Unknown into either <c>missing_parameter</c> (absent) or
    /// <c>unsupported_track_type</c> (present-but-unrecognized) — see the add_track handler.
    /// </summary>
    internal static class AnimationTrackTypeParser
    {
        internal static AnimationTrackType Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return AnimationTrackType.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "value": return AnimationTrackType.Value;
                case "position_3d": return AnimationTrackType.Position3D;
                case "rotation_3d": return AnimationTrackType.Rotation3D;
                case "scale_3d": return AnimationTrackType.Scale3D;
                default: return AnimationTrackType.Unknown;
            }
        }
    }

    /// <summary>
    /// Normalized <c>Animation.InterpolationType</c> token. The catalog surface is nearest /
    /// linear / cubic (the common Godot choices); angle-variant interpolations are deferred.
    /// <see cref="Unknown"/> covers absent / unrecognized — the handler treats absent as
    /// "leave Godot default (Linear)" on insert_key and invalid_parameter on an explicit bad
    /// token.
    /// </summary>
    internal enum AnimationInterpolation
    {
        Unknown = 0,
        Nearest = 1,
        Linear = 2,
        Cubic = 3,
    }

    internal static class AnimationInterpolationParser
    {
        internal static AnimationInterpolation Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return AnimationInterpolation.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "nearest": return AnimationInterpolation.Nearest;
                case "linear": return AnimationInterpolation.Linear;
                case "cubic": return AnimationInterpolation.Cubic;
                default: return AnimationInterpolation.Unknown;
            }
        }
    }

    /// <summary>
    /// Normalized <c>Animation.UpdateMode</c> token (value tracks only). Catalog surface is
    /// continuous / discrete / capture, mirroring Godot's UpdateContinuous / UpdateDiscrete /
    /// UpdateCapture. <see cref="Unknown"/> = absent or unrecognized; absent defaults to
    /// continuous (the Godot default for value tracks) at the handler.
    /// </summary>
    internal enum AnimationUpdateMode
    {
        Unknown = 0,
        Continuous = 1,
        Discrete = 2,
        Capture = 3,
    }

    internal static class AnimationUpdateModeParser
    {
        internal static AnimationUpdateMode Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return AnimationUpdateMode.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "continuous": return AnimationUpdateMode.Continuous;
                case "discrete": return AnimationUpdateMode.Discrete;
                case "capture": return AnimationUpdateMode.Capture;
                default: return AnimationUpdateMode.Unknown;
            }
        }
    }

    // ===========================================================================
    // AnimationKeyValue — typed JSON value for an animation key.
    //
    // Godot's `Animation.TrackInsertKey` takes a Variant. An agent supplies the key value as
    // JSON, and the body parser must produce a typed structure the editor-only handler can
    // turn into a Godot Variant. The supported value shapes for v1 (spec design decision §4):
    //   number  → double (a Godot float Variant; ints are a subset)
    //   bool    → bool
    //   string  → string
    //   vector2 → { "x": number, "y": number }
    //   vector3 → { "x": number, "y": number, "z": number }
    //   color   → { "r": number, "g": number, "b": number, "a": number }
    // The discriminator is `valueType`. The handler switch-maps each case to the matching
    // Godot Variant (implicit conversion handles the rest). An unparseable value sets
    // Kind = Invalid so the handler surfaces `invalid_parameter`.
    //
    // The parser is hand-rolled against the raw body substring (the same IndexOf style as
    // JsonScalar) — no JSON DOM. It does NOT recurse into arbitrary nesting; it only reads
    // the four well-known shapes above.
    // ===========================================================================

    /// <summary>
    /// Discriminator for <see cref="AnimationKeyValue"/>. Mirrors the four JSON shapes an
    /// animation key can carry (number / bool / string / object-with-known-keys) plus Invalid
    /// for an unparseable value.
    /// </summary>
    internal enum AnimationValueKind
    {
        Invalid = 0,
        Number = 1,
        Bool = 2,
        String = 3,
        Vector2 = 4,
        Vector3 = 5,
        Color = 6,
    }

    /// <summary>
    /// Typed JSON value extracted from a request body's <c>value</c> field. Carries the
    /// discriminator (<see cref="Kind"/>) plus the parsed components. The editor-only handler
    /// converts each case to a Godot Variant for <c>Animation.TrackInsertKey</c>. Pure-
    /// managed so the parser is unit-testable without the editor.
    /// </summary>
    internal sealed class AnimationKeyValue
    {
        internal AnimationValueKind Kind { get; private set; }
        internal double Number { get; private set; }
        internal bool Bool { get; private set; }
        internal string? StringValue { get; private set; }
        // Vector2 / Vector3 / Color share x/y(/z(/a)) components.
        internal double X { get; private set; }
        internal double Y { get; private set; }
        internal double Z { get; private set; }
        internal double W { get; private set; }

        internal bool IsValid => Kind != AnimationValueKind.Invalid;

        AnimationKeyValue() { }

        internal static AnimationKeyValue NumberValue(double v)
        {
            var k = new AnimationKeyValue { Kind = AnimationValueKind.Number };
            k.Number = v;
            return k;
        }

        internal static AnimationKeyValue BoolValue(bool v)
        {
            var k = new AnimationKeyValue { Kind = AnimationValueKind.Bool };
            k.Bool = v;
            return k;
        }

        internal static AnimationKeyValue StringValueOf(string v)
        {
            var k = new AnimationKeyValue { Kind = AnimationValueKind.String };
            k.StringValue = v;
            return k;
        }

        internal static AnimationKeyValue Vector2Value(double x, double y)
        {
            var k = new AnimationKeyValue { Kind = AnimationValueKind.Vector2 };
            k.X = x; k.Y = y;
            return k;
        }

        internal static AnimationKeyValue Vector3Value(double x, double y, double z)
        {
            var k = new AnimationKeyValue { Kind = AnimationValueKind.Vector3 };
            k.X = x; k.Y = y; k.Z = z;
            return k;
        }

        internal static AnimationKeyValue ColorValue(double r, double g, double b, double a)
        {
            var k = new AnimationKeyValue { Kind = AnimationValueKind.Color };
            k.X = r; k.Y = g; k.Z = b; k.W = a;
            return k;
        }

        internal static AnimationKeyValue Invalid() => new AnimationKeyValue();

        // --- Parser -------------------------------------------------------------

        /// <summary>
        /// Parse the <c>value</c> field out of a raw request body. Returns
        /// <see cref="Invalid"/> when the key is absent, explicitly null, or matches none of
        /// the supported shapes. The parser is hand-rolled against the body substring (no
        /// JSON DOM) — it locates `"value"`, reads the JSON token that follows, and dispatches
        /// on its shape (bare number/bool, quoted string, or `{…}` object with known keys).
        /// </summary>
        internal static AnimationKeyValue Parse(string? body)
        {
            if (string.IsNullOrEmpty(body)) return Invalid();
            var raw = JsonScalar.ExtractRawValue(body!, "value");
            if (raw == null) return Invalid();
            return ParseToken(raw);
        }

        /// <summary>
        /// Parse a raw value token (the substring ExtractRawValue already unwrapped — for a
        /// string the quotes are gone; for an object the surrounding braces are still
        /// present because ExtractRawValue's bare-token slicer stops at the matching close
        /// brace). Exposed for unit tests so each shape can be pinned without the surrounding
        /// body scaffolding.
        /// </summary>
        internal static AnimationKeyValue ParseToken(string raw)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) return Invalid();

            // Object shapes: vector2 / vector3 / color. The agent may also wrap these in a
            // type-tagged object like {"type":"vector3","x":...} — handle both by reading the
            // components if present and inferring the kind from which components are set.
            if (trimmed.StartsWith("{", System.StringComparison.Ordinal))
                return ParseObject(trimmed);

            // Quoted string (ExtractRawValue already unwraps quotes for string values, but a
            // nested value-object reaches us with quotes intact via the nested call below — be
            // defensive). If ExtractRawValue returned the unquoted text, treat it as a string
            // literal only when it is not a recognized bool / number token.
            // Bool literals.
            if (trimmed == "true") return BoolValue(true);
            if (trimmed == "false") return BoolValue(false);

            // Number — accept int or float, invariant culture so a comma-decimal locale
            // cannot corrupt the value.
            if (double.TryParse(trimmed, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var n))
                return NumberValue(n);

            // Fall through: treat as a string. (ExtractRawValue unwraps JSON string quotes,
            // so a JSON "hello" arrives here as the bare text hello.)
            return StringValueOf(trimmed);
        }

        /// <summary>
        /// Parse a <c>{…}</c> object into a vector2 / vector3 / color value. Recognizes a
        /// <c>"type"</c> discriminator if present (one of "vector2" / "vector3" / "color");
        /// otherwise infers from the components present (x+y → vector2; x+y+z → vector3;
        /// r+g+b(+a) → color). Returns Invalid when no recognized components are present.
        /// </summary>
        static AnimationKeyValue ParseObject(string raw)
        {
            // A type tag, if present, wins — it tells us how to read the components.
            var typeTag = JsonScalar.ExtractString(raw, "type")?.ToLowerInvariant();
            var x = ExtractDoubleOrNull(raw, "x");
            var y = ExtractDoubleOrNull(raw, "y");
            var z = ExtractDoubleOrNull(raw, "z");
            var r = ExtractDoubleOrNull(raw, "r");
            var g = ExtractDoubleOrNull(raw, "g");
            var b = ExtractDoubleOrNull(raw, "b");
            var a = ExtractDoubleOrNull(raw, "a");

            switch (typeTag)
            {
                case "vector2":
                    return (x.HasValue && y.HasValue)
                        ? Vector2Value(x.Value, y.Value)
                        : Invalid();
                case "vector3":
                    return (x.HasValue && y.HasValue && z.HasValue)
                        ? Vector3Value(x.Value, y.Value, z.Value)
                        : Invalid();
                case "color":
                    {
                        if (!(r.HasValue && g.HasValue && b.HasValue)) return Invalid();
                        return ColorValue(r.Value, g.Value, b.Value, a ?? 1.0);
                    }
                case null:
                    break; // infer below
                default:
                    return Invalid();
            }

            // Infer from components. Color wins only when r/g/b are present (an x-only object
            // is ambiguous, so require the full color triple or the full vector set).
            if (r.HasValue && g.HasValue && b.HasValue)
                return ColorValue(r.Value, g.Value, b.Value, a ?? 1.0);
            if (x.HasValue && y.HasValue && z.HasValue)
                return Vector3Value(x.Value, y.Value, z.Value);
            if (x.HasValue && y.HasValue)
                return Vector2Value(x.Value, y.Value);
            return Invalid();
        }

        /// <summary>
        /// Extract a double component (x / y / z / r / g / b / a) from an object body. Returns
        /// null when the key is absent, explicitly null, or not a parseable number. Mirrors
        /// JsonScalar.ExtractFloat but returns double so the caller can compose Color (a=1.0
        /// default) and Vector3 components without float→double widening surprises.
        /// </summary>
        static double? ExtractDoubleOrNull(string body, string key)
        {
            var raw = JsonScalar.ExtractRawValue(body, key);
            if (raw == null) return null;
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) return null;
            if (double.TryParse(trimmed, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var v))
                return v;
            return null;
        }
    }

    // ===========================================================================
    // Request body types — one per tool that needs structured extraction.
    // ===========================================================================

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_animation_defaults</c> (P12.4). Pure helper
    /// — the handler returns recommended starter scalars for an Animation (length + loop
    /// mode). No fields are read in v1 (the defaults are dimension-agnostic); the body type
    /// exists so the handler signature matches the other packs and a future field can land
    /// without reshaping the dispatch.
    /// </summary>
    internal sealed class AnimationDefaultsBody
    {
        internal static AnimationDefaultsBody Parse(string? body)
        {
            // No fields today; the type exists for signature parity + future extension.
            _ = body;
            return new AnimationDefaultsBody();
        }

        AnimationDefaultsBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_animation_player_create</c> (P12.4). Mirrors
    /// <c>node_create</c> for the shape an agent already knows (name / parent_node_path /
    /// position); an AnimationPlayer is just a Node. No dimension — AnimationPlayer is a
    /// single class.
    /// </summary>
    internal sealed class AnimationPlayerCreateBody
    {
        internal string? Name { get; private set; }
        internal string? ParentNodePath { get; private set; }
        internal string? Position { get; private set; }

        internal static AnimationPlayerCreateBody Parse(string? body)
        {
            var parsed = new AnimationPlayerCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Name = JsonScalar.ExtractString(body, "name");
            parsed.ParentNodePath = JsonScalar.ExtractString(body, "parent_node_path");
            parsed.Position = JsonScalar.ExtractString(body, "position");
            return parsed;
        }

        AnimationPlayerCreateBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_animation_library_add</c> (P12.4). Carries
    /// the player target (node_path) and the library name (the key under which the new empty
    /// AnimationLibrary registers on the player — Godot's default library name is "" but the
    /// catalog convention names libraries like "default" / "player").
    /// </summary>
    internal sealed class AnimationLibraryAddBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);
        internal string? Library { get; private set; }

        /// <summary>Effective library name. Defaults to "default" when absent — the catalog
        /// convention. The handler treats an empty string the same as absent.</summary>
        internal string EffectiveLibrary => string.IsNullOrEmpty(Library) ? "default" : Library!;

        internal static AnimationLibraryAddBody Parse(string? body)
        {
            var parsed = new AnimationLibraryAddBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.Library = JsonScalar.ExtractString(body, "library");
            return parsed;
        }

        AnimationLibraryAddBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_animation_create</c> (P12.4). Carries the
    /// player target, the animation name, an optional library (defaults to "default"; the
    /// handler auto-creates the library when missing — spec design decision §3), an optional
    /// length (seconds, defaults to 1.0), and an optional loop_mode.
    /// </summary>
    internal sealed class AnimationCreateBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);
        internal string? Animation { get; private set; }
        internal bool HasAnimation => !string.IsNullOrEmpty(Animation);
        internal string? Library { get; private set; }
        internal string EffectiveLibrary => string.IsNullOrEmpty(Library) ? "default" : Library!;
        internal float? Length { get; private set; }
        internal AnimationLoopMode LoopMode { get; private set; }

        /// <summary>True when the raw body carried an explicit loop_mode key (any value,
        /// including an unrecognized token). The handler uses this to distinguish "leave the
        /// Godot default" from "reject the bad token".</summary>
        internal bool HasLoopMode { get; private set; }

        internal static AnimationCreateBody Parse(string? body)
        {
            var parsed = new AnimationCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.Animation = JsonScalar.ExtractString(body, "animation");
            parsed.Library = JsonScalar.ExtractString(body, "library");
            parsed.Length = JsonScalar.ExtractFloat(body, "length");
            var rawLoop = JsonScalar.ExtractString(body, "loop_mode");
            parsed.HasLoopMode = rawLoop != null;
            parsed.LoopMode = AnimationLoopModeParser.Parse(rawLoop);
            return parsed;
        }

        AnimationCreateBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_animation_add_track</c> (P12.4). Carries
    /// the player target, the library + animation name selecting the clip, the track_type
    /// (value / position_3d / rotation_3d / scale_3d), and the track_path (NodePath string
    /// Godot expects, relative to the AnimationPlayer's root_node — see the README for the
    /// exact format). An optional value_type hint (for value tracks) and update_mode round
    /// out the v1 surface.
    /// </summary>
    internal sealed class AnimationAddTrackBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);
        internal string? Library { get; private set; }
        internal string EffectiveLibrary => string.IsNullOrEmpty(Library) ? "default" : Library!;
        internal string? Animation { get; private set; }
        internal bool HasAnimation => !string.IsNullOrEmpty(Animation);
        internal AnimationTrackType TrackType { get; private set; }

        /// <summary>True when the raw body carried an explicit track_type key. Distinguishes
        /// "missing_parameter" (absent) from "unsupported_track_type" (present but bad).
        /// </summary>
        internal bool HasTrackType { get; private set; }

        internal string? TrackPath { get; private set; }
        internal bool HasTrackPath => !string.IsNullOrEmpty(TrackPath);

        internal string? ValueType { get; private set; }

        internal AnimationUpdateMode UpdateMode { get; private set; }
        internal bool HasUpdateMode { get; private set; }

        internal static AnimationAddTrackBody Parse(string? body)
        {
            var parsed = new AnimationAddTrackBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.Library = JsonScalar.ExtractString(body, "library");
            parsed.Animation = JsonScalar.ExtractString(body, "animation");
            var rawType = JsonScalar.ExtractString(body, "track_type");
            parsed.HasTrackType = rawType != null;
            parsed.TrackType = AnimationTrackTypeParser.Parse(rawType);
            parsed.TrackPath = JsonScalar.ExtractString(body, "track_path");
            parsed.ValueType = JsonScalar.ExtractString(body, "value_type");
            var rawUpdate = JsonScalar.ExtractString(body, "update_mode");
            parsed.HasUpdateMode = rawUpdate != null;
            parsed.UpdateMode = AnimationUpdateModeParser.Parse(rawUpdate);
            return parsed;
        }

        AnimationAddTrackBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_animation_insert_key</c> (P12.4). Carries
    /// the player target, the library + animation name selecting the clip, the track_index,
    /// the time (seconds), the value (parsed into an <see cref="AnimationKeyValue"/>), and
    /// optional interpolation / transition. The handler converts the typed value into a Godot
    /// Variant for <c>Animation.TrackInsertKey</c>.
    /// </summary>
    internal sealed class AnimationInsertKeyBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);
        internal string? Library { get; private set; }
        internal string EffectiveLibrary => string.IsNullOrEmpty(Library) ? "default" : Library!;
        internal string? Animation { get; private set; }
        internal bool HasAnimation => !string.IsNullOrEmpty(Animation);
        internal int? TrackIndex { get; private set; }
        internal float? Time { get; private set; }
        internal bool HasTime => Time.HasValue;
        /// <summary>Default to Invalid so the property is never null; the Parse method
        /// overwrites it with the parsed value (or leaves Invalid when absent).</summary>
        internal AnimationKeyValue Value { get; private set; } = AnimationKeyValue.Invalid();
        internal AnimationInterpolation Interpolation { get; private set; }
        internal bool HasInterpolation { get; private set; }
        internal float? Transition { get; private set; }

        internal static AnimationInsertKeyBody Parse(string? body)
        {
            var parsed = new AnimationInsertKeyBody();
            // Value defaults to Invalid until parsed.
            parsed.Value = AnimationKeyValue.Invalid();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.Library = JsonScalar.ExtractString(body, "library");
            parsed.Animation = JsonScalar.ExtractString(body, "animation");
            parsed.TrackIndex = JsonScalar.ExtractIntOrNull(body, "track_index");
            parsed.Time = JsonScalar.ExtractFloat(body, "time");
            parsed.Value = AnimationKeyValue.Parse(body);
            var rawInterp = JsonScalar.ExtractString(body, "interpolation");
            parsed.HasInterpolation = rawInterp != null;
            parsed.Interpolation = AnimationInterpolationParser.Parse(rawInterp);
            parsed.Transition = JsonScalar.ExtractFloat(body, "transition");
            return parsed;
        }

        AnimationInsertKeyBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_animation_get</c> (P12.4). Read-only —
    /// carries the player target plus optional filters (library / animation) and the
    /// include_keys + max_keys flags that bound the key dump (spec design decision §6).
    /// </summary>
    internal sealed class AnimationGetBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);
        internal string? Library { get; private set; }
        internal bool HasLibraryFilter => !string.IsNullOrEmpty(Library);
        internal string? Animation { get; private set; }
        internal bool HasAnimationFilter => !string.IsNullOrEmpty(Animation);
        internal bool IncludeKeys { get; private set; }

        /// <summary>Max keys per track when include_keys is true. Defaults to 32; hard max
        /// 256 (the spec's cap). The handler clamps to [0, 256]. 0 is allowed and returns
        /// no keys (just the keyCount).</summary>
        internal int MaxKeys { get; private set; } = 32;

        internal bool HasMaxKeys { get; private set; }

        internal static AnimationGetBody Parse(string? body)
        {
            var parsed = new AnimationGetBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.Library = JsonScalar.ExtractString(body, "library");
            parsed.Animation = JsonScalar.ExtractString(body, "animation");
            parsed.IncludeKeys = JsonScalar.ExtractBool(body, "include_keys") ?? false;
            var max = JsonScalar.ExtractIntOrNull(body, "max_keys");
            if (max.HasValue)
            {
                parsed.HasMaxKeys = true;
                parsed.MaxKeys = max.Value;
            }
            return parsed;
        }

        AnimationGetBody() { }
    }
}
