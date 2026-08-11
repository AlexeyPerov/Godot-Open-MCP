#nullable enable
using System.Linq;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P17.2 reserialize unit tests for the pure-managed, off-editor-testable piece: the
    /// <see cref="ReserializeBody"/> request-body parser. The editor-only handler
    /// (<see cref="ReserializeTools.Reserialize"/>) is <c>#if TOOLS</c> and coupled to
    /// <c>EditorInterface.Singleton</c> + <c>ResourceSaver</c>, neither of which the binary-less
    /// xUnit host can construct — that path is exercised by the headless Godot smoke / live call
    /// path, not here.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <c>TilemapBodiesTests</c> /
    /// <c>ResourceMutationTests</c> (copy fidelity for the parser + array-extraction test shape),
    /// with cases specific to the reserialize <c>paths</c> string array. Lives in the same xUnit
    /// collection-free zone as the other pure-managed suites (no HTTP listener, no shared static
    /// state), so no <c>[Collection]</c> attribute is needed.
    /// </para>
    /// </summary>
    public class ReserializeBodiesTests
    {
        // --- ReserializeBody -----------------------------------------------------

        [Fact]
        public void ReserializeBody_empty_body_returns_no_paths()
        {
            var b = ReserializeBody.Parse(null);
            Assert.Empty(b.Paths);
            Assert.False(b.HasPaths);
        }

        [Fact]
        public void ReserializeBody_empty_string_returns_no_paths()
        {
            var b = ReserializeBody.Parse("");
            Assert.Empty(b.Paths);
            Assert.False(b.HasPaths);
        }

        [Fact]
        public void ReserializeBody_missing_paths_key_returns_no_paths()
        {
            var b = ReserializeBody.Parse("{\"paths_hint\":[\"res://a.tres\"]}");
            Assert.Empty(b.Paths);
            Assert.False(b.HasPaths);
        }

        [Fact]
        public void ReserializeBody_reads_a_single_path()
        {
            var b = ReserializeBody.Parse("{\"paths\":[\"res://a.tres\"]}");
            Assert.Single(b.Paths);
            Assert.Equal("res://a.tres", b.Paths[0]);
            Assert.True(b.HasPaths);
        }

        [Fact]
        public void ReserializeBody_reads_multiple_paths_in_order()
        {
            var b = ReserializeBody.Parse(
                "{\"paths\":[\"res://a.tres\",\"res://Scenes/Main.tscn\",\"res://b.res\"]}");
            Assert.Equal(3, b.Paths.Count);
            Assert.Equal("res://a.tres", b.Paths[0]);
            Assert.Equal("res://Scenes/Main.tscn", b.Paths[1]);
            Assert.Equal("res://b.res", b.Paths[2]);
        }

        [Fact]
        public void ReserializeBody_accepts_a_folder_path()
        {
            var b = ReserializeBody.Parse("{\"paths\":[\"res://Resources/\"]}");
            Assert.Single(b.Paths);
            Assert.Equal("res://Resources/", b.Paths[0]);
        }

        [Fact]
        public void ReserializeBody_unquotes_escaped_string_values()
        {
            // A path with an escaped quote and backslash round-trips intact.
            var b = ReserializeBody.Parse("{\"paths\":[\"res://a\\\"b\\\\c.tres\"]}");
            Assert.Single(b.Paths);
            Assert.Equal("res://a\"b\\c.tres", b.Paths[0]);
        }

        [Fact]
        public void ReserializeBody_decodes_unicode_escape()
        {
            // \u00e9 → é
            var b = ReserializeBody.Parse("{\"paths\":[\"res://caf\\u00e9.tres\"]}");
            Assert.Single(b.Paths);
            Assert.Equal("res://café.tres", b.Paths[0]);
        }

        [Fact]
        public void ReserializeBody_empty_array_returns_no_paths()
        {
            var b = ReserializeBody.Parse("{\"paths\":[]}");
            Assert.Empty(b.Paths);
            Assert.False(b.HasPaths);
        }

        [Fact]
        public void ReserializeBody_non_array_value_returns_no_paths()
        {
            // A scalar where an array is expected is a contract violation → treated as absent so the
            // handler's missing_parameter guard surfaces it.
            var b = ReserializeBody.Parse("{\"paths\":\"res://a.tres\"}");
            Assert.Empty(b.Paths);
            Assert.False(b.HasPaths);
        }

        [Fact]
        public void ReserializeBody_non_string_element_yields_no_paths()
        {
            // A non-string element (paths:[42]) is a contract violation → the whole array is
            // treated as absent rather than silently dropping elements.
            var b = ReserializeBody.Parse("{\"paths\":[42]}");
            Assert.Empty(b.Paths);
            Assert.False(b.HasPaths);
        }

        [Fact]
        public void ReserializeBody_mixed_string_and_non_string_yields_no_paths()
        {
            var b = ReserializeBody.Parse("{\"paths\":[\"res://a.tres\",42]}");
            Assert.Empty(b.Paths);
        }

        [Fact]
        public void ReserializeBody_tolerates_whitespace_between_elements()
        {
            var b = ReserializeBody.Parse("{\"paths\": [ \"res://a.tres\" , \"res://b.tres\" ]}");
            Assert.Equal(2, b.Paths.Count);
            Assert.Equal("res://a.tres", b.Paths[0]);
            Assert.Equal("res://b.tres", b.Paths[1]);
        }

        [Fact]
        public void ReserializeBody_ignores_unrelated_keys()
        {
            var b = ReserializeBody.Parse(
                "{\"paths\":[\"res://a.tres\"],\"paths_hint\":[\"res://a.tres\"],\"gate\":\"enforce\"}");
            Assert.Single(b.Paths);
            Assert.Equal("res://a.tres", b.Paths[0]);
        }

        [Fact]
        public void ReserializeBody_clamps_to_max_paths()
        {
            // Build a body with MaxPaths + 5 entries; the parser should keep only MaxPaths.
            var many = Enumerable.Range(0, ReserializeBody.MaxPaths + 5)
                .Select(i => $"\"res://f{i}.tres\"");
            var body = "{\"paths\":[" + string.Join(",", many) + "]}";
            var b = ReserializeBody.Parse(body);
            Assert.Equal(ReserializeBody.MaxPaths, b.Paths.Count);
        }
    }
}
