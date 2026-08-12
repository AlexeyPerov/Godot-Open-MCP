#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P18.1 input-map pack unit tests for the pure-managed, off-editor-testable
    /// pieces: the event-type parser (+ its schema-string round-trip) and the three
    /// request-body parsers (get / action_add / action_set_events, including the
    /// <c>events[]</c> array-of-objects walk + <see cref="InputEventSpec"/> field
    /// extraction). The editor-only handlers (<see cref="InputTools.GetMap"/> /
    /// <see cref="InputTools.ActionAdd"/> / <see cref="InputTools.ActionSetEvents"/>)
    /// are <c>#if TOOLS</c> and coupled to <c>InputMap</c> + <c>ProjectSettings</c>
    /// + <c>Godot.Json</c>, none of which the binary-less xUnit host can construct —
    /// those paths are exercised by the headless Godot smoke / live call path, not
    /// here.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <c>SettingsBodiesTests</c> (copy
    /// fidelity for the parser + body-parser test shape), with cases specific to the
    /// input field set (event-type enum + round-trip, the <c>events[]</c>
    /// array-of-objects extraction, the nullable event-field contract that
    /// distinguishes "absent" from an explicit <c>0</c>). Lives in the same xUnit
    /// collection-free zone as the other pure-managed suites (no HTTP listener, no
    /// shared static state), so no <c>[Collection]</c> attribute is needed.
    /// </para>
    /// </summary>
    public class InputBodiesTests
    {
        // --- InputEventTypeParser (parse direction) ----------------------------
        // InputEventType is internal, so the [Theory] passes the expected token as
        // its underlying int and casts back inside (xUnit's InlineData serializer
        // requires a public-parameter-accessible type, which an internal enum is
        // not — same workaround the settings + CSG packs use).

        [Theory]
        [InlineData("key", 1)]
        [InlineData("mouse_button", 2)]
        [InlineData("joypad_button", 3)]
        [InlineData("joypad_motion", 4)]
        [InlineData("Key", 1)]               // case-insensitive
        [InlineData("MOUSE_BUTTON", 2)]
        [InlineData(" joypad_button ", 3)]   // tolerates whitespace
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("keyboard", 0)]          // not in the type vocabulary
        [InlineData("touch", 0)]             // a real InputEvent kind we don't build
        public void EventTypeParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((InputEventType)expected, InputEventTypeParser.Parse(raw));
        }

        // --- InputEventTypeParser (schema-string round-trip) -------------------

        [Theory]
        [InlineData(1, "key")]
        [InlineData(2, "mouse_button")]
        [InlineData(3, "joypad_button")]
        [InlineData(4, "joypad_motion")]
        [InlineData(0, "")]                  // Unknown → empty
        public void EventTypeParser_ToSchemaString_round_trips(int typeInt, string expected)
        {
            Assert.Equal(expected, InputEventTypeParser.ToSchemaString((InputEventType)typeInt));
        }

        [Fact]
        public void EventTypeParser_parse_then_render_round_trips_for_known_tokens()
        {
            foreach (var token in new[] { "key", "mouse_button", "joypad_button", "joypad_motion" })
            {
                var type = InputEventTypeParser.Parse(token);
                Assert.Equal(token, InputEventTypeParser.ToSchemaString(type));
            }
        }

        // --- InputMapGetBody ---------------------------------------------------

        [Fact]
        public void GetBody_empty_body_has_no_action()
        {
            var b = InputMapGetBody.Parse(null);
            Assert.False(b.HasAction);
        }

        [Fact]
        public void GetBody_reads_action()
        {
            var b = InputMapGetBody.Parse("{\"action\":\"jump\"}");
            Assert.True(b.HasAction);
            Assert.Equal("jump", b.Action);
        }

        [Fact]
        public void GetBody_treats_explicit_null_as_absent()
        {
            // Mirrors the JsonScalar nullable-extractor contract — a JSON null
            // degrades to null so the handler treats it as "list all".
            var b = InputMapGetBody.Parse("{\"action\":null}");
            Assert.False(b.HasAction);
        }

        // --- InputMapActionAddBody --------------------------------------------

        [Fact]
        public void ActionAddBody_empty_body_has_no_action_and_no_deadzone()
        {
            var b = InputMapActionAddBody.Parse(null);
            Assert.False(b.HasAction);
            Assert.Null(b.Deadzone);
        }

        [Fact]
        public void ActionAddBody_reads_action()
        {
            var b = InputMapActionAddBody.Parse("{\"action\":\"fire\"}");
            Assert.True(b.HasAction);
            Assert.Equal("fire", b.Action);
        }

        [Fact]
        public void ActionAddBody_reads_deadzone()
        {
            var b = InputMapActionAddBody.Parse("{\"action\":\"fire\",\"deadzone\":0.2}");
            Assert.True(b.HasAction);
            Assert.Equal(0.2f, b.Deadzone);
        }

        [Fact]
        public void ActionAddBody_deadzone_absent_is_null_not_default()
        {
            // null (absent) is distinct from an explicit value — the handler
            // supplies Godot's 0.5 default, not the parser.
            var b = InputMapActionAddBody.Parse("{\"action\":\"fire\"}");
            Assert.Null(b.Deadzone);
        }

        [Fact]
        public void ActionAddBody_treats_explicit_null_deadzone_as_absent()
        {
            var b = InputMapActionAddBody.Parse("{\"action\":\"fire\",\"deadzone\":null}");
            Assert.Null(b.Deadzone);
        }

        // --- InputMapActionSetEventsBody --------------------------------------

        [Fact]
        public void SetEventsBody_empty_body_has_no_action_and_no_events()
        {
            var b = InputMapActionSetEventsBody.Parse(null);
            Assert.False(b.HasAction);
            Assert.Empty(b.Events);
        }

        [Fact]
        public void SetEventsBody_reads_action()
        {
            var b = InputMapActionSetEventsBody.Parse("{\"action\":\"jump\"}");
            Assert.True(b.HasAction);
            Assert.Equal("jump", b.Action);
        }

        [Fact]
        public void SetEventsBody_absent_events_yields_empty_list()
        {
            var b = InputMapActionSetEventsBody.Parse("{\"action\":\"jump\"}");
            Assert.Empty(b.Events);
        }

        [Fact]
        public void SetEventsBody_treats_null_events_as_empty()
        {
            var b = InputMapActionSetEventsBody.Parse("{\"action\":\"jump\",\"events\":null}");
            Assert.Empty(b.Events);
        }

        [Fact]
        public void SetEventsBody_reads_a_single_key_event()
        {
            var b = InputMapActionSetEventsBody.Parse(
                "{\"action\":\"jump\",\"events\":[{\"type\":\"key\",\"physical_keycode\":32}]}");
            Assert.True(b.HasAction);
            var spec = Assert.Single(b.Events);
            Assert.Equal(InputEventType.Key, spec.Type);
            Assert.Equal(32, spec.PhysicalKeycode);
            Assert.Null(spec.Keycode);     // absent → null, not 0
            Assert.Null(spec.Unicode);
        }

        [Fact]
        public void SetEventsBody_reads_a_mouse_button_event_with_optional_fields()
        {
            var b = InputMapActionSetEventsBody.Parse(
                "{\"action\":\"fire\",\"events\":[{\"type\":\"mouse_button\",\"button_index\":1,\"doubleclick\":true}]}");
            var spec = Assert.Single(b.Events);
            Assert.Equal(InputEventType.MouseButton, spec.Type);
            Assert.Equal(1, spec.ButtonIndex);
            Assert.True(spec.HasButtonIndex);
            Assert.True(spec.DoubleClick);
        }

        [Fact]
        public void SetEventsBody_reads_a_joypad_motion_event()
        {
            var b = InputMapActionSetEventsBody.Parse(
                "{\"action\":\"move\",\"events\":[{\"type\":\"joypad_motion\",\"axis\":0,\"axis_value\":-1}]}");
            var spec = Assert.Single(b.Events);
            Assert.Equal(InputEventType.JoypadMotion, spec.Type);
            Assert.Equal(0, spec.Axis);
            Assert.True(spec.HasAxis);
            Assert.Equal(-1f, spec.AxisValue);
            Assert.True(spec.HasAxisValue);
        }

        [Fact]
        public void SetEventsBody_reads_a_joypad_button_event_with_device()
        {
            var b = InputMapActionSetEventsBody.Parse(
                "{\"action\":\"fire\",\"events\":[{\"type\":\"joypad_button\",\"button_index\":0,\"device\":0}]}");
            var spec = Assert.Single(b.Events);
            Assert.Equal(InputEventType.JoypadButton, spec.Type);
            Assert.Equal(0, spec.ButtonIndex);
            Assert.Equal(0, spec.Device);
        }

        [Fact]
        public void SetEventsBody_reads_multiple_events_in_order()
        {
            var b = InputMapActionSetEventsBody.Parse(
                "{\"action\":\"jump\",\"events\":[" +
                "{\"type\":\"key\",\"physical_keycode\":32}," +
                "{\"type\":\"mouse_button\",\"button_index\":1}," +
                "{\"type\":\"joypad_button\",\"button_index\":0}]}");
            Assert.Equal(3, b.Events.Count);
            Assert.Equal(InputEventType.Key, b.Events[0].Type);
            Assert.Equal(InputEventType.MouseButton, b.Events[1].Type);
            Assert.Equal(InputEventType.JoypadButton, b.Events[2].Type);
        }

        [Fact]
        public void SetEventsBody_explicit_zero_physical_keycode_is_distinct_from_absent()
        {
            // The nullable contract is load-bearing: an explicit 0 (KEY_NONE)
            // must be preserved as 0, while an absent field stays null. The
            // handler treats "no physical_keycode AND no keycode" as a skip.
            var b = InputMapActionSetEventsBody.Parse(
                "{\"action\":\"a\",\"events\":[{\"type\":\"key\",\"physical_keycode\":0}]}");
            var spec = Assert.Single(b.Events);
            Assert.Equal(0, spec.PhysicalKeycode);
            Assert.Null(spec.Keycode);
        }

        [Fact]
        public void SetEventsBody_unknown_type_is_unknown_not_thrown()
        {
            // The parser records Unknown and lets the handler surface
            // invalid_event_type / a warning — it never throws.
            var b = InputMapActionSetEventsBody.Parse(
                "{\"action\":\"a\",\"events\":[{\"type\":\"touch\",\"button_index\":0}]}");
            var spec = Assert.Single(b.Events);
            Assert.Equal(InputEventType.Unknown, spec.Type);
        }

        [Fact]
        public void SetEventsBody_absent_type_is_unknown()
        {
            var b = InputMapActionSetEventsBody.Parse(
                "{\"action\":\"a\",\"events\":[{\"physical_keycode\":32}]}");
            var spec = Assert.Single(b.Events);
            Assert.Equal(InputEventType.Unknown, spec.Type);
            // The fields still parse — the handler decides what to do with an
            // unknown-typed spec (skip with a warning).
            Assert.Equal(32, spec.PhysicalKeycode);
        }

        [Fact]
        public void SetEventsBody_empty_events_array_yields_empty_list()
        {
            var b = InputMapActionSetEventsBody.Parse("{\"action\":\"a\",\"events\":[]}");
            Assert.Empty(b.Events);
        }

        [Fact]
        public void SetEventsBody_skips_non_object_array_entries()
        {
            // A stray scalar / string inside the events array is skipped, not
            // crashing the walk (defensive against malformed input).
            var b = InputMapActionSetEventsBody.Parse(
                "{\"action\":\"a\",\"events\":[42,\"oops\",{\"type\":\"key\",\"physical_keycode\":65}]}");
            var spec = Assert.Single(b.Events);
            Assert.Equal(InputEventType.Key, spec.Type);
            Assert.Equal(65, spec.PhysicalKeycode);
        }

        [Fact]
        public void SetEventsBody_object_with_nested_comma_does_not_split()
        {
            // A nested object inside an event (e.g. a future field) must NOT split
            // the entry — the array walk skips over balanced braces.
            var b = InputMapActionSetEventsBody.Parse(
                "{\"action\":\"a\",\"events\":[{\"type\":\"key\",\"physical_keycode\":65,\"meta\":{\"x\":1,\"y\":2}}]}");
            var spec = Assert.Single(b.Events);
            Assert.Equal(InputEventType.Key, spec.Type);
            Assert.Equal(65, spec.PhysicalKeycode);
        }
    }
}
