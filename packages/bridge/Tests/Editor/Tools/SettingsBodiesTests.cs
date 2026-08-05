#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P16.1 project-settings pack unit tests for the pure-managed, off-editor-testable
    /// pieces: the section parser (+ its schema-string round-trip), the centralized
    /// section allowlist (<see cref="SettingsSectionCatalog"/> — mirrors the P12.3
    /// particles pack's centralized clamp-table design decision), and the two
    /// request-body parsers (get + set, including the <c>fields[]</c> array walk).
    /// The editor-only handlers (<see cref="SettingsTools.GetProject"/> /
    /// <see cref="SettingsTools.SetProject"/>) are <c>#if TOOLS</c> and coupled to
    /// <c>ProjectSettings</c> and <c>Godot.Json</c>, neither of which the binary-less
    /// xUnit host can construct — those paths are exercised by the headless Godot
    /// smoke / live call path, not here.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <c>CsgBodiesTests</c> (copy fidelity for
    /// the parser + allowlist test shape), with cases specific to the settings field
    /// set (section enum + round-trip, the writable-section allowlist, the
    /// section-prefix map, the <c>fields[]</c> array-of-objects extraction). The
    /// allowlist is the highest-value unit test surface in this pack — it pins the
    /// writable sections and the section→prefix map without requiring the editor.
    /// Lives in the same xUnit collection-free zone as the other pure-managed suites
    /// (no HTTP listener, no shared static state), so no <c>[Collection]</c>
    /// attribute is needed.
    /// </para>
    /// </summary>
    public class SettingsBodiesTests
    {
        // --- SettingsSectionParser (parse direction) ----------------------------
        // SettingsSection is internal, so the [Theory] passes the expected token as
        // its underlying int and casts back inside (xUnit's InlineData serializer
        // requires a public-parameter-accessible type, which an internal enum is
        // not — same workaround the CSG pack uses).

        [Theory]
        [InlineData("rendering", 1)]
        [InlineData("physics", 2)]
        [InlineData("input", 3)]
        [InlineData("layer_names", 4)]
        [InlineData("autoload", 5)]
        [InlineData("application", 6)]
        [InlineData("display", 7)]
        [InlineData("all", 8)]
        [InlineData("Rendering", 1)]        // case-insensitive
        [InlineData("PHYSICS", 2)]
        [InlineData(" layer_names ", 4)]    // tolerates whitespace
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("graphics", 0)]         // not in the v1 catalog
        [InlineData("quality", 0)]          // Unity section name — NOT a Godot section
        public void SectionParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((SettingsSection)expected, SettingsSectionParser.Parse(raw));
        }

        // --- SettingsSectionParser (schema-string round-trip) -------------------

        [Theory]
        [InlineData(1, "rendering")]
        [InlineData(2, "physics")]
        [InlineData(3, "input")]
        [InlineData(4, "layer_names")]
        [InlineData(5, "autoload")]
        [InlineData(6, "application")]
        [InlineData(7, "display")]
        [InlineData(8, "all")]
        [InlineData(0, "")]                 // Unknown → empty
        public void SectionParser_ToSchemaString_round_trips(int sectionInt, string expected)
        {
            Assert.Equal(expected, SettingsSectionParser.ToSchemaString((SettingsSection)sectionInt));
        }

        [Fact]
        public void SectionParser_parse_then_render_round_trips_for_known_tokens()
        {
            foreach (var token in new[] {
                "rendering", "physics", "input", "layer_names",
                "autoload", "application", "display", "all",
            })
            {
                var section = SettingsSectionParser.Parse(token);
                Assert.Equal(token, SettingsSectionParser.ToSchemaString(section));
            }
        }

        // --- SettingsSectionCatalog (the allowlist — highest-value unit surface) --
        // SettingsSection is internal, so the [Theory] passes the section as its
        // underlying int and casts back inside (xUnit's InlineData serializer requires a
        // public-parameter-accessible type, which an internal enum is not — same
        // workaround the CSG pack uses for CsgKind / CsgOperation).

        [Theory]
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(3, true)]
        [InlineData(4, true)]
        [InlineData(5, true)]
        [InlineData(6, true)]
        [InlineData(7, true)]
        [InlineData(8, false)]          // All — read summary switch, not writable
        [InlineData(0, false)]          // Unknown
        public void IsWritable_pins_the_writable_allowlist(int sectionInt, bool expected)
        {
            Assert.Equal(expected, SettingsSectionCatalog.IsWritable((SettingsSection)sectionInt));
        }

        [Fact]
        public void WritableSections_lists_exactly_the_seven_domains()
        {
            // "all" + Unknown must be absent — they are not writable domains.
            var writable = SettingsSectionCatalog.WritableSections;
            Assert.Equal(7, writable.Length);
            Assert.DoesNotContain(SettingsSection.All, writable);
            Assert.DoesNotContain(SettingsSection.Unknown, writable);
            Assert.Contains(SettingsSection.Rendering, writable);
            Assert.Contains(SettingsSection.Physics, writable);
            Assert.Contains(SettingsSection.Input, writable);
            Assert.Contains(SettingsSection.LayerNames, writable);
            Assert.Contains(SettingsSection.Autoload, writable);
            Assert.Contains(SettingsSection.Application, writable);
            Assert.Contains(SettingsSection.Display, writable);
        }

        [Theory]
        [InlineData(1, "rendering/")]
        [InlineData(2, "physics/")]
        [InlineData(3, "input/")]
        [InlineData(4, "layer_names/")]
        [InlineData(5, "autoload/")]
        [InlineData(6, "application/")]
        [InlineData(7, "display/")]
        [InlineData(8, "")]
        [InlineData(0, "")]
        public void SectionPrefix_maps_each_section_to_its_godot_prefix(
            int sectionInt, string expected)
        {
            Assert.Equal(expected, SettingsSectionCatalog.SectionPrefix((SettingsSection)sectionInt));
        }

        // --- SettingsGetProjectBody --------------------------------------------

        [Fact]
        public void GetProjectBody_empty_body_is_unknown()
        {
            var b = SettingsGetProjectBody.Parse(null);
            Assert.Equal(SettingsSection.Unknown, b.Section);
        }

        [Fact]
        public void GetProjectBody_reads_section()
        {
            Assert.Equal(SettingsSection.Rendering,
                SettingsGetProjectBody.Parse("{\"section\":\"rendering\"}").Section);
            Assert.Equal(SettingsSection.All,
                SettingsGetProjectBody.Parse("{\"section\":\"all\"}").Section);
        }

        [Fact]
        public void GetProjectBody_unrecognized_section_is_unknown()
        {
            var b = SettingsGetProjectBody.Parse("{\"section\":\"quality\"}");
            Assert.Equal(SettingsSection.Unknown, b.Section);
        }

        [Fact]
        public void GetProjectBody_treats_explicit_null_as_unknown()
        {
            // Mirrors the JsonScalar nullable-extractor contract — a JSON null degrades
            // to null/Unknown so the handler treats it as "absent".
            var b = SettingsGetProjectBody.Parse("{\"section\":null}");
            Assert.Equal(SettingsSection.Unknown, b.Section);
        }

        // --- SettingsSetProjectBody -------------------------------------------

        [Fact]
        public void SetProjectBody_empty_body_has_unknown_section_and_no_fields()
        {
            var b = SettingsSetProjectBody.Parse(null);
            Assert.Equal(SettingsSection.Unknown, b.Section);
            Assert.Empty(b.Fields);
        }

        [Fact]
        public void SetProjectBody_reads_section()
        {
            var b = SettingsSetProjectBody.Parse("{\"section\":\"physics\"}");
            Assert.Equal(SettingsSection.Physics, b.Section);
        }

        [Fact]
        public void SetProjectBody_reads_a_single_string_patch()
        {
            // The value token is extracted VERBATIM — quotes preserved so the handler's
            // Json.ParseString round-trip keeps string type fidelity (a bare "42" stays
            // a string, not an int). A string value lands WITH surrounding quotes.
            var b = SettingsSetProjectBody.Parse(
                "{\"section\":\"application\",\"fields\":[{\"key\":\"run/main_scene\",\"value\":\"res://main.tscn\"}]}");
            Assert.Equal(SettingsSection.Application, b.Section);
            Assert.Single(b.Fields);
            Assert.Equal("run/main_scene", b.Fields[0].Key);
            Assert.Equal("\"res://main.tscn\"", b.Fields[0].ValueRaw);
            Assert.True(b.Fields[0].HasKey);
        }

        [Fact]
        public void SetProjectBody_reads_a_number_patch()
        {
            // A bare numeric token is extracted as-is (no quotes) so Json.ParseString
            // yields an Int/Float Variant, preserving the numeric type.
            var b = SettingsSetProjectBody.Parse(
                "{\"section\":\"physics\",\"fields\":[{\"key\":\"common/physics_ticks_per_second\",\"value\":120}]}");
            Assert.Single(b.Fields);
            Assert.Equal("120", b.Fields[0].ValueRaw);
        }

        [Fact]
        public void SetProjectBody_reads_a_bool_patch()
        {
            var b = SettingsSetProjectBody.Parse(
                "{\"section\":\"rendering\",\"fields\":[{\"key\":\"anti_aliasing/quality/msaa_3d\",\"value\":true}]}");
            Assert.Single(b.Fields);
            Assert.Equal("true", b.Fields[0].ValueRaw);
        }

        [Fact]
        public void SetProjectBody_string_token_preserves_quotes_for_type_fidelity()
        {
            // A string "42" must extract WITH quotes so the handler's Json.ParseString
            // yields a String Variant (not Int). This is the key reason the value
            // extractor is verbatim rather than the quote-stripping JsonScalar one.
            var b = SettingsSetProjectBody.Parse(
                "{\"section\":\"application\",\"fields\":[{\"key\":\"config/version\",\"value\":\"42\"}]}");
            Assert.Single(b.Fields);
            Assert.Equal("\"42\"", b.Fields[0].ValueRaw);
        }

        [Fact]
        public void SetProjectBody_reads_an_object_patch_color()
        {
            // A Godot setting can be a Color — the value is a JSON object the handler
            // re-parses into a Variant. The body parser records the balanced slice
            // verbatim (including the outer braces).
            var b = SettingsSetProjectBody.Parse(
                "{\"section\":\"rendering\",\"fields\":[{\"key\":\"environment/defaults/default_clear_color\",\"value\":{\"r\":0.1,\"g\":0.2,\"b\":0.3}}]}");
            Assert.Single(b.Fields);
            Assert.Equal("{\"r\":0.1,\"g\":0.2,\"b\":0.3}", b.Fields[0].ValueRaw);
        }

        [Fact]
        public void SetProjectBody_reads_multiple_patches_in_order()
        {
            var b = SettingsSetProjectBody.Parse(
                "{\"section\":\"application\",\"fields\":[" +
                "{\"key\":\"config/name\",\"value\":\"My Game\"}," +
                "{\"key\":\"run/main_scene\",\"value\":\"res://main.tscn\"}," +
                "{\"key\":\"config/version\",\"value\":\"1.0.0\"}]}");
            Assert.Equal(3, b.Fields.Count);
            Assert.Equal("config/name", b.Fields[0].Key);
            Assert.Equal("run/main_scene", b.Fields[1].Key);
            Assert.Equal("config/version", b.Fields[2].Key);
        }

        [Fact]
        public void SetProjectBody_missing_value_key_yields_null_raw()
        {
            // A missing value key clears the setting (the handler parses null → Nil
            // variant → SetSetting clears). Mirrors the Unity "value may be null"
            // contract.
            var b = SettingsSetProjectBody.Parse(
                "{\"section\":\"application\",\"fields\":[{\"key\":\"config/description\"}]}");
            Assert.Single(b.Fields);
            Assert.Equal("config/description", b.Fields[0].Key);
            Assert.Null(b.Fields[0].ValueRaw);
        }

        [Fact]
        public void SetProjectBody_patch_with_no_key_has_haskey_false()
        {
            var b = SettingsSetProjectBody.Parse(
                "{\"section\":\"application\",\"fields\":[{\"value\":\"no key here\"}]}");
            Assert.Single(b.Fields);
            Assert.False(b.Fields[0].HasKey);
        }

        [Fact]
        public void SetProjectBody_absent_fields_yields_empty_list()
        {
            var b = SettingsSetProjectBody.Parse("{\"section\":\"physics\"}");
            Assert.Equal(SettingsSection.Physics, b.Section);
            Assert.Empty(b.Fields);
        }

        [Fact]
        public void SetProjectBody_empty_fields_array_yields_empty_list()
        {
            var b = SettingsSetProjectBody.Parse("{\"section\":\"physics\",\"fields\":[]}");
            Assert.Empty(b.Fields);
        }

        [Fact]
        public void SetProjectBody_treats_null_fields_as_empty()
        {
            // A JSON null for the fields value degrades to empty (the handler surfaces
            // missing_parameter rather than crashing on a null array).
            var b = SettingsSetProjectBody.Parse("{\"section\":\"physics\",\"fields\":null}");
            Assert.Empty(b.Fields);
        }

        [Fact]
        public void SetProjectBody_unquotes_string_keys_with_escapes()
        {
            // The shared JsonScalar extractor unwraps quoted strings, honoring
            // backslash escapes — pin that an embedded quote in a key survives.
            var b = SettingsSetProjectBody.Parse(
                "{\"section\":\"application\",\"fields\":[{\"key\":\"weird/\\\"key\\\"\",\"value\":1}]}");
            Assert.Single(b.Fields);
            Assert.Equal("weird/\"key\"", b.Fields[0].Key);
        }

        [Fact]
        public void SetProjectBody_object_value_with_nested_comma_does_not_split()
        {
            // A nested object value containing a comma (e.g. a Dictionary with two
            // entries) must NOT split into two patches — both the array walk and the
            // verbatim value extractor skip over balanced braces/brackets.
            var b = SettingsSetProjectBody.Parse(
                "{\"section\":\"input\",\"fields\":[{\"key\":\"ui_accept\",\"value\":{\"deadzone\":0.5,\"events\":[\"a\",\"b\"]}}]}");
            Assert.Single(b.Fields);
            // The verbatim value is the full balanced object slice.
            Assert.Equal("{\"deadzone\":0.5,\"events\":[\"a\",\"b\"]}", b.Fields[0].ValueRaw);
        }
    }
}
