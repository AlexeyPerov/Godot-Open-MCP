#nullable enable
using System.IO;
using GodotOpenMcp.Verify.Fixes;
using Xunit;

namespace GodotOpenMcp.Bridge.Tests
{
    /// <summary>
    /// Tests for <see cref="FixRollback"/>, the byte-level snapshot/restore that backs auto-fix
    /// rollback. Pure-managed (File/Path only), so it runs in the binary-less host.
    ///
    /// <para>
    /// The load-bearing case here is the <b>backup-failure</b> path. <see cref="FixRollback.Snapshot"/>
    /// records <c>ExistedBefore=false</c> for files the fix CREATED, and
    /// <see cref="FixRollback.Restore"/> rolls those back by <c>File.Delete</c>. A failed
    /// <c>File.Copy</c> during Snapshot used to be recorded the same way — so a file that existed but
    /// could not be backed up was reclassified as "created by the fix" and Restore DELETED it, while
    /// reporting <c>Success=true</c> and listing the destroyed path under <c>RestoredPaths</c>. On a
    /// locked or permission-denied scene that meant permanent, silent loss of the user's file on the
    /// path whose entire job is to undo damage. The three cases must stay clearly separated:
    /// rewrite/delete → restore the bytes; create → delete; backup failed → report unrestored and
    /// never touch the file.
    /// </para>
    /// </summary>
    public class FixRollbackTests
    {
        static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "gom-fixrollback-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Snapshot_ThenRestore_RewritesModifiedFileBackToOriginalBytes()
        {
            var dir = NewTempDir();
            try
            {
                var file = Path.Combine(dir, "Main.tscn");
                File.WriteAllText(file, "ORIGINAL");

                var rollback = new FixRollback();
                var backedUp = rollback.Snapshot(new[] { file });
                Assert.Equal(1, backedUp);

                File.WriteAllText(file, "MUTATED BY FIX");
                var result = rollback.Restore();

                Assert.True(result.Success);
                Assert.Contains(file, result.RestoredPaths);
                Assert.Empty(result.UnrestoredPaths);
                Assert.Equal("ORIGINAL", File.ReadAllText(file));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Snapshot_ThenRestore_RecreatesAFileTheFixDeleted()
        {
            var dir = NewTempDir();
            try
            {
                var file = Path.Combine(dir, "Main.tscn");
                File.WriteAllText(file, "ORIGINAL");

                var rollback = new FixRollback();
                rollback.Snapshot(new[] { file });

                File.Delete(file);
                var result = rollback.Restore();

                Assert.True(result.Success);
                Assert.True(File.Exists(file));
                Assert.Equal("ORIGINAL", File.ReadAllText(file));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Restore_DeletesAFileTheFixCreated()
        {
            var dir = NewTempDir();
            try
            {
                var created = Path.Combine(dir, "New.tres");
                Assert.False(File.Exists(created));

                var rollback = new FixRollback();
                var backedUp = rollback.Snapshot(new[] { created });
                Assert.Equal(0, backedUp); // nothing existed, so nothing was copied

                File.WriteAllText(created, "made by the fix");
                var result = rollback.Restore();

                Assert.True(result.Success);
                Assert.False(File.Exists(created));
                Assert.Contains(created, result.RestoredPaths);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Snapshot_WhenBackupCopyFails_RestoreReportsUnrestoredAndDoesNotDeleteTheFile()
        {
            // Making File.Copy fail portably is awkward: chmod is a no-op for root in CI, and an
            // exclusive FileStream lock only blocks the copy on Windows. So this test asserts the
            // INVARIANT rather than the mechanism — under either outcome (backup taken or backup
            // failed), a pre-existing file must survive Restore. That is exactly the property the
            // ExistedBefore=false conflation violated; Restore_MissingBackupFileIsReportedUnrestored-
            // NotDeleted below pins the failed-backup branch deterministically.
            var dir = NewTempDir();
            try
            {
                var file = Path.Combine(dir, "Locked.tscn");
                File.WriteAllText(file, "PRECIOUS USER DATA");

                var rollback = new FixRollback();

                // Hold the file open for exclusive read/write. On Windows this makes File.Copy throw
                // IOException; on Unix the copy succeeds, so the assertions below are written to hold
                // in BOTH cases: either the backup was taken (restorable) or it failed (reported as
                // unrestored) — but never "silently deleted".
                using (var _ = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    rollback.Snapshot(new[] { file });
                }

                var result = rollback.Restore();

                // The one thing that must never happen: the pre-existing file being deleted.
                Assert.True(
                    File.Exists(file),
                    "a file that existed before the fix must never be deleted by Restore");

                if (result.Success)
                {
                    // Backup was taken — the bytes must be intact.
                    Assert.Equal("PRECIOUS USER DATA", File.ReadAllText(file));
                }
                else
                {
                    // Backup failed — it must be reported, not silently swallowed.
                    Assert.Contains(file, result.UnrestoredPaths);
                    Assert.DoesNotContain(file, result.RestoredPaths);
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Restore_MissingBackupFileIsReportedUnrestoredNotDeleted()
        {
            // Directly pins the invariant the ExistedBefore=false conflation broke: when a file
            // existed before but no backup bytes are available at restore time, the file must be left
            // alone and reported, and Success must be false.
            var dir = NewTempDir();
            try
            {
                var file = Path.Combine(dir, "Main.tscn");
                File.WriteAllText(file, "ORIGINAL");

                var rollback = new FixRollback();
                rollback.Snapshot(new[] { file });

                // Simulate the backup disappearing between snapshot and restore (temp reaper, etc.).
                var backupRoot = Path.Combine(Path.GetTempPath(), "godot-open-mcp-fix-rollback");
                foreach (var sub in Directory.GetDirectories(backupRoot))
                {
                    foreach (var f in Directory.GetFiles(sub))
                    {
                        if (File.ReadAllText(f) == "ORIGINAL") File.Delete(f);
                    }
                }

                var result = rollback.Restore();

                Assert.False(result.Success);
                Assert.Contains(file, result.UnrestoredPaths);
                Assert.True(File.Exists(file), "the original must not be deleted");
                rollback.Discard();
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Snapshot_SkipsNullAndEmptyPaths()
        {
            var rollback = new FixRollback();
            var backedUp = rollback.Snapshot(new[] { "", null! });
            Assert.Equal(0, backedUp);
            var result = rollback.Restore();
            Assert.True(result.Success);
            Assert.Empty(result.RestoredPaths);
            Assert.Empty(result.UnrestoredPaths);
        }
    }
}
