#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace GodotOpenMcp.Bridge.Editor
{
    // ===========================================================================
    // P18.1 input-map pack request bodies + event-type enum.
    //
    // Three tools land in this pack:
    //   - godot_open_mcp_input_map_get (read-only) — list every InputMap action
    //     + its events, or read a single named action.
    //   - godot_open_mcp_input_map_action_add (mutating, gated) — add a new action
    //     to the InputMap + persist to project.godot's [input] section.
    //   - godot_open_mcp_input_map_action_set_events (mutating, gated) — replace an
    //     existing action's event list.
    //
    // The body types mirror the hand-rolled IndexOf-substring style already used by
    // the P12.x / P16.x packs (see packages/bridge/AGENTS.md §Transport: the bridge
    // deliberately carries no typed JSON DOM dependency on the hot path). Pure-
    // managed (no Godot API surface, no `#if TOOLS`), so the parsing logic is
    // unit-testable in the binary-less xUnit host.
    //
    // The shared extraction primitives live in the P12.1 `JsonScalar` static class
    // (sibling Extensions/Tilemap/TilemapBodies.cs). P18.1 reuses ExtractString /
    // ExtractIntOrNull / ExtractFloat / ExtractBool and the ExtractRawValue entry
    // point. A list-of-objects extractor for the set_events `events[]` array is
    // added here (SliceObjectEntries) because no existing JsonScalar helper walks a
    // list of objects; the P16.1 settings pack carries its own local copy of the
    // same array walk for its `fields[]` array.
    //
    // Event-type vocabulary: a single `type` discriminator on each event selects
    // which InputEvent subclass the handler builds (key / mouse_button /
    // joypad_button / joypad_motion). The design decision (P18.1 §2 "Event type
    // enum") deliberately avoids one tool per event kind — one set_events call can
    // mix key + mouse + joypad events in a single replacement list.
    //
    // Fidelity: adapt — Unity Open MCP's `inputsystem_action_add` / `binding_add`
    // (TypedTools/Extensions/InputSystem/InputSystemTools.cs) supplies the
    // action/binding CRUD shape (add checks for duplicates; a mutator persists the
    // asset once and returns a count). Godot's InputMap is flat (action → events,
    // no ActionMap hierarchy), writes route through the InputMap API mirrored to
    // ProjectSettings `input/<name>` + ProjectSettings.Save (never raw text edits),
    // and events are Godot InputEvent subclasses (no Unity InputAction/Binding/
    // composite/interactions/processors specifics).
    // ===========================================================================

    /// <summary>
    /// Normalized event-type token extracted from an event object's <c>type</c>
    /// field. <see cref="Unknown"/> covers both "absent" and "not a valid token";
    /// the handler turns that into <c>invalid_event_type</c>. The four supported
    /// kinds map one-to-one to the InputEvent subclasses an InputMap action
    /// commonly binds: <c>InputEventKey</c> / <c>InputEventMouseButton</c> /
    /// <c>InputEventJoypadButton</c> / <c>InputEventJoypadMotion</c>.
    /// </summary>
    internal enum InputEventType
    {
        Unknown = 0,
        Key = 1,
        MouseButton = 2,
        JoypadButton = 3,
        JoypadMotion = 4,
    }

    /// <summary>
    /// Map a raw <c>type</c> string to an <see cref="InputEventType"/>. Returns
    /// <see cref="InputEventType.Unknown"/> for null / empty / unrecognized tokens
    /// so the handler can surface a single <c>invalid_event_type</c> error.
    /// Case-insensitive to tolerate an agent sending "Key" / "MOUSE_BUTTON".
    /// </summary>
    internal static class InputEventTypeParser
    {
        internal static InputEventType Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return InputEventType.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "key": return InputEventType.Key;
                case "mouse_button": return InputEventType.MouseButton;
                case "joypad_button": return InputEventType.JoypadButton;
                case "joypad_motion": return InputEventType.JoypadMotion;
                default: return InputEventType.Unknown;
            }
        }

        /// <summary>
        /// Render an <see cref="InputEventType"/> back to its MCP schema string.
        /// Used by the read path so the JSON a get returns round-trips into
        /// set_events. Returns an empty string for <see cref="InputEventType.Unknown"/>.
        /// </summary>
        internal static string ToSchemaString(InputEventType type)
        {
            switch (type)
            {
                case InputEventType.Key: return "key";
                case InputEventType.MouseButton: return "mouse_button";
                case InputEventType.JoypadButton: return "joypad_button";
                case InputEventType.JoypadMotion: return "joypad_motion";
                default: return "";
            }
        }
    }

    /// <summary>
    /// Pure-managed representation of one InputEvent extracted from a set_events
    /// <c>events[]</c> entry. Carries the <c>type</c> discriminator plus the
    /// nullable fields each kind reads — the handler (<c>#if TOOLS</c>) converts
    /// the populated fields into the matching Godot <c>InputEvent</c> subclass.
    /// Every field is nullable so "absent" is distinguishable from an explicit 0
    /// (e.g. a missing <c>physical_keycode</c> vs an explicit <c>0</c> = KEY_NONE).
    ///
    /// <para>
    /// The int fields hold Godot enum ordinals directly (Key / MouseButton /
    /// JoyButton / JoyAxis) so they round-trip verbatim through the read path —
    /// the get tool emits the same ints the handler consumes here. All known
    /// Godot Key/MouseButton/Joy ordinals fit in the int range.
    /// </para>
    /// </summary>
    internal sealed class InputEventSpec
    {
        internal InputEventType Type { get; }
        internal int? PhysicalKeycode { get; }    // Key enum ordinal
        internal int? Keycode { get; }            // Key enum ordinal (logical)
        internal int? Unicode { get; }
        internal int? ButtonIndex { get; }        // MouseButton / JoyButton ordinal
        internal int? Axis { get; }               // JoyAxis ordinal
        internal float? AxisValue { get; }
        internal int? Device { get; }
        internal bool? DoubleClick { get; }

        internal bool HasButtonIndex => ButtonIndex.HasValue;
        internal bool HasAxis => Axis.HasValue;
        internal bool HasAxisValue => AxisValue.HasValue;

        internal InputEventSpec(
            InputEventType type,
            int? physicalKeycode,
            int? keycode,
            int? unicode,
            int? buttonIndex,
            int? axis,
            float? axisValue,
            int? device,
            bool? doubleClick)
        {
            Type = type;
            PhysicalKeycode = physicalKeycode;
            Keycode = keycode;
            Unicode = unicode;
            ButtonIndex = buttonIndex;
            Axis = axis;
            AxisValue = axisValue;
            Device = device;
            DoubleClick = doubleClick;
        }

        /// <summary>
        /// Parse one event entry (a single JSON <c>{...}</c> object substring).
        /// Reads the <c>type</c> discriminator plus every nullable field; the
        /// handler validates the type-specific subset (e.g. a key event needs at
        /// least one of physical_keycode / keycode). Fields an agent omits stay
        /// null rather than degrading to a silent default.
        /// </summary>
        internal static InputEventSpec Parse(string entry)
        {
            var type = InputEventTypeParser.Parse(JsonScalar.ExtractString(entry, "type"));
            return new InputEventSpec(
                type,
                JsonScalar.ExtractIntOrNull(entry, "physical_keycode"),
                JsonScalar.ExtractIntOrNull(entry, "keycode"),
                JsonScalar.ExtractIntOrNull(entry, "unicode"),
                JsonScalar.ExtractIntOrNull(entry, "button_index"),
                JsonScalar.ExtractIntOrNull(entry, "axis"),
                JsonScalar.ExtractFloat(entry, "axis_value"),
                JsonScalar.ExtractIntOrNull(entry, "device"),
                JsonScalar.ExtractBool(entry, "doubleclick"));
        }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_input_map_get</c> (P18.1,
    /// read-only). Carries only the optional <c>action</c> filter — when null the
    /// handler lists every InputMap action; when set it reads a single named
    /// action.
    /// </summary>
    internal sealed class InputMapGetBody
    {
        internal string? Action { get; private set; }
        internal bool HasAction => !string.IsNullOrEmpty(Action);

        internal static InputMapGetBody Parse(string? body)
        {
            var parsed = new InputMapGetBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Action = JsonScalar.ExtractString(body, "action");
            return parsed;
        }

        InputMapGetBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_input_map_action_add</c> (P18.1,
    /// mutating, gated). Carries the <c>action</c> name to add and the optional
    /// <c>deadzone</c> (clamped to [0, 1] by the handler; defaults to 0.5 — Godot's
    /// own default).
    /// </summary>
    internal sealed class InputMapActionAddBody
    {
        internal string? Action { get; private set; }
        internal float? Deadzone { get; private set; }
        internal bool HasAction => !string.IsNullOrEmpty(Action);

        internal static InputMapActionAddBody Parse(string? body)
        {
            var parsed = new InputMapActionAddBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Action = JsonScalar.ExtractString(body, "action");
            parsed.Deadzone = JsonScalar.ExtractFloat(body, "deadzone");
            return parsed;
        }

        InputMapActionAddBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_input_map_action_set_events</c>
    /// (P18.1, mutating, gated). Carries the target <c>action</c> name and the
    /// replacement <c>events[]</c> list of <see cref="InputEventSpec"/> entries.
    /// The handler validates the action exists, clears its current events, applies
    /// each spec, and persists.
    /// </summary>
    internal sealed class InputMapActionSetEventsBody
    {
        internal string? Action { get; private set; }
        internal IReadOnlyList<InputEventSpec> Events { get; private set; } = Array.Empty<InputEventSpec>();
        internal bool HasAction => !string.IsNullOrEmpty(Action);

        internal static InputMapActionSetEventsBody Parse(string? body)
        {
            var parsed = new InputMapActionSetEventsBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Action = JsonScalar.ExtractString(body, "action");
            parsed.Events = ParseEventsArray(body);
            return parsed;
        }

        /// <summary>
        /// Walk the <c>"events"</c> JSON array and return each top-level object as
        /// an <see cref="InputEventSpec"/>. Mirrors the P16.1 settings pack's
        /// <c>fields[]</c> array walk: extract the balanced array via
        /// <see cref="JsonScalar.ExtractRawValue"/>, split it into top-level object
        /// slices (skipping over nested strings / objects so a comma inside one does
        /// not split), then feed each slice to <see cref="InputEventSpec.Parse"/>.
        /// An empty / absent / null array yields an empty list (the handler surfaces
        /// <c>missing_parameter</c>).
        /// </summary>
        static IReadOnlyList<InputEventSpec> ParseEventsArray(string body)
        {
            var rawArray = JsonScalar.ExtractRawValue(body, "events");
            if (string.IsNullOrEmpty(rawArray)) return Array.Empty<InputEventSpec>();
            var entries = SliceObjectEntries(rawArray);
            var specs = new List<InputEventSpec>(entries.Count);
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                specs.Add(InputEventSpec.Parse(entry));
            }
            return specs;
        }

        /// <summary>
        /// Walk a JSON array of objects and return each top-level <c>{...}</c>
        /// element verbatim (the substring inside the surrounding brackets, split on
        /// top-level commas, with nested strings / objects / arrays skipped over so
        /// a comma inside one does not split). Mirrors the P16.1 settings pack's
        /// <c>SliceArrayEntries</c> helper — the existing <see cref="JsonScalar"/>
        /// extractors operate on a single object body, so the array walk is local
        /// to this pack.
        /// </summary>
        static List<string> SliceObjectEntries(string rawArray)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(rawArray)) return result;
            var v = rawArray.Trim();
            var openIdx = v.IndexOf('[');
            if (openIdx < 0) return result;
            int i = openIdx + 1;
            while (i < v.Length)
            {
                while (i < v.Length && char.IsWhiteSpace(v[i])) i++;
                if (i >= v.Length || v[i] == ']') break;

                // Only object elements are meaningful event specs; a non-object
                // (bare scalar / string) is skipped to the next comma.
                if (v[i] != '{')
                {
                    while (i < v.Length && v[i] != ',' && v[i] != ']') i++;
                    while (i < v.Length && (v[i] == ',' || char.IsWhiteSpace(v[i]))) i++;
                    continue;
                }

                int start = i;
                int depth = 1;
                i++; // consume the opening '{'
                while (i < v.Length && depth > 0)
                {
                    if (v[i] == '"')
                    {
                        i++;
                        while (i < v.Length)
                        {
                            if (v[i] == '\\' && i + 1 < v.Length) { i += 2; continue; }
                            if (v[i] == '"') { i++; break; }
                            i++;
                        }
                        continue;
                    }
                    if (v[i] == '{') depth++;
                    else if (v[i] == '}') depth--;
                    i++;
                }
                result.Add(v.Substring(start, i - start).Trim());
                while (i < v.Length && (v[i] == ',' || char.IsWhiteSpace(v[i]))) i++;
            }
            return result;
        }

        InputMapActionSetEventsBody() { }
    }
}
