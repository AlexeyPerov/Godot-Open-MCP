#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    // ===========================================================================
    // P16.5 UI pack request bodies + control-kind / container-kind catalogs.
    //
    // Five tools land in this pack:
    //   - godot_open_mcp_control_create (mutating, gated) — create a Control
    //     subclass by `type` (Button / Label / LineEdit / TextureRect / ColorRect
    //     / ProgressBar / CheckBox / ...) in the edited scene, with starter
    //     full-rect anchors applied so the control is visible without manual
    //     layout.
    //   - godot_open_mcp_control_modify (mutating, gated) — bulk-patch one or
    //     many allow-listed control scalars (text / tooltip_text / disabled /
    //     color / min_size / custom_minimum_size / anchors / offset / etc.) on an
    //     existing Control node.
    //   - godot_open_mcp_container_add (mutating, gated) — add a container node
    //     (VBoxContainer / HBoxContainer / GridContainer / MarginContainer /
    //     ScrollContainer) under a parent.
    //   - godot_open_mcp_container_set_layout (mutating, gated) — bulk-patch
    //     container layout properties (separation / alignment / columns /
    //     margins / etc.) on an existing container.
    //   - godot_open_mcp_theme_apply (mutating, gated) — load a `Theme` resource
    //     (.tres) and assign it to a Control subtree (the target node and all
    //     descendant Controls inherit the theme).
    //
    // The body types mirror the hand-rolled IndexOf-substring style already used by
    // the P12.x domain packs and the P16.1 / P16.2 / P16.3 / P16.4 packs (see
    // packages/bridge/AGENTS.md §Transport: the bridge deliberately carries no
    // typed JSON DOM dependency on the hot path). Pure-managed (no Godot API
    // surface, no `#if TOOLS`), so the parsing logic is unit-testable in the
    // binary-less xUnit host.
    //
    // The shared extraction primitives live in the P12.1 `JsonScalar` static class
    // (GodotOpenMcp.Bridge.Editor namespace, sibling file
    // Extensions/Tilemap/TilemapBodies.cs). P16.5 reuses ExtractString /
    // ExtractFloat / ExtractInt / ExtractBool.
    //
    // Control-kind catalog: the common Godot 4.3+ Control leaf subclasses are
    // creatable — Button / Label / LineEdit / TextEdit / TextureRect / ColorRect /
    // ProgressBar / CheckBox / CheckButton / Slider (HSlider) / SpinBox /
    // OptionButton / Separator / NinePatchRect / RichTextLabel. The catalog maps
    // the kind token to the Godot class name the editor-only handler instantiates
    // via `new T()`. Centralized here so the vocabulary is unit-testable without
    // the editor (mirrors the P16.3 lighting pack's kind-catalog design decision).
    //
    // Container-kind catalog: VBoxContainer / HBoxContainer / GridContainer /
    // MarginContainer / ScrollContainer — the five Godot 4.3+ container families
    // the plan names. The catalog maps the kind token to the Godot class name.
    //
    // Fidelity: adapt — Unity Open MCP's UITools (TypedTools/Extensions/UI/
    // UITools.cs — ui_element_add / ui_element_modify / ui_layout_group_add shape)
    // supplies the type-enum create + container-add + typed-modify pattern. The
    // deltas are:
    // (1) Godot UI is a tree of Control nodes (each a CanvasItem), not Unity uGUI
    // components attached to a GameObject under a Canvas — create makes a node,
    // not a component add; there is no separate Canvas / EventSystem step (Godot
    // has implicit viewport-level input dispatch);
    // (2) Godot's anchor / offset / size_flags layout model replaces Unity's
    // RectTransform (anchorMin/anchorMax/sizeDelta/pivot) — the typed modify
    // surface speaks Godot's vocabulary (anchors_preset / offset_left /
    // offset_right / custom_minimum_size / size_flags_horizontal /
    // size_flags_vertical);
    // (3) Godot's Theme resource model replaces Unity uGUI's per-Control
    // component properties — theme_apply loads a .tres Theme and assigns it to a
    // Control's `theme` property (children inherit it);
    // (4) Unity renderMode / CanvasScaler / GraphicRaycaster / EventSystem are
    // NOT ported (no Godot equivalents in the same form — the viewport is the
    // canvas);
    // (5) Unity layout-group padding (RectOffset) / spacing (Vector2) /
    // childControlWidth / childForceExpand are mapped to the Godot container
    // vocabulary (BoxContainer separation / Container sizing, GridContainer
    // columns + separation, MarginContainer margins).
    // ===========================================================================

    /// <summary>
    /// Normalized control-kind token extracted from a <c>control_create</c>
    /// request body. <see cref="Unknown"/> covers both "absent" and "not a valid
    /// token"; the handler turns that into <c>invalid_parameter</c>. The values
    /// map to the common Godot 4.3+ Control leaf subclasses.
    /// </summary>
    internal enum ControlKind
    {
        Unknown = 0,
        Button = 1,         // Button
        Label = 2,          // Label
        LineEdit = 3,       // LineEdit
        TextEdit = 4,       // TextEdit
        TextureRect = 5,    // TextureRect
        ColorRect = 6,      // ColorRect
        ProgressBar = 7,    // ProgressBar
        CheckBox = 8,       // CheckBox
        CheckButton = 9,    // CheckButton
        Slider = 10,        // HSlider
        SpinBox = 11,       // SpinBox
        OptionButton = 12,  // OptionButton
        Separator = 13,     // VSeparator (full-rect → vertical fill)
        NinePatchRect = 14, // NinePatchRect
        RichTextLabel = 15, // RichTextLabel
    }

    /// <summary>
    /// Map a raw <c>type</c> string ("button" | "label" | ...) to a
    /// <see cref="ControlKind"/>. Returns <see cref="ControlKind.Unknown"/> for
    /// null / empty / unrecognized tokens so the handler can surface a single
    /// <c>invalid_parameter</c> error. Case-insensitive to tolerate an agent
    /// sending "Button" / "LABEL".
    /// </summary>
    internal static class ControlKindParser
    {
        internal static ControlKind Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return ControlKind.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "button": return ControlKind.Button;
                case "label": return ControlKind.Label;
                case "lineedit": return ControlKind.LineEdit;
                case "textedit": return ControlKind.TextEdit;
                case "texturerect": return ControlKind.TextureRect;
                case "colorrect": return ControlKind.ColorRect;
                case "progressbar": return ControlKind.ProgressBar;
                case "checkbox": return ControlKind.CheckBox;
                case "checkbutton": return ControlKind.CheckButton;
                case "slider": return ControlKind.Slider;
                case "spinbox": return ControlKind.SpinBox;
                case "optionbutton": return ControlKind.OptionButton;
                case "separator": return ControlKind.Separator;
                case "ninepatchrect": return ControlKind.NinePatchRect;
                case "richtextlabel": return ControlKind.RichTextLabel;
                default: return ControlKind.Unknown;
            }
        }

        /// <summary>
        /// Render a <see cref="ControlKind"/> back to its MCP schema string. Used
        /// by the create handler so the JSON key an agent reads round-trips into a
        /// subsequent <c>control_modify</c> call. Returns an empty string for
        /// <see cref="ControlKind.Unknown"/>.
        /// </summary>
        internal static string ToSchemaString(ControlKind kind)
        {
            switch (kind)
            {
                case ControlKind.Button: return "button";
                case ControlKind.Label: return "label";
                case ControlKind.LineEdit: return "lineedit";
                case ControlKind.TextEdit: return "textedit";
                case ControlKind.TextureRect: return "texturerect";
                case ControlKind.ColorRect: return "colorrect";
                case ControlKind.ProgressBar: return "progressbar";
                case ControlKind.CheckBox: return "checkbox";
                case ControlKind.CheckButton: return "checkbutton";
                case ControlKind.Slider: return "slider";
                case ControlKind.SpinBox: return "spinbox";
                case ControlKind.OptionButton: return "optionbutton";
                case ControlKind.Separator: return "separator";
                case ControlKind.NinePatchRect: return "ninepatchrect";
                case ControlKind.RichTextLabel: return "richtextlabel";
                default: return "";
            }
        }

        /// <summary>
        /// The Godot class name the editor-only handler instantiates for a kind.
        /// The handler instantiates via <c>new T()</c> (these are concrete engine
        /// nodes). Returns an empty string for <see cref="ControlKind.Unknown"/>.
        /// </summary>
        internal static string ToClassName(ControlKind kind)
        {
            switch (kind)
            {
                case ControlKind.Button: return "Button";
                case ControlKind.Label: return "Label";
                case ControlKind.LineEdit: return "LineEdit";
                case ControlKind.TextEdit: return "TextEdit";
                case ControlKind.TextureRect: return "TextureRect";
                case ControlKind.ColorRect: return "ColorRect";
                case ControlKind.ProgressBar: return "ProgressBar";
                case ControlKind.CheckBox: return "CheckBox";
                case ControlKind.CheckButton: return "CheckButton";
                case ControlKind.Slider: return "HSlider";
                case ControlKind.SpinBox: return "SpinBox";
                case ControlKind.OptionButton: return "OptionButton";
                case ControlKind.Separator: return "VSeparator";
                case ControlKind.NinePatchRect: return "NinePatchRect";
                case ControlKind.RichTextLabel: return "RichTextLabel";
                default: return "";
            }
        }
    }

    /// <summary>
    /// Normalized container-kind token extracted from a <c>container_add</c>
    /// request body. <see cref="Unknown"/> covers both "absent" and "not a valid
    /// token"; the handler turns that into <c>invalid_parameter</c>. The five
    /// values map to the five Godot 4.3+ container families the plan names.
    /// </summary>
    internal enum ContainerKind
    {
        Unknown = 0,
        VBox = 1,        // VBoxContainer
        HBox = 2,        // HBoxContainer
        Grid = 3,        // GridContainer
        Margin = 4,      // MarginContainer
        Scroll = 5,      // ScrollContainer
    }

    /// <summary>
    /// Map a raw <c>type</c> string ("vbox" | "hbox" | "grid" | "margin" |
    /// "scroll") to a <see cref="ContainerKind"/>. Returns
    /// <see cref="ContainerKind.Unknown"/> for null / empty / unrecognized tokens
    /// so the handler can surface a single <c>invalid_parameter</c> error.
    /// Case-insensitive to tolerate an agent sending "VBox" / "HBOX".
    /// </summary>
    internal static class ContainerKindParser
    {
        internal static ContainerKind Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return ContainerKind.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "vbox":
                case "vboxcontainer":
                    return ContainerKind.VBox;
                case "hbox":
                case "hboxcontainer":
                    return ContainerKind.HBox;
                case "grid":
                case "gridcontainer":
                    return ContainerKind.Grid;
                case "margin":
                case "margincontainer":
                    return ContainerKind.Margin;
                case "scroll":
                case "scrollcontainer":
                    return ContainerKind.Scroll;
                default: return ContainerKind.Unknown;
            }
        }

        /// <summary>
        /// Render a <see cref="ContainerKind"/> back to its MCP schema string.
        /// Used by the create handler so the JSON key an agent reads round-trips
        /// into a subsequent <c>container_set_layout</c> call. Returns an empty
        /// string for <see cref="ContainerKind.Unknown"/>.
        /// </summary>
        internal static string ToSchemaString(ContainerKind kind)
        {
            switch (kind)
            {
                case ContainerKind.VBox: return "vbox";
                case ContainerKind.HBox: return "hbox";
                case ContainerKind.Grid: return "grid";
                case ContainerKind.Margin: return "margin";
                case ContainerKind.Scroll: return "scroll";
                default: return "";
            }
        }

        /// <summary>
        /// The Godot class name the editor-only handler instantiates for a kind.
        /// The handler instantiates via <c>new T()</c> (these are concrete engine
        /// nodes). Returns an empty string for
        /// <see cref="ContainerKind.Unknown"/>.
        /// </summary>
        internal static string ToClassName(ContainerKind kind)
        {
            switch (kind)
            {
                case ContainerKind.VBox: return "VBoxContainer";
                case ContainerKind.HBox: return "HBoxContainer";
                case ContainerKind.Grid: return "GridContainer";
                case ContainerKind.Margin: return "MarginContainer";
                case ContainerKind.Scroll: return "ScrollContainer";
                default: return "";
            }
        }

        /// <summary>
        /// True when the container is a <c>BoxContainer</c> subclass
        /// (VBoxContainer / HBoxContainer). The handler uses this to decide
        /// whether the <c>separation</c> layout property applies (BoxContainer
        /// only; GridContainer uses <c>columns</c> + a different separation
        /// surface; MarginContainer uses <c>margin_* </c>; ScrollContainer has no
        /// separation).
        /// </summary>
        internal static bool IsBox(ContainerKind kind)
        {
            return kind == ContainerKind.VBox || kind == ContainerKind.HBox;
        }
    }

    /// <summary>
    /// Centralized clamp table for the UI scalar allow-list (mirrors the
    /// P12.x / P16.x packs' design decision §2). Every scalar the create / modify
    /// / set-layout handlers accept is clamped here so the table is unit-testable
    /// without the editor. Each <c>Clamp</c> overload returns the clamped value;
    /// the handler echoes the clamped result so an agent can see what landed.
    ///
    /// <para>
    /// The valid ranges mirror Godot's documented bounds for the Control /
    /// Container properties:
    /// <list type="bullet">
    /// <item><c>custom_minimum_size</c> / offset / separation / margins /
    /// columns: floats/ints clamped to non-negative (a negative minimum size or
    /// separation is meaningless and Godot clamps it internally anyway). No hard
    /// upper bound (an agent may intentionally drive a very large layout).</item>
    /// <item><c>value</c> (ProgressBar / Slider / SpinBox): passed through (the
    /// concrete Control clamps against its own min/max at apply time).</item>
    /// <item><c>color</c> / <c>text</c> / <c>tooltip_text</c> / <c>disabled</c> /
    /// <c>anchors_preset</c> / alignment enums: passed through (no numeric
    /// clamp).</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class UiPropertyClamp
    {
        /// <summary>Clamp a float to non-negative (minimum sizes, offsets when
        /// used as widths, separation). Mirrors the lighting pack's ClampEnergy
        /// floor.</summary>
        internal static float ClampNonNegativeFloat(float v) => v < 0f ? 0f : v;

        /// <summary>Clamp an int to non-negative (GridContainer columns,
        /// margins). A non-positive column count is meaningless.</summary>
        internal static int ClampNonNegativeInt(int v) => v < 0 ? 0 : v;
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_control_create</c> (P16.5,
    /// mutating, gated). Carries the <c>type</c> (which Control subclass to
    /// create), the standard node-creation fields (<c>name</c> /
    /// <c>parent_node_path</c>), an optional <c>text</c> starter (Button / Label
    /// / LineEdit / RichTextLabel), and a starter layout profile
    /// (<c>anchors_preset</c>). The handler applies the starter anchors so the
    /// control is visible without manual layout.
    /// </summary>
    internal sealed class ControlCreateBody
    {
        internal ControlKind Kind { get; private set; }

        internal string? Name { get; private set; }
        internal string? ParentNodePath { get; private set; }

        /// <summary>Optional starter text (Button.Text / Label.Text /
        /// LineEdit.Text / RichTextLabel.Text / CheckButton.Text /
        /// CheckBox.Text / Button.tooltip). Null means "leave the engine
        /// default".</summary>
        internal string? Text { get; private set; }

        /// <summary>Optional starter anchors preset name (full_rect / top_left /
        /// center / bottom_wide / ...). Defaults to <c>full_rect</c> so the
        /// control fills its parent without manual layout. The handler maps the
        /// name to <c>Control.SetAnchorsPreset</c>.</summary>
        internal string? AnchorsPreset { get; private set; }

        internal static ControlCreateBody Parse(string? body)
        {
            var parsed = new ControlCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Kind = ControlKindParser.Parse(JsonScalar.ExtractString(body, "type"));
            parsed.Name = JsonScalar.ExtractString(body, "name");
            parsed.ParentNodePath = JsonScalar.ExtractString(body, "parent_node_path");
            parsed.Text = JsonScalar.ExtractString(body, "text");
            parsed.AnchorsPreset = JsonScalar.ExtractString(body, "anchors_preset");
            return parsed;
        }

        ControlCreateBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_control_modify</c> (P16.5,
    /// mutating, gated). Carries the control target (<c>node_path</c>) and a
    /// <c>fields</c> map of {field → value} entries. The handler resolves the
    /// node, type-checks it against <c>Control</c>, and applies each field
    /// through the allow-listed + clamped path, accumulating per-field results
    /// (applied + errors) so a single bad entry does not abort the batch.
    ///
    /// <para>
    /// The <c>fields</c> map is extracted as the raw JSON object slice — the
    /// handler walks its top-level keys (each key is a control scalar field
    /// name, each value the verbatim token) so the same field allow-list +
    /// <see cref="UiPropertyClamp"/> validation covers the batch.
    /// </para>
    /// </summary>
    internal sealed class ControlModifyBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        /// <summary>The raw JSON object slice for the <c>fields</c> map (without
        /// the surrounding braces), or null when absent. The handler walks it
        /// top-level to extract each {field → verbatim value} pair. Null/empty
        /// surfaces <c>missing_parameter</c>.</summary>
        internal string? FieldsRaw { get; private set; }
        internal bool HasFields => !string.IsNullOrEmpty(FieldsRaw);

        internal static ControlModifyBody Parse(string? body)
        {
            var parsed = new ControlModifyBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.FieldsRaw = ExtractObjectSlice(body, "fields");
            return parsed;
        }

        /// <summary>
        /// Extract the raw JSON object slice (the text between the outer braces,
        /// exclusive) for a key. Returns null when the key is absent or the value
        /// is not a JSON object. The handler walks the slice top-level to
        /// enumerate {field → value} pairs. Verbatim from the P16.3 lighting
        /// pack's <c>LightModifyBody.ExtractObjectSlice</c>.
        /// </summary>
        static string? ExtractObjectSlice(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;

            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length || body[start] != '{') return null;

            // Find the matching close brace, tracking depth + skipping strings.
            int depth = 0;
            int i = start;
            bool inString = false;
            while (i < body.Length)
            {
                char c = body[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < body.Length) { i += 2; continue; }
                    if (c == '"') inString = false;
                    i++;
                    continue;
                }
                if (c == '"') { inString = true; i++; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return body.Substring(start + 1, i - start - 1);
                }
                i++;
            }
            return null;
        }

        ControlModifyBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_container_add</c> (P16.5,
    /// mutating, gated). Carries the <c>type</c> (which container family to
    /// create), the standard node-creation fields, and a starter layout profile
    /// (<c>anchors_preset</c>). The handler applies the starter anchors so the
    /// container fills its parent without manual layout.
    /// </summary>
    internal sealed class ContainerAddBody
    {
        internal ContainerKind Kind { get; private set; }

        internal string? Name { get; private set; }
        internal string? ParentNodePath { get; private set; }

        /// <summary>Optional starter anchors preset name (full_rect / top_left /
        /// center / ...). Defaults to <c>full_rect</c> so the container fills its
        /// parent. The handler maps the name to
        /// <c>Control.SetAnchorsPreset</c>.</summary>
        internal string? AnchorsPreset { get; private set; }

        internal static ContainerAddBody Parse(string? body)
        {
            var parsed = new ContainerAddBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Kind = ContainerKindParser.Parse(JsonScalar.ExtractString(body, "type"));
            parsed.Name = JsonScalar.ExtractString(body, "name");
            parsed.ParentNodePath = JsonScalar.ExtractString(body, "parent_node_path");
            parsed.AnchorsPreset = JsonScalar.ExtractString(body, "anchors_preset");
            return parsed;
        }

        ContainerAddBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_container_set_layout</c>
    /// (P16.5, mutating, gated). Carries the container target (<c>node_path</c>)
    /// and a <c>fields</c> map of {field → value} entries. The handler resolves
    /// the node, type-checks it against <c>Container</c>, and applies each layout
    /// field through the allow-listed + clamped path, accumulating per-field
    /// results so a single bad entry does not abort the batch.
    ///
    /// <para>
    /// The <c>fields</c> map is extracted as the raw JSON object slice (same
    /// slicer shape as <see cref="ControlModifyBody"/>).
    /// </para>
    /// </summary>
    internal sealed class ContainerSetLayoutBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal string? FieldsRaw { get; private set; }
        internal bool HasFields => !string.IsNullOrEmpty(FieldsRaw);

        internal static ContainerSetLayoutBody Parse(string? body)
        {
            var parsed = new ContainerSetLayoutBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.FieldsRaw = ExtractObjectSlice(body, "fields");
            return parsed;
        }

        /// <summary>
        /// Extract the raw JSON object slice for a key. Verbatim from the
        /// P16.3 lighting pack's <c>LightModifyBody.ExtractObjectSlice</c>.
        /// </summary>
        static string? ExtractObjectSlice(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;

            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length || body[start] != '{') return null;

            int depth = 0;
            int i = start;
            bool inString = false;
            while (i < body.Length)
            {
                char c = body[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < body.Length) { i += 2; continue; }
                    if (c == '"') inString = false;
                    i++;
                    continue;
                }
                if (c == '"') { inString = true; i++; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return body.Substring(start + 1, i - start - 1);
                }
                i++;
            }
            return null;
        }

        ContainerSetLayoutBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_theme_apply</c> (P16.5,
    /// mutating, gated). Carries the target (<c>node_path</c> — the Control that
    /// receives the theme; its descendants inherit it) and the
    /// <c>theme_path</c> (a res:// Theme .tres to load and assign). When
    /// <c>recursive</c> is true the handler also assigns the Theme to every
    /// descendant Control explicitly (not just the root); default false relies on
    /// Godot's natural theme inheritance.
    /// </summary>
    internal sealed class ThemeApplyBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal string? ThemePath { get; private set; }
        internal bool HasThemePath => !string.IsNullOrEmpty(ThemePath);

        /// <summary>When true, assign the Theme to every descendant Control
        /// explicitly (overrides per-child themes). Default false — Godot's
        /// natural theme inheritance cascades the root's Theme to children that
        /// do not override it.</summary>
        internal bool? Recursive { get; private set; }

        internal static ThemeApplyBody Parse(string? body)
        {
            var parsed = new ThemeApplyBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.ThemePath = JsonScalar.ExtractString(body, "theme_path");
            parsed.Recursive = JsonScalar.ExtractBool(body, "recursive");
            return parsed;
        }

        ThemeApplyBody() { }
    }
}
