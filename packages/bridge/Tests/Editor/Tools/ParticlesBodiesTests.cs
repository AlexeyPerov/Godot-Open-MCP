#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P12.3 particles pack unit tests for the pure-managed, off-editor-testable pieces: the
    /// dimension parser, the centralized clamp table (<see cref="ParticlesPropertyClamp"/> — the
    /// P12.3 design decision §2), the five request-body parsers, and the
    /// <see cref="JsonScalar.ExtractIntOrNull"/> extractor added in P12.3. The editor-only handlers
    /// (<see cref="ParticlesTools.Create"/> / Configure / SetEmitting / Get) are <c>#if TOOLS</c> and
    /// coupled to <c>EditorInterface.Singleton</c> and the GpuParticles node classes, neither of
    /// which the binary-less xUnit host can construct — those paths are exercised by the headless
    /// Godot smoke / live call path, not here.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <c>NavigationBodiesTests</c> (copy fidelity for the
    /// parser + extractor test shape), with cases specific to the particles field set (dimension
    /// enum, int/float/bool nullable extraction, the clamp table ranges). The clamp table is the
    /// highest-value unit test surface in this pack — it pins the allow-list ranges without
    /// requiring the editor. Lives in the same xUnit collection-free zone as the other pure-managed
    /// suites (no HTTP listener, no shared static state), so no <c>[Collection]</c> attribute is
    /// needed.
    /// </para>
    /// </summary>
    public class ParticlesBodiesTests
    {
        // --- ParticlesDimensionParser -------------------------------------------
        // ParticlesDimension is internal, so the [Theory] passes the expected token as its
        // underlying int and casts back inside (xUnit's InlineData serializer requires a public-
        // parameter-accessible type, which an internal enum is not).

        [Theory]
        [InlineData("2d", 2)]
        [InlineData("3d", 3)]
        [InlineData("2D", 2)]
        [InlineData("3D", 3)]
        [InlineData(" 2d ", 2)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("4d", 0)]
        [InlineData("cpu", 0)]
        public void DimensionParser_maps_known_and_unknown_tokens(string? raw, int expected)
        {
            Assert.Equal((ParticlesDimension)expected, ParticlesDimensionParser.Parse(raw));
        }

        // --- ParticlesPropertyClamp (design decision §2 — the clamp table) -------
        // These are the highest-value unit tests in the pack: they pin the allow-list ranges
        // without requiring the editor. The handler echoes the clamped result, so an agent sees
        // what landed.

        [Theory]
        [InlineData(1, 1)]
        [InlineData(8, 8)]
        [InlineData(0, 1)]            // below the floor → clamped up to 1
        [InlineData(-5, 1)]           // negative → clamped up to 1
        [InlineData(100000, 100000)]  // the ceiling
        [InlineData(100001, 100000)]  // above the ceiling → clamped down
        [InlineData(1000000, 100000)] // a typo that would tank perf → clamped down
        public void ClampAmount_enforces_one_to_max(int input, int expected)
        {
            Assert.Equal(expected, ParticlesPropertyClamp.ClampAmount(input));
        }

        [Theory]
        [InlineData(1.0f, 1.0f)]
        [InlineData(0.5f, 0.5f)]
        [InlineData(0.0001f, 0.0001f)]      // the floor
        [InlineData(0f, 0.0001f)]            // zero → clamped up to the floor
        [InlineData(-1f, 0.0001f)]           // negative → clamped up to the floor
        public void ClampLifetime_is_strictly_positive(float input, float expected)
        {
            Assert.Equal(expected, ParticlesPropertyClamp.ClampLifetime(input));
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(2.5f, 2.5f)]
        [InlineData(-1f, 0f)]   // negative → clamped to 0
        public void ClampNonNegative_clamps_negatives_to_zero(float input, float expected)
        {
            Assert.Equal(expected, ParticlesPropertyClamp.ClampNonNegative(input));
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(0.5f, 0.5f)]
        [InlineData(1f, 1f)]
        [InlineData(-0.1f, 0f)]   // below 0 → clamped to 0
        [InlineData(1.5f, 1f)]    // above 1 → clamped to 1
        [InlineData(5f, 1f)]      // way above → clamped to 1
        public void ClampUnit_enforces_zero_to_one(float input, float expected)
        {
            Assert.Equal(expected, ParticlesPropertyClamp.ClampUnit(input));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(60, 60)]
        [InlineData(-1, 0)]   // negative → clamped to 0 (0 = render frame rate)
        public void ClampFixedFps_is_non_negative(int input, int expected)
        {
            Assert.Equal(expected, ParticlesPropertyClamp.ClampFixedFps(input));
        }

        // --- ParticlesDefaultsBody ----------------------------------------------

        [Fact]
        public void ParticlesDefaultsBody_empty_body_is_unknown()
        {
            var b = ParticlesDefaultsBody.Parse(null);
            Assert.Equal(ParticlesDimension.Unknown, b.Dimension);
        }

        [Fact]
        public void ParticlesDefaultsBody_reads_dimension()
        {
            var b = ParticlesDefaultsBody.Parse("{\"dimension\":\"2d\"}");
            Assert.Equal(ParticlesDimension.TwoD, b.Dimension);
        }

        [Fact]
        public void ParticlesDefaultsBody_unrecognized_dimension_is_unknown()
        {
            var b = ParticlesDefaultsBody.Parse("{\"dimension\":\"cpu\"}");
            Assert.Equal(ParticlesDimension.Unknown, b.Dimension);
        }

        // --- ParticlesCreateBody ------------------------------------------------

        [Fact]
        public void ParticlesCreateBody_empty_body_returns_defaults()
        {
            var b = ParticlesCreateBody.Parse(null);
            Assert.Equal(ParticlesDimension.Unknown, b.Dimension);
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
            Assert.Null(b.ProcessMaterialPath);
            Assert.False(b.HasProcessMaterialPath);
        }

        [Fact]
        public void ParticlesCreateBody_reads_all_fields()
        {
            var b = ParticlesCreateBody.Parse(
                "{\"dimension\":\"3d\",\"name\":\"Smoke\",\"parent_node_path\":\"Main/Effects\"," +
                "\"position\":\"1,2,3\",\"process_material_path\":\"res://particles/smoke.tres\"}");
            Assert.Equal(ParticlesDimension.ThreeD, b.Dimension);
            Assert.Equal("Smoke", b.Name);
            Assert.Equal("Main/Effects", b.ParentNodePath);
            Assert.Equal("1,2,3", b.Position);
            Assert.Equal("res://particles/smoke.tres", b.ProcessMaterialPath);
            Assert.True(b.HasProcessMaterialPath);
        }

        [Fact]
        public void ParticlesCreateBody_treats_explicit_null_as_absent()
        {
            var b = ParticlesCreateBody.Parse(
                "{\"dimension\":null,\"name\":null,\"parent_node_path\":null,\"position\":null," +
                "\"process_material_path\":null}");
            Assert.Equal(ParticlesDimension.Unknown, b.Dimension);
            Assert.Null(b.Name);
            Assert.Null(b.ParentNodePath);
            Assert.Null(b.Position);
            Assert.False(b.HasProcessMaterialPath);
        }

        // --- ParticlesConfigureBody ---------------------------------------------

        [Fact]
        public void ParticlesConfigureBody_empty_body_has_no_scalars()
        {
            var b = ParticlesConfigureBody.Parse(null);
            Assert.False(b.HasNodePath);
            Assert.Null(b.Amount);
            Assert.Null(b.Lifetime);
            Assert.Null(b.OneShot);
            Assert.Null(b.Preprocess);
            Assert.Null(b.SpeedScale);
            Assert.Null(b.Explosiveness);
            Assert.Null(b.Randomness);
            Assert.Null(b.FixedFps);
            Assert.Null(b.Interpolate);
            Assert.Null(b.FractDelta);
            Assert.Null(b.LocalCoords);
        }

        [Fact]
        public void ParticlesConfigureBody_reads_all_scalars()
        {
            var b = ParticlesConfigureBody.Parse(
                "{\"node_path\":\"Main/Smoke\",\"amount\":32,\"lifetime\":2.0,\"one_shot\":true," +
                "\"preprocess\":0.5,\"speed_scale\":1.5,\"explosiveness\":0.3,\"randomness\":0.2," +
                "\"fixed_fps\":60,\"interpolate\":false,\"fract_delta\":true,\"local_coords\":false}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Main/Smoke", b.NodePath);
            Assert.Equal(32, b.Amount);
            Assert.Equal(2.0f, b.Lifetime);
            Assert.True(b.OneShot);
            Assert.Equal(0.5f, b.Preprocess);
            Assert.Equal(1.5f, b.SpeedScale);
            Assert.Equal(0.3f, b.Explosiveness);
            Assert.Equal(0.2f, b.Randomness);
            Assert.Equal(60, b.FixedFps);
            Assert.False(b.Interpolate);
            Assert.True(b.FractDelta);
            Assert.False(b.LocalCoords);
        }

        [Fact]
        public void ParticlesConfigureBody_absent_scalars_leave_null()
        {
            // Only amount sent — the rest must stay null (the handler treats null as "unchanged").
            var b = ParticlesConfigureBody.Parse(
                "{\"node_path\":\"A\",\"amount\":50}");
            Assert.Equal(50, b.Amount);
            Assert.Null(b.Lifetime);
            Assert.Null(b.OneShot);
            Assert.Null(b.Preprocess);
            Assert.Null(b.SpeedScale);
            Assert.Null(b.Explosiveness);
            Assert.Null(b.Randomness);
            Assert.Null(b.FixedFps);
            Assert.Null(b.Interpolate);
            Assert.Null(b.FractDelta);
            Assert.Null(b.LocalCoords);
        }

        [Fact]
        public void ParticlesConfigureBody_non_numeric_scalar_is_null()
        {
            // A present-but-non-numeric value degrades to null (the handler skips it silently,
            // matching node_modify's non-aborting contract).
            var b = ParticlesConfigureBody.Parse(
                "{\"node_path\":\"A\",\"amount\":\"oops\",\"lifetime\":\"many\"}");
            Assert.Null(b.Amount);
            Assert.Null(b.Lifetime);
        }

        [Fact]
        public void ParticlesConfigureBody_out_of_range_scalar_is_read_as_is()
        {
            // The body parser does NOT clamp — it records the raw value. Clamping is the handler's
            // job (so the clamped result can be echoed). Pin the parser's no-clamp contract.
            var b = ParticlesConfigureBody.Parse(
                "{\"node_path\":\"A\",\"amount\":-3,\"explosiveness\":5.0}");
            Assert.Equal(-3, b.Amount);
            Assert.Equal(5.0f, b.Explosiveness);
        }

        [Fact]
        public void ParticlesConfigureBody_bool_literals_parse()
        {
            var b = ParticlesConfigureBody.Parse(
                "{\"node_path\":\"A\",\"one_shot\":false,\"local_coords\":true}");
            Assert.False(b.OneShot);
            Assert.True(b.LocalCoords);
        }

        [Fact]
        public void ParticlesConfigureBody_invalid_bool_is_null()
        {
            // "True" (capitalized) is not a JSON bool literal — degrades to null.
            var b = ParticlesConfigureBody.Parse(
                "{\"node_path\":\"A\",\"one_shot\":True}");
            Assert.Null(b.OneShot);
        }

        [Fact]
        public void ParticlesConfigureBody_unquotes_node_path()
        {
            var b = ParticlesConfigureBody.Parse(
                "{\"node_path\":\"Emitter \\\"A\\\"\",\"amount\":10}");
            Assert.Equal("Emitter \"A\"", b.NodePath);
        }

        // --- ParticlesSetEmittingBody -------------------------------------------

        [Fact]
        public void ParticlesSetEmittingBody_empty_body_has_no_node_path()
        {
            var b = ParticlesSetEmittingBody.Parse("");
            Assert.False(b.HasNodePath);
            Assert.Null(b.Emitting);
            Assert.False(b.Restart);
        }

        [Fact]
        public void ParticlesSetEmittingBody_reads_emitting_true()
        {
            var b = ParticlesSetEmittingBody.Parse(
                "{\"node_path\":\"Main/Smoke\",\"emitting\":true}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Main/Smoke", b.NodePath);
            Assert.True(b.Emitting);
            Assert.False(b.Restart);
        }

        [Fact]
        public void ParticlesSetEmittingBody_reads_restart_true()
        {
            var b = ParticlesSetEmittingBody.Parse(
                "{\"node_path\":\"A\",\"emitting\":true,\"restart\":true}");
            Assert.True(b.Restart);
        }

        [Fact]
        public void ParticlesSetEmittingBody_restart_defaults_false_when_absent()
        {
            var b = ParticlesSetEmittingBody.Parse(
                "{\"node_path\":\"A\",\"emitting\":false}");
            Assert.False(b.Restart);
        }

        [Fact]
        public void ParticlesSetEmittingBody_invalid_emitting_is_null()
        {
            // "1" is not a JSON bool literal — degrades to null (the handler rejects with
            // missing_parameter, mirroring a missing emitting key).
            var b = ParticlesSetEmittingBody.Parse(
                "{\"node_path\":\"A\",\"emitting\":1}");
            Assert.Null(b.Emitting);
        }

        // --- ParticlesGetBody ---------------------------------------------------

        [Fact]
        public void ParticlesGetBody_empty_body_has_no_node_path()
        {
            var b = ParticlesGetBody.Parse("");
            Assert.False(b.HasNodePath);
        }

        [Fact]
        public void ParticlesGetBody_reads_node_path()
        {
            var b = ParticlesGetBody.Parse("{\"node_path\":\"Main/Effects/Smoke\"}");
            Assert.True(b.HasNodePath);
            Assert.Equal("Main/Effects/Smoke", b.NodePath);
        }

        // --- JsonScalar ExtractIntOrNull (P12.3 addition) -----------------------

        [Fact]
        public void JsonScalar_ExtractIntOrNull_absent_key_returns_null()
        {
            Assert.Null(JsonScalar.ExtractIntOrNull("{\"x\":1}", "y"));
        }

        [Fact]
        public void JsonScalar_ExtractIntOrNull_null_literal_returns_null()
        {
            Assert.Null(JsonScalar.ExtractIntOrNull("{\"x\":null}", "x"));
        }

        [Fact]
        public void JsonScalar_ExtractIntOrNull_reads_positive_and_negative()
        {
            Assert.Equal(5, JsonScalar.ExtractIntOrNull("{\"x\":5}", "x"));
            Assert.Equal(-3, JsonScalar.ExtractIntOrNull("{\"x\":-3}", "x"));
            Assert.Equal(0, JsonScalar.ExtractIntOrNull("{\"x\":0}", "x"));
        }

        [Fact]
        public void JsonScalar_ExtractIntOrNull_truncates_decimal_to_leading_int()
        {
            // Mirrors the existing ExtractInt contract: the scanner reads the leading integer run
            // and parses that. "5.5" scans "5" (stops at '.') → 5. The JSON schema (type: integer)
            // prevents a conforming client from sending a decimal in the first place; this pins the
            // non-conforming-input behavior so it stays consistent with ExtractInt.
            Assert.Equal(5, JsonScalar.ExtractIntOrNull("{\"x\":5.5}", "x"));
        }

        [Fact]
        public void JsonScalar_ExtractIntOrNull_rejects_non_numeric()
        {
            Assert.Null(JsonScalar.ExtractIntOrNull("{\"x\":\"oops\"}", "x"));
            Assert.Null(JsonScalar.ExtractIntOrNull("{\"x\":true}", "x"));
        }

        [Fact]
        public void JsonScalar_ExtractIntOrNull_is_invariant_under_locale()
        {
            // The scanner reads the leading integer run; a thousands separator ("1_000") stops the
            // scan at "1" and the long.TryParse succeeds on that prefix. This matches the existing
            // ExtractInt contract (leading-int run). The JSON schema (type: integer) prevents a
            // conforming client from sending separators; this pins the non-conforming-input behavior.
            Assert.Equal(1, JsonScalar.ExtractIntOrNull("{\"x\":1}", "x"));
            Assert.Equal(1, JsonScalar.ExtractIntOrNull("{\"x\":1_000}", "x"));
        }
    }
}
