#if TOOLS
#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Filesystem tool family (P4.4) — the Godot analog of Unity Open MCP's
    /// <c>list-assets</c> / <c>list-assets-of-type</c> (indexed directory listing) and
    /// <c>assets-refresh</c> (reimport/scan). Two tools:
    /// <list type="bullet">
    /// <item><description><c>godot_open_mcp_filesystem_list</c> (read-only) — one-level indexed
    /// directory listing under <c>res://</c>, paged, directories-first then files, deterministic
    /// name ordering.</description></item>
    /// <item><description><c>godot_open_mcp_filesystem_reimport</c> (mutating, default gate
    /// <c>enforce</c>) — exact-file reimport via <c>EditorFileSystem.ReimportFiles</c> or a full
    /// <c>EditorFileSystem.Scan</c>, with a bounded, truthful settle status.</description></item>
    /// </list>
    ///
    /// <para>
    /// Godot ↔ Unity mapping: <c>EditorFileSystem</c> ↔ <c>AssetDatabase</c> (the importer
    /// metadata index); a <c>res://</c> path / <c>uid://</c> ↔ a Unity asset path / GUID. The list
    /// reads the importer-assigned type and UID straight from the index (no eager resource load);
    /// reimport maps Unity's <c>AssetDatabase.Refresh</c> to Godot's
    /// <c>ReimportFiles</c>/<c>Scan</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Adapted from Godot-MCP's <c>Tool_FileSystem.List</c> /
    /// <c>Tool_FileSystem.Reimport</c></b> (behavior reference): the
    /// <c>EditorFileSystemDirectory</c> one-level walk (subdirectories then files), the
    /// importer-metadata type/UID lookup (<c>GetFileType</c> + <c>ResourceLoader.GetResourceUid</c>),
    /// the up-front exact-reimport validation, and the prime-then-drain settle state machine for
    /// the full-scan asynchronous <c>Scan()</c> are lifted from there. The structured settle DTO,
    /// paging, deterministic ordering, gate integration, and the bounded timeout contract are
    /// greenfield for this port.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): the handlers touch <see cref="EditorInterface"/>,
    /// <see cref="EditorFileSystem"/>, <see cref="FileAccess"/>, and <see cref="OS"/>. The
    /// pure-managed pieces (<see cref="FileSystemListBody"/>, <see cref="FileSystemReimportBody"/>,
    /// <see cref="FileSystemEntryData"/>) live outside this guard and are unit-tested.
    /// </summary>
    internal static class FileSystemTools
    {
        /// <summary>The MCP tool name for the filesystem lister (P4.4).</summary>
        internal const string FilesystemListToolName = "godot_open_mcp_filesystem_list";

        /// <summary>The MCP tool name for the filesystem reimporter (P4.4).</summary>
        internal const string FilesystemReimportToolName = "godot_open_mcp_filesystem_reimport";

        /// <summary>Interval between <c>IsScanning</c>/<c>IsReady</c> polls while waiting for the
        /// import pipeline to start or drain. Bounded so a wedged pipeline surfaces as
        /// <c>settled:false</c> within the caller's timeout rather than hanging the
        /// dispatch.</summary>
        internal const int ScanPollIntervalMs = 25;

        /// <summary>
        /// Poll interval expressed as a fraction of the settle budget. The prime window (waiting
        /// for an asynchronous <c>Scan()</c> to be observed running) consumes at most this fraction
        /// of the total timeout; the rest is reserved for draining. Keeps the prime from eating the
        /// whole budget when the scan starts late.
        /// </summary>
        internal const double PrimeBudgetFraction = 0.2;

        // --- registration -----------------------------------------------------------

        /// <summary>
        /// Register the filesystem tool family. P4.4 adds one read-only tool — the indexed
        /// directory lister <c>godot_open_mcp_filesystem_list</c> (group <c>filesystem</c>,
        /// default gate <c>off</c> — read-only) — and one gated mutator — the reimporter/full-scan
        /// <c>godot_open_mcp_filesystem_reimport</c> (group <c>filesystem</c>, default gate
        /// <c>enforce</c> — reimport can modify generated/import state). Registered once at plugin
        /// enable; safe to call again on re-enable (the registry is idempotent).
        /// </summary>
        internal static void RegisterFilesystemTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: FilesystemListToolName,
                isMutating: false,
                defaultGate: "off",
                group: "filesystem",
                handler: List));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: FilesystemReimportToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "filesystem",
                handler: Reimport));
        }

        // --- godot_open_mcp_filesystem_list -----------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_filesystem_list</c>. Read-only. Lists the immediate
        /// children of one <c>res://</c> directory from the editor filesystem index — directories
        /// first, then files, each group sorted by name. Adapted from Unity Open MCP's
        /// <c>list-assets</c> (adapt fidelity — bounded, paged listing) and Godot-MCP's
        /// <c>Tool_FileSystem.List</c> (behavior reference — the <c>EditorFileSystemDirectory</c>
        /// one-level walk + importer-metadata type/UID lookup).
        ///
        /// <para>
        /// No resource is loaded: the type comes from <c>EditorFileSystemDirectory.GetFileType</c>
        /// and the UID from <c>ResourceLoader.GetResourceUid</c> (both read the import index). The
        /// result carries the directory path, the full-count totals (across the whole directory),
        /// the current page of entries, and a paging cursor. A path that is not a <c>res://</c>
        /// directory, contains a parent-traversal segment, or names a file is rejected with
        /// <c>invalid_path</c>.
        /// </para>
        ///
        /// Structured failures: <c>invalid_path</c>, <c>directory_not_found</c>,
        /// <c>filesystem_unavailable</c>. Must not throw.
        /// </summary>
        internal static ToolDispatchResult List(string body)
        {
            var request = FileSystemListBody.Parse(body);

            // Normalize the directory argument (trailing-slash res:// form).
            string scope;
            if (request.HasPath)
            {
                var normalized = NormalizeDirectory(request.Path, out var dirError);
                if (normalized == null)
                    return ToolDispatchResult.Fail("invalid_path", dirError!);
                scope = normalized;
            }
            else
            {
                scope = ResourcePathNormalizer.ResScheme;
            }

            // Editor filesystem must be available and ready.
            var efs = EditorInterface.Singleton.GetResourceFilesystem();
            if (efs == null)
                return ToolDispatchResult.Fail(
                    "filesystem_unavailable",
                    "The editor filesystem is not available.");

            var rootDir = efs.GetFilesystemPath(scope);
            if (rootDir == null)
                return ToolDispatchResult.Fail(
                    "directory_not_found",
                    $"Directory '{scope}' was not found in the project filesystem.");

            // Collect directories first (sorted), then files (sorted).
            var dirs = new List<FileSystemEntryData>();
            var files = new List<FileSystemEntryData>();

            var subCount = rootDir.GetSubdirCount();
            for (int i = 0; i < subCount; i++)
            {
                var sub = rootDir.GetSubdir(i);
                if (sub == null) continue;
                var subPath = sub.GetPath(); // trailing-slash res:// form
                dirs.Add(new FileSystemEntryData
                {
                    Name = GetLeafName(subPath),
                    Path = subPath,
                    IsDirectory = true,
                });
            }

            var fileCount = rootDir.GetFileCount();
            for (int i = 0; i < fileCount; i++)
            {
                var filePath = rootDir.GetFilePath(i);
                var fileType = rootDir.GetFileType(i).ToString();
                var uidText = GodotUidForPath(filePath);
                files.Add(new FileSystemEntryData
                {
                    Name = GetLeafName(filePath),
                    Path = filePath,
                    IsDirectory = false,
                    ResourceType = string.IsNullOrEmpty(fileType) ? null : fileType,
                    Uid = string.IsNullOrEmpty(uidText) ? null : uidText,
                });
            }

            // Deterministic ordering: name ascending (ordinal) within each group.
            dirs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            return PagedListResult(scope, dirs, files, request.PageSize, request.Cursor);
        }

        // --- godot_open_mcp_filesystem_reimport -------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_filesystem_reimport</c>. Mutating (default gate
        /// <c>enforce</c>). Two modes:
        /// <list type="bullet">
        /// <item><description><b>exact files</b> — reimport the given <c>res://</c> files via
        /// <c>EditorFileSystem.ReimportFiles</c>. The entire list is validated and normalized before
        /// any file is touched; a single bad/missing entry is a clean error, never a partial
        /// effect.</description></item>
        /// <item><description><b>full scan</b> — trigger <c>EditorFileSystem.Scan</c> to pick up
        /// added/removed/changed files. <c>Scan()</c> runs asynchronously, so the handler primes
        /// (waits up to a bounded fraction of the timeout for the scan to be observed running)
        /// before draining (waits for <c>IsScanning</c> to clear).</description></item>
        /// </list>
        /// Adapted from Unity Open MCP's <c>assets-refresh</c> (adapt fidelity —
        /// <c>AssetDatabase.Refresh</c> becomes <c>ReimportFiles</c>/<c>Scan</c>) and Godot-MCP's
        /// <c>Tool_FileSystem.Reimport</c> (behavior reference — the prime-then-drain settle state
        /// machine).
        ///
        /// <para>
        /// <b>paths_hint.</b> Mandatory. For exact-file mode it must contain every requested file
        /// (the gate scope AND a handler-level guard that fires even under <c>gate:"off"</c>); for
        /// full-scan mode it must be <c>["res://"]</c> (explicit whole-project scope, no implicit
        /// gate fallback).
        /// </para>
        ///
        /// <para>
        /// <b>Settle status.</b> A timeout is a successful request with <c>settled:false</c> — the
        /// tool never falsely claims completion. <c>agentNextSteps</c> tells the caller to list/find
        /// again to observe the post-scan state.
        /// </para>
        ///
        /// Structured failures: <c>paths_hint_required</c>, <c>invalid_path</c>,
        /// <c>file_not_found</c>, <c>filesystem_unavailable</c>, <c>reimport_failed</c>,
        /// <c>invalid_timeout</c>. Must not throw.
        /// </summary>
        internal static ToolDispatchResult Reimport(string body)
        {
            var request = FileSystemReimportBody.Parse(body);

            // Handler-level paths_hint guard — fires even under gate:"off".
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "filesystem_reimport requires 'paths_hint': the exact files, or [\"res://\"] for a full scan.");

            // Editor filesystem must be available.
            var efs = EditorInterface.Singleton.GetResourceFilesystem();
            if (efs == null)
                return ToolDispatchResult.Fail(
                    "filesystem_unavailable",
                    "The editor filesystem is not available.");

            if (request.IsExactFilesMode)
                return ReimportExactFiles(efs, request.Files!, pathsHint, request.TimeoutMs);

            return ReimportFullScan(efs, pathsHint, request.TimeoutMs);
        }

        // --- exact-file reimport ----------------------------------------------------

        static ToolDispatchResult ReimportExactFiles(
            EditorFileSystem efs, string[] requestedFiles, string[]? pathsHint, int timeoutMs)
        {
            // Validate the entire list BEFORE touching anything — no partial effect.
            var normalized = new List<string>(requestedFiles.Length);
            foreach (var raw in requestedFiles)
            {
                if (!TryRequireResFileForReimport(raw, out var resPath, out var pathError))
                    return ToolDispatchResult.Fail("invalid_path", pathError);
                // De-duplicate while preserving first occurrence (deterministic).
                if (!normalized.Contains(resPath))
                    normalized.Add(resPath);
            }

            // Every requested file must be present in paths_hint (gate scope containment).
            foreach (var p in normalized)
            {
                if (!ArrayContains(pathsHint, p))
                    return ToolDispatchResult.Fail(
                        "paths_hint_required",
                        $"paths_hint must contain every requested file; missing '{p}'.");
            }

            // Each file must exist on disk.
            foreach (var p in normalized)
            {
                if (!FileAccess.FileExists(p))
                    return ToolDispatchResult.Fail(
                        "file_not_found",
                        $"No file exists at '{p}'.");
            }

            // Perform the reimport.
            Error reimportErr;
            try
            {
                reimportErr = efs.ReimportFiles(normalized.ToArray());
            }
            catch (System.Exception e)
            {
                var fail = BuildReimportFail(
                    "reimport_failed",
                    $"EditorFileSystem.ReimportFiles threw: {e.Message}",
                    "files", normalized, timeoutMs, null);
                return ToolDispatchResult.FailWithOutput(fail.Code, fail.Message, fail.Json);
            }

            if (reimportErr != Error.Ok)
            {
                var fail = BuildReimportFail(
                    "reimport_failed",
                    $"EditorFileSystem.ReimportFiles returned {reimportErr}.",
                    "files", normalized, timeoutMs, null);
                return ToolDispatchResult.FailWithOutput(fail.Code, fail.Message, fail.Json);
            }

            // ReimportFiles is synchronous; drain any tail scan still in flight (bounded).
            var settle = DrainScan(efs, timeoutMs);
            return BuildReimportSuccess("files", normalized, settle);
        }

        // --- full-scan reimport -----------------------------------------------------

        static ToolDispatchResult ReimportFullScan(
            EditorFileSystem efs, string[]? pathsHint, int timeoutMs)
        {
            // Full scan requires explicit paths_hint: ["res://"] — no implicit whole-project gate
            // fallback. Accept the trailing-slash form too.
            var hasRootHint = false;
            if (pathsHint != null)
            {
                foreach (var p in pathsHint)
                {
                    // Accept the bare project root (the only valid whole-project scope). Trailing
                    // content like "res://materials/" is a narrow scope, not a full scan.
                    if (p == ResourcePathNormalizer.ResScheme)
                    {
                        hasRootHint = true;
                        break;
                    }
                }
            }
            if (!hasRootHint)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "Full-scan mode requires paths_hint: [\"res://\"] (explicit whole-project scope).");

            // Start the scan.
            try
            {
                efs.Scan();
            }
            catch (System.Exception e)
            {
                var fail = BuildReimportFail(
                    "reimport_failed",
                    $"EditorFileSystem.Scan threw: {e.Message}",
                    "full_scan", null, timeoutMs, null);
                return ToolDispatchResult.FailWithOutput(fail.Code, fail.Message, fail.Json);
            }

            // Prime: wait up to a bounded fraction of the timeout for the async scan to be observed
            // running. A naive `while (IsScanning())` exits immediately when Scan() hasn't begun
            // yet and would falsely report "settled".
            var primeBudget = System.Math.Max(
                ScanPollIntervalMs, (int)(timeoutMs * PrimeBudgetFraction));
            var primed = false;
            var primeElapsed = 0;
            while (primeElapsed < primeBudget)
            {
                if (efs.IsScanning())
                {
                    primed = true;
                    break;
                }
                OS.DelayMsec(ScanPollIntervalMs);
                primeElapsed += ScanPollIntervalMs;
            }

            float? progress = SafeGetScanningProgress(efs);

            if (!primed)
            {
                // The scan never started within the prime window — report it explicitly rather than
                // silently claiming "settled".
                var settle = new FilesystemSettleStatus
                {
                    Mode = "full_scan",
                    ScanStarted = false,
                    Settled = false,
                    ScanningProgress = progress,
                    ElapsedMs = primeElapsed,
                    Reason = "scan_did_not_start",
                };
                return BuildReimportSuccess(null, null, settle);
            }

            // Drain: wait for IsScanning to clear within the remaining budget.
            var drainBudget = System.Math.Max(ScanPollIntervalMs, timeoutMs - primeElapsed);
            var drainElapsed = 0;
            while (efs.IsScanning() && drainElapsed < drainBudget)
            {
                OS.DelayMsec(ScanPollIntervalMs);
                drainElapsed += ScanPollIntervalMs;
            }

            var settled = !efs.IsScanning();
            progress = SafeGetScanningProgress(efs);
            var drainSettle = new FilesystemSettleStatus
            {
                Mode = "full_scan",
                ScanStarted = true,
                Settled = settled,
                ScanningProgress = progress,
                ElapsedMs = primeElapsed + drainElapsed,
                Reason = settled ? null : "timeout",
            };
            return BuildReimportSuccess(null, null, drainSettle);
        }

        /// <summary>
        /// Drain any scan currently in flight (used after the synchronous <c>ReimportFiles</c>).
        /// Bounded by <paramref name="timeoutMs"/>. Returns the settle status.
        /// </summary>
        static FilesystemSettleStatus DrainScan(EditorFileSystem efs, int timeoutMs)
        {
            var elapsed = 0;
            while (efs.IsScanning() && elapsed < timeoutMs)
            {
                OS.DelayMsec(ScanPollIntervalMs);
                elapsed += ScanPollIntervalMs;
            }
            var settled = !efs.IsScanning();
            return new FilesystemSettleStatus
            {
                Mode = "files",
                ScanStarted = true, // ReimportFiles ran synchronously; a tail scan may be in flight.
                Settled = settled,
                ScanningProgress = SafeGetScanningProgress(efs),
                ElapsedMs = elapsed,
                Reason = settled ? null : "timeout",
            };
        }

        // --- shared helpers ---------------------------------------------------------

        /// <summary>res:// → uid:// text for a file, or empty when the file has no registered uid.
        /// Main-thread only (touches <see cref="ResourceLoader"/>). Lifted from Godot-MCP's
        /// <c>Tool_FileSystem.List</c>.</summary>
        static string GodotUidForPath(string resPath)
        {
            try
            {
                var id = ResourceLoader.GetResourceUid(resPath);
                return id == ResourceUid.InvalidId ? string.Empty : ResourceUid.IdToText(id);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Leaf (last segment) of a res:// path; for directories the trailing slash is
        /// stripped first. Lifted from Godot-MCP's <c>GetLeafName</c>.</summary>
        static string GetLeafName(string resPath)
        {
            var trimmed = resPath.TrimEnd('/');
            var idx = trimmed.LastIndexOf('/');
            return idx < 0 ? trimmed : trimmed.Substring(idx + 1);
        }

        /// <summary>
        /// Normalize a user-supplied directory argument to a trailing-slash <c>res://</c> path.
        /// Returns null + an error message when the input is not a valid <c>res://</c> directory
        /// (rejects non-<c>res://</c>, parent traversal, and file paths — a trailing slash is the
        /// directory signal but is also added when missing). Mirrors
        /// <c>ResourceTools.NormalizeDirectoryScope</c> but lives here so the filesystem family is
        /// self-contained.
        /// </summary>
        static string? NormalizeDirectory(string? raw, out string? error)
        {
            var dir = (raw ?? string.Empty).Trim();
            if (dir.Length == 0 || dir == ResourcePathNormalizer.ResScheme)
            {
                error = null;
                return ResourcePathNormalizer.ResScheme;
            }

            if (!dir.StartsWith(ResourcePathNormalizer.ResScheme, System.StringComparison.Ordinal))
            {
                error = $"path must be a '{ResourcePathNormalizer.ResScheme}' directory (or empty for the project root); got '{raw}'.";
                return null;
            }

            // Reject parent traversal so res://a/../b cannot bypass a guard.
            foreach (var segment in dir.Split('/'))
            {
                if (segment == "..")
                {
                    error = $"path must not contain a '..' parent-directory segment; got '{raw}'.";
                    return null;
                }
            }

            if (!dir.EndsWith("/", System.StringComparison.Ordinal))
                dir += "/";

            error = null;
            return dir;
        }

        /// <summary>
        /// Validate that <paramref name="raw"/> is a <c>res://</c> file path (not a directory, not
        /// the bare root). Returns the trimmed path on success or an error message on failure. Does
        /// NOT touch the filesystem — existence is checked separately. Unlike the resource family's
        /// <c>RequireResFilePath</c>, this does NOT restrict the extension (any res:// file can be
        /// reimported, not just <c>.tres</c>/<c>.res</c>).
        /// </summary>
        static bool TryRequireResFileForReimport(string? raw, out string normalized, out string? error)
        {
            var p = (raw ?? string.Empty).Trim();
            if (!ResourcePathNormalizer.IsResPath(p))
            {
                normalized = string.Empty;
                error = $"Path must be a '{ResourcePathNormalizer.ResScheme}' path; got '{raw}'.";
                return false;
            }
            if (p == ResourcePathNormalizer.ResScheme)
            {
                normalized = string.Empty;
                error = $"Path must name a file under '{ResourcePathNormalizer.ResScheme}', not the bare project root; got '{raw}'.";
                return false;
            }
            if (p.EndsWith("/", System.StringComparison.Ordinal))
            {
                normalized = string.Empty;
                error = $"Path must be a file, not a directory (trailing slash); got '{raw}'.";
                return false;
            }
            foreach (var segment in p.Split('/'))
            {
                if (segment == "..")
                {
                    normalized = string.Empty;
                    error = $"Path must not contain a '..' parent-directory segment; got '{raw}'.";
                    return false;
                }
            }
            normalized = p;
            error = null;
            return true;
        }

        /// <summary>Read <c>GetScanningProgress()</c> defensively (returns null on any error or
        /// when the scanner is idle).</summary>
        static float? SafeGetScanningProgress(EditorFileSystem efs)
        {
            try
            {
                return efs.IsScanning() ? efs.GetScanningProgress() : (float?)null;
            }
            catch
            {
                return null;
            }
        }

        static bool ArrayContains(string[]? arr, string value)
        {
            if (arr == null) return false;
            foreach (var a in arr)
            {
                if (a == value) return true;
            }
            return false;
        }

        // --- result builders --------------------------------------------------------

        /// <summary>
        /// Build the paged list result envelope. Directories come first, then files; each group is
        /// already sorted by name. <see cref="directoryCount"/>/<see cref="fileCount"/> describe the
        /// full directory, while the entries array is the current page. The cursor is the index of
        /// the next entry across the combined (dirs ++ files) stream.
        /// </summary>
        static ToolDispatchResult PagedListResult(
            string scope, List<FileSystemEntryData> dirs, List<FileSystemEntryData> files,
            int pageSize, string? cursor)
        {
            var total = dirs.Count + files.Count;
            var skip = ParseCursor(cursor);
            if (skip < 0) skip = 0;

            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"path\":").Append(BridgeJson.EscapeString(scope));
            sb.Append(",\"directoryCount\":").Append(dirs.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"fileCount\":").Append(files.Count.ToString(CultureInfo.InvariantCulture));

            sb.Append(",\"entries\":[");
            var take = System.Math.Min(pageSize, System.Math.Max(0, total - skip));
            var emitted = 0;
            // The combined stream is dirs ++ files; index into it by offsetting.
            for (int i = 0; i < take; i++)
            {
                var idx = skip + i;
                FileSystemEntryData entry;
                if (idx < dirs.Count)
                    entry = dirs[idx];
                else
                    entry = files[idx - dirs.Count];
                if (emitted > 0) sb.Append(',');
                entry.AppendJsonTo(sb);
                emitted++;
            }
            sb.Append(']');

            var hasMore = skip + take < total;
            var nextCursor = hasMore
                ? (skip + take).ToString(CultureInfo.InvariantCulture)
                : null;
            sb.Append(",\"pagination\":{\"nextCursor\":").Append(BridgeJson.EscapeString(nextCursor));
            sb.Append(",\"hasMore\":").Append(hasMore ? "true" : "false").Append('}');
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        static ToolDispatchResult BuildReimportSuccess(
            string? mode, List<string>? requestedFiles, FilesystemSettleStatus settle)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            if (mode != null)
                sb.Append("\"mode\":").Append(BridgeJson.EscapeString(mode)).Append(',');
            else
                sb.Append("\"mode\":").Append(BridgeJson.EscapeString(settle.Mode)).Append(',');

            if (requestedFiles != null)
            {
                sb.Append("\"requestedFiles\":[");
                for (int i = 0; i < requestedFiles.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BridgeJson.EscapeString(requestedFiles[i]));
                }
                sb.Append("],");
                sb.Append("\"reimportedCount\":")
                  .Append(requestedFiles.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
            }
            else
            {
                sb.Append("\"requestedFiles\":[],");
                sb.Append("\"reimportedCount\":0,");
            }

            settle.AppendJsonTo(sb);
            sb.Append(",\"reimported\":true}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        static (string Code, string Message, string Json) BuildReimportFail(
            string code, string message,
            string mode, List<string>? requestedFiles, int timeoutMs, FilesystemSettleStatus? settle)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"mode\":").Append(BridgeJson.EscapeString(mode)).Append(',');

            if (requestedFiles != null)
            {
                sb.Append("\"requestedFiles\":[");
                for (int i = 0; i < requestedFiles.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BridgeJson.EscapeString(requestedFiles[i]));
                }
                sb.Append("],");
                sb.Append("\"reimportedCount\":0,");
            }
            else
            {
                sb.Append("\"requestedFiles\":[],");
                sb.Append("\"reimportedCount\":0,");
            }

            if (settle != null)
                settle.AppendJsonTo(sb);
            else
                sb.Append("\"settle\":{\"scanStarted\":false,\"settled\":false,\"scanningProgress\":null,\"elapsedMs\":0,\"reason\":\"not_attempted\"}");

            sb.Append(",\"reimported\":false}");
            return (code, message, sb.ToString());
        }

        /// <summary>Parse the opaque paging cursor (a non-negative index). Returns -1 when
        /// absent/invalid.</summary>
        static int ParseCursor(string? cursor)
        {
            if (string.IsNullOrWhiteSpace(cursor)) return -1;
            if (int.TryParse(cursor.AsSpan().Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0)
                return idx;
            return -1;
        }

        // --- settle DTO -------------------------------------------------------------

        /// <summary>Bounded, truthful settle status for a reimport/full-scan operation. The settle
        /// poll may time out — <see cref="Settled"/> is false in that case and <see cref="Reason"/>
        /// explains why. The tool never falsely claims completion.</summary>
        internal sealed class FilesystemSettleStatus
        {
            /// <summary><c>"files"</c> or <c>"full_scan"</c> — which reimport mode produced this
            /// settle status.</summary>
            public string Mode { get; set; } = "files";

            /// <summary>True when the reimport/scan was actually started (the settle poll may still
            /// time out before completion).</summary>
            public bool ScanStarted { get; set; }

            /// <summary>True when the import pipeline reported idle within the settle budget. False
            /// on timeout or when the scan never started.</summary>
            public bool Settled { get; set; }

            /// <summary>Editor scan progress (0..1) when available and the scanner is still busy;
            /// null otherwise.</summary>
            public float? ScanningProgress { get; set; }

            /// <summary>Elapsed milliseconds spent in the settle poll.</summary>
            public int ElapsedMs { get; set; }

            /// <summary>Diagnostic reason when <see cref="Settled"/> is false (e.g.
            /// <c>timeout</c>, <c>scan_did_not_start</c>, <c>not_attempted</c>). Null on
            /// success.</summary>
            public string? Reason { get; set; }

            internal void AppendJsonTo(StringBuilder sb)
            {
                sb.Append("\"settle\":{");
                sb.Append("\"scanStarted\":").Append(ScanStarted ? "true" : "false").Append(',');
                sb.Append("\"settled\":").Append(Settled ? "true" : "false").Append(',');
                if (ScanningProgress.HasValue)
                {
                    sb.Append("\"scanningProgress\":")
                      .Append(ScanningProgress.Value.ToString("0.##", CultureInfo.InvariantCulture)).Append(',');
                }
                else
                {
                    sb.Append("\"scanningProgress\":null,");
                }
                sb.Append("\"elapsedMs\":").Append(ElapsedMs.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"reason\":").Append(BridgeJson.EscapeString(Reason));
                sb.Append('}');
            }
        }
    }
}
#endif
