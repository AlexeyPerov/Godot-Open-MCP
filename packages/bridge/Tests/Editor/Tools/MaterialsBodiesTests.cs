#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P16.2 materials/shaders pack unit tests for the pure-managed, off-editor-
    /// testable pieces: the material-kind parser (+ its schema-string + class-name
    /// round-trip) and the five request-body parsers (create / get_properties /
    /// set_property / set_shader / shader_get_data, including the verbatim
    /// value-token extraction for set_property). The editor-only handlers
    /// (<see cref="MaterialsTools.MaterialCreate"/> /
    /// <see cref="MaterialsTools.MaterialGetProperties"/> /
    /// <see cref="MaterialsTools.MaterialSetProperty"/> /
    /// <see cref="MaterialsTools.MaterialSetShader"/> /
    /// <see cref="MaterialsTools.ShaderGetData"/>) are <c>#if TOOLS</c> and coupled
    /// to <c>ResourceLoader</c> / <c>ResourceSaver</c> / <c>Material</c> /
    /// <c>Shader</c> / <c>ShaderMaterial</c>, none of which the binary-less xUnit
    /// host can construct — those paths are exercised by the headless Godot smoke
    /// / live call path, not here.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <c>SettingsBodiesTests</c> (copy
    /// fidelity for the parser + body-parser test shape), with cases specific to
    /// the materials field set (kind enum + round-trip, the three creatable
    /// classes, the verbatim value-token type-fidelity contract the P16.1 settings
    /// pack introduced). The kind vocabulary is the highest-value unit-test
    /// surface in this pack — it pins the three creatable families and their Godot
    /// class names without requiring the editor. Lives in the same xUnit
    /// collection-free zone as the other pure-managed suites (no HTTP listener, no
    /// shared static state), so no <c>[Collection]</c> attribute is needed.
    /// </para>
    /// </summary>
    public class MaterialsBodiesTests
    {
        // --- MaterialKindParser (parse direction) -------------------------------
        // MaterialKind is internal, so the [Theory] passes the expected token as its
        // underlying int and casts back inside (xUnit's InlineData serializer requires
        // a public-parameter-accessible type, which an internal enum is not — same
        // workaround the CSG/settings packs use).

        [Theory]
        [InlineData("standard", 1)]
        [InlineData("orm", 2)]
        [InlineData("shader", 3)]
        [InlineData("Standard", 1)]         // case-insensitive
        [InlineData("ORM", 2)]
        [InlineData(" shader ", 3)]         // tolerates whitespace
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("metallic", 0)]         // not a Godot material kind
        [InlineData("pbr", 0)]              // Unity-flavored token — not a Godot kind
        public void KindParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((MaterialKind)expected, MaterialKindParser.Parse(raw));
        }

        // --- MaterialKindParser (schema-string round-trip) ----------------------

        [Theory]
        [InlineData(1, "standard")]
        [InlineData(2, "orm")]
        [InlineData(3, "shader")]
        [InlineData(0, "")]                 // Unknown → empty
        public void KindParser_ToSchemaString_round_trips(int kindInt, string expected)
        {
            Assert.Equal(expected, MaterialKindParser.ToSchemaString((MaterialKind)kindInt));
        }

        [Fact]
        public void KindParser_parse_then_render_round_trips_for_known_tokens()
        {
            foreach (var token in new[] { "standard", "orm", "shader" })
            {
                var kind = MaterialKindParser.Parse(token);
                Assert.Equal(token, MaterialKindParser.ToSchemaString(kind));
            }
        }

        // --- MaterialKindParser (class-name map) --------------------------------
        // The class names are the Godot types the editor-only handler instantiates
        // via ClassDB. Pinning them here catches a rename across the Godot version
        // floor without requiring the editor.

        [Theory]
        [InlineData(1, "StandardMaterial3D")]
        [InlineData(2, "ORMMaterial3D")]
        [InlineData(3, "ShaderMaterial")]
        [InlineData(0, "")]
        public void KindParser_ToClassName_maps_each_kind_to_its_godot_class(
            int kindInt, string expected)
        {
            Assert.Equal(expected, MaterialKindParser.ToClassName((MaterialKind)kindInt));
        }

        [Fact]
        public void KindParser_schema_string_and_class_name_are_consistent_for_known_kinds()
        {
            foreach (var kind in new[] { MaterialKind.Standard, MaterialKind.Orm, MaterialKind.Shader })
            {
                Assert.False(string.IsNullOrEmpty(MaterialKindParser.ToSchemaString(kind)));
                Assert.False(string.IsNullOrEmpty(MaterialKindParser.ToClassName(kind)));
            }
        }

        // --- MaterialCreateBody -------------------------------------------------

        [Fact]
        public void CreateBody_empty_body_is_unknown_kind_and_no_paths()
        {
            var b = MaterialCreateBody.Parse(null);
            Assert.Equal(MaterialKind.Unknown, b.Kind);
            Assert.False(b.HasResourcePath);
            Assert.False(b.HasShaderPath);
            Assert.Null(b.Overwrite);
        }

        [Fact]
        public void CreateBody_reads_kind_and_resource_path()
        {
            var b = MaterialCreateBody.Parse(
                "{\"kind\":\"standard\",\"resource_path\":\"res://materials/default.tres\"}");
            Assert.Equal(MaterialKind.Standard, b.Kind);
            Assert.True(b.HasResourcePath);
            Assert.Equal("res://materials/default.tres", b.ResourcePath);
        }

        [Fact]
        public void CreateBody_reads_shader_kind_with_shader_path()
        {
            var b = MaterialCreateBody.Parse(
                "{\"kind\":\"shader\",\"resource_path\":\"res://materials/outline.tres\"," +
                "\"shader_path\":\"res://shaders/outline.gdshader\"}");
            Assert.Equal(MaterialKind.Shader, b.Kind);
            Assert.True(b.HasShaderPath);
            Assert.Equal("res://shaders/outline.gdshader", b.ShaderPath);
        }

        [Fact]
        public void CreateBody_reads_overwrite_flag()
        {
            var b = MaterialCreateBody.Parse(
                "{\"kind\":\"orm\",\"resource_path\":\"res://m.tres\",\"overwrite\":true}");
            Assert.Equal(true, b.Overwrite);
        }

        [Fact]
        public void CreateBody_unrecognized_kind_is_unknown()
        {
            var b = MaterialCreateBody.Parse(
                "{\"kind\":\"metallic\",\"resource_path\":\"res://m.tres\"}");
            Assert.Equal(MaterialKind.Unknown, b.Kind);
        }

        [Fact]
        public void CreateBody_treats_explicit_null_overwrite_as_null()
        {
            var b = MaterialCreateBody.Parse(
                "{\"kind\":\"standard\",\"resource_path\":\"res://m.tres\",\"overwrite\":null}");
            Assert.Null(b.Overwrite);
        }

        // --- MaterialGetPropertiesBody -----------------------------------------

        [Fact]
        public void GetPropertiesBody_empty_body_has_no_path()
        {
            var b = MaterialGetPropertiesBody.Parse(null);
            Assert.False(b.HasResourcePath);
        }

        [Fact]
        public void GetPropertiesBody_reads_resource_path()
        {
            var b = MaterialGetPropertiesBody.Parse(
                "{\"resource_path\":\"res://materials/default.tres\"}");
            Assert.True(b.HasResourcePath);
            Assert.Equal("res://materials/default.tres", b.ResourcePath);
        }

        [Fact]
        public void GetPropertiesBody_accepts_uid_path()
        {
            var b = MaterialGetPropertiesBody.Parse(
                "{\"resource_path\":\"uid://abc123\"}");
            Assert.True(b.HasResourcePath);
            Assert.Equal("uid://abc123", b.ResourcePath);
        }

        // --- MaterialSetPropertyBody -------------------------------------------

        [Fact]
        public void SetPropertyBody_empty_body_has_no_path_or_property()
        {
            var b = MaterialSetPropertyBody.Parse(null);
            Assert.False(b.HasResourcePath);
            Assert.False(b.HasProperty);
            Assert.Null(b.ValueRaw);
        }

        [Fact]
        public void SetPropertyBody_reads_path_and_property()
        {
            var b = MaterialSetPropertyBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"property\":\"albedo_color\"}");
            Assert.True(b.HasResourcePath);
            Assert.True(b.HasProperty);
            Assert.Equal("albedo_color", b.Property);
        }

        [Fact]
        public void SetPropertyBody_string_value_preserves_quotes_for_type_fidelity()
        {
            // The verbatim value extractor keeps the surrounding quotes so the
            // handler's Json.ParseString yields a String Variant (not an int) — a
            // string "42" stays a string. This is the key reason the extractor is
            // verbatim rather than the quote-stripping JsonScalar one.
            var b = MaterialSetPropertyBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"property\":\"name\",\"value\":\"42\"}");
            Assert.Equal("\"42\"", b.ValueRaw);
        }

        [Fact]
        public void SetPropertyBody_reads_a_number_value()
        {
            var b = MaterialSetPropertyBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"property\":\"metallic\",\"value\":0.8}");
            Assert.Equal("0.8", b.ValueRaw);
        }

        [Fact]
        public void SetPropertyBody_reads_a_bool_value()
        {
            var b = MaterialSetPropertyBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"property\":\"rough\",\"value\":true}");
            Assert.Equal("true", b.ValueRaw);
        }

        [Fact]
        public void SetPropertyBody_reads_an_int_value()
        {
            var b = MaterialSetPropertyBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"property\":\"render_priority\",\"value\":5}");
            Assert.Equal("5", b.ValueRaw);
        }

        [Fact]
        public void SetPropertyBody_reads_an_object_value_color()
        {
            // A Color is a JSON object the handler re-parses into a Variant. The body
            // parser records the balanced slice verbatim (including the outer braces).
            var b = MaterialSetPropertyBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"property\":\"albedo_color\"," +
                "\"value\":{\"r\":0.1,\"g\":0.2,\"b\":0.3}}");
            Assert.Equal("{\"r\":0.1,\"g\":0.2,\"b\":0.3}", b.ValueRaw);
        }

        [Fact]
        public void SetPropertyBody_reads_an_array_value_vector()
        {
            // A vector arrives as a JSON array — the balanced slice is recorded verbatim.
            var b = MaterialSetPropertyBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"property\":\"uv1_offset\"," +
                "\"value\":[0.0,0.0,0.0]}");
            Assert.Equal("[0.0,0.0,0.0]", b.ValueRaw);
        }

        [Fact]
        public void SetPropertyBody_missing_value_key_yields_null_raw()
        {
            // A missing value key clears the property (the handler parses null → Nil
            // variant → Set clears).
            var b = MaterialSetPropertyBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"property\":\"texture_albedo\"}");
            Assert.Null(b.ValueRaw);
        }

        [Fact]
        public void SetPropertyBody_explicit_null_value_yields_null_raw()
        {
            var b = MaterialSetPropertyBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"property\":\"texture_albedo\",\"value\":null}");
            Assert.Null(b.ValueRaw);
        }

        [Fact]
        public void SetPropertyBody_object_value_with_nested_comma_does_not_split()
        {
            // A nested object value containing a comma (e.g. a Dictionary with two
            // entries) must NOT split — the verbatim value extractor skips over
            // balanced braces/brackets.
            var b = MaterialSetPropertyBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"property\":\"metadata\"," +
                "\"value\":{\"a\":1,\"b\":2}}");
            Assert.Equal("{\"a\":1,\"b\":2}", b.ValueRaw);
        }

        // --- MaterialSetShaderBody ---------------------------------------------

        [Fact]
        public void SetShaderBody_empty_body_has_no_paths()
        {
            var b = MaterialSetShaderBody.Parse(null);
            Assert.False(b.HasResourcePath);
            Assert.False(b.HasShaderPath);
        }

        [Fact]
        public void SetShaderBody_reads_both_paths()
        {
            var b = MaterialSetShaderBody.Parse(
                "{\"resource_path\":\"res://materials/outline.tres\"," +
                "\"shader_path\":\"res://shaders/outline.gdshader\"}");
            Assert.True(b.HasResourcePath);
            Assert.True(b.HasShaderPath);
            Assert.Equal("res://materials/outline.tres", b.ResourcePath);
            Assert.Equal("res://shaders/outline.gdshader", b.ShaderPath);
        }

        // --- ShaderGetDataBody --------------------------------------------------

        [Fact]
        public void GetDataBody_empty_body_has_no_path()
        {
            var b = ShaderGetDataBody.Parse(null);
            Assert.False(b.HasShaderPath);
        }

        [Fact]
        public void GetDataBody_reads_shader_path()
        {
            var b = ShaderGetDataBody.Parse(
                "{\"shader_path\":\"res://shaders/outline.gdshader\"}");
            Assert.True(b.HasShaderPath);
            Assert.Equal("res://shaders/outline.gdshader", b.ShaderPath);
        }
    }
}
