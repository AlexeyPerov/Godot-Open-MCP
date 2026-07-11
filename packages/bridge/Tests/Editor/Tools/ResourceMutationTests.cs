#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P4.2 unit tests for the pure-managed resource-mutation pieces: the property-path grammar
    /// (<see cref="ResourcePropertyPatch"/>), the shared patch-list extractor, and the
    /// <c>resource_create</c> / <c>resource_modify</c> body parsers.
    ///
    /// <para>
    /// These mirror the P4.1 <c>ResourceReadTests</c> suite: the units covered here are exactly the
    /// ones most prone to off-by-one / escaping / grammar bugs, and they are pure-managed so they
    /// run in the binary-less xUnit host (no Godot editor needed). The <c>#if TOOLS</c> handler
    /// (<c>ResourceTools.Create</c>/<c>Modify</c>) and the patcher (<c>ResourcePropertyPatcher</c>)
    /// are exercised by the headless Godot smoke, not here.
    /// </para>
    /// </summary>
    public class ResourcePropertyPatchTests
    {
        // --- path grammar ---------------------------------------------------------

        [Fact]
        public void ParsePath_SimpleProperty_OnePropertyNameSegment()
        {
            var (segments, error) = ResourcePropertyPatch.ParsePath("albedo_color");
            Assert.Null(error);
            Assert.Single(segments);
            Assert.Equal(SegmentKind.PropertyName, segments[0].Kind);
            Assert.Equal("albedo_color", segments[0].PropertyName);
        }

        [Fact]
        public void ParsePath_NestedProperty_TwoSegments()
        {
            var (segments, error) = ResourcePropertyPatch.ParsePath("metadata/name");
            Assert.Null(error);
            Assert.Equal(2, segments.Count);
            Assert.Equal(SegmentKind.PropertyName, segments[0].Kind);
            Assert.Equal("metadata", segments[0].PropertyName);
            Assert.Equal(SegmentKind.PropertyName, segments[1].Kind);
            Assert.Equal("name", segments[1].PropertyName);
        }

        [Fact]
        public void ParsePath_ArrayIndex_IndexSegment()
        {
            var (segments, error) = ResourcePropertyPatch.ParsePath("array/[0]");
            Assert.Null(error);
            Assert.Equal(2, segments.Count);
            Assert.Equal(SegmentKind.PropertyName, segments[0].Kind);
            Assert.Equal(SegmentKind.ArrayIndex, segments[1].Kind);
            Assert.Equal(0, segments[1].ArrayIndex);
        }

        [Fact]
        public void ParsePath_DictionaryKey_KeySegment()
        {
            var (segments, error) = ResourcePropertyPatch.ParsePath("dict/[player]");
            Assert.Null(error);
            Assert.Equal(2, segments.Count);
            Assert.Equal(SegmentKind.DictionaryKey, segments[1].Kind);
            Assert.Equal("player", segments[1].DictionaryKey);
        }

        [Fact]
        public void ParsePath_DictionaryKeyWithSlash_KeyPreserved()
        {
            // Slashes inside brackets are part of the key, not separators.
            var (segments, error) = ResourcePropertyPatch.ParsePath("dict/[a/b]");
            Assert.Null(error);
            Assert.Equal(2, segments.Count);
            Assert.Equal(SegmentKind.DictionaryKey, segments[1].Kind);
            Assert.Equal("a/b", segments[1].DictionaryKey);
        }

        [Fact]
        public void ParsePath_DeepNesting_FourSegments()
        {
            var (segments, error) = ResourcePropertyPatch.ParsePath("a/b/[0]/[key]");
            Assert.Null(error);
            Assert.Equal(4, segments.Count);
            Assert.Equal(SegmentKind.PropertyName, segments[0].Kind);
            Assert.Equal(SegmentKind.PropertyName, segments[1].Kind);
            Assert.Equal(SegmentKind.ArrayIndex, segments[2].Kind);
            Assert.Equal(SegmentKind.DictionaryKey, segments[3].Kind);
        }

        // --- grammar errors -------------------------------------------------------

        [Fact]
        public void ParsePath_Empty_Error()
        {
            var (segments, error) = ResourcePropertyPatch.ParsePath("");
            Assert.NotNull(error);
            Assert.Empty(segments);
        }

        [Fact]
        public void ParsePath_WhitespaceOnly_Error()
        {
            var (segments, error) = ResourcePropertyPatch.ParsePath("   ");
            Assert.NotNull(error);
            Assert.Empty(segments);
        }

        [Fact]
        public void ParsePath_LeadingIndex_Error()
        {
            var (segments, error) = ResourcePropertyPatch.ParsePath("[0]");
            Assert.NotNull(error);
            Assert.Empty(segments);
        }

        [Fact]
        public void ParsePath_LeadingKey_Error()
        {
            var (segments, error) = ResourcePropertyPatch.ParsePath("[key]");
            Assert.NotNull(error);
            Assert.Empty(segments);
        }

        [Fact]
        public void ParsePath_UnclosedBracket_Error()
        {
            var (segments, error) = ResourcePropertyPatch.ParsePath("dict/[key");
            Assert.NotNull(error);
            Assert.Empty(segments);
        }

        [Fact]
        public void ParsePath_EmptySegment_Error()
        {
            var (segments, error) = ResourcePropertyPatch.ParsePath("a//b");
            Assert.NotNull(error);
            Assert.Empty(segments);
        }

        [Fact]
        public void ParsePath_TrailingSlash_Error()
        {
            var (segments, error) = ResourcePropertyPatch.ParsePath("a/");
            Assert.NotNull(error);
            Assert.Empty(segments);
        }

        [Fact]
        public void ParsePath_NullInput_Error()
        {
            var (segments, error) = ResourcePropertyPatch.ParsePath(null);
            Assert.NotNull(error);
            Assert.Empty(segments);
        }

        // --- FromRaw ---------------------------------------------------------------

        [Fact]
        public void FromRaw_ValidPath_IsValid()
        {
            var patch = ResourcePropertyPatch.FromRaw("albedo_color", "[1,0,0,1]");
            Assert.True(patch.IsValid);
            Assert.Equal("albedo_color", patch.RawPath);
            Assert.Equal("[1,0,0,1]", patch.RawValue);
            Assert.Single(patch.Segments);
        }

        [Fact]
        public void FromRaw_InvalidPath_HasParseError()
        {
            var patch = ResourcePropertyPatch.FromRaw("[0]", "42");
            Assert.False(patch.IsValid);
            Assert.NotNull(patch.ParseError);
        }

        [Fact]
        public void FromRaw_EmptyPath_HasParseError()
        {
            var patch = ResourcePropertyPatch.FromRaw("", "42");
            Assert.False(patch.IsValid);
            Assert.False(patch.HasPath);
        }

        // --- ParseList (shared patch-list extraction) ------------------------------

        [Fact]
        public void ParseList_EmptyBody_EmptyList()
        {
            var patches = ResourcePropertyPatch.ParseList(null, "patches");
            Assert.Empty(patches);
        }

        [Fact]
        public void ParseList_MissingKey_EmptyList()
        {
            var patches = ResourcePropertyPatch.ParseList("{\"foo\":1}", "patches");
            Assert.Empty(patches);
        }

        [Fact]
        public void ParseList_SinglePatch_PathAndValue()
        {
            var body = "{\"patches\":[{\"path\":\"albedo_color\",\"value\":[1,0,0,1]}]}";
            var patches = ResourcePropertyPatch.ParseList(body, "patches");
            Assert.Single(patches);
            Assert.Equal("albedo_color", patches[0].RawPath);
            Assert.Equal("[1,0,0,1]", patches[0].RawValue);
            Assert.True(patches[0].IsValid);
        }

        [Fact]
        public void ParseList_MultiplePatches_OrderPreserved()
        {
            var body = "{\"patches\":[" +
                       "{\"path\":\"name\",\"value\":\"Wood\"}," +
                       "{\"path\":\"roughness\",\"value\":0.5}," +
                       "{\"path\":\"metallic\",\"value\":1}" +
                       "]}";
            var patches = ResourcePropertyPatch.ParseList(body, "patches");
            Assert.Equal(3, patches.Count);
            Assert.Equal("name", patches[0].RawPath);
            Assert.Equal("Wood", patches[0].RawValue);
            Assert.Equal("roughness", patches[1].RawPath);
            Assert.Equal("0.5", patches[1].RawValue);
            Assert.Equal("metallic", patches[2].RawPath);
            Assert.Equal("1", patches[2].RawValue);
        }

        [Fact]
        public void ParseList_StringValue_Unescaped()
        {
            var body = "{\"patches\":[{\"path\":\"label\",\"value\":\"hello\\nworld\"}]}";
            var patches = ResourcePropertyPatch.ParseList(body, "patches");
            Assert.Single(patches);
            Assert.Equal("hello\nworld", patches[0].RawValue);
        }

        [Fact]
        public void ParseList_BoolValue_PreservedAsToken()
        {
            var body = "{\"patches\":[{\"path\":\"flag\",\"value\":true}]}";
            var patches = ResourcePropertyPatch.ParseList(body, "patches");
            Assert.Single(patches);
            Assert.Equal("true", patches[0].RawValue);
        }

        [Fact]
        public void ParseList_NullValue_StoredAsNull()
        {
            var body = "{\"patches\":[{\"path\":\"ref\",\"value\":null}]}";
            var patches = ResourcePropertyPatch.ParseList(body, "patches");
            Assert.Single(patches);
            Assert.Null(patches[0].RawValue);
        }

        [Fact]
        public void ParseList_ObjectValue_PreservedAsJson()
        {
            var body = "{\"patches\":[{\"path\":\"ref\",\"value\":{\"resource_path\":\"res://a.tres\"}}]}";
            var patches = ResourcePropertyPatch.ParseList(body, "patches");
            Assert.Single(patches);
            Assert.Equal("{\"resource_path\":\"res://a.tres\"}", patches[0].RawValue);
        }

        [Fact]
        public void ParseList_ArrayValue_PreservedAsJson()
        {
            var body = "{\"patches\":[{\"path\":\"vec\",\"value\":[1,2,3]}]}";
            var patches = ResourcePropertyPatch.ParseList(body, "patches");
            Assert.Single(patches);
            Assert.Equal("[1,2,3]", patches[0].RawValue);
        }

        [Fact]
        public void ParseList_NestedPath_ParsedIntoSegments()
        {
            var body = "{\"patches\":[{\"path\":\"metadata/[key]\",\"value\":42}]}";
            var patches = ResourcePropertyPatch.ParseList(body, "patches");
            Assert.Single(patches);
            Assert.True(patches[0].IsValid);
            Assert.Equal(2, patches[0].Segments.Count);
            Assert.Equal(SegmentKind.DictionaryKey, patches[0].Segments[1].Kind);
        }

        [Fact]
        public void ParseList_MalformedEntry_Skipped()
        {
            // A non-object entry is skipped without aborting the array.
            var body = "{\"patches\":[\"bad\",{\"path\":\"ok\",\"value\":1}]}";
            var patches = ResourcePropertyPatch.ParseList(body, "patches");
            Assert.Single(patches);
            Assert.Equal("ok", patches[0].RawPath);
        }

        [Fact]
        public void ParseList_EmptyArray_EmptyList()
        {
            var body = "{\"patches\":[]}";
            var patches = ResourcePropertyPatch.ParseList(body, "patches");
            Assert.Empty(patches);
        }

        [Fact]
        public void ParseList_MissingPath_StillExtracted()
        {
            // An entry without "path" is added with a null RawPath — the handler/validator rejects it.
            var body = "{\"patches\":[{\"value\":42}]}";
            var patches = ResourcePropertyPatch.ParseList(body, "patches");
            Assert.Single(patches);
            Assert.False(patches[0].HasPath);
        }
    }

    /// <summary>
    /// Body-parser tests for <see cref="ResourceModifyBody"/> — the pure-managed parser for
    /// <c>godot_open_mcp_resource_modify</c>.
    /// </summary>
    public class ResourceModifyBodyTests
    {
        [Fact]
        public void Parse_EmptyBody_NoResourcePathNoPatches()
        {
            var body = ResourceModifyBody.Parse(null);
            Assert.False(body.HasResourcePath);
            Assert.False(body.HasPatches);
        }

        [Fact]
        public void Parse_ResourcePath_RoundTrips()
        {
            var body = ResourceModifyBody.Parse("{\"resource_path\":\"res://mat.tres\"}");
            Assert.True(body.HasResourcePath);
            Assert.Equal("res://mat.tres", body.ResourcePath);
        }

        [Fact]
        public void Parse_ResourcePathCamelCase_RoundTrips()
        {
            var body = ResourceModifyBody.Parse("{\"resourcePath\":\"res://mat.tres\"}");
            Assert.True(body.HasResourcePath);
            Assert.Equal("res://mat.tres", body.ResourcePath);
        }

        [Fact]
        public void Parse_Patches_Extracted()
        {
            var body = ResourceModifyBody.Parse(
                "{\"resource_path\":\"res://mat.tres\",\"patches\":[{\"path\":\"roughness\",\"value\":0.5}]}");
            Assert.True(body.HasPatches);
            Assert.Single(body.Patches);
            Assert.Equal("roughness", body.Patches[0].RawPath);
        }

        [Fact]
        public void Parse_PropertiesAlias_Extracted()
        {
            // "properties" is accepted as an alias for "patches".
            var body = ResourceModifyBody.Parse(
                "{\"resource_path\":\"res://mat.tres\",\"properties\":[{\"path\":\"roughness\",\"value\":0.5}]}");
            Assert.True(body.HasPatches);
            Assert.Single(body.Patches);
        }

        [Fact]
        public void Parse_ExplicitNullResourcePath_TreatedAsAbsent()
        {
            var body = ResourceModifyBody.Parse("{\"resource_path\":null}");
            Assert.False(body.HasResourcePath);
        }

        [Fact]
        public void Parse_MultiplePatches_OrderPreserved()
        {
            var body = ResourceModifyBody.Parse(
                "{\"patches\":[{\"path\":\"a\",\"value\":1},{\"path\":\"b\",\"value\":2},{\"path\":\"c\",\"value\":3}]}");
            Assert.Equal(3, body.Patches.Count);
            Assert.Equal("a", body.Patches[0].RawPath);
            Assert.Equal("c", body.Patches[2].RawPath);
        }

        [Fact]
        public void Parse_PatchClampedToMaxPatches()
        {
            // Build a body with MaxPatches+1 entries; the parser should clamp.
            var entries = new System.Text.StringBuilder();
            for (int i = 0; i <= ResourceModifyBody.MaxPatches; i++)
            {
                if (i > 0) entries.Append(',');
                entries.Append("{\"path\":\"p").Append(i).Append("\",\"value\":").Append(i).Append('}');
            }
            var body = ResourceModifyBody.Parse("{\"patches\":[" + entries + "]}");
            Assert.Equal(ResourceModifyBody.MaxPatches, body.Patches.Count);
        }
    }

    /// <summary>
    /// Body-parser tests for <see cref="ResourceCreateBody"/> — the pure-managed parser for
    /// <c>godot_open_mcp_resource_create</c>.
    /// </summary>
    public class ResourceCreateBodyTests
    {
        [Fact]
        public void Parse_EmptyBody_Defaults()
        {
            var body = ResourceCreateBody.Parse(null);
            Assert.False(body.HasResourcePath);
            Assert.Equal(ResourceCreateBody.DefaultTypeClassName, body.EffectiveTypeClassName);
            Assert.Empty(body.Properties);
        }

        [Fact]
        public void Parse_ResourcePath_RoundTrips()
        {
            var body = ResourceCreateBody.Parse("{\"resource_path\":\"res://new.tres\"}");
            Assert.True(body.HasResourcePath);
            Assert.Equal("res://new.tres", body.ResourcePath);
        }

        [Fact]
        public void Parse_ResourcePathCamelCase_RoundTrips()
        {
            var body = ResourceCreateBody.Parse("{\"resourcePath\":\"res://new.tres\"}");
            Assert.Equal("res://new.tres", body.ResourcePath);
        }

        [Fact]
        public void Parse_TypeClassName_DefaultIsResource()
        {
            var body = ResourceCreateBody.Parse("{\"resource_path\":\"res://new.tres\"}");
            Assert.Equal("Resource", body.EffectiveTypeClassName);
        }

        [Fact]
        public void Parse_TypeClassName_Custom()
        {
            var body = ResourceCreateBody.Parse(
                "{\"resource_path\":\"res://new.tres\",\"type_class_name\":\"StandardMaterial3D\"}");
            Assert.Equal("StandardMaterial3D", body.EffectiveTypeClassName);
        }

        [Fact]
        public void Parse_TypeClassNameCamelCase_Custom()
        {
            var body = ResourceCreateBody.Parse(
                "{\"resourcePath\":\"res://new.tres\",\"typeClassName\":\"StandardMaterial3D\"}");
            Assert.Equal("StandardMaterial3D", body.EffectiveTypeClassName);
        }

        [Fact]
        public void Parse_Properties_Extracted()
        {
            var body = ResourceCreateBody.Parse(
                "{\"resource_path\":\"res://new.tres\",\"properties\":[{\"path\":\"albedo_color\",\"value\":[1,0,0,1]}]}");
            Assert.Single(body.Properties);
            Assert.Equal("albedo_color", body.Properties[0].RawPath);
            Assert.Equal("[1,0,0,1]", body.Properties[0].RawValue);
        }

        [Fact]
        public void Parse_PatchesAlias_Extracted()
        {
            var body = ResourceCreateBody.Parse(
                "{\"resource_path\":\"res://new.tres\",\"patches\":[{\"path\":\"name\",\"value\":\"X\"}]}");
            Assert.Single(body.Properties);
        }

        [Fact]
        public void Parse_ExplicitNullTypeClassName_FallsBackToDefault()
        {
            var body = ResourceCreateBody.Parse(
                "{\"resource_path\":\"res://new.tres\",\"type_class_name\":null}");
            Assert.Equal("Resource", body.EffectiveTypeClassName);
        }
    }
}
