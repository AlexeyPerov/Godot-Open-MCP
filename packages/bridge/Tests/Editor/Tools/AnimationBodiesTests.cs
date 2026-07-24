#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P12.4 animation pack unit tests for the pure-managed, off-editor-testable pieces: the
    /// four enum parsers (loop mode / track type / interpolation / update mode), the seven
    /// request-body parsers, and the JSON value parser (<see cref="AnimationKeyValue"/>) —
    /// the new piece this pack adds. The editor-only handlers
    /// (<see cref="AnimationTools.PlayerCreate"/> / LibraryAdd / AnimationCreate / AddTrack /
    /// InsertKey / Get) are <c>#if TOOLS</c> and coupled to <c>EditorInterface.Singleton</c>
    /// and the AnimationPlayer / AnimationLibrary / Animation classes, none of which the
    /// binary-less xUnit host can construct — those paths are exercised by the headless Godot
    /// smoke / live call path, not here.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <c>ParticlesBodiesTests</c> (copy fidelity for
    /// the parser test shape), with cases specific to the animation field set (loop-mode /
    /// track-type / interpolation / update-mode enums, value JSON shapes for number / bool /
    /// string / vector2 / vector3 / color, and the auto-library default). The value parser
    /// is the highest-value unit test surface in this pack — it pins the JSON-to-Variant
    /// contract without requiring the editor. Lives in the same xUnit collection-free zone
    /// as the other pure-managed suites (no HTTP listener, no shared static state), so no
    /// <c>[Collection]</c> attribute is needed.
    /// </para>
    /// </summary>
    public class AnimationBodiesTests
    {
        // --- AnimationLoopModeParser ---------------------------------------------
        // The internal enums are passed via their underlying int and cast back inside
        // (xUnit's InlineData serializer cannot reach an internal enum).

        [Theory]
        [InlineData("none", 1)]
        [InlineData("linear", 2)]
        [InlineData("pingpong", 3)]
        [InlineData("Linear", 2)]        // case-insensitive
        [InlineData(" none ", 1)]        // trimmed
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("wrap", 0)]          // unrecognized
        public void LoopModeParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((AnimationLoopMode)expected, AnimationLoopModeParser.Parse(raw));
        }

        // --- AnimationTrackTypeParser -------------------------------------------

        [Theory]
        [InlineData("value", 1)]
        [InlineData("position_3d", 2)]
        [InlineData("rotation_3d", 3)]
        [InlineData("scale_3d", 4)]
        [InlineData("Value", 1)]         // case-insensitive
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("blend_shape", 0)]   // unsupported in v1
        [InlineData("method", 0)]
        [InlineData("bezier", 0)]
        public void TrackTypeParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((AnimationTrackType)expected, AnimationTrackTypeParser.Parse(raw));
        }

        // --- AnimationInterpolationParser ---------------------------------------

        [Theory]
        [InlineData("nearest", 1)]
        [InlineData("linear", 2)]
        [InlineData("cubic", 3)]
        [InlineData("Linear", 2)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("spline", 0)]
        public void InterpolationParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((AnimationInterpolation)expected, AnimationInterpolationParser.Parse(raw));
        }

        // --- AnimationUpdateModeParser ------------------------------------------

        [Theory]
        [InlineData("continuous", 1)]
        [InlineData("discrete", 2)]
        [InlineData("capture", 3)]
        [InlineData("Continuous", 1)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("trigger", 0)]
        public void UpdateModeParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((AnimationUpdateMode)expected, AnimationUpdateModeParser.Parse(raw));
        }

        // --- AnimationDefaultsBody ----------------------------------------------

        [Fact]
        public void AnimationDefaultsBody_parses_without_fields()
        {
            // No fields in v1 — the type exists for signature parity.
            var b = AnimationDefaultsBody.Parse(null);
            Assert.NotNull(b);
            var b2 = AnimationDefaultsBody.Parse("{\"unused\":1}");
            Assert.NotNull(b2);
        }

        // --- AnimationPlayerCreateBody ------------------------------------------

        [Fact]
        public void PlayerCreateBody_empty_body_returns_defaults()
        {
            var b = AnimationPlayerCreateBody.Parse(null);
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
        }

        [Fact]
        public void PlayerCreateBody_reads_all_fields()
        {
            var b = AnimationPlayerCreateBody.Parse(
                "{\"name\":\"Anim\",\"parent_node_path\":\"Main\",\"position\":\"1,2,3\"}");
            Assert.Equal("Anim", b.Name);
            Assert.Equal("Main", b.ParentNodePath);
            Assert.Equal("1,2,3", b.Position);
        }

        // --- AnimationLibraryAddBody --------------------------------------------

        [Fact]
        public void LibraryAddBody_empty_body_has_no_node_path()
        {
            var b = AnimationLibraryAddBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.Equal("default", b.EffectiveLibrary); // defaults to "default"
        }

        [Fact]
        public void LibraryAddBody_reads_node_path_and_library()
        {
            var b = AnimationLibraryAddBody.Parse(
                "{\"node_path\":\"Main/Player\",\"library\":\"player\"}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Main/Player", b.NodePath);
            Assert.Equal("player", b.Library);
            Assert.Equal("player", b.EffectiveLibrary);
        }

        [Fact]
        public void LibraryAddBody_empty_library_falls_back_to_default()
        {
            var b = AnimationLibraryAddBody.Parse(
                "{\"node_path\":\"Main/Player\",\"library\":\"\"}");
            Assert.Equal("default", b.EffectiveLibrary);
        }

        // --- AnimationCreateBody ------------------------------------------------

        [Fact]
        public void CreateBody_empty_body_has_nothing()
        {
            var b = AnimationCreateBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.False(b.HasAnimation);
            Assert.Equal("default", b.EffectiveLibrary);
            Assert.Null(b.Length);
            Assert.Equal(AnimationLoopMode.Unknown, b.LoopMode);
            Assert.False(b.HasLoopMode);
        }

        [Fact]
        public void CreateBody_reads_all_fields()
        {
            var b = AnimationCreateBody.Parse(
                "{\"node_path\":\"Main/Player\",\"animation\":\"Idle\",\"library\":\"player\"," +
                "\"length\":2.5,\"loop_mode\":\"linear\"}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Main/Player", b.NodePath);
            Assert.True(b.HasAnimation);
            Assert.Equal("Idle", b.Animation);
            Assert.Equal("player", b.EffectiveLibrary);
            Assert.Equal(2.5f, b.Length);
            Assert.Equal(AnimationLoopMode.Linear, b.LoopMode);
            Assert.True(b.HasLoopMode);
        }

        [Fact]
        public void CreateBody_unrecognized_loop_mode_is_unknown_but_present()
        {
            // The parser records Unknown but HasLoopMode stays true so the handler can
            // distinguish "leave default" (absent) from "reject the bad token" (present).
            var b = AnimationCreateBody.Parse(
                "{\"node_path\":\"A\",\"animation\":\"X\",\"loop_mode\":\"wrap\"}");
            Assert.Equal(AnimationLoopMode.Unknown, b.LoopMode);
            Assert.True(b.HasLoopMode);
        }

        // --- AnimationAddTrackBody ----------------------------------------------

        [Fact]
        public void AddTrackBody_empty_body_has_nothing()
        {
            var b = AnimationAddTrackBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.False(b.HasAnimation);
            Assert.False(b.HasTrackPath);
            Assert.Equal(AnimationTrackType.Unknown, b.TrackType);
            Assert.False(b.HasTrackType);
        }

        [Fact]
        public void AddTrackBody_reads_all_fields()
        {
            var b = AnimationAddTrackBody.Parse(
                "{\"node_path\":\"Main/Player\",\"library\":\"default\",\"animation\":\"Idle\"," +
                "\"track_type\":\"value\",\"track_path\":\"Sprite2D:position\"," +
                "\"value_type\":\"float\",\"update_mode\":\"discrete\"}");
            Assert.Equal("Main/Player", b.NodePath);
            Assert.Equal("default", b.EffectiveLibrary);
            Assert.Equal("Idle", b.Animation);
            Assert.Equal(AnimationTrackType.Value, b.TrackType);
            Assert.True(b.HasTrackType);
            Assert.Equal("Sprite2D:position", b.TrackPath);
            Assert.Equal("float", b.ValueType);
            Assert.Equal(AnimationUpdateMode.Discrete, b.UpdateMode);
            Assert.True(b.HasUpdateMode);
        }

        [Fact]
        public void AddTrackBody_unrecognized_track_type_is_unknown_but_present()
        {
            var b = AnimationAddTrackBody.Parse(
                "{\"node_path\":\"A\",\"animation\":\"X\",\"track_type\":\"blend_shape\"," +
                "\"track_path\":\"Mesh:blend_shapes/Mouth\"}");
            Assert.Equal(AnimationTrackType.Unknown, b.TrackType);
            Assert.True(b.HasTrackType);
        }

        // --- AnimationInsertKeyBody ---------------------------------------------

        [Fact]
        public void InsertKeyBody_empty_body_has_no_scalars_and_invalid_value()
        {
            var b = AnimationInsertKeyBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.False(b.HasAnimation);
            Assert.Null(b.TrackIndex);
            Assert.False(b.HasTime);
            Assert.False(b.Value.IsValid);
        }

        [Fact]
        public void InsertKeyBody_reads_number_value()
        {
            var b = AnimationInsertKeyBody.Parse(
                "{\"node_path\":\"A\",\"animation\":\"X\",\"track_index\":0,\"time\":0.5," +
                "\"value\":1.5}");
            Assert.Equal("A", b.NodePath);
            Assert.Equal("X", b.Animation);
            Assert.Equal(0, b.TrackIndex);
            Assert.Equal(0.5f, b.Time);
            Assert.True(b.Value.IsValid);
            Assert.Equal(AnimationValueKind.Number, b.Value.Kind);
            Assert.Equal(1.5, b.Value.Number);
        }

        [Fact]
        public void InsertKeyBody_reads_vector3_value_and_interpolation()
        {
            var b = AnimationInsertKeyBody.Parse(
                "{\"node_path\":\"A\",\"animation\":\"X\",\"track_index\":2,\"time\":0.0," +
                "\"value\":{\"x\":1,\"y\":2,\"z\":3},\"interpolation\":\"cubic\"," +
                "\"transition\":0.5}");
            Assert.Equal(2, b.TrackIndex);
            Assert.True(b.Value.IsValid);
            Assert.Equal(AnimationValueKind.Vector3, b.Value.Kind);
            Assert.Equal(1.0, b.Value.X);
            Assert.Equal(2.0, b.Value.Y);
            Assert.Equal(3.0, b.Value.Z);
            Assert.Equal(AnimationInterpolation.Cubic, b.Interpolation);
            Assert.True(b.HasInterpolation);
            Assert.Equal(0.5f, b.Transition);
        }

        [Fact]
        public void InsertKeyBody_null_value_is_invalid()
        {
            var b = AnimationInsertKeyBody.Parse(
                "{\"node_path\":\"A\",\"animation\":\"X\",\"track_index\":0,\"time\":0," +
                "\"value\":null}");
            Assert.False(b.Value.IsValid);
        }

        // --- AnimationGetBody ---------------------------------------------------

        [Fact]
        public void GetBody_empty_body_has_no_node_path_and_default_caps()
        {
            var b = AnimationGetBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.False(b.HasLibraryFilter);
            Assert.False(b.HasAnimationFilter);
            Assert.False(b.IncludeKeys);
            Assert.Equal(32, b.MaxKeys);
            Assert.False(b.HasMaxKeys);
        }

        [Fact]
        public void GetBody_reads_filters_and_caps()
        {
            var b = AnimationGetBody.Parse(
                "{\"node_path\":\"A\",\"library\":\"default\",\"animation\":\"Idle\"," +
                "\"include_keys\":true,\"max_keys\":64}");
            Assert.Equal("A", b.NodePath);
            Assert.True(b.HasLibraryFilter);
            Assert.Equal("default", b.Library);
            Assert.True(b.HasAnimationFilter);
            Assert.Equal("Idle", b.Animation);
            Assert.True(b.IncludeKeys);
            Assert.Equal(64, b.MaxKeys);
            Assert.True(b.HasMaxKeys);
        }

        [Fact]
        public void GetBody_include_keys_defaults_false()
        {
            var b = AnimationGetBody.Parse("{\"node_path\":\"A\"}");
            Assert.False(b.IncludeKeys);
        }

        // ===========================================================================
        // AnimationKeyValue — the parser added in P12.4. The highest-value unit test
        // surface in this pack: it pins the JSON-to-Variant contract without the editor.
        // ===========================================================================

        [Fact]
        public void Value_number_int_parses_as_float_kind()
        {
            var v = AnimationKeyValue.ParseToken("42");
            Assert.Equal(AnimationValueKind.Number, v.Kind);
            Assert.Equal(42.0, v.Number);
        }

        [Fact]
        public void Value_number_float_parses_with_invariant_culture()
        {
            var v = AnimationKeyValue.ParseToken("1.5");
            Assert.Equal(AnimationValueKind.Number, v.Kind);
            Assert.Equal(1.5, v.Number);
        }

        [Fact]
        public void Value_negative_number_parses()
        {
            var v = AnimationKeyValue.ParseToken("-3.14");
            Assert.Equal(AnimationValueKind.Number, v.Kind);
            Assert.Equal(-3.14, v.Number);
        }

        [Fact]
        public void Value_bool_literals_parse()
        {
            Assert.Equal(AnimationValueKind.Bool, AnimationKeyValue.ParseToken("true").Kind);
            Assert.True(AnimationKeyValue.ParseToken("true").Bool);
            Assert.Equal(AnimationValueKind.Bool, AnimationKeyValue.ParseToken("false").Kind);
            Assert.False(AnimationKeyValue.ParseToken("false").Bool);
        }

        [Fact]
        public void Value_bare_word_parses_as_string()
        {
            // ExtractRawValue unwraps JSON string quotes, so a JSON "hello" arrives here as
            // the bare text "hello". A bare non-numeric, non-bool token is treated as string
            // text. (A non-conforming client that sent an unquoted bare word would still get
            // a string here, which is the safest degradation.)
            var v = AnimationKeyValue.ParseToken("hello");
            Assert.Equal(AnimationValueKind.String, v.Kind);
            Assert.Equal("hello", v.StringValue);
        }

        [Fact]
        public void Value_vector2_components_infer_vector2()
        {
            var v = AnimationKeyValue.ParseToken("{\"x\":1,\"y\":2}");
            Assert.Equal(AnimationValueKind.Vector2, v.Kind);
            Assert.Equal(1.0, v.X);
            Assert.Equal(2.0, v.Y);
        }

        [Fact]
        public void Value_vector3_components_infer_vector3()
        {
            var v = AnimationKeyValue.ParseToken("{\"x\":1,\"y\":2,\"z\":3}");
            Assert.Equal(AnimationValueKind.Vector3, v.Kind);
            Assert.Equal(3.0, v.Z);
        }

        [Fact]
        public void Value_color_components_infer_color_with_alpha_default_one()
        {
            var v = AnimationKeyValue.ParseToken("{\"r\":1,\"g\":0.5,\"b\":0}");
            Assert.Equal(AnimationValueKind.Color, v.Kind);
            Assert.Equal(1.0, v.X);  // r
            Assert.Equal(0.5, v.Y);  // g
            Assert.Equal(0.0, v.Z);  // b
            Assert.Equal(1.0, v.W);  // a defaults to 1.0
        }

        [Fact]
        public void Value_color_components_explicit_alpha()
        {
            var v = AnimationKeyValue.ParseToken("{\"r\":1,\"g\":1,\"b\":1,\"a\":0.5}");
            Assert.Equal(AnimationValueKind.Color, v.Kind);
            Assert.Equal(0.5, v.W);
        }

        [Fact]
        public void Value_type_tagged_vector3_wins_over_component_inference()
        {
            // A type tag overrides component inference.
            var v = AnimationKeyValue.ParseToken("{\"type\":\"vector3\",\"x\":1,\"y\":2,\"z\":3}");
            Assert.Equal(AnimationValueKind.Vector3, v.Kind);
        }

        [Fact]
        public void Value_type_tagged_vector2_requires_both_components()
        {
            var v = AnimationKeyValue.ParseToken("{\"type\":\"vector2\",\"x\":1}");
            Assert.Equal(AnimationValueKind.Invalid, v.Kind);
        }

        [Fact]
        public void Value_unrecognized_type_tag_is_invalid()
        {
            var v = AnimationKeyValue.ParseToken("{\"type\":\"quaternion\",\"x\":1}");
            Assert.Equal(AnimationValueKind.Invalid, v.Kind);
        }

        [Fact]
        public void Value_empty_object_is_invalid()
        {
            var v = AnimationKeyValue.ParseToken("{}");
            Assert.Equal(AnimationValueKind.Invalid, v.Kind);
        }

        [Fact]
        public void Value_empty_string_is_invalid()
        {
            var v = AnimationKeyValue.ParseToken("");
            Assert.Equal(AnimationValueKind.Invalid, v.Kind);
        }

        [Fact]
        public void Value_Parse_reads_from_body()
        {
            var v = AnimationKeyValue.Parse(
                "{\"node_path\":\"A\",\"animation\":\"X\",\"track_index\":0,\"time\":0," +
                "\"value\":{\"x\":10,\"y\":20}}");
            Assert.Equal(AnimationValueKind.Vector2, v.Kind);
            Assert.Equal(10.0, v.X);
            Assert.Equal(20.0, v.Y);
        }

        [Fact]
        public void Value_Parse_absent_returns_invalid()
        {
            var v = AnimationKeyValue.Parse("{\"node_path\":\"A\"}");
            Assert.Equal(AnimationValueKind.Invalid, v.Kind);
        }

        [Fact]
        public void Value_Parse_null_returns_invalid()
        {
            var v = AnimationKeyValue.Parse("{\"value\":null}");
            Assert.Equal(AnimationValueKind.Invalid, v.Kind);
        }

        [Fact]
        public void Value_factory_methods_set_correct_kind()
        {
            Assert.Equal(AnimationValueKind.Number, AnimationKeyValue.NumberValue(1.0).Kind);
            Assert.Equal(AnimationValueKind.Bool, AnimationKeyValue.BoolValue(true).Kind);
            Assert.Equal(AnimationValueKind.String, AnimationKeyValue.StringValueOf("x").Kind);
            Assert.Equal(AnimationValueKind.Vector2, AnimationKeyValue.Vector2Value(1, 2).Kind);
            Assert.Equal(AnimationValueKind.Vector3, AnimationKeyValue.Vector3Value(1, 2, 3).Kind);
            Assert.Equal(AnimationValueKind.Color, AnimationKeyValue.ColorValue(1, 1, 1, 1).Kind);
            Assert.Equal(AnimationValueKind.Invalid, AnimationKeyValue.Invalid().Kind);
        }

        [Fact]
        public void Value_IsValid_is_false_only_for_invalid()
        {
            Assert.False(AnimationKeyValue.Invalid().IsValid);
            Assert.True(AnimationKeyValue.NumberValue(0).IsValid);
            Assert.True(AnimationKeyValue.StringValueOf("").IsValid);
        }
    }
}
