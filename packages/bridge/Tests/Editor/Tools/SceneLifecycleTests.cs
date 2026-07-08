#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P2.6 <c>godot_open_mcp_scene_open</c> / <c>scene_save</c> / <c>scene_list_opened</c> unit tests
    /// for the pure-managed, off-editor-testable pieces of the scene tool family. The editor-only
    /// handler (<see cref="SceneTools.Open"/> / <see cref="SceneTools.Save"/> /
    /// <see cref="SceneTools.ListOpened"/>) is <c>#if TOOLS</c> and coupled to
    /// <c>EditorInterface.Singleton</c>, which the binary-less xUnit host cannot construct — those
    /// paths are exercised by the headless Godot smoke / live call path, not here. The units covered
    /// here are the request-body parsers and the <see cref="SceneSummary"/> DTO serialization, exactly
    /// the pieces most prone to escaping / default-value bugs.
    ///
    /// <para>
    /// <b>Test parity note:</b> adapted from <see cref="NodeCreateBodyTests"/> (copy fidelity for the
    /// parser shape) and <see cref="NodeDataJsonTests"/> (copy fidelity for the DTO JSON contract),
    /// with cases specific to the scene-open / scene-save field sets and the SceneSummary shape.
    /// Lives in the same xUnit collection-free zone as the other pure-managed suites (no HTTP
    /// listener, no shared static state), so no <c>[Collection]</c> attribute is needed. The
    /// editor-only open/save/list behaviors (dirty guard, save-as verification, open-scene
    /// enumeration) are ported conceptually from Unity Open MCP's <c>ScenesToolsTests.cs</c> but
    /// cannot run in this host — they are covered by the live smoke once the gate-less P2.6 handlers
    /// are reachable end-to-end.
    /// </para>
    /// </summary>
    public class SceneOpenBodyTests
    {
        [Fact]
        public void Parse_empty_body_returns_defaults()
        {
            var b = SceneOpenBody.Parse(null);
            Assert.Null(b.Path);
            Assert.False(b.IgnoreDirty);
        }

        [Fact]
        public void Parse_reads_path()
        {
            var b = SceneOpenBody.Parse("{\"path\":\"res://levels/level_1.tscn\"}");
            Assert.Equal("res://levels/level_1.tscn", b.Path);
            Assert.False(b.IgnoreDirty);
        }

        [Fact]
        public void Parse_reads_ignore_dirty_true()
        {
            var b = SceneOpenBody.Parse("{\"path\":\"res://x.tscn\",\"ignore_dirty\":true}");
            Assert.True(b.IgnoreDirty);
        }

        [Fact]
        public void Parse_ignore_dirty_defaults_false_when_absent()
        {
            var b = SceneOpenBody.Parse("{\"path\":\"res://x.tscn\"}");
            Assert.False(b.IgnoreDirty);
        }

        [Fact]
        public void Parse_treats_explicit_null_path_as_absent()
        {
            // A null path must not be treated as a present (empty) path — the handler fails with
            // missing_parameter when path is null/empty.
            var b = SceneOpenBody.Parse("{\"path\":null,\"ignore_dirty\":true}");
            Assert.Null(b.Path);
            Assert.True(b.IgnoreDirty);
        }

        [Fact]
        public void Parse_unquotes_escaped_string_values()
        {
            var b = SceneOpenBody.Parse("{\"path\":\"res:\\/\\/a\\\"b.tscn\"}");
            Assert.Equal("res://a\"b.tscn", b.Path);
        }

        [Fact]
        public void Parse_unquotes_unicode_escape()
        {
            // \u0041 == 'A'. Proves the \uXXXX branch decodes for scene paths too. Two \/
            // escapes decode to the two slashes in a res:// path.
            var b = SceneOpenBody.Parse("{\"path\":\"res:\\/\\/\\u0041.tscn\"}");
            Assert.Equal("res://A.tscn", b.Path);
        }

        [Fact]
        public void Parse_unparseable_bool_falls_back_to_default()
        {
            var b = SceneOpenBody.Parse("{\"ignore_dirty\":\"yes\"}");
            Assert.False(b.IgnoreDirty);
        }

        [Fact]
        public void Parse_handles_surrounding_whitespace_and_braces()
        {
            var b = SceneOpenBody.Parse("  {  \"path\" : \"res://x.tscn\" , \"ignore_dirty\" : true }  ");
            Assert.Equal("res://x.tscn", b.Path);
            Assert.True(b.IgnoreDirty);
        }

        [Fact]
        public void Parse_does_not_confuse_key_substring_inside_string_value()
        {
            // A path containing the literal substring '"ignore_dirty":' must not re-trigger the key
            // finder.
            var b = SceneOpenBody.Parse("{\"path\":\"res:\\/\\\"ignore_dirty\\\":x.tscn\"}");
            Assert.Equal("res:/\"ignore_dirty\":x.tscn", b.Path);
            Assert.False(b.IgnoreDirty);
        }
    }

    public class SceneSaveBodyTests
    {
        [Fact]
        public void Parse_empty_body_returns_save_current_defaults()
        {
            var b = SceneSaveBody.Parse(null);
            Assert.Null(b.Path);
            Assert.False(b.SaveAll);
            Assert.False(b.IsSaveAll);
            Assert.False(b.IsSaveAs);
        }

        [Fact]
        public void Parse_reads_save_as_path()
        {
            var b = SceneSaveBody.Parse("{\"path\":\"res://scenes/main.tscn\"}");
            Assert.Equal("res://scenes/main.tscn", b.Path);
            Assert.False(b.SaveAll);
            Assert.False(b.IsSaveAll);
            Assert.True(b.IsSaveAs);
        }

        [Fact]
        public void Parse_reads_save_all_true()
        {
            var b = SceneSaveBody.Parse("{\"save_all\":true}");
            Assert.True(b.SaveAll);
            Assert.True(b.IsSaveAll);
            Assert.False(b.IsSaveAs);
        }

        [Fact]
        public void Parse_save_all_takes_precedence_over_path()
        {
            // When save_all is true, path is ignored (IsSaveAs is false even with a path present).
            var b = SceneSaveBody.Parse("{\"save_all\":true,\"path\":\"res://x.tscn\"}");
            Assert.True(b.IsSaveAll);
            Assert.False(b.IsSaveAs);
        }

        [Fact]
        public void Parse_save_all_defaults_false_when_absent()
        {
            var b = SceneSaveBody.Parse("{\"path\":\"res://x.tscn\"}");
            Assert.False(b.SaveAll);
        }

        [Fact]
        public void Parse_treats_explicit_null_path_as_absent()
        {
            var b = SceneSaveBody.Parse("{\"path\":null}");
            Assert.Null(b.Path);
            Assert.False(b.IsSaveAs);
        }

        [Fact]
        public void Parse_unquotes_escaped_string_values()
        {
            var b = SceneSaveBody.Parse("{\"path\":\"res:\\/\\/a\\\\b.tscn\"}");
            Assert.Equal("res://a\\b.tscn", b.Path);
        }

        [Fact]
        public void Parse_unparseable_bool_falls_back_to_default()
        {
            var b = SceneSaveBody.Parse("{\"save_all\":1}");
            Assert.False(b.SaveAll);
        }
    }

    /// <summary>
    /// SceneSummary JSON serialization. Asserts the fixed field order, that strings flow through
    /// <see cref="BridgeJson"/> for escaping, that null path/rootType render as <c>"null"</c>, and
    /// that the boolean flags render as bare <c>true</c>/<c>false</c>. The handler relies on this
    /// output being valid JSON (the dispatcher splices it verbatim into the result envelope), so a
    /// serialization bug would produce <c>bridge_response_unparsable</c> on the MCP side — these
    /// tests pin the contract before that round-trip.
    /// </summary>
    public class SceneSummaryJsonTests
    {
        [Fact]
        public void ToJsonString_produces_canonical_shape_with_fixed_field_order()
        {
            var s = new SceneSummary
            {
                Path = "res://levels/level_1.tscn",
                Name = "Level1",
                IsDirty = false,
                RootType = "Node3D",
                IsActive = true,
            };
            var json = s.ToJsonString();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal("res://levels/level_1.tscn", root.GetProperty("path").GetString());
            Assert.Equal("Level1", root.GetProperty("name").GetString());
            Assert.False(root.GetProperty("isDirty").GetBoolean());
            Assert.Equal("Node3D", root.GetProperty("rootType").GetString());
            Assert.True(root.GetProperty("isActive").GetBoolean());

            // Field order is fixed: path, name, isDirty, rootType, isActive.
            var order = string.Join(',', EnumeratePropertyNames(root));
            Assert.Equal("path,name,isDirty,rootType,isActive", order);
        }

        static IEnumerable<string> EnumeratePropertyNames(JsonElement obj)
        {
            foreach (var p in obj.EnumerateObject())
                yield return p.Name;
        }

        [Fact]
        public void ToJsonString_renders_nulls_for_unset_path_and_rootType()
        {
            // A non-active open scene has no root accessor → rootType null; a freshly-created unsaved
            // scene has no path → path null.
            var s = new SceneSummary
            {
                Path = null,
                Name = "unsaved",
                RootType = null,
                IsActive = true,
                IsDirty = true,
            };
            var json = s.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("path").ValueKind);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("rootType").ValueKind);
            Assert.True(doc.RootElement.GetProperty("isDirty").GetBoolean());
        }

        [Fact]
        public void ToJsonString_escapes_special_characters_in_name()
        {
            var s = new SceneSummary { Name = "a\"b\\c\nd" };
            var json = s.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("a\"b\\c\nd", doc.RootElement.GetProperty("name").GetString());
        }

        [Fact]
        public void ToJsonString_renders_booleans_as_bare_tokens()
        {
            // isDirty / isActive must be bare true/false, not quoted strings.
            var s = new SceneSummary { IsDirty = true, IsActive = false };
            var json = s.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.True, doc.RootElement.GetProperty("isDirty").ValueKind);
            Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("isActive").ValueKind);
        }

        [Fact]
        public void ToJsonString_escapes_path_with_special_chars()
        {
            var s = new SceneSummary { Path = "res://a\"b.tscn" };
            var json = s.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("res://a\"b.tscn", doc.RootElement.GetProperty("path").GetString());
        }
    }
}
