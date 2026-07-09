#nullable enable
using System;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// Tests <see cref="JsonBody"/> — the raw-JSON scalar/array extraction the P3.6 gate meta-tools
    /// (<c>validate_edit</c> / <c>checkpoint_create</c> / <c>delta</c>) use to read their request
    /// fields. Pure-managed string parsing, no Godot APIs — unit-tested directly here.
    ///
    /// <para>
    /// Ported (copy for the test shape) from Unity Open MCP's <c>JsonBody</c> usage in its meta-tool
    /// tests; the cases mirror the field shapes the three Godot meta-tools declare
    /// (<c>paths</c> array, <c>checkpoint_id</c> string, <c>categories</c> array, <c>label</c> string).
    /// </para>
    /// </summary>
    public class JsonBodyTests
    {
        // --- GetString -----------------------------------------------------------

        [Theory]
        [InlineData("{\"checkpoint_id\":\"cp_ab12cd\"}", "checkpoint_id", "cp_ab12cd")]
        [InlineData(" { \"checkpoint_id\" : \"cp_ab12cd\" } ", "checkpoint_id", "cp_ab12cd")]
        [InlineData("{\"label\":\"pre-mutation baseline\",\"paths\":[]}", "label", "pre-mutation baseline")]
        public void GetString_ReadsQuotedScalar(string json, string key, string expected)
        {
            Assert.Equal(expected, JsonBody.GetString(json, key));
        }

        [Theory]
        [InlineData("{\"paths\":[]}", "checkpoint_id")]          // absent key
        [InlineData("{\"checkpoint_id\":null}", "checkpoint_id")] // literal null
        [InlineData("{\"checkpoint_id\":42}", "checkpoint_id")]   // non-string value
        [InlineData("", "checkpoint_id")]                         // empty body
        public void GetString_ReturnsNull_WhenAbsentNullOrNonString(string json, string key)
        {
            Assert.Null(JsonBody.GetString(json, key));
        }

        [Fact]
        public void GetString_DecodesStandardEscapes()
        {
            // The delta unavailable warning contains an apostrophe + quotes; ensure the parser
            // round-trips escaped quotes/backslashes/newlines.
            var json = "{\"label\":\"a\\\"b\\\\c\\nd\"}";
            Assert.Equal("a\"b\\c\nd", JsonBody.GetString(json, "label"));
        }

        [Fact]
        public void GetString_PicksFirstKeyOccurrence()
        {
            // Hand-rolled IndexOf scans hit the first match; a duplicate key resolves to the first
            // value (matches Unity's JsonBody behavior — agents should not send duplicate keys, but
            // the parser is deterministic).
            var json = "{\"label\":\"first\",\"label\":\"second\"}";
            Assert.Equal("first", JsonBody.GetString(json, "label"));
        }

        // --- GetStringArray ------------------------------------------------------

        [Fact]
        public void GetStringArray_ReadsStringElements()
        {
            var json = "{\"paths\":[\"res://Main.tscn\",\"res://Player.tscn\"]}";
            var arr = JsonBody.GetStringArray(json, "paths");
            Assert.NotNull(arr);
            Assert.Equal(new[] { "res://Main.tscn", "res://Player.tscn" }, arr);
        }

        [Fact]
        public void GetStringArray_EmptyArray_ReturnsEmptyNotNull()
        {
            var json = "{\"paths\":[]}";
            var arr = JsonBody.GetStringArray(json, "paths");
            Assert.NotNull(arr);
            Assert.Empty(arr);
        }

        [Theory]
        [InlineData("{\"label\":\"x\"}", "paths")]   // absent
        [InlineData("{\"paths\":null}", "paths")]    // literal null
        [InlineData("{\"paths\":\"res://Main.tscn\"}", "paths")] // not an array
        public void GetStringArray_ReturnsNull_WhenAbsentNullOrNotArray(string json, string key)
        {
            Assert.Null(JsonBody.GetStringArray(json, key));
        }

        [Fact]
        public void GetStringArray_DecodesEscapedElements()
        {
            var json = "{\"paths\":[\"res://a\\\"b.tscn\",\"res\\\\c.tscn\"]}";
            var arr = JsonBody.GetStringArray(json, "paths");
            Assert.NotNull(arr);
            Assert.Equal(new[] { "res://a\"b.tscn", "res\\c.tscn" }, arr!);
        }
    }
}
