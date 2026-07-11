#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    // ============================================================================
    // P4.1 resource_find / resource_get_data — pure-managed unit tests.
    //
    // Mirrors the NodeFindTests pattern: the editor-only handler (ResourceTools.Find /
    // ResourceTools.GetData) and the GodotPropertySerializer are #if TOOLS and coupled to
    // EditorInterface / EditorFileSystem / ResourceLoader, which the binary-less xUnit host cannot
    // construct. Those paths are exercised by the headless Godot smoke / live call path, not here.
    // The units covered here are exactly the ones most prone to off-by-one / escaping bugs:
    // res:// + uid:// path normalization, the find/get-data body parsers, and the ResourceIdentity /
    // ResourcePropertyData / TruncationInfo JSON shapes.
    //
    // Test parity note: adapted from Godot-MCP's ResPathNormalizer unit tests (behavior reference)
    // and the implicit shape contracts in the Godot-MCP Data DTOs, with additions for the P4.1
    // greenfield pieces (uid:// validation, .tres/.res extension check, profile depth map).
    // ============================================================================

    /// <summary>
    /// <see cref="ResourcePathNormalizer"/> validation: res:// + uid:// scheme detection,
    /// .tres/.res extension enforcement, parent-traversal rejection, and the try/require variants.
    /// </summary>
    public class ResourcePathNormalizerTests
    {
        [Theory]
        [InlineData("res://materials/wood.tres", true, "valid .tres path")]
        [InlineData("res://a/b.res", true, "valid .res path")]
        [InlineData("res://wood.TRES", true, "uppercase extension accepted")]
        [InlineData("res://wood.tres", true, "root-level file")]
        [InlineData("res://materials/wood.png", false, "non-resource extension rejected")]
        [InlineData("res://materials/wood", false, "no extension rejected")]
        [InlineData("user://a.tres", false, "user:// scheme rejected")]
        [InlineData("a.tres", false, "bare path without scheme rejected")]
        [InlineData("", false, "empty rejected")]
        public void RequireResFilePath_validates_scheme_and_extension(string path, bool ok, string reason)
        {
            var success = ResourcePathNormalizer.TryRequireResFilePath(path, out var normalized, out var error);
            Assert.True(success == ok, $"path validation regression: {reason} (path={path}, error={error})");
            if (ok)
            {
                Assert.Null(error);
                Assert.Equal(path, normalized);
            }
        }

        [Fact]
        public void RequireResFilePath_rejects_bare_scheme_root()
        {
            var ok = ResourcePathNormalizer.TryRequireResFilePath("res://", out _, out var error);
            Assert.False(ok);
            Assert.Contains("bare project root", error);
        }

        [Fact]
        public void RequireResFilePath_rejects_directory_trailing_slash()
        {
            var ok = ResourcePathNormalizer.TryRequireResFilePath("res://materials/", out _, out var error);
            Assert.False(ok);
            Assert.Contains("not a directory", error);
        }

        [Fact]
        public void RequireResFilePath_rejects_parent_traversal()
        {
            var ok = ResourcePathNormalizer.TryRequireResFilePath("res://a/../b.tres", out _, out var error);
            Assert.False(ok);
            Assert.Contains("..", error);
        }

        [Theory]
        [InlineData("uid://abc123", true)]
        [InlineData("uid://x", true)]
        [InlineData("res://a.tres", false)]
        [InlineData("uid:", false)]
        [InlineData("", false)]
        public void IsUid_detects_uid_scheme(string value, bool expected)
        {
            Assert.Equal(expected, ResourcePathNormalizer.IsUid(value));
        }

        [Theory]
        [InlineData("uid://abc123", true)]
        [InlineData("uid://x", true)]
        [InlineData("res://a.tres", false, "res:// is not a uid")]
        [InlineData("", false)]
        public void RequireUid_validates_scheme(string uid, bool ok, string? reason = null)
        {
            var success = ResourcePathNormalizer.TryRequireUid(uid, out var normalized, out var error);
            Assert.True(success == ok, $"uid validation regression: {reason ?? uid} (error={error})");
            if (ok) Assert.Equal(uid, normalized);
        }

        [Fact]
        public void IsResPath_and_IsUid_are_disjoint()
        {
            Assert.True(ResourcePathNormalizer.IsResPath("res://a.tres"));
            Assert.False(ResourcePathNormalizer.IsUid("res://a.tres"));
            Assert.True(ResourcePathNormalizer.IsUid("uid://abc"));
            Assert.False(ResourcePathNormalizer.IsResPath("uid://abc"));
        }

        [Theory]
        [InlineData("a.tres", true)]
        [InlineData("a.res", true)]
        [InlineData("a.TRES", true)]
        [InlineData("a.png", false)]
        [InlineData("a", false)]
        public void HasResourceExtension_checks_extension(string path, bool expected)
        {
            Assert.Equal(expected, ResourcePathNormalizer.HasResourceExtension(path));
        }
    }

    /// <summary>
    /// <see cref="ResourceFindBody"/> parsing: selector detection, precedence, defaults, and
    /// JSON-escape round-tripping.
    /// </summary>
    public class ResourceFindBodyTests
    {
        [Fact]
        public void Parse_empty_body_has_no_selectors()
        {
            var b = ResourceFindBody.Parse(null);
            Assert.False(b.HasAnySelector);
            Assert.False(b.IsDirectLookup);
            Assert.Null(b.Uid);
            Assert.Null(b.ResourcePath);
            Assert.Null(b.TypeFilter);
        }

        [Fact]
        public void Parse_uid_takes_precedence_over_path()
        {
            var b = ResourceFindBody.Parse("{\"uid\":\"uid://abc\",\"resource_path\":\"res://a.tres\"}");
            Assert.True(b.IsDirectLookup);
            Assert.Equal("uid://abc", b.Uid);
            Assert.Equal("res://a.tres", b.ResourcePath);
        }

        [Fact]
        public void Parse_resource_path_sets_direct_lookup()
        {
            var b = ResourceFindBody.Parse("{\"resource_path\":\"res://mats/wood.tres\"}");
            Assert.True(b.IsDirectLookup);
            Assert.Null(b.Uid);
            Assert.Equal("res://mats/wood.tres", b.ResourcePath);
        }

        [Fact]
        public void Parse_resourcePath_camelCase_alias_accepted()
        {
            var b = ResourceFindBody.Parse("{\"resourcePath\":\"res://mats/wood.tres\"}");
            Assert.Equal("res://mats/wood.tres", b.ResourcePath);
        }

        [Fact]
        public void Parse_type_filter_only_is_not_direct_lookup()
        {
            var b = ResourceFindBody.Parse("{\"type_filter\":\"StandardMaterial3D\"}");
            Assert.False(b.IsDirectLookup);
            Assert.True(b.HasAnySelector);
            Assert.Equal("StandardMaterial3D", b.TypeFilter);
        }

        [Fact]
        public void Parse_typeFilter_camelCase_alias_accepted()
        {
            var b = ResourceFindBody.Parse("{\"typeFilter\":\"Texture2D\"}");
            Assert.Equal("Texture2D", b.TypeFilter);
        }

        [Fact]
        public void Parse_page_size_defaults_and_clamps()
        {
            var defaultB = ResourceFindBody.Parse("{}");
            Assert.Equal(ResourceFindBody.DefaultPageSize, defaultB.PageSize);

            var clamped = ResourceFindBody.Parse("{\"page_size\":99999}");
            Assert.Equal(ResourceFindBody.MaxPageSize, clamped.PageSize);

            var low = ResourceFindBody.Parse("{\"page_size\":0}");
            Assert.Equal(1, low.PageSize);

            var negative = ResourceFindBody.Parse("{\"page_size\":-5}");
            Assert.Equal(1, negative.PageSize);
        }

        [Fact]
        public void Parse_directory_and_cursor_round_trip()
        {
            var b = ResourceFindBody.Parse(
                "{\"directory\":\"res://mats/\",\"cursor\":\"25\"}");
            Assert.Equal("res://mats/", b.Directory);
            Assert.Equal("25", b.Cursor);
        }

        [Fact]
        public void Parse_treats_explicit_null_as_absent()
        {
            var b = ResourceFindBody.Parse("{\"uid\":null,\"resource_path\":null,\"type_filter\":null}");
            Assert.False(b.HasAnySelector);
        }

        [Fact]
        public void Parse_unquotes_escaped_strings()
        {
            var b = ResourceFindBody.Parse(
                "{\"resource_path\":\"res://a\\\\b\\u0041.tres\"}");
            Assert.Equal("res://a\\bA.tres", b.ResourcePath);
        }
    }

    /// <summary>
    /// <see cref="ResourceGetDataBody"/> parsing: required path, profile, depth, and defaults.
    /// </summary>
    public class ResourceGetDataBodyTests
    {
        [Fact]
        public void Parse_empty_body_has_no_path_and_compact_default()
        {
            var b = ResourceGetDataBody.Parse(null);
            Assert.False(b.HasResourcePath);
            Assert.Equal(ResourceGetDataBody.DefaultProfile, b.Profile);
            // Compact profile default depth = 0.
            Assert.Equal(0, b.EffectiveDepth);
        }

        [Fact]
        public void Parse_resource_path_round_trips()
        {
            var b = ResourceGetDataBody.Parse("{\"resource_path\":\"res://m.tres\"}");
            Assert.True(b.HasResourcePath);
            Assert.Equal("res://m.tres", b.ResourcePath);
        }

        [Fact]
        public void Parse_resourcePath_alias_accepted()
        {
            var b = ResourceGetDataBody.Parse("{\"resourcePath\":\"res://m.tres\"}");
            Assert.Equal("res://m.tres", b.ResourcePath);
        }

        [Theory]
        [InlineData("compact", 0)]
        [InlineData("balanced", 2)]
        [InlineData("full", ResourceGetDataBody.HardMaxDepth)]
        public void Profile_maps_to_depth(string profile, int expectedDepth)
        {
            var b = ResourceGetDataBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"profile\":\"" + profile + "\"}");
            Assert.Equal(profile, b.Profile);
            Assert.Equal(expectedDepth, b.EffectiveDepth);
        }

        [Fact]
        public void Unknown_profile_falls_back_to_compact_depth()
        {
            var b = ResourceGetDataBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"profile\":\"nonsense\"}");
            Assert.Equal(0, b.EffectiveDepth);
        }

        [Fact]
        public void Explicit_max_depth_overrides_profile()
        {
            var b = ResourceGetDataBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"profile\":\"compact\",\"max_depth\":3}");
            Assert.Equal(3, b.EffectiveDepth);
        }

        [Fact]
        public void Explicit_max_depth_clamped_to_hard_cap()
        {
            var b = ResourceGetDataBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"max_depth\":9999}");
            Assert.Equal(ResourceGetDataBody.HardMaxDepth, b.EffectiveDepth);
        }

        [Fact]
        public void Collection_page_size_defaults_and_clamps()
        {
            var def = ResourceGetDataBody.Parse("{\"resource_path\":\"res://m.tres\"}");
            Assert.Equal(ResourceGetDataBody.DefaultCollectionPageSize, def.CollectionPageSize);

            var clamped = ResourceGetDataBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"collection_page_size\":99999}");
            Assert.Equal(ResourceGetDataBody.MaxCollectionPageSize, clamped.CollectionPageSize);
        }

        [Fact]
        public void Property_path_round_trips()
        {
            var b = ResourceGetDataBody.Parse(
                "{\"resource_path\":\"res://m.tres\",\"property_path\":\"albedo_color\"}");
            Assert.Equal("albedo_color", b.PropertyPath);
        }
    }

    /// <summary>
    /// <see cref="ResourceIdentity"/>, <see cref="ResourcePropertyData"/>, and
    /// <see cref="TruncationInfo"/> JSON serialization — the shapes the handler emits verbatim into
    /// the result envelope, pinned before the live round-trip so a serialization bug surfaces as a
    /// test failure, not <c>bridge_response_unparsable</c> on the MCP side.
    /// </summary>
    public class ResourceDataJsonTests
    {
        [Fact]
        public void ResourceIdentity_produces_canonical_shape_and_field_order()
        {
            var id = new ResourceIdentity
            {
                ResourcePath = "res://materials/wood.tres",
                Uid = "uid://abc",
                Type = "StandardMaterial3D",
            };
            var json = id.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("res://materials/wood.tres", root.GetProperty("resourcePath").GetString());
            Assert.Equal("uid://abc", root.GetProperty("uid").GetString());
            Assert.Equal("StandardMaterial3D", root.GetProperty("type").GetString());

            var order = string.Join(',', EnumerateNames(root));
            Assert.Equal("resourcePath,uid,type", order);
        }

        [Fact]
        public void ResourceIdentity_null_uid_renders_as_json_null()
        {
            var id = new ResourceIdentity { ResourcePath = "res://a.tres" };
            var json = id.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("uid").ValueKind);
        }

        [Fact]
        public void ResourceIdentity_escapes_special_characters_in_path()
        {
            var id = new ResourceIdentity { ResourcePath = "res://a\"b\\c.tres" };
            var json = id.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("res://a\"b\\c.tres", doc.RootElement.GetProperty("resourcePath").GetString());
        }

        [Fact]
        public void ResourcePropertyData_scalar_string_leaf_shape()
        {
            var node = new ResourcePropertyData
            {
                Name = "resource_name",
                VariantType = "String",
                Value = "Wood",
            };
            var json = node.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("resource_name", root.GetProperty("name").GetString());
            Assert.Equal("String", root.GetProperty("variantType").GetString());
            Assert.Equal("Wood", root.GetProperty("value").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("children").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("referenceDescription").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("truncationReason").ValueKind);

            var order = string.Join(',', EnumerateNames(root));
            Assert.Equal("name,variantType,value,children,referenceDescription,truncationReason", order);
        }

        [Fact]
        public void ResourcePropertyData_raw_json_number_value()
        {
            // When ValueIsRawJson is true, the value is emitted as a bare token (number/bool/array).
            var node = new ResourcePropertyData
            {
                Name = "roughness",
                VariantType = "Float",
                Value = "0.5",
                ValueIsRawJson = true,
            };
            var json = node.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(0.5, doc.RootElement.GetProperty("value").GetDouble());
        }

        [Fact]
        public void ResourcePropertyData_null_value_renders_as_json_null()
        {
            var node = new ResourcePropertyData { Name = "x", VariantType = "Nil" };
            var json = node.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("value").ValueKind);
        }

        [Fact]
        public void ResourcePropertyData_children_array_recurses()
        {
            var node = new ResourcePropertyData
            {
                Name = "colors",
                VariantType = "Array",
                Value = null,
                Children = new List<ResourcePropertyData>
                {
                    new ResourcePropertyData { Name = "[0]", VariantType = "Color", Value = "[1,0,0,1]", ValueIsRawJson = true },
                    new ResourcePropertyData { Name = "[1]", VariantType = "Color", Value = "[0,1,0,1]", ValueIsRawJson = true },
                },
            };
            var json = node.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            var children = doc.RootElement.GetProperty("children");
            Assert.Equal(JsonValueKind.Array, children.ValueKind);
            Assert.Equal(2, children.GetArrayLength());
        }

        [Fact]
        public void ResourcePropertyData_empty_children_renders_empty_array()
        {
            var node = new ResourcePropertyData
            {
                Name = "empty",
                VariantType = "Array",
                Children = new List<ResourcePropertyData>(),
            };
            var json = node.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("children").ValueKind);
            Assert.Empty(doc.RootElement.GetProperty("children").EnumerateArray());
        }

        [Fact]
        public void ResourcePropertyData_truncation_reason_round_trips()
        {
            var node = new ResourcePropertyData
            {
                Name = "big",
                VariantType = "String",
                Value = "clipped",
                TruncationReason = "string clipped from 9999 to 4000 characters",
            };
            var json = node.ToJsonString();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("string clipped from 9999 to 4000 characters",
                doc.RootElement.GetProperty("truncationReason").GetString());
        }

        [Fact]
        public void TruncationInfo_shape()
        {
            var info = new TruncationInfo { Truncated = true };
            info.Reasons.Add("max_nodes (2000) reached");
            info.Reasons.Add("array clipped to 200 of 1200 items");

            var sb = new System.Text.StringBuilder();
            info.AppendJsonTo(sb);
            using var doc = JsonDocument.Parse(sb.ToString());
            var root = doc.RootElement;

            Assert.True(root.GetProperty("truncated").GetBoolean());
            var reasons = root.GetProperty("truncationReasons");
            Assert.Equal(2, reasons.GetArrayLength());
        }

        [Fact]
        public void TruncationInfo_not_truncated_has_empty_reasons()
        {
            var info = new TruncationInfo();
            var sb = new System.Text.StringBuilder();
            info.AppendJsonTo(sb);
            using var doc = JsonDocument.Parse(sb.ToString());
            Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
            Assert.Empty(doc.RootElement.GetProperty("truncationReasons").EnumerateArray());
        }

        static IEnumerable<string> EnumerateNames(JsonElement obj)
        {
            foreach (var p in obj.EnumerateObject())
                yield return p.Name;
        }
    }
}
