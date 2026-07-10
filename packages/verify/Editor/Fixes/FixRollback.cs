#nullable enable
using System.Collections.Generic;
using System.IO;

namespace GodotOpenMcp.Verify.Fixes
{
    /// <summary>
    /// Result of a <see cref="FixRollback.Restore"/> call. <see cref="Success"/> is false when at least
    /// one path could not be restored; the two arrays partition the snapshotted paths by outcome.
    /// Ported (copy) from Unity Open MCP's <c>RestoreResult</c>.
    /// </summary>
    public struct RestoreResult
    {
        /// <summary>True only when every snapshotted path was restored.</summary>
        public bool Success;

        /// <summary>Absolute paths actually restored (rewritten to their pre-fix bytes, or deleted).</summary>
        public string[] RestoredPaths;

        /// <summary>Absolute paths that could not be restored (backup missing or restore threw).</summary>
        public string[] UnrestoredPaths;
    }

    /// <summary>
    /// File-level snapshot/restore for safe auto-fix rollback (P3.7). The gate's checkpoint fingerprint is a
    /// hash used for COMPARISON (did the fix make things worse?), not a restore mechanism. To actually undo a
    /// fix that failed or introduced new errors, <see cref="Snapshot"/> keeps byte-level backups of every
    /// predicted touched path BEFORE the fix runs, and <see cref="Restore"/> can put them back. Three cases:
    /// <list type="bullet">
    ///   <item><b>rewrite</b> — file existed before, the fix rewrote it → restore the backup copy.</item>
    ///   <item><b>delete</b> — file existed before, the fix deleted it → restore the backup copy.</item>
    ///   <item><b>create</b> — file did NOT exist before, the fix created it → delete it.</item>
    /// </list>
    /// Restores happen via plain <c>File.Copy</c>/<c>File.Delete</c> against absolute paths; the caller then
    /// triggers a Godot resource reimport so the editor picks up the restored bytes. Pure-managed (no Godot
    /// API) so the verify package stays standalone-testable in the binary-less host.
    /// <para>
    /// Ported (copy) from Unity Open MCP's <c>FixRollback</c>. The only intentional delta is the log channel:
    /// Unity routes backup/restore warnings through <c>UnityEngine.Debug.Log*</c>; Godot has no equivalent
    /// pure-managed log surface in the verify package, so failures are surfaced via the returned
    /// <see cref="RestoreResult"/> (the caller decides how to log) rather than a global sink.
    /// </para>
    /// </summary>
    public sealed class FixRollback
    {
        private struct BackupEntry
        {
            public string OriginalPath;   // absolute path the fix may touch
            public string BackupPath;     // temp copy of the pre-fix bytes
            public bool ExistedBefore;    // false => a fix-created file is rolled back by deleting it
        }

        private readonly List<BackupEntry> _entries = new();
        private readonly string _backupRoot;

        public FixRollback()
        {
            // One temp dir per snapshot so concurrent fixes (different agents) do not collide. Persist under
            // the OS temp tree, not under res://, so Godot never tries to import the backups.
            _backupRoot = Path.Combine(Path.GetTempPath(), "godot-open-mcp-fix-rollback",
                System.Guid.NewGuid().ToString("N"));
        }

        /// <summary>
        /// Snapshot every path the fix may touch. Paths that do not exist are recorded with
        /// <see cref="RestoreResult"/> semantics so a fix that creates them can be rolled back by deleting.
        /// Returns the count of paths that had a real file backup taken.
        /// </summary>
        public int Snapshot(IEnumerable<string> absolutePaths)
        {
            int backedUp = 0;
            Directory.CreateDirectory(_backupRoot);
            var index = 0;
            foreach (var path in absolutePaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                var existed = File.Exists(path);
                var entry = new BackupEntry
                {
                    OriginalPath = path,
                    BackupPath = Path.Combine(_backupRoot, (++index).ToString()),
                    ExistedBefore = existed,
                };

                if (existed)
                {
                    try
                    {
                        File.Copy(path, entry.BackupPath, overwrite: true);
                        backedUp++;
                    }
                    catch (System.Exception)
                    {
                        // A backup failure must NOT silently let a failed fix go unrestored. Skip — Restore()
                        // reports the unrestored path via ExistedBefore=false.
                        entry.ExistedBefore = false;
                    }
                }

                _entries.Add(entry);
            }
            return backedUp;
        }

        /// <summary>
        /// Restore every snapshotted path to its pre-fix state. Returns the list of paths actually restored
        /// (rewritten or deleted) and the list that could not be restored.
        /// </summary>
        public RestoreResult Restore()
        {
            var restored = new List<string>();
            var unrestored = new List<string>();
            var ok = true;

            foreach (var entry in _entries)
            {
                try
                {
                    if (entry.ExistedBefore)
                    {
                        // Rewrite / undelete case: copy the backup back over the original (creating it if the
                        // fix deleted it).
                        if (File.Exists(entry.BackupPath))
                        {
                            var dir = Path.GetDirectoryName(entry.OriginalPath);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                Directory.CreateDirectory(dir);
                            File.Copy(entry.BackupPath, entry.OriginalPath, overwrite: true);
                            restored.Add(entry.OriginalPath);
                        }
                        else
                        {
                            unrestored.Add(entry.OriginalPath);
                            ok = false;
                        }
                    }
                    else
                    {
                        // Create case: the fix created a file that did not exist before — rolling back means
                        // deleting it. If the fix never created it, the delete is a no-op.
                        if (File.Exists(entry.OriginalPath))
                        {
                            File.Delete(entry.OriginalPath);
                            restored.Add(entry.OriginalPath);
                        }
                    }
                }
                catch (System.Exception)
                {
                    unrestored.Add(entry.OriginalPath);
                    ok = false;
                }
            }

            return new RestoreResult { Success = ok, RestoredPaths = restored.ToArray(), UnrestoredPaths = unrestored.ToArray() };
        }

        /// <summary>
        /// Delete the temp backup dir. Call after a successful fix (no rollback needed) or after
        /// <see cref="Restore"/>. Best-effort: a failure to clean up the temp tree is swallowed.
        /// </summary>
        public void Discard()
        {
            try
            {
                if (Directory.Exists(_backupRoot))
                    Directory.Delete(_backupRoot, recursive: true);
            }
            catch (System.Exception)
            {
                // Swallowed — a leftover temp dir is not fatal.
            }
            _entries.Clear();
        }

        /// <summary>True when <see cref="Snapshot"/> has been called with at least one recorded path.</summary>
        public bool HasSnapshot => _entries.Count > 0;
    }
}
