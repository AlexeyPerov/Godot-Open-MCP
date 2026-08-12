#if TOOLS
#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Input-map pack (P18.1) — three typed tools for reading and writing the
    /// Godot <c>InputMap</c> (actions + their bound events) via Godot's own
    /// <c>InputMap</c> API, persisted to <c>project.godot</c>'s <c>[input]</c>
    /// section through <c>ProjectSettings</c>:
    /// <c>godot_open_mcp_input_map_get</c> (read-only),
    /// <c>godot_open_mcp_input_map_action_add</c> (mutating, gated), and
    /// <c>godot_open_mcp_input_map_action_set_events</c> (mutating, gated).
    ///
    /// <para>
    /// <b>Fidelity:</b> adapt — Unity Open MCP's <c>inputsystem_action_add</c> /
    /// <c>binding_add</c> (<c>TypedTools/Extensions/InputSystem/InputSystemTools.cs</c>)
    /// supplies the action/binding CRUD shape (an add checks for duplicates and
    /// returns a count; a mutator persists the asset once). The deltas from the
    /// Unity pattern are: (1) Godot's <c>InputMap</c> is flat — action → events,
    /// with no Unity ActionMap / Action / Binding / composite / interactions /
    /// processors hierarchy; (2) writes route through the <c>InputMap</c> API
    /// (so the running editor reflects the change immediately) mirrored to
    /// <c>ProjectSettings.SetSetting("input/&lt;name&gt;", ...)</c> +
    /// <c>ProjectSettings.Save</c> (so the change persists to
    /// <c>project.godot</c>) — never raw text edits, which risk corrupting the
    /// file's <c>Object(InputEventKey,...)</c> escaping; (3) events are Godot
    /// <c>InputEvent</c> subclasses addressed by a single <c>type</c> enum (key /
    /// mouse_button / joypad_button / joypad_motion) rather than Unity binding
    /// path strings.
    /// </para>
    ///
    /// <para>
    /// <b>Read path.</b> <c>get</c> enumerates <c>InputMap.GetActions()</c> (the
    /// runtime authority — reflects the live editor state, not just the on-disk
    /// file), reads each action's deadzone + events, and serializes each event to
    /// a clean <c>{ type, ...kind-specific fields }</c> JSON object. An optional
    /// <c>action</c> filter reads one named action; omitting it lists every action.
    /// </para>
    ///
    /// <para>
    /// <b>Write path.</b> <c>action_add</c> adds a new action (rejecting a name
    /// that already exists); <c>action_set_events</c> replaces an existing action's
    /// whole event list. Both mutate the live <c>InputMap</c> first (immediate
    /// effect in the editor), then mirror the action to
    /// <c>ProjectSettings</c> <c>input/&lt;name&gt;</c> and persist once with
    /// <c>ProjectSettings.Save</c>. Per-event failures in <c>set_events</c> are
    /// accumulated as warnings (non-aborting) so a batch's good events still land;
    /// an entirely unbuildable batch surfaces <c>no_applicable_events</c> and
    /// leaves the existing events intact.
    /// </para>
    ///
    /// <para>
    /// <b>Gate contract.</b> Both mutators register with
    /// <see cref="BridgeToolEntry.DefaultGate"/> <c>"enforce"</c> and validate
    /// <c>paths_hint</c> themselves (mirrors the P16.1 settings mutator and the
    /// P12.x domain mutators). The dispatch layer rejects an empty hint when the
    /// effective gate is not <c>off</c>; the handler-level guard ALSO fires when an
    /// agent overrides with <c>gate:"off"</c>, so <c>paths_hint</c> is always
    /// required. The read-only <c>get</c> has no gate surface.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): every handler touches <c>InputMap</c> and
    /// <c>ProjectSettings</c>. The pure-managed pieces (<see cref="InputEventType"/>
    /// / <see cref="InputEventTypeParser"/> / <see cref="InputEventSpec"/> /
    /// <see cref="InputMapGetBody"/> / <see cref="InputMapActionAddBody"/> /
    /// <see cref="InputMapActionSetEventsBody"/>) live outside this guard and are
    /// unit-tested.
    /// </summary>
    internal static class InputTools
    {
        internal const string InputMapGetToolName = "godot_open_mcp_input_map_get";
        internal const string InputMapActionAddToolName = "godot_open_mcp_input_map_action_add";
        internal const string InputMapActionSetEventsToolName = "godot_open_mcp_input_map_action_set_events";

        /// <summary>
        /// The <c>paths_hint</c> scope every mutator call must declare. InputMap
        /// actions persist into <c>project.godot</c>'s <c>[input]</c> section, so
        /// the single mutated file is <c>project.godot</c> — the same scope the
        /// P16.1 settings mutator uses. Surfacing the literal here keeps the tool's
        /// contract self-documenting and lets an agent copy it verbatim.
        /// </summary>
        internal const string ProjectGodotHint = "res://project.godot";

        /// <summary>
        /// The default deadzone Godot itself uses for a new action (<c>0.5</c>).
        /// Matches <c>InputMap.AddAction</c>'s default so an agent that omits the
        /// field lands the same value the editor's Input Map dialog would.
        /// </summary>
        internal const float DefaultDeadzone = 0.5f;

        /// <summary>
        /// Device id meaning "any device" in Godot — the sensible default for
        /// keyboard / mouse bindings (an agent rarely wants to bind to exactly one
        /// gamepad slot). Applied when an event spec omits <c>device</c>.
        /// </summary>
        internal const int AnyDevice = -1;

        /// <summary>
        /// Register the input-map tool family. The mutators (<c>action_add</c> +
        /// <c>action_set_events</c>) declare <c>defaultGate:"enforce"</c> and
        /// <c>isMutating:true</c>; the read-only <c>get</c> is <c>off</c>. All three
        /// belong to the <c>input</c> group. Registered once at plugin enable;
        /// idempotent (the registry replaces on re-register).
        /// </summary>
        internal static void RegisterInputTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: InputMapGetToolName,
                isMutating: false,
                defaultGate: "off",
                group: "input",
                handler: GetMap));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: InputMapActionAddToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "input",
                handler: ActionAdd));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: InputMapActionSetEventsToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "input",
                handler: ActionSetEvents));
        }

        // ===========================================================================
        // 1. godot_open_mcp_input_map_get (read-only)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_input_map_get</c>. Lists every InputMap
        /// action + its deadzone + its events, or reads a single named action when
        /// <c>action</c> is set. No scene required; no gate surface. An absent
        /// single-action read returns <c>action_not_found</c>.
        ///
        /// <para>
        /// Structured failures: <c>execution_error</c>, <c>action_not_found</c>
        /// (single-action read of a name not in the InputMap).
        /// </para>
        /// </summary>
        internal static ToolDispatchResult GetMap(string body)
        {
            var request = InputMapGetBody.Parse(body);
            try
            {
                if (request.HasAction)
                    return ReadSingleAction(request.Action!);
                return ReadAllActions();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        /// <summary>
        /// Read one named action. Returns <c>action_not_found</c> when the name is
        /// not in the InputMap (a typo is more likely than a deliberate probe of a
        /// missing action).
        /// </summary>
        static ToolDispatchResult ReadSingleAction(string action)
        {
            if (!InputMap.HasAction(action))
                return ToolDispatchResult.Fail(
                    "action_not_found",
                    $"No InputMap action named '{action}'. Use input_map_get with no " +
                    "action to list every action.");
            return ToolDispatchResult.Ok(SerializeAction(action, includeEvents: true));
        }

        /// <summary>
        /// List every InputMap action sorted for stable output (the raw
        /// <c>GetActions()</c> order is registration order — stable across a session
        /// but not meaningful to an agent). Each entry carries the action name, its
        /// deadzone, its event count, and its serialized events.
        /// </summary>
        static ToolDispatchResult ReadAllActions()
        {
            var names = new List<string>();
            foreach (var actionVariant in InputMap.GetActions())
            {
                var name = actionVariant.AsString();
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            names.Sort(System.StringComparer.Ordinal);

            var sb = new StringBuilder(128 + names.Count * 96);
            sb.Append('{');
            sb.Append("\"count\":").Append(names.Count);
            sb.Append(",\"actions\":[");
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(SerializeAction(names[i], includeEvents: true));
            }
            sb.Append(']');
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        /// <summary>
        /// Serialize one action to a JSON object: <c>{ action, deadzone, eventCount,
        /// events: [...] }</c>. The deadzone is read from the InputMap (the runtime
        /// authority); each event is serialized via <see cref="SerializeEvent"/>.
        /// </summary>
        static string SerializeAction(string action, bool includeEvents)
        {
            var deadzone = InputMap.ActionGetDeadzone(action);
            var events = InputMap.ActionGetEvents(action);

            var sb = new StringBuilder(64 + events.Count * 48);
            sb.Append('{');
            sb.Append("\"action\":").Append(BridgeJson.EscapeString(action));
            sb.Append(",\"deadzone\":").Append(deadzone.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"eventCount\":").Append(events.Count);
            if (includeEvents)
            {
                sb.Append(",\"events\":[");
                for (int i = 0; i < events.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var evt = events[i].As<InputEvent>();
                    sb.Append(evt != null ? SerializeEvent(evt) : "null");
                }
                sb.Append(']');
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Serialize one InputEvent to a clean <c>{ type, ... }</c> JSON object.
        /// Detects the concrete subclass via C# pattern matching and emits the
        /// kind-specific fields an agent round-trips back into
        /// <c>action_set_events</c>. Int / enum ordinals are emitted as the raw int
        /// (Key / MouseButton / JoyAxis / JoyButton ordinals) so the read path's
        /// output is exactly what the write path consumes. An unrecognized subclass
        /// surfaces as <c>{ type: "other", class: "&lt;Godot class&gt;" }</c> so an
        /// agent at least knows the event exists (it cannot be edited via
        /// set_events, but it is not silently dropped from the listing).
        /// </summary>
        static string SerializeEvent(InputEvent evt)
        {
            var sb = new StringBuilder(48);
            sb.Append('{');
            switch (evt)
            {
                case InputEventKey k:
                    sb.Append("\"type\":\"key\"");
                    sb.Append(",\"physical_keycode\":").Append((int)k.PhysicalKeycode);
                    sb.Append(",\"keycode\":").Append((int)k.Keycode);
                    sb.Append(",\"unicode\":").Append(k.Unicode);
                    sb.Append(",\"device\":").Append(k.Device);
                    break;
                case InputEventMouseButton m:
                    sb.Append("\"type\":\"mouse_button\"");
                    sb.Append(",\"button_index\":").Append((int)m.ButtonIndex);
                    sb.Append(",\"doubleclick\":").Append(m.DoubleClick ? "true" : "false");
                    sb.Append(",\"device\":").Append(m.Device);
                    break;
                case InputEventJoypadButton jb:
                    sb.Append("\"type\":\"joypad_button\"");
                    sb.Append(",\"button_index\":").Append((int)jb.ButtonIndex);
                    sb.Append(",\"device\":").Append(jb.Device);
                    break;
                case InputEventJoypadMotion jm:
                    sb.Append("\"type\":\"joypad_motion\"");
                    sb.Append(",\"axis\":").Append((int)jm.Axis);
                    sb.Append(",\"axis_value\":").Append(
                        jm.AxisValue.ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(",\"device\":").Append(jm.Device);
                    break;
                default:
                    // An InputEvent subclass set_events cannot build (e.g.
                    // InputEventScreenTouch / InputEventMagnifyGesture). Surface it
                    // rather than silently dropping — the agent sees the binding exists.
                    sb.Append("\"type\":\"other\"");
                    sb.Append(",\"class\":").Append(BridgeJson.EscapeString(evt.GetClass()));
                    break;
            }
            sb.Append('}');
            return sb.ToString();
        }

        // ===========================================================================
        // 2. godot_open_mcp_input_map_action_add (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_input_map_action_add</c>. Adds a new action
        /// to the InputMap (with a clamped deadzone) and persists it to
        /// <c>project.godot</c>'s <c>[input]</c> section. An action that already
        /// exists surfaces <c>action_exists</c> — use <c>action_set_events</c> to
        /// change an existing action's events. <c>paths_hint</c> =
        /// <c>res://project.godot</c> is mandatory.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>action_exists</c>, <c>execution_error</c> (including
        /// <c>ProjectSettings.Save</c> failure — the in-memory action lands but is
        /// not persisted).
        /// </para>
        /// </summary>
        internal static ToolDispatchResult ActionAdd(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "input_map_action_add is mutating; pass a non-empty paths_hint scoped to " +
                    $"the project file ({ProjectGodotHint}).");

            var request = InputMapActionAddBody.Parse(body);

            if (!request.HasAction)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "input_map_action_add requires 'action' (the action name to add).");

            var action = request.Action!;
            if (InputMap.HasAction(action))
                return ToolDispatchResult.Fail(
                    "action_exists",
                    $"An InputMap action named '{action}' already exists. Use " +
                    "input_map_action_set_events to change its events.");

            var deadzone = ClampDeadzone(request.Deadzone);

            try
            {
                InputMap.AddAction(action, deadzone);
                PersistAction(action);

                var saveErr = ProjectSettings.Save();
                if (saveErr != Error.Ok)
                    return ToolDispatchResult.Fail(
                        "execution_error",
                        $"ProjectSettings.Save failed with error {saveErr}. The in-memory action " +
                        "was added but was NOT persisted to project.godot.");

                var sb = new StringBuilder(96);
                sb.Append('{');
                sb.Append("\"action\":").Append(BridgeJson.EscapeString(action));
                sb.Append(",\"actionAdded\":true");
                sb.Append(",\"deadzone\":").Append(deadzone.ToString("R", CultureInfo.InvariantCulture));
                sb.Append(",\"eventCount\":0");
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ===========================================================================
        // 3. godot_open_mcp_input_map_action_set_events (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_input_map_action_set_events</c>. Replaces
        /// an existing action's whole event list. Each event in the
        /// <c>events[]</c> array is built from its <c>type</c> + kind-specific
        /// fields; an unbuildable event (unknown type / missing required field) is
        /// skipped with a warning (non-aborting) so a batch's good events still
        /// land. An entirely unbuildable batch surfaces
        /// <c>no_applicable_events</c> and leaves the existing events intact (the
        /// action is NOT cleared). <c>paths_hint</c> = <c>res://project.godot</c>
        /// is mandatory.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>action_not_found</c>, <c>invalid_event_type</c> (every event has an
        /// unknown type), <c>no_applicable_events</c>, <c>execution_error</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult ActionSetEvents(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "input_map_action_set_events is mutating; pass a non-empty paths_hint scoped to " +
                    $"the project file ({ProjectGodotHint}).");

            var request = InputMapActionSetEventsBody.Parse(body);

            if (!request.HasAction)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "input_map_action_set_events requires 'action' (the existing action to edit).");

            if (request.Events.Count == 0)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "input_map_action_set_events requires a non-empty 'events' array. To clear an " +
                    "action's events, pass an explicit empty events:[] — clearing is intentionally a " +
                    "separate action the type system surfaces.");

            var action = request.Action!;
            if (!InputMap.HasAction(action))
                return ToolDispatchResult.Fail(
                    "action_not_found",
                    $"No InputMap action named '{action}'. Use input_map_action_add to create it first.");

            // Build every event FIRST (accumulating warnings for the unbuildable
            // ones) so a fully-bad batch can be rejected without touching the
            // action's existing events. The erase+add happens only once at least one
            // event built successfully.
            var built = new List<InputEvent>(request.Events.Count);
            var warnings = new List<string>();
            bool anyUnknownType = false;
            bool anyKnownType = false;
            foreach (var spec in request.Events)
            {
                if (spec.Type == InputEventType.Unknown)
                {
                    anyUnknownType = true;
                    warnings.Add($"Skipped an event with an unknown or absent 'type' (expected " +
                        "key / mouse_button / joypad_button / joypad_motion).");
                    continue;
                }
                anyKnownType = true;
                var evt = BuildEvent(spec, warnings);
                if (evt != null) built.Add(evt);
            }

            if (!anyKnownType && anyUnknownType)
                return ToolDispatchResult.Fail(
                    "invalid_event_type",
                    "Every event in the 'events' array had an unknown or absent 'type'. Expected " +
                    "each event's 'type' to be one of: key / mouse_button / joypad_button / " +
                    "joypad_motion.");

            if (built.Count == 0)
                return ToolDispatchResult.Fail(
                    "no_applicable_events",
                    "No events could be built from the 'events' array. " +
                    (warnings.Count > 0 ? string.Join(" ", warnings) : ""));

            try
            {
                InputMap.ActionEraseEvents(action);
                foreach (var evt in built)
                    InputMap.ActionAddEvent(action, evt);
                PersistAction(action);

                var saveErr = ProjectSettings.Save();
                if (saveErr != Error.Ok)
                    return ToolDispatchResult.Fail(
                        "execution_error",
                        $"ProjectSettings.Save failed with error {saveErr}. The in-memory action " +
                        $"was updated ({built.Count} event(s)) but was NOT persisted to project.godot.");

                var sb = new StringBuilder(96 + warnings.Count * 64);
                sb.Append('{');
                sb.Append("\"action\":").Append(BridgeJson.EscapeString(action));
                sb.Append(",\"eventCount\":").Append(built.Count);
                AppendWarnings(sb, warnings);
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ===========================================================================
        // Shared helpers
        // ===========================================================================

        /// <summary>
        /// Build one Godot <c>InputEvent</c> from a spec. Returns null + appends a
        /// warning when the spec is missing a field required for its kind (e.g. a
        /// <c>key</c> event with neither <c>physical_keycode</c> nor
        /// <c>keycode</c>). Device defaults to <see cref="AnyDevice"/> (-1) when the
        /// spec omits it — the sensible default for keyboard / mouse bindings.
        /// </summary>
        static InputEvent? BuildEvent(InputEventSpec spec, List<string> warnings)
        {
            switch (spec.Type)
            {
                case InputEventType.Key:
                {
                    if (!spec.PhysicalKeycode.HasValue && !spec.Keycode.HasValue)
                    {
                        warnings.Add("Skipped a 'key' event with no physical_keycode or keycode.");
                        return null;
                    }
                    var k = new InputEventKey();
                    k.PhysicalKeycode = spec.PhysicalKeycode.HasValue
                        ? (Key)spec.PhysicalKeycode.Value : Key.None;
                    k.Keycode = spec.Keycode.HasValue ? (Key)spec.Keycode.Value : Key.None;
                    if (spec.Unicode.HasValue) k.Unicode = spec.Unicode.Value;
                    k.Device = spec.Device ?? AnyDevice;
                    return k;
                }
                case InputEventType.MouseButton:
                {
                    if (!spec.HasButtonIndex)
                    {
                        warnings.Add("Skipped a 'mouse_button' event with no button_index.");
                        return null;
                    }
                    var m = new InputEventMouseButton();
                    m.ButtonIndex = (MouseButton)spec.ButtonIndex!.Value;
                    m.Device = spec.Device ?? AnyDevice;
                    if (spec.DoubleClick.HasValue) m.DoubleClick = spec.DoubleClick.Value;
                    return m;
                }
                case InputEventType.JoypadButton:
                {
                    if (!spec.HasButtonIndex)
                    {
                        warnings.Add("Skipped a 'joypad_button' event with no button_index.");
                        return null;
                    }
                    var jb = new InputEventJoypadButton();
                    jb.ButtonIndex = (JoyButton)spec.ButtonIndex!.Value;
                    jb.Device = spec.Device ?? AnyDevice;
                    return jb;
                }
                case InputEventType.JoypadMotion:
                {
                    if (!spec.HasAxis || !spec.HasAxisValue)
                    {
                        warnings.Add("Skipped a 'joypad_motion' event missing axis or axis_value.");
                        return null;
                    }
                    var jm = new InputEventJoypadMotion();
                    jm.Axis = (JoyAxis)spec.Axis!.Value;
                    jm.AxisValue = spec.AxisValue!.Value;
                    jm.Device = spec.Device ?? AnyDevice;
                    return jm;
                }
                default:
                    // Unreachable: the handler rejects an all-unknown batch before
                    // building, and an unknown spec never reaches BuildEvent. Keep
                    // the warning for safety.
                    warnings.Add("Skipped an event with an unrecognized type.");
                    return null;
            }
        }

        /// <summary>
        /// Mirror one action's current InputMap state (deadzone + events) into
        /// <c>ProjectSettings</c> under <c>input/&lt;name&gt;</c>. This is the
        /// persistence step the editor's own Input Map dialog performs on save:
        /// <c>InputMap</c> and <c>ProjectSettings</c> are separate at runtime, so a
        /// live <c>InputMap</c> mutation must be explicitly mirrored before
        /// <c>ProjectSettings.Save</c> writes the <c>[input]</c> section. The events
        /// array carries the actual <c>InputEvent</c> objects — Godot serializes
        /// them to the <c>Object(InputEventKey,...)</c> notation natively.
        /// </summary>
        static void PersistAction(string action)
        {
            var deadzone = InputMap.ActionGetDeadzone(action);
            var events = InputMap.ActionGetEvents(action);

            var eventsArr = new Godot.Collections.Array();
            foreach (var evtVariant in events)
            {
                var evt = evtVariant.As<InputEvent>();
                if (evt != null) eventsArr.Add(evt);
            }

            var dict = new Godot.Collections.Dictionary
            {
                { "deadzone", deadzone },
                { "events", eventsArr },
            };
            ProjectSettings.SetSetting("input/" + action, dict);
        }

        /// <summary>
        /// Clamp a deadzone to Godot's valid range [0, 1]. A null deadzone degrades
        /// to <see cref="DefaultDeadzone"/> (Godot's own default for a new action).
        /// A value outside the range is clamped rather than rejected so a slightly
        /// out-of-range agent estimate still lands.
        /// </summary>
        static float ClampDeadzone(float? raw)
        {
            var d = raw ?? DefaultDeadzone;
            if (d < 0f) d = 0f;
            if (d > 1f) d = 1f;
            return d;
        }

        /// <summary>
        /// Append a <c>"warnings":[...]</c> array to <paramref name="sb"/> when
        /// there are warnings; append nothing when there are none (the ok envelope
        /// stays clean for a fully-successful batch). Mirrors the P16.1 settings +
        /// Unity BuildSettingsTools AppendWarnings shape.
        /// </summary>
        static void AppendWarnings(StringBuilder sb, List<string> warnings)
        {
            if (warnings == null || warnings.Count == 0) return;
            sb.Append(",\"warnings\":[");
            for (int i = 0; i < warnings.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(warnings[i]));
            }
            sb.Append(']');
        }
    }
}
#endif
