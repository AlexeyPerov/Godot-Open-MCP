#nullable enable
using System.IO;
using GodotOpenMcp.Verify.Fixes;
using Xunit;

namespace GodotOpenMcp.Verify.Tests.Fixes
{
    /// <summary>
    /// P3.7 tests for <see cref="FixRollback"/> — the byte-level snapshot/restore that backs safe auto-fix
    /// rollback. Pins the three restore cases (rewrite, delete, create) plus the no-snapshot and discard
    /// semantics. Pure file I/O against temp files — no Godot API.
    /// </summary>
    public class FixRollbackTests
    {
        [Fact]
        public void HasSnapshot_FalseBeforeSnapshot()
        {
            var rb = new FixRollback();
            Assert.False(rb.HasSnapshot);
        }

        [Fact]
        public void HasSnapshot_TrueAfterSnapshot()
        {
            var path = Path.GetTempFileName();
            try
            {
                var rb = new FixRollback();
                rb.Snapshot(new[] { path });
                Assert.True(rb.HasSnapshot);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Restore_RewrittenFile_RestoresPreFixBytes()
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, "before");
            try
            {
                var rb = new FixRollback();
                rb.Snapshot(new[] { path });

                // Simulate the fix rewriting the file.
                File.WriteAllText(path, "after-fix");

                var result = rb.Restore();

                Assert.True(result.Success);
                Assert.Equal(path, Assert.Single(result.RestoredPaths));
                Assert.Empty(result.UnrestoredPaths);
                Assert.Equal("before", File.ReadAllText(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Restore_DeletedFile_RestoresPreFixBytes()
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, "before");
            try
            {
                var rb = new FixRollback();
                rb.Snapshot(new[] { path });

                // Simulate the fix deleting the file.
                File.Delete(path);
                Assert.False(File.Exists(path));

                var result = rb.Restore();

                Assert.True(result.Success);
                Assert.Equal("before", File.ReadAllText(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Restore_FixCreatedFile_DeletesIt()
        {
            var path = Path.Combine(Path.GetTempPath(), "godot-open-mcp-fix-rollback-test-create-" + System.Guid.NewGuid().ToString("N"));
            // The file does NOT exist before the snapshot — record the create-case.
            Assert.False(File.Exists(path));
            try
            {
                var rb = new FixRollback();
                rb.Snapshot(new[] { path });

                // Simulate the fix creating the file.
                File.WriteAllText(path, "fix-created");

                var result = rb.Restore();

                Assert.True(result.Success);
                Assert.False(File.Exists(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Restore_NoSnapshot_ReturnsEmpty()
        {
            var rb = new FixRollback();

            var result = rb.Restore();

            Assert.True(result.Success);
            Assert.Empty(result.RestoredPaths);
            Assert.Empty(result.UnrestoredPaths);
        }

        [Fact]
        public void Discard_ClearsEntriesAndTempDir()
        {
            var path = Path.GetTempFileName();
            try
            {
                var rb = new FixRollback();
                rb.Snapshot(new[] { path });
                Assert.True(rb.HasSnapshot);

                rb.Discard();

                Assert.False(rb.HasSnapshot);
                // A restore after discard is a no-op (empty).
                var result = rb.Restore();
                Assert.Empty(result.RestoredPaths);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
