#nullable enable
using GodotOpenMcp.Bridge.Editor;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// P4.4 unit tests for the pure-managed filesystem pieces: the <see cref="FileSystemListBody"/>
    /// and <see cref="FileSystemReimportBody"/> body parsers, plus the <see cref="FileSystemEntryData"/>
    /// JSON serialization.
    ///
    /// <para>
    /// These mirror the P4.1/P4.3 suites (<c>ResourceReadTests</c> / <c>ResourceFileOperationTests</c>):
    /// the units covered here are exactly the ones most prone to off-by-one / escaping bugs, and they
    /// are pure-managed so they run in the binary-less xUnit host (no Godot editor needed). The
    /// <c>#if TOOLS</c> handlers (<c>FileSystemTools.List</c>/<c>Reimport</c>) are exercised by the
    /// headless Godot smoke and the gate-dispatch tests, not here.
    /// </para>
    /// </summary>
    public class FileSystemListBodyTests
    {
        // --- FileSystemListBody ------------------------------------------------------

        [Fact]
        public void Parse_EmptyBody_AllDefaults()
        {
            var body = FileSystemListBody.Parse(null);
            Assert.False(body.HasPath);
            Assert.Equal(FileSystemListBody.DefaultPageSize, body.PageSize);
            Assert.Null(body.Cursor);
            Assert.False(body.IncludeHidden);
        }

        [Fact]
        public void Parse_Path_RoundTrips()
        {
            var body = FileSystemListBody.Parse("{\"path\":\"res://materials/\"}");
            Assert.True(body.HasPath);
            Assert.Equal("res://materials/", body.Path);
        }

        [Fact]
        public void Parse_PathWithoutTrailingSlash_RoundTrips()
        {
            var body = FileSystemListBody.Parse("{\"path\":\"res://materials\"}");
            Assert.True(body.HasPath);
            Assert.Equal("res://materials", body.Path);
        }

        [Fact]
        public void Parse_Cursor_RoundTrips()
        {
            var body = FileSystemListBody.Parse("{\"cursor\":\"50\"}");
            Assert.Equal("50", body.Cursor);
        }

        [Fact]
        public void Parse_PageSize_RoundTrips()
        {
            var body = FileSystemListBody.Parse("{\"page_size\":25}");
            Assert.Equal(25, body.PageSize);
        }

        [Fact]
        public void Parse_PageSizeCamelCase_RoundTrips()
        {
            var body = FileSystemListBody.Parse("{\"pageSize\":25}");
            Assert.Equal(25, body.PageSize);
        }

        [Fact]
        public void Parse_PageSizeClampedToMax()
        {
            var body = FileSystemListBody.Parse("{\"page_size\":99999}");
            Assert.Equal(FileSystemListBody.MaxPageSize, body.PageSize);
        }

        [Fact]
        public void Parse_PageSizeClampedToMin()
        {
            var body = FileSystemListBody.Parse("{\"page_size\":0}");
            Assert.Equal(1, body.PageSize);
        }

        [Fact]
        public void Parse_PageSizeDefaultWhenAbsent()
        {
            var body = FileSystemListBody.Parse("{\"path\":\"res://\"}");
            Assert.Equal(FileSystemListBody.DefaultPageSize, body.PageSize);
        }

        [Fact]
        public void Parse_IncludeHidden_True()
        {
            var body = FileSystemListBody.Parse("{\"include_hidden\":true}");
            Assert.True(body.IncludeHidden);
        }

        [Fact]
        public void Parse_IncludeHiddenCamelCase_True()
        {
            var body = FileSystemListBody.Parse("{\"includeHidden\":true}");
            Assert.True(body.IncludeHidden);
        }

        [Fact]
        public void Parse_IncludeHidden_DefaultFalse()
        {
            var body = FileSystemListBody.Parse("{\"path\":\"res://\"}");
            Assert.False(body.IncludeHidden);
        }

        [Fact]
        public void Parse_ExplicitNullPath_TreatedAsAbsent()
        {
            var body = FileSystemListBody.Parse("{\"path\":null}");
            Assert.False(body.HasPath);
        }

        [Fact]
        public void Parse_JsonEscapedPath_Unescaped()
        {
            var body = FileSystemListBody.Parse("{\"path\":\"res://a\\\\b/\"}");
            Assert.Equal("res://a\\b/", body.Path);
        }
    }

    public class FileSystemReimportBodyTests
    {
        // --- FileSystemReimportBody --------------------------------------------------

        [Fact]
        public void Parse_EmptyBody_FullScanMode()
        {
            var body = FileSystemReimportBody.Parse(null);
            Assert.False(body.IsExactFilesMode);
            Assert.False(body.HasFilesField);
            Assert.Null(body.Files);
            Assert.Equal(FileSystemReimportBody.DefaultTimeoutMs, body.TimeoutMs);
        }

        [Fact]
        public void Parse_FilesArray_ExactFilesMode()
        {
            var body = FileSystemReimportBody.Parse(
                "{\"files\":[\"res://a.tres\",\"res://b.tres\"]}");
            Assert.True(body.IsExactFilesMode);
            Assert.True(body.HasFilesField);
            Assert.Equal(2, body.Files!.Length);
            Assert.Equal("res://a.tres", body.Files[0]);
            Assert.Equal("res://b.tres", body.Files[1]);
        }

        [Fact]
        public void Parse_EmptyFilesArray_FullScanMode()
        {
            var body = FileSystemReimportBody.Parse("{\"files\":[]}");
            // Present-but-empty → full scan mode (same behavior as omitted).
            Assert.False(body.IsExactFilesMode);
            Assert.True(body.HasFilesField);
            Assert.Equal(0, body.Files!.Length);
        }

        [Fact]
        public void Parse_ExplicitNullFiles_TreatedAsAbsent()
        {
            var body = FileSystemReimportBody.Parse("{\"files\":null}");
            Assert.False(body.IsExactFilesMode);
            Assert.False(body.HasFilesField);
            Assert.Null(body.Files);
        }

        [Fact]
        public void Parse_TimeoutMs_RoundTrips()
        {
            var body = FileSystemReimportBody.Parse("{\"timeout_ms\":3000}");
            Assert.Equal(3000, body.TimeoutMs);
        }

        [Fact]
        public void Parse_TimeoutMsCamelCase_RoundTrips()
        {
            var body = FileSystemReimportBody.Parse("{\"timeoutMs\":3000}");
            Assert.Equal(3000, body.TimeoutMs);
        }

        [Fact]
        public void Parse_TimeoutMsClampedToMin()
        {
            var body = FileSystemReimportBody.Parse("{\"timeout_ms\":100}");
            Assert.Equal(FileSystemReimportBody.MinTimeoutMs, body.TimeoutMs);
        }

        [Fact]
        public void Parse_TimeoutMsClampedToMax()
        {
            var body = FileSystemReimportBody.Parse("{\"timeout_ms\":999999}");
            Assert.Equal(FileSystemReimportBody.MaxTimeoutMs, body.TimeoutMs);
        }

        [Fact]
        public void Parse_TimeoutMsDefaultWhenAbsent()
        {
            var body = FileSystemReimportBody.Parse("{\"files\":[\"res://a.tres\"]}");
            Assert.Equal(FileSystemReimportBody.DefaultTimeoutMs, body.TimeoutMs);
        }

        [Fact]
        public void Parse_FilesWithEscapedQuote_Unescaped()
        {
            var body = FileSystemReimportBody.Parse("{\"files\":[\"res://a\\\"b.tres\"]}");
            Assert.Single(body.Files!);
            Assert.Equal("res://a\"b.tres", body.Files![0]);
        }

        [Fact]
        public void Parse_NonStringElement_FilesNull()
        {
            // A non-string array element is a contract violation → the whole array is treated as
            // absent (null), so the handler falls back to full-scan mode.
            var body = FileSystemReimportBody.Parse("{\"files\":[42]}");
            Assert.False(body.IsExactFilesMode);
            Assert.Null(body.Files);
        }
    }

    public class FileSystemEntryDataTests
    {
        // --- FileSystemEntryData JSON serialization ----------------------------------

        [Fact]
        public void DirectoryEntry_SerializesWithNullTypeAndUid()
        {
            var entry = new FileSystemEntryData
            {
                Name = "sub",
                Path = "res://materials/sub/",
                IsDirectory = true,
            };
            var json = entry.ToJsonString();
            Assert.Contains("\"name\":\"sub\"", json);
            Assert.Contains("\"path\":\"res://materials/sub/\"", json);
            Assert.Contains("\"isDirectory\":true", json);
            Assert.Contains("\"resourceType\":null", json);
            Assert.Contains("\"uid\":null", json);
        }

        [Fact]
        public void FileEntry_SerializesWithTypeAndUid()
        {
            var entry = new FileSystemEntryData
            {
                Name = "wood.tres",
                Path = "res://materials/wood.tres",
                IsDirectory = false,
                ResourceType = "StandardMaterial3D",
                Uid = "uid://abc123",
            };
            var json = entry.ToJsonString();
            Assert.Contains("\"isDirectory\":false", json);
            Assert.Contains("\"resourceType\":\"StandardMaterial3D\"", json);
            Assert.Contains("\"uid\":\"uid://abc123\"", json);
        }

        [Fact]
        public void FileEntry_NullType_SerializesAsNull()
        {
            var entry = new FileSystemEntryData
            {
                Name = "unknown.bin",
                Path = "res://unknown.bin",
                IsDirectory = false,
                ResourceType = null,
                Uid = null,
            };
            var json = entry.ToJsonString();
            Assert.Contains("\"resourceType\":null", json);
        }

        [Fact]
        public void Entry_EscapesSpecialCharsInName()
        {
            var entry = new FileSystemEntryData
            {
                Name = "with\"quote",
                Path = "res://with\"quote.tres",
                IsDirectory = false,
            };
            var json = entry.ToJsonString();
            Assert.Contains("\"name\":\"with\\\"quote\"", json);
        }
    }
}
