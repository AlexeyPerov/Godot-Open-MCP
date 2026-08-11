#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Reserialize tool family (P17.2) — the Godot analog of Unity Open MCP's
    /// <c>reserialize</c>. One live, mutating tool: <c>godot_open_mcp_reserialize</c>. Round-trips
    /// writable Godot assets (<c>.tres</c>/<c>.tscn</c>/<c>.res</c>) through
    /// <c>ResourceLoader.Load</c> + <c>ResourceSaver.Save</c> so on-disk drift (hand-edits, stale
    /// format, missing fields) is normalized through the first-party serializer.
    ///
    /// <para>
    /// Registered under group <c>asset-intelligence</c> (the first live member of that group — the
    /// P13/P17.1 peers are offline readers). Route <c>live</c>; default gate <c>enforce</c>. Like
    /// every mutator in this bridge, the handler validates <c>paths_hint</c> itself so the guard
    /// fires even under <c>gate:"off"</c>, and the dispatcher routes the call through
    /// <c>GatePolicy.Execute</c> (checkpoint → mutate → validate → delta) for any non-off mode.
    /// </para>
    ///
    /// <para>
    /// <b>Adapted from Unity Open MCP's <c>reserialize</c></b> (adapt fidelity for the path-list
    /// input + gate surface): Unity's <c>AssetDatabase.ForceReserializeAssets</c> becomes Godot's
    /// load+save round-trip; Unity's all-or-nothing pre-flight becomes a per-path status report
    /// (the P17.2 spec asks for per-path success/failure). Imported/generated resources (those with
    /// a <c>.import</c> sidecar) are rejected per-path with <c>not_writable</c> — only
    /// hand-authored <c>.tres</c>/<c>.tscn</c>/<c>.res</c> files are reserializable.
    /// </para>
    ///
    /// <para>
    /// Folder expansion (spec design decision #1): a <c>paths</c> entry may be a <c>res://</c>
    /// directory, which is walked recursively for writable resource files. The expanded target set
    /// is deduped. <c>paths_hint</c> MUST contain every expanded target file (the gate validates
    /// exactly what was rewritten — consistent with <c>paths_hint = the target paths</c> and the
    /// no-whole-project-fallback rule); if it does not, the call fails with
    /// <c>paths_hint_required</c> listing the missing targets so the agent can retry.
    /// </para>
    /// </summary>
    internal static class ReserializeTools
    {
        /// <summary>The MCP tool name (ADR-003 <c>godot_open_mcp_*</c> prefix).</summary>
        internal const string ReserializeToolName = "godot_open_mcp_reserialize";

        /// <summary>
        /// Extensions reserialize will round-trip: Godot text resource (<c>.tres</c>), text scene
        /// (<c>.tscn</c>), and binary resource (<c>.res</c>). Binary scene (<c>.scn</c>) is omitted
        /// (rare in hand-authored projects). Imported assets are rejected by the
        /// <c>.import</c>-sidecar check regardless of extension.
        /// </summary>
        private static readonly string[] ReserializableExtensions = { ".tres", ".tscn", ".res" };

        /// <summary>
        /// Hard cap on the number of expanded targets in one call — bounds the fan-out when a large
        /// directory is passed. Mirrors <see cref="ReserializeBody.MaxPaths"/> (folder expansion can
        /// multiply a short input into many files, so the cap is enforced after expansion).
        /// </summary>
        private const int MaxTargets = 500;

        // --- registration ----------------------------------------------------------

        /// <summary>
        /// Register the reserialize tool family. Idempotent — re-register replaces the entry (the
        /// plugin calls this from <c>_EnterTree</c> on every reload).
        /// </summary>
        internal static void RegisterReserializeTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ReserializeToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "asset-intelligence",
                handler: Reserialize));
        }

        // --- handler ---------------------------------------------------------------

        /// <summary>
        /// <c>godot_open_mcp_reserialize</c> handler. Round-trips each expanded target through
        /// <c>ResourceLoader.Load</c> + <c>ResourceSaver.Save</c> and reports per-path status.
        ///
        /// <para>
        /// Error codes: <c>missing_parameter</c> (no <c>paths</c>),
        /// <c>paths_hint_required</c> (hint missing or does not cover every expanded target),
        /// <c>filesystem_unavailable</c> (editor import scan not finished),
        /// <c>too_many_targets</c> (expanded set exceeds the cap). Per-path load/save failures are
        /// reported in the <c>results</c> array, not as a dispatch failure. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Reserialize(string body)
        {
            var request = ReserializeBody.Parse(body);

            if (!request.HasPaths)
            {
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "reserialize requires a non-empty 'paths' array of res:// file or folder paths.");
            }

            // Handler-level paths_hint guard — fires even under gate:"off".
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "paths_hint is required and must contain every target path being reserialized.");

            // Build a hint set for O(1) containment probes.
            var hintSet = new HashSet<string>(pathsHint, StringComparer.Ordinal);

            // Expand paths entries into concrete target files, recording rejects (not_found /
            // unsupported) so they surface in the per-path result rather than silently dropping.
            var targets = new List<string>();
            var rejects = new List<(string path, string status, string error)>();
            foreach (var raw in request.Paths)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                ExpandEntry(raw.Trim(), targets, rejects);
            }

            // Cap the expanded fan-out.
            if (targets.Count > MaxTargets)
            {
                return ToolDispatchResult.Fail(
                    "too_many_targets",
                    $"reserialize expanded to {targets.Count} target files; the cap is {MaxTargets}. " +
                    "Pass a smaller set of paths or a narrower folder.");
            }

            // Every expanded target must be in paths_hint — the gate validates exactly what was
            // rewritten. Surface the missing ones so the agent can retry with the right scope.
            if (targets.Count > 0)
            {
                var missing = new List<string>();
                foreach (var t in targets)
                    if (!hintSet.Contains(t)) missing.Add(t);
                if (missing.Count > 0)
                {
                    var limit = missing.Count > 20 ? 20 : missing.Count;
                    var sb = new StringBuilder();
                    for (int i = 0; i < limit; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(missing[i]);
                    }
                    if (missing.Count > limit) sb.Append($" … (+{missing.Count - limit} more)");
                    return ToolDispatchResult.Fail(
                        "paths_hint_required",
                        $"paths_hint must contain every expanded target path; missing {missing.Count}: {sb}.");
                }
            }

            // Filesystem must be ready (the import scan owns the resource cache).
            var efs = EditorInterface.Singleton.GetResourceFilesystem();
            if (efs == null || !efs.GetFilesystem().IsReady())
                return ToolDispatchResult.Fail(
                    "filesystem_unavailable",
                    "The editor filesystem is not ready; wait for the import scan to finish and retry.");

            // Per-target round-trip.
            int reserialized = 0;
            var results = new List<(string path, string status, bool? normalized, string? error)>();
            foreach (var reject in rejects)
                results.Add((reject.path, reject.status, null, reject.error));

            foreach (var resPath in targets)
            {
                // Reject imported/generated resources whose source of truth is external. Only
                // hand-authored .tres/.tscn/.res are writable (mirrors resource_modify).
                if (IsImportedResource(resPath))
                {
                    results.Add((resPath, "not_writable", null,
                        "imported/generated resource — source of truth is external (.import sidecar)."));
                    continue;
                }

                // Snapshot on-disk bytes before the save so we can report whether the round-trip
                // actually normalized the content (a no-op save leaves bytes identical).
                byte[] before = ReadBytesSafe(resPath);

                // Load.
                Resource resource;
                try
                {
                    resource = ResourceLoader.Load(resPath);
                }
                catch (Exception e)
                {
                    results.Add((resPath, "load_failed", null,
                        $"ResourceLoader.Load threw: {e.Message}"));
                    continue;
                }
                if (resource == null)
                {
                    results.Add((resPath, "load_failed", null,
                        "ResourceLoader.Load returned null."));
                    continue;
                }

                // Save (round-trip through the first-party serializer).
                Error saveErr;
                try
                {
                    saveErr = ResourceSaver.Save(resource, resPath);
                }
                catch (Exception e)
                {
                    results.Add((resPath, "save_failed", null,
                        $"ResourceSaver.Save threw: {e.Message}"));
                    continue;
                }
                if (saveErr != Error.Ok)
                {
                    results.Add((resPath, "save_failed", null,
                        $"ResourceSaver.Save returned {saveErr}."));
                    continue;
                }

                // Notify the editor filesystem so cached copies are refreshed (non-fatal).
                try { efs.UpdateFile(resPath); }
                catch { /* UpdateFile failure is non-fatal. */ }

                // Detect normalization: compare on-disk bytes before vs after.
                byte[] after = ReadBytesSafe(resPath);
                bool? normalized = BytesEqual(before, after) ? false : (before == null ? (bool?)null : true);

                results.Add((resPath, "reserialized", normalized, null));
                reserialized++;
            }

            // Build the result envelope.
            var json = new StringBuilder(256);
            json.Append("{\"results\":[");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (i > 0) json.Append(',');
                json.Append("{\"path\":").Append(BridgeJson.EscapeString(r.path));
                json.Append(",\"status\":").Append(BridgeJson.EscapeString(r.status));
                if (r.normalized.HasValue)
                    json.Append(",\"normalized\":").Append(r.normalized.Value ? "true" : "false");
                if (r.error != null)
                    json.Append(",\"error\":").Append(BridgeJson.EscapeString(r.error));
                json.Append('}');
            }
            int failed = results.Count - reserialized;
            json.Append("],\"summary\":{\"total\":").Append(results.Count);
            json.Append(",\"reserialized\":").Append(reserialized);
            json.Append(",\"failed\":").Append(failed);
            json.Append("}}");

            return ToolDispatchResult.Ok(json.ToString());
        }

        // --- expansion + helpers ---------------------------------------------------

        /// <summary>
        /// Expand one <paramref name="entry"/> into target files appended to <paramref name="targets"/>.
        /// A directory is walked recursively; a file is added directly. Rejects (not_found /
        /// unsupported extension) are appended to <paramref name="rejects"/> so they surface in the
        /// per-path result. <c>uid://</c> identifiers are resolved to their <c>res://</c> path first.
        /// </summary>
        static void ExpandEntry(string entry, List<string> targets,
            List<(string, string, string)> rejects)
        {
            string resPath;
            if (ResourcePathNormalizer.IsUid(entry))
            {
                var resolved = ResolveUidToPath(entry);
                if (resolved == null)
                {
                    rejects.Add((entry, "not_found", $"uid '{entry}' does not resolve to any resource."));
                    return;
                }
                resPath = resolved;
            }
            else
            {
                if (!ResourcePathNormalizer.IsResPath(entry))
                {
                    rejects.Add((entry, "unsupported", "path must be a res:// file or folder path."));
                    return;
                }
                resPath = entry;
            }

            // Directory → recursive walk.
            bool isDir;
            try { isDir = DirAccess.DirExists(resPath); }
            catch { isDir = false; }
            if (isDir)
            {
                var folder = resPath.EndsWith("/", StringComparison.Ordinal) ? resPath : resPath + "/";
                WalkFolder(folder, targets);
                return;
            }

            // File → validate extension.
            bool exists;
            try { exists = FileAccess.FileExists(resPath); }
            catch { exists = false; }
            if (!exists)
            {
                rejects.Add((resPath, "not_found", "file not found."));
                return;
            }
            if (!HasReserializableExtension(resPath))
            {
                rejects.Add((resPath, "unsupported",
                    $"unsupported extension (supported: {string.Join("/", ReserializableExtensions)})."));
                return;
            }

            // Dedup — skip if already added (a folder + explicit file, or two folders overlapping).
            if (!targets.Contains(resPath))
                targets.Add(resPath);
        }

        /// <summary>
        /// Recursively walk <paramref name="dirRes"/> (must end in <c>/</c>) and append every
        /// writable resource file (<c>.tres</c>/<c>.tscn</c>/<c>.res</c>, non-imported) to
        /// <paramref name="targets"/>. Best-effort — a directory that fails to open is skipped.
        /// </summary>
        static void WalkFolder(string dirRes, List<string> targets)
        {
            DirAccess dir;
            try { dir = DirAccess.Open(dirRes); }
            catch { return; }
            if (dir == null) return;
            try
            {
                dir.ListDirBegin();
                string name;
                while ((name = dir.GetNext()) != "")
                {
                    if (name == "." || name == "..") continue;
                    // Hidden files (starting with '.') are skipped; the only ones we'd see here are
                    // the navigation entries already handled above.
                    if (name.StartsWith(".", StringComparison.Ordinal)) continue;

                    var full = dirRes + name;
                    if (dir.CurrentIsDir())
                        WalkFolder(full + "/", targets);
                    else if (HasReserializableExtension(full) && !IsImportedResource(full))
                        if (!targets.Contains(full))
                            targets.Add(full);
                }
                dir.ListDirEnd();
            }
            finally
            {
                dir.Dispose();
            }
        }

        /// <summary>True when <paramref name="path"/> ends with a reserializable extension
        /// (case-insensitive).</summary>
        static bool HasReserializableExtension(string path)
        {
            foreach (var ext in ReserializableExtensions)
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// True when <paramref name="resPath"/> is an imported/generated resource (has a
        /// <c>.import</c> sidecar). Mirrors <c>ResourceTools.IsImportedResource</c>: the probe MUST
        /// go through Godot's file API (<c>res://</c> is a virtual scheme <c>System.IO</c> cannot
        /// resolve). Duplicated here per the bridge's isolated-helper convention.
        /// </summary>
        static bool IsImportedResource(string resPath)
        {
            try { return FileAccess.FileExists(resPath + ".import"); }
            catch { return false; }
        }

        /// <summary>
        /// Resolve a <c>uid://</c> identifier to its canonical <c>res://</c> path. Mirrors
        /// <c>ResourceTools.ResolveUidToPath</c>. Main-thread only. Returns null on any failure.
        /// </summary>
        static string? ResolveUidToPath(string uidText)
        {
            long uidId;
            try { uidId = ResourceUid.TextToId(uidText); }
            catch { return null; }
            if (uidId == ResourceUid.InvalidId || !ResourceUid.HasId(uidId)) return null;
            var path = ResourceUid.GetIdPath(uidId);
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>Read a file's bytes via Godot's file API. Returns null on any failure
        /// (missing, unreadable). Used for before/after normalization detection.</summary>
        static byte[]? ReadBytesSafe(string resPath)
        {
            try { return FileAccess.GetFileAsBytes(resPath); }
            catch { return null; }
        }

        /// <summary>Byte-for-byte equality (null-safe; two nulls are equal).</summary>
        static bool BytesEqual(byte[]? a, byte[]? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
#endif
