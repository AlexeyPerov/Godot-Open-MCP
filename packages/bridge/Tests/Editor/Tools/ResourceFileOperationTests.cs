#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P4.3 unit tests for the pure-managed resource-file-operation pieces: the
    /// <see cref="ResourceMoveBody"/> and <see cref="ResourceDeleteBody"/> body parsers, plus the
    /// path-normalization edge cases specific to move (same-path, extension changes).
    ///
    /// <para>
    /// These mirror the P4.2 <c>ResourceMutationTests</c> suite: the units covered here are exactly
    /// the ones most prone to off-by-one / escaping bugs, and they are pure-managed so they run in
    /// the binary-less xUnit host (no Godot editor needed). The <c>#if TOOLS</c> handlers
    /// (<c>ResourceTools.Move</c>/<c>Delete</c>) and the file-operation abstraction
    /// (<c>ResourceFileOperations</c>) are exercised by the headless Godot smoke, not here.
    /// </para>
    /// </summary>
    public class ResourceMoveBodyTests
    {
        [Fact]
        public void Parse_EmptyBody_NoPaths()
        {
            var body = ResourceMoveBody.Parse(null);
            Assert.False(body.HasSourcePath);
            Assert.False(body.HasDestinationPath);
        }

        [Fact]
        public void Parse_SourcePath_RoundTrips()
        {
            var body = ResourceMoveBody.Parse("{\"source_path\":\"res://a.tres\"}");
            Assert.True(body.HasSourcePath);
            Assert.Equal("res://a.tres", body.SourcePath);
            Assert.False(body.HasDestinationPath);
        }

        [Fact]
        public void Parse_SourcePathCamelCase_RoundTrips()
        {
            var body = ResourceMoveBody.Parse("{\"sourcePath\":\"res://a.tres\"}");
            Assert.True(body.HasSourcePath);
            Assert.Equal("res://a.tres", body.SourcePath);
        }

        [Fact]
        public void Parse_DestinationPath_RoundTrips()
        {
            var body = ResourceMoveBody.Parse("{\"destination_path\":\"res://b.tres\"}");
            Assert.True(body.HasDestinationPath);
            Assert.Equal("res://b.tres", body.DestinationPath);
            Assert.False(body.HasSourcePath);
        }

        [Fact]
        public void Parse_DestinationPathCamelCase_RoundTrips()
        {
            var body = ResourceMoveBody.Parse("{\"destinationPath\":\"res://b.tres\"}");
            Assert.True(body.HasDestinationPath);
            Assert.Equal("res://b.tres", body.DestinationPath);
        }

        [Fact]
        public void Parse_BothPaths_RoundTrips()
        {
            var body = ResourceMoveBody.Parse(
                "{\"source_path\":\"res://old.tres\",\"destination_path\":\"res://dir/new.tres\"}");
            Assert.True(body.HasSourcePath);
            Assert.True(body.HasDestinationPath);
            Assert.Equal("res://old.tres", body.SourcePath);
            Assert.Equal("res://dir/new.tres", body.DestinationPath);
        }

        [Fact]
        public void Parse_ExplicitNullSourcePath_TreatedAsAbsent()
        {
            var body = ResourceMoveBody.Parse("{\"source_path\":null,\"destination_path\":\"res://b.tres\"}");
            Assert.False(body.HasSourcePath);
            Assert.True(body.HasDestinationPath);
        }

        [Fact]
        public void Parse_ExplicitNullDestinationPath_TreatedAsAbsent()
        {
            var body = ResourceMoveBody.Parse("{\"source_path\":\"res://a.tres\",\"destination_path\":null}");
            Assert.True(body.HasSourcePath);
            Assert.False(body.HasDestinationPath);
        }

        [Fact]
        public void Parse_EscapedString_Unescaped()
        {
            // JSON source_path value "res://a\"b.tres" — the \" is a JSON-escaped quote.
            var body = ResourceMoveBody.Parse(
                "{\"source_path\":\"res://a\\\"b.tres\",\"destination_path\":\"res://c.tres\"}");
            Assert.Equal("res://a\"b.tres", body.SourcePath);
        }

        [Fact]
        public void Parse_WhitespaceOnly_TreatedAsAbsent()
        {
            var body = ResourceMoveBody.Parse("{\"source_path\":\"  \",\"destination_path\":\"\\t\"}");
            Assert.False(body.HasSourcePath);
            Assert.False(body.HasDestinationPath);
        }
    }

    /// <summary>
    /// Body-parser tests for <see cref="ResourceDeleteBody"/> — the pure-managed parser for
    /// <c>godot_open_mcp_resource_delete</c>.
    /// </summary>
    public class ResourceDeleteBodyTests
    {
        [Fact]
        public void Parse_EmptyBody_NoResourcePath()
        {
            var body = ResourceDeleteBody.Parse(null);
            Assert.False(body.HasResourcePath);
        }

        [Fact]
        public void Parse_ResourcePath_RoundTrips()
        {
            var body = ResourceDeleteBody.Parse("{\"resource_path\":\"res://mat.tres\"}");
            Assert.True(body.HasResourcePath);
            Assert.Equal("res://mat.tres", body.ResourcePath);
        }

        [Fact]
        public void Parse_ResourcePathCamelCase_RoundTrips()
        {
            var body = ResourceDeleteBody.Parse("{\"resourcePath\":\"res://mat.tres\"}");
            Assert.True(body.HasResourcePath);
            Assert.Equal("res://mat.tres", body.ResourcePath);
        }

        [Fact]
        public void Parse_ExplicitNullResourcePath_TreatedAsAbsent()
        {
            var body = ResourceDeleteBody.Parse("{\"resource_path\":null}");
            Assert.False(body.HasResourcePath);
        }

        [Fact]
        public void Parse_WhitespaceOnly_TreatedAsAbsent()
        {
            var body = ResourceDeleteBody.Parse("{\"resource_path\":\"   \"}");
            Assert.False(body.HasResourcePath);
        }

        [Fact]
        public void Parse_EscapedString_Unescaped()
        {
            // JSON resource_path value "res://a\nb.tres" — the \n is a JSON-escaped newline.
            var body = ResourceDeleteBody.Parse("{\"resource_path\":\"res://a\\nb.tres\"}");
            Assert.Equal("res://a\nb.tres", body.ResourcePath);
        }
    }

    /// <summary>
    /// Path-normalization edge cases specific to move/delete — extension validation, same-path
    /// detection, and the <c>uid://</c> shape checks that the handlers route through.
    /// </summary>
    public class ResourceFileOperationPathTests
    {
        [Theory]
        [InlineData("res://a.tres", true)]
        [InlineData("res://a.res", true)]
        [InlineData("res://a.TRES", true)]
        [InlineData("res://a.Res", true)]
        [InlineData("res://a.png", false)]
        [InlineData("res://a.tscn", false)]
        [InlineData("res://a", false)]
        [InlineData("res://a.import", false)]
        public void HasResourceExtension_ChecksTresRes(string path, bool expected)
        {
            Assert.Equal(expected, ResourcePathNormalizer.HasResourceExtension(path));
        }

        [Fact]
        public void TryRequireResFilePath_RejectsNonResPath()
        {
            var ok = ResourcePathNormalizer.TryRequireResFilePath("user://a.tres", out _, out var error);
            Assert.False(ok);
            Assert.NotNull(error);
        }

        [Fact]
        public void TryRequireResFilePath_RejectsBareScheme()
        {
            var ok = ResourcePathNormalizer.TryRequireResFilePath("res://", out _, out var error);
            Assert.False(ok);
            Assert.NotNull(error);
        }

        [Fact]
        public void TryRequireResFilePath_RejectsDirectory()
        {
            var ok = ResourcePathNormalizer.TryRequireResFilePath("res://dir/", out _, out var error);
            Assert.False(ok);
            Assert.NotNull(error);
        }

        [Fact]
        public void TryRequireResFilePath_RejectsParentTraversal()
        {
            var ok = ResourcePathNormalizer.TryRequireResFilePath("res://a/../b.tres", out _, out var error);
            Assert.False(ok);
            Assert.NotNull(error);
        }

        [Fact]
        public void TryRequireResFilePath_RejectsUnsupportedExtension()
        {
            var ok = ResourcePathNormalizer.TryRequireResFilePath("res://a.png", out _, out var error);
            Assert.False(ok);
            Assert.NotNull(error);
        }

        [Fact]
        public void TryRequireResFilePath_AcceptsValidTresPath()
        {
            var ok = ResourcePathNormalizer.TryRequireResFilePath("res://dir/a.tres", out var normalized, out var error);
            Assert.True(ok);
            Assert.Equal("res://dir/a.tres", normalized);
            Assert.Null(error);
        }

        [Fact]
        public void TryRequireResFilePath_TrimsWhitespace()
        {
            var ok = ResourcePathNormalizer.TryRequireResFilePath("  res://a.tres  ", out var normalized, out _);
            Assert.True(ok);
            Assert.Equal("res://a.tres", normalized);
        }

        [Fact]
        public void IsUid_DetectsUidScheme()
        {
            Assert.True(ResourcePathNormalizer.IsUid("uid://abc123"));
            Assert.False(ResourcePathNormalizer.IsUid("res://a.tres"));
            Assert.False(ResourcePathNormalizer.IsUid(null));
            Assert.False(ResourcePathNormalizer.IsUid(""));
        }

        [Fact]
        public void IsResPath_DetectsResScheme()
        {
            Assert.True(ResourcePathNormalizer.IsResPath("res://a.tres"));
            Assert.False(ResourcePathNormalizer.IsResPath("uid://abc123"));
            Assert.False(ResourcePathNormalizer.IsResPath(null));
            Assert.False(ResourcePathNormalizer.IsResPath(""));
        }

        // --- same-path detection (the handler checks normalized equality) ------------

        [Fact]
        public void SamePath_NormalizedEquality()
        {
            // After normalization, whitespace is trimmed — the handler's string-equality guard
            // catches the same-path case.
            var ok1 = ResourcePathNormalizer.TryRequireResFilePath("  res://a.tres  ", out var src, out _);
            var ok2 = ResourcePathNormalizer.TryRequireResFilePath("res://a.tres", out var dst, out _);
            Assert.True(ok1 && ok2);
            Assert.Equal(src, dst);
        }

        [Fact]
        public void DifferentPaths_NotEqual()
        {
            var ok1 = ResourcePathNormalizer.TryRequireResFilePath("res://a.tres", out var src, out _);
            var ok2 = ResourcePathNormalizer.TryRequireResFilePath("res://b.tres", out var dst, out _);
            Assert.True(ok1 && ok2);
            Assert.NotEqual(src, dst);
        }
    }
}
