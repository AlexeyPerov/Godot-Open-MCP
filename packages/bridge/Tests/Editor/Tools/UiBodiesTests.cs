#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P16.5 UI pack unit tests for the pure-managed, off-editor-testable
    /// pieces: the control-kind parser (+ its schema-string + class-name
    /// round-trip), the container-kind parser (+ round-trip + IsBox), the clamp
    /// table, and the five request-body parsers (control_create /
    /// control_modify / container_add / container_set_layout / theme_apply,
    /// including the verbatim fields-map extraction for the two modify-style
    /// bodies). The editor-only handlers (<see cref="UiTools.ControlCreate"/> /
    /// <see cref="UiTools.ControlModify"/> / <see cref="UiTools.ContainerAdd"/>
    /// / <see cref="UiTools.ContainerSetLayout"/> / <see cref="UiTools.ThemeApply"/>)
    /// are <c>#if TOOLS</c> and coupled to <c>EditorInterface</c> /
    /// <c>Control</c> / <c>Container</c> / <c>Theme</c> / <c>ResourceLoader</c>,
    /// none of which the binary-less xUnit host can construct — those paths are
    /// exercised by the headless Godot smoke / live call path, not here.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <c>MaterialsBodiesTests</c> +
    /// <c>LightingBodiesTests</c>-style (copy fidelity for the parser + body-
    /// parser test shape), with cases specific to the UI field set (two kind
    /// enums + round-trip, the verbatim fields-map extraction contract). The
    /// kind vocabularies are the highest-value unit-test surface in this pack —
    /// they pin the creatable families and their Godot class names without
    /// requiring the editor. Lives in the same xUnit collection-free zone as
    /// the other pure-managed suites (no HTTP listener, no shared static
    /// state), so no <c>[Collection]</c> attribute is needed.
    /// </para>
    /// </summary>
    public class UiBodiesTests
    {
        // --- ControlKindParser (parse direction) -------------------------------
        // ControlKind is internal, so the [Theory] passes the expected token as its
        // underlying int and casts back inside (xUnit's InlineData serializer requires
        // a public-parameter-accessible type, which an internal enum is not — same
        // workaround the CSG/settings/materials/lighting packs use).

        [Theory]
        [InlineData("button", 1)]
        [InlineData("label", 2)]
        [InlineData("lineedit", 3)]
        [InlineData("textedit", 4)]
        [InlineData("texturerect", 5)]
        [InlineData("colorrect", 6)]
        [InlineData("progressbar", 7)]
        [InlineData("checkbox", 8)]
        [InlineData("checkbutton", 9)]
        [InlineData("slider", 10)]
        [InlineData("spinbox", 11)]
        [InlineData("optionbutton", 12)]
        [InlineData("separator", 13)]
        [InlineData("ninepatchrect", 14)]
        [InlineData("richtextlabel", 15)]
        [InlineData("Button", 1)]           // case-insensitive
        [InlineData("LABEL", 2)]
        [InlineData(" lineedit ", 3)]       // tolerates whitespace
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("canvas", 0)]           // not a Godot Control kind
        [InlineData("rect", 0)]             // partial — not a kind
        public void ControlKindParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((ControlKind)expected, ControlKindParser.Parse(raw));
        }

        // --- ControlKindParser (schema-string round-trip) ----------------------

        [Theory]
        [InlineData(1, "button")]
        [InlineData(2, "label")]
        [InlineData(3, "lineedit")]
        [InlineData(4, "textedit")]
        [InlineData(5, "texturerect")]
        [InlineData(6, "colorrect")]
        [InlineData(7, "progressbar")]
        [InlineData(8, "checkbox")]
        [InlineData(9, "checkbutton")]
        [InlineData(10, "slider")]
        [InlineData(11, "spinbox")]
        [InlineData(12, "optionbutton")]
        [InlineData(13, "separator")]
        [InlineData(14, "ninepatchrect")]
        [InlineData(15, "richtextlabel")]
        [InlineData(0, "")]                 // Unknown → empty
        public void ControlKindParser_ToSchemaString_round_trips(int kindInt, string expected)
        {
            Assert.Equal(expected, ControlKindParser.ToSchemaString((ControlKind)kindInt));
        }

        [Fact]
        public void ControlKindParser_parse_then_render_round_trips_for_known_tokens()
        {
            foreach (var token in new[] {
                "button", "label", "lineedit", "textedit", "texturerect",
                "colorrect", "progressbar", "checkbox", "checkbutton",
                "slider", "spinbox", "optionbutton", "separator",
                "ninepatchrect", "richtextlabel",
            })
            {
                var kind = ControlKindParser.Parse(token);
                Assert.Equal(token, ControlKindParser.ToSchemaString(kind));
            }
        }

        // --- ControlKindParser (class-name map) --------------------------------

        [Theory]
        [InlineData(1, "Button")]
        [InlineData(2, "Label")]
        [InlineData(3, "LineEdit")]
        [InlineData(4, "TextEdit")]
        [InlineData(5, "TextureRect")]
        [InlineData(6, "ColorRect")]
        [InlineData(7, "ProgressBar")]
        [InlineData(8, "CheckBox")]
        [InlineData(9, "CheckButton")]
        [InlineData(10, "HSlider")]         // slider → HSlider
        [InlineData(11, "SpinBox")]
        [InlineData(12, "OptionButton")]
        [InlineData(13, "VSeparator")]      // separator → VSeparator
        [InlineData(14, "NinePatchRect")]
        [InlineData(15, "RichTextLabel")]
        [InlineData(0, "")]
        public void ControlKindParser_ToClassName_maps_each_kind_to_its_godot_class(
            int kindInt, string expected)
        {
            Assert.Equal(expected, ControlKindParser.ToClassName((ControlKind)kindInt));
        }

        [Fact]
        public void ControlKindParser_schema_string_and_class_name_are_consistent_for_known_kinds()
        {
            // Every known kind has a non-empty schema string + class name.
            for (int i = 1; i <= 15; i++)
            {
                var kind = (ControlKind)i;
                Assert.False(string.IsNullOrEmpty(ControlKindParser.ToSchemaString(kind)));
                Assert.False(string.IsNullOrEmpty(ControlKindParser.ToClassName(kind)));
            }
        }

        // --- ContainerKindParser (parse direction) -----------------------------

        [Theory]
        [InlineData("vbox", 1)]
        [InlineData("hbox", 2)]
        [InlineData("grid", 3)]
        [InlineData("margin", 4)]
        [InlineData("scroll", 5)]
        [InlineData("VBoxContainer", 1)]    // tolerates the full class name
        [InlineData("HBoxContainer", 2)]
        [InlineData("GRID", 3)]
        [InlineData(" margin ", 4)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("flowcontainer", 0)]    // not in the creatable set
        [InlineData("tabcontainer", 0)]
        public void ContainerKindParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((ContainerKind)expected, ContainerKindParser.Parse(raw));
        }

        // --- ContainerKindParser (schema-string round-trip) --------------------

        [Theory]
        [InlineData(1, "vbox")]
        [InlineData(2, "hbox")]
        [InlineData(3, "grid")]
        [InlineData(4, "margin")]
        [InlineData(5, "scroll")]
        [InlineData(0, "")]
        public void ContainerKindParser_ToSchemaString_round_trips(int kindInt, string expected)
        {
            Assert.Equal(expected, ContainerKindParser.ToSchemaString((ContainerKind)kindInt));
        }

        [Theory]
        [InlineData(1, "VBoxContainer")]
        [InlineData(2, "HBoxContainer")]
        [InlineData(3, "GridContainer")]
        [InlineData(4, "MarginContainer")]
        [InlineData(5, "ScrollContainer")]
        [InlineData(0, "")]
        public void ContainerKindParser_ToClassName_maps_each_kind_to_its_godot_class(
            int kindInt, string expected)
        {
            Assert.Equal(expected, ContainerKindParser.ToClassName((ContainerKind)kindInt));
        }

        // --- ContainerKindParser (IsBox) ---------------------------------------

        [Theory]
        [InlineData(1, true)]   // VBox
        [InlineData(2, true)]   // HBox
        [InlineData(3, false)]  // Grid
        [InlineData(4, false)]  // Margin
        [InlineData(5, false)]  // Scroll
        [InlineData(0, false)]  // Unknown
        public void ContainerKindParser_IsBox_only_for_vbox_and_hbox(int kindInt, bool expected)
        {
            Assert.Equal(expected, ContainerKindParser.IsBox((ContainerKind)kindInt));
        }

        // --- UiPropertyClamp ---------------------------------------------------

        [Theory]
        [InlineData(-1f, 0f)]
        [InlineData(0f, 0f)]
        [InlineData(0.5f, 0.5f)]
        [InlineData(100f, 100f)]
        public void ClampNonNegativeFloat_floors_at_zero(float raw, float expected)
        {
            Assert.Equal(expected, UiPropertyClamp.ClampNonNegativeFloat(raw));
        }

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(0, 0)]
        [InlineData(5, 5)]
        public void ClampNonNegativeInt_floors_at_zero(int raw, int expected)
        {
            Assert.Equal(expected, UiPropertyClamp.ClampNonNegativeInt(raw));
        }

        // --- ControlCreateBody -------------------------------------------------

        [Fact]
        public void ControlCreateBody_empty_body_is_unknown_kind_and_nulls()
        {
            var b = ControlCreateBody.Parse(null);
            Assert.Equal(ControlKind.Unknown, b.Kind);
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Text);
            Assert.Null(b.AnchorsPreset);
        }

        [Fact]
        public void ControlCreateBody_reads_kind_and_fields()
        {
            var b = ControlCreateBody.Parse(
                "{\"type\":\"button\",\"name\":\"StartBtn\",\"parent_node_path\":\"/root/UI\"," +
                "\"text\":\"Start\",\"anchors_preset\":\"center\"}");
            Assert.Equal(ControlKind.Button, b.Kind);
            Assert.Equal("StartBtn", b.Name);
            Assert.Equal("/root/UI", b.ParentNodePath);
            Assert.Equal("Start", b.Text);
            Assert.Equal("center", b.AnchorsPreset);
        }

        [Fact]
        public void ControlCreateBody_unknown_kind_token_is_unknown()
        {
            var b = ControlCreateBody.Parse("{\"type\":\"flowcontainer\"}");
            Assert.Equal(ControlKind.Unknown, b.Kind);
        }

        // --- ControlModifyBody -------------------------------------------------

        [Fact]
        public void ControlModifyBody_empty_body_has_no_node_path_and_no_fields()
        {
            var b = ControlModifyBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.False(b.HasFields);
        }

        [Fact]
        public void ControlModifyBody_reads_node_path()
        {
            var b = ControlModifyBody.Parse("{\"node_path\":\"/root/UI/Btn\"}");
            Assert.True(b.HasNodePath);
            Assert.Equal("/root/UI/Btn", b.NodePath);
        }

        [Fact]
        public void ControlModifyBody_extracts_fields_map_inner_slice()
        {
            // The fields map is the text between the outer braces (exclusive).
            // The handler walks it top-level — pin the extracted slice here.
            var b = ControlModifyBody.Parse(
                "{\"node_path\":\"/Btn\",\"fields\":{\"text\":\"Go\",\"disabled\":true}}");
            Assert.True(b.HasFields);
            // The inner slice contains both entries (order preserved from the
            // source JSON). We do not assert exact whitespace — the handler's
            // walker tolerates it — only that both keys are present.
            Assert.Contains("\"text\"", b.FieldsRaw!);
            Assert.Contains("\"disabled\"", b.FieldsRaw!);
        }

        [Fact]
        public void ControlModifyBody_non_object_fields_is_absent()
        {
            var b = ControlModifyBody.Parse("{\"node_path\":\"/Btn\",\"fields\":\"not-an-object\"}");
            Assert.False(b.HasFields);
        }

        [Fact]
        public void ControlModifyBody_absent_fields_is_absent()
        {
            var b = ControlModifyBody.Parse("{\"node_path\":\"/Btn\"}");
            Assert.False(b.HasFields);
        }

        // --- ContainerAddBody --------------------------------------------------

        [Fact]
        public void ContainerAddBody_empty_body_is_unknown_kind_and_nulls()
        {
            var b = ContainerAddBody.Parse(null);
            Assert.Equal(ContainerKind.Unknown, b.Kind);
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.AnchorsPreset);
        }

        [Fact]
        public void ContainerAddBody_reads_kind_and_fields()
        {
            var b = ContainerAddBody.Parse(
                "{\"type\":\"vbox\",\"name\":\"Column\",\"parent_node_path\":\"/root/UI\"," +
                "\"anchors_preset\":\"full_rect\"}");
            Assert.Equal(ContainerKind.VBox, b.Kind);
            Assert.Equal("Column", b.Name);
            Assert.Equal("/root/UI", b.ParentNodePath);
            Assert.Equal("full_rect", b.AnchorsPreset);
        }

        [Fact]
        public void ContainerAddBody_unknown_kind_token_is_unknown()
        {
            var b = ContainerAddBody.Parse("{\"type\":\"flowcontainer\"}");
            Assert.Equal(ContainerKind.Unknown, b.Kind);
        }

        // --- ContainerSetLayoutBody --------------------------------------------

        [Fact]
        public void ContainerSetLayoutBody_empty_body_has_no_node_path_and_no_fields()
        {
            var b = ContainerSetLayoutBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.False(b.HasFields);
        }

        [Fact]
        public void ContainerSetLayoutBody_reads_node_path()
        {
            var b = ContainerSetLayoutBody.Parse("{\"node_path\":\"/root/UI/VBox\"}");
            Assert.True(b.HasNodePath);
            Assert.Equal("/root/UI/VBox", b.NodePath);
        }

        [Fact]
        public void ContainerSetLayoutBody_extracts_fields_map_inner_slice()
        {
            var b = ContainerSetLayoutBody.Parse(
                "{\"node_path\":\"/VBox\",\"fields\":{\"separation\":8,\"alignment\":\"center\"}}");
            Assert.True(b.HasFields);
            Assert.Contains("\"separation\"", b.FieldsRaw!);
            Assert.Contains("\"alignment\"", b.FieldsRaw!);
        }

        [Fact]
        public void ContainerSetLayoutBody_non_object_fields_is_absent()
        {
            var b = ContainerSetLayoutBody.Parse("{\"node_path\":\"/VBox\",\"fields\":42}");
            Assert.False(b.HasFields);
        }

        // --- ThemeApplyBody ----------------------------------------------------

        [Fact]
        public void ThemeApplyBody_empty_body_has_no_paths_and_null_recursive()
        {
            var b = ThemeApplyBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.False(b.HasThemePath);
            Assert.Null(b.Recursive);
        }

        [Fact]
        public void ThemeApplyBody_reads_paths_and_recursive_flag()
        {
            var b = ThemeApplyBody.Parse(
                "{\"node_path\":\"/root/UI\",\"theme_path\":\"res://default.tres\",\"recursive\":true}");
            Assert.True(b.HasNodePath);
            Assert.Equal("/root/UI", b.NodePath);
            Assert.True(b.HasThemePath);
            Assert.Equal("res://default.tres", b.ThemePath);
            Assert.True(b.Recursive);
        }

        [Fact]
        public void ThemeApplyBody_recursive_defaults_to_null_when_absent()
        {
            var b = ThemeApplyBody.Parse(
                "{\"node_path\":\"/UI\",\"theme_path\":\"res://t.tres\"}");
            Assert.Null(b.Recursive);
        }

        [Fact]
        public void ThemeApplyBody_recursive_false_parses()
        {
            var b = ThemeApplyBody.Parse(
                "{\"node_path\":\"/UI\",\"theme_path\":\"res://t.tres\",\"recursive\":false}");
            Assert.False(b.Recursive);
        }
    }
}
