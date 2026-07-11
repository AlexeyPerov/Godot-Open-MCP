#if TOOLS
#nullable enable
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// File-level move/delete for the resource tool family (P4.3). Unlike
    /// <c>resource_create</c>/<c>resource_modify</c> (which load/instantiate a <see cref="Resource"/>
    /// and persist via <see cref="ResourceSaver"/>), move and delete operate on the on-disk file
    /// directly through <see cref="DirAccess"/>/<see cref="FileAccess"/> — a resource is moved or
    /// removed as a file, not re-saved. This mirrors the Godot-MCP reference
    /// (<c>Tool_Resource.Move</c>/<c>Delete</c>) and Unity Open MCP's
    /// <c>AssetsTools.Move</c>/<c>Delete</c> (which delegate to <c>AssetDatabase.MoveAsset</c>/
    /// <c>DeleteAsset</c>).
    ///
    /// <para>
    /// <b>Sidecar handling.</b> Imported resources carry a <c>.import</c> sidecar next to the source
    /// file. Move/delete must relocate/remove the sidecar alongside the primary file so the editor
    /// filesystem index does not reference a stale path. The primary file is moved/removed FIRST; the
    /// sidecar SECOND. If the sidecar operation fails, the result exposes the partial state (rollback
    /// for move; orphan report for delete) so the agent can recover — the contract never claims
    /// atomicity across two OS-level operations.
    /// </para>
    ///
    /// <para>
    /// <b>Filesystem reconciliation.</b> After any operation (success or partial failure) the editor
    /// filesystem is rescanned so the index reflects on-disk reality. The scan runs in a
    /// <c>finally</c> block and the settle status is reported in the result.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): touches <see cref="DirAccess"/>, <see cref="FileAccess"/>,
    /// <see cref="EditorInterface"/>. The pure-managed body parsers (<see cref="ResourceMoveBody"/>,
    /// <see cref="ResourceDeleteBody"/>) and <see cref="ResourcePathNormalizer"/> live outside this
    /// guard and are unit-tested.
    ///
    /// <para>
    /// <b>Adapted from Godot-MCP's <c>Tool_Resource.Move</c>/<c>Delete</c></b> (behavior reference):
    /// the <c>DirAccess.RenameAbsolute</c>/<c>RemoveAbsolute</c> + sidecar <c>try/finally</c>-scan
    /// pattern is lifted from there. Unity's <c>AssetDatabase</c>-managed move/delete (which handles
    /// <c>.meta</c> atomically) becomes explicit file + <c>.import</c> handling because Godot does
    /// not offer a single engine API that moves both in one call.
    /// </para>
    /// </summary>
    internal static class ResourceFileOperations
    {
        /// <summary>Maximum time to wait for the editor filesystem scan to settle, in milliseconds.
        /// Bounded so a wedged import pipeline surfaces as <c>filesystem_scan_timeout</c> instead of
        /// hanging the dispatch.</summary>
        internal const int ScanSettleTimeoutMs = 5000;

        /// <summary>Interval between <c>IsReady</c> polls while waiting for the scan to settle.</summary>
        internal const int ScanPollIntervalMs = 50;

        // --- move -------------------------------------------------------------------

        /// <summary>
        /// Move a resource file (and its <c>.import</c> sidecar when present) from
        /// <paramref name="sourcePath"/> to <paramref name="destinationPath"/>. Both paths must be
        /// normalized <c>res://</c> file paths. The destination parent directory is created when
        /// missing.
        ///
        /// <para>
        /// Returns a <see cref="MoveResult"/> with the before/after identity, whether the sidecar was
        /// moved, the filesystem-scan settle status, and — on partial failure — the observed
        /// existence of each path plus recovery guidance. Never throws.
        /// </para>
        /// </summary>
        internal static MoveResult Move(string sourcePath, string destinationPath)
        {
            var before = ResourceTools.BuildIdentityForFileOps(sourcePath);
            var sidecarMoved = false;
            string? partialError = null;
            string? rollbackNote = null;
            var rolledBack = false;

            // 1. Ensure the destination parent directory exists (DirAccess.Rename does not create it).
            var dstDir = ParentDirectory(destinationPath);
            EnsureDirectoryExists(dstDir);

            // 2. Move the primary file.
            Error renameErr;
            try
            {
                renameErr = DirAccess.RenameAbsolute(sourcePath, destinationPath);
            }
            catch (System.Exception e)
            {
                return new MoveResult
                {
                    Success = false,
                    Before = before,
                    ErrorCode = "resource_move_failed",
                    ErrorMessage = $"DirAccess.RenameAbsolute threw moving '{sourcePath}' to '{destinationPath}': {e.Message}",
                    FilesystemScan = ScanAndSettle(),
                };
            }

            if (renameErr != Error.Ok)
            {
                return new MoveResult
                {
                    Success = false,
                    Before = before,
                    ErrorCode = "resource_move_failed",
                    ErrorMessage = $"DirAccess.RenameAbsolute returned {renameErr} moving '{sourcePath}' to '{destinationPath}'.",
                    FilesystemScan = ScanAndSettle(),
                };
            }

            // 3. Move the sidecar when present.
            var srcImport = sourcePath + ".import";
            var dstImport = destinationPath + ".import";
            if (FileAccess.FileExists(srcImport))
            {
                try
                {
                    var sidecarErr = DirAccess.RenameAbsolute(srcImport, dstImport);
                    if (sidecarErr == Error.Ok)
                    {
                        sidecarMoved = true;
                    }
                    else
                    {
                        // Primary moved but sidecar failed → attempt rollback of the primary file.
                        partialError = $"Sidecar move returned {sidecarErr} moving '{srcImport}' to '{dstImport}'.";
                        rolledBack = TryRollbackPrimary(destinationPath, sourcePath, out rollbackNote);
                    }
                }
                catch (System.Exception e)
                {
                    partialError = $"DirAccess.RenameAbsolute threw moving sidecar '{srcImport}' to '{dstImport}': {e.Message}";
                    rolledBack = TryRollbackPrimary(destinationPath, sourcePath, out rollbackNote);
                }
            }
            else
            {
                // No sidecar — the move is complete.
                sidecarMoved = false;
            }

            // 4. Always reconcile the filesystem.
            var scan = ScanAndSettle();

            if (partialError != null)
            {
                return new MoveResult
                {
                    Success = false,
                    Before = before,
                    ErrorCode = rolledBack ? "resource_move_failed" : "resource_move_partial",
                    ErrorMessage = rolledBack
                        ? $"Primary file moved then sidecar move failed; primary rolled back. {partialError} {rollbackNote}"
                        : $"Primary file moved but sidecar move failed and rollback was not possible — the project is in a partial state. {partialError} {rollbackNote}",
                    SidecarMoved = sidecarMoved,
                    FilesystemScan = scan,
                    SourceExistsAfter = FileAccess.FileExists(sourcePath),
                    DestinationExistsAfter = FileAccess.FileExists(destinationPath),
                    RolledBack = rolledBack,
                };
            }

            // 5. Success — build identity at destination.
            var after = ResourceTools.BuildIdentityForFileOps(destinationPath);
            return new MoveResult
            {
                Success = true,
                Before = before,
                After = after,
                SidecarMoved = sidecarMoved,
                FilesystemScan = scan,
            };
        }

        /// <summary>
        /// Attempt to move the primary file back to the source path after a sidecar-move failure.
        /// Returns true when the rollback succeeded; <paramref name="note"/> carries a diagnostic.
        /// </summary>
        static bool TryRollbackPrimary(string dst, string src, out string note)
        {
            try
            {
                var err = DirAccess.RenameAbsolute(dst, src);
                if (err == Error.Ok)
                {
                    note = $"Rolled back primary file to '{src}'.";
                    return true;
                }
                note = $"Rollback of primary file to '{src}' returned {err} — the file remains at '{dst}'.";
                return false;
            }
            catch (System.Exception e)
            {
                note = $"Rollback of primary file to '{src}' threw: {e.Message} — the file remains at '{dst}'.";
                return false;
            }
        }

        // --- delete -----------------------------------------------------------------

        /// <summary>
        /// Delete a resource file (and its <c>.import</c> sidecar when present) at
        /// <paramref name="resourcePath"/>. The path must be a normalized <c>res://</c> file path.
        ///
        /// <para>
        /// Returns a <see cref="DeleteResult"/> with the pre-delete identity snapshot, whether the
        /// sidecar was deleted, the filesystem-scan settle status, and — on partial failure — the
        /// orphan sidecar path plus recovery guidance. Never throws.
        /// </para>
        /// </summary>
        internal static DeleteResult Delete(string resourcePath)
        {
            // Snapshot identity BEFORE removal (path/uid/type are unrecoverable afterwards).
            var snapshot = ResourceTools.BuildIdentityForFileOps(resourcePath);
            var sidecarDeleted = false;
            string? partialError = null;

            // 1. Remove the primary file.
            Error removeErr;
            try
            {
                removeErr = DirAccess.RemoveAbsolute(resourcePath);
            }
            catch (System.Exception e)
            {
                return new DeleteResult
                {
                    Success = false,
                    Resource = snapshot,
                    ErrorCode = "resource_delete_failed",
                    ErrorMessage = $"DirAccess.RemoveAbsolute threw removing '{resourcePath}': {e.Message}",
                    FilesystemScan = ScanAndSettle(),
                };
            }

            if (removeErr != Error.Ok)
            {
                return new DeleteResult
                {
                    Success = false,
                    Resource = snapshot,
                    ErrorCode = "resource_delete_failed",
                    ErrorMessage = $"DirAccess.RemoveAbsolute returned {removeErr} removing '{resourcePath}'.",
                    FilesystemScan = ScanAndSettle(),
                };
            }

            // 2. Remove the sidecar when present.
            var importPath = resourcePath + ".import";
            if (FileAccess.FileExists(importPath))
            {
                try
                {
                    var sidecarErr = DirAccess.RemoveAbsolute(importPath);
                    if (sidecarErr == Error.Ok)
                    {
                        sidecarDeleted = true;
                    }
                    else
                    {
                        partialError = $"Sidecar removal returned {sidecarErr} removing '{importPath}'.";
                    }
                }
                catch (System.Exception e)
                {
                    partialError = $"DirAccess.RemoveAbsolute threw removing sidecar '{importPath}': {e.Message}";
                }
            }
            else
            {
                sidecarDeleted = false;
            }

            // 3. Always reconcile the filesystem.
            var scan = ScanAndSettle();

            if (partialError != null)
            {
                return new DeleteResult
                {
                    Success = false,
                    Resource = snapshot,
                    ErrorCode = "resource_delete_partial",
                    ErrorMessage = $"Primary file removed but sidecar removal failed — an orphan '.import' sidecar remains. {partialError}",
                    SidecarDeleted = sidecarDeleted,
                    OrphanSidecarPath = importPath,
                    FilesystemScan = scan,
                };
            }

            return new DeleteResult
            {
                Success = true,
                Resource = snapshot,
                SidecarDeleted = sidecarDeleted,
                FilesystemScan = scan,
            };
        }

        // --- shared helpers ---------------------------------------------------------

        /// <summary>Derive the trailing-slash parent directory of a <c>res://</c> file path
        /// (e.g. <c>res://a/b.tres</c> → <c>res://a/</c>). Root-level files yield
        /// <c>res://</c>.</summary>
        static string ParentDirectory(string resFilePath)
        {
            var lastSlash = resFilePath.LastIndexOf('/');
            // res:// always has at least the scheme slash, so lastSlash >= the slash in "res://".
            return lastSlash >= 0 ? resFilePath.Substring(0, lastSlash + 1) : ResourcePathNormalizer.ResScheme;
        }

        /// <summary>Create <paramref name="resDir"/> (recursively) when it does not already exist.
        /// Mirrors Godot-MCP's <c>EditorToolGuards.EnsureDirectoryExists</c>.</summary>
        static void EnsureDirectoryExists(string resDir)
        {
            if (!DirAccess.DirExistsAbsolute(resDir))
                DirAccess.MakeDirRecursiveAbsolute(resDir);
        }

        /// <summary>
        /// Trigger a full editor filesystem scan and poll until the filesystem reports ready (or the
        /// settle budget expires). Returns the settle status metadata for the result envelope.
        /// </summary>
        static FilesystemScanStatus ScanAndSettle()
        {
            var status = new FilesystemScanStatus();
            try
            {
                var efs = EditorInterface.Singleton.GetResourceFilesystem();
                if (efs == null)
                {
                    status.Settled = false;
                    status.Reason = "editor_filesystem_unavailable";
                    return status;
                }

                efs.Scan();
                status.ScanTriggered = true;

                var elapsed = 0;
                while (elapsed < ScanSettleTimeoutMs)
                {
                    var fs = efs.GetFilesystem();
                    if (fs != null && fs.IsReady())
                    {
                        status.Settled = true;
                        status.SettledMs = elapsed;
                        return status;
                    }
                    OS.DelayMsec(ScanPollIntervalMs);
                    elapsed += ScanPollIntervalMs;
                }

                status.Settled = false;
                status.SettledMs = elapsed;
                status.Reason = "timeout";
            }
            catch (System.Exception e)
            {
                status.Settled = false;
                status.Reason = $"scan_exception: {e.Message}";
            }
            return status;
        }

        // --- result types -----------------------------------------------------------

        /// <summary>Outcome metadata for the editor filesystem scan after a move/delete.</summary>
        internal sealed class FilesystemScanStatus
        {
            /// <summary>True when the scan was triggered and the filesystem reported ready within the
            /// settle budget.</summary>
            public bool Settled { get; set; }

            /// <summary>True when <c>EditorFileSystem.Scan()</c> was invoked (the settle poll may still
            /// time out).</summary>
            public bool ScanTriggered { get; set; }

            /// <summary>Elapsed milliseconds waiting for the settle poll. Zero when settled on the
            /// first check.</summary>
            public int SettledMs { get; set; }

            /// <summary>Diagnostic reason when <see cref="Settled"/> is false (e.g. <c>timeout</c>,
            /// <c>editor_filesystem_unavailable</c>, <c>scan_exception: ...</c>).</summary>
            public string? Reason { get; set; }

            internal void AppendJsonTo(StringBuilder sb)
            {
                sb.Append('{');
                sb.Append("\"settled\":").Append(Settled ? "true" : "false").Append(',');
                sb.Append("\"scanTriggered\":").Append(ScanTriggered ? "true" : "false").Append(',');
                sb.Append("\"settledMs\":").Append(SettledMs.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"reason\":").Append(BridgeJson.EscapeString(Reason));
                sb.Append('}');
            }
        }

        /// <summary>Result of a file-level move operation.</summary>
        internal sealed class MoveResult
        {
            public bool Success { get; set; }
            public ResourceIdentity? Before { get; set; }
            public ResourceIdentity? After { get; set; }
            public bool SidecarMoved { get; set; }
            public FilesystemScanStatus FilesystemScan { get; set; } = new();
            public string? ErrorCode { get; set; }
            public string? ErrorMessage { get; set; }
            // Partial-state diagnostics (populated only when Success is false).
            public bool SourceExistsAfter { get; set; }
            public bool DestinationExistsAfter { get; set; }
            public bool RolledBack { get; set; }
        }

        /// <summary>Result of a file-level delete operation.</summary>
        internal sealed class DeleteResult
        {
            public bool Success { get; set; }
            public ResourceIdentity Resource { get; set; } = new();
            public bool SidecarDeleted { get; set; }
            public FilesystemScanStatus FilesystemScan { get; set; } = new();
            public string? ErrorCode { get; set; }
            public string? ErrorMessage { get; set; }
            // Partial-state diagnostic (populated only when the sidecar could not be removed).
            public string? OrphanSidecarPath { get; set; }
        }
    }
}
#endif
