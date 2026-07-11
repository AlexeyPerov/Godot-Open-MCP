#if TOOLS
#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Resource tool family — the Godot analog of Unity Open MCP's
    /// <c>TypedTools/AssetsTools.cs</c> read surface. P4.1 implements two live, read-only tools:
    /// <c>godot_open_mcp_resource_find</c> (exact path/UID lookup + indexed type search) and
    /// <c>godot_open_mcp_resource_get_data</c> (bounded, cycle-safe property inspection). The P4.2/P4.3
    /// mutators (create/modify/move/delete) are separate plans.
    ///
    /// <para>
    /// Godot ↔ Unity mapping: a Godot <see cref="Resource"/> on disk (<c>.tres</c>/<c>.res</c>) ↔ a
    /// Unity asset; a <c>res://</c> path / <c>uid://</c> ↔ a Unity asset path / GUID;
    /// <see cref="EditorFileSystem"/> ↔ <c>AssetDatabase</c> (the importer metadata index, used for
    /// type-filtered search without eagerly loading every candidate). The Unity GUID/local ID identity
    /// pair becomes the canonical Godot path plus an optional UID.
    /// </para>
    ///
    /// <para>
    /// <b>Adapted from Godot-MCP's <c>Tool_Resource.Find</c> / <c>Tool_Resource.GetData</c></b>
    /// (behavior reference): the UID→path resolution (<c>ResourceUid.TextToId</c> /
    /// <c>GetIdPath</c>), the <c>EditorFileSystemDirectory</c> recursive scan, and the
    /// <c>ClassDB.IsParentClass</c> type-inheritance matching are lifted from there. The property
    /// serialization is greenfield (first-party <see cref="GodotPropertySerializer"/> replacing the
    /// disallowed ReflectorNet).
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): the handlers touch <see cref="EditorInterface"/>,
    /// <see cref="EditorFileSystem"/>, and live <see cref="Resource"/> objects. The pure-managed pieces
    /// (<see cref="ResourcePathNormalizer"/>, <see cref="ResourceFindBody"/>,
    /// <see cref="ResourceGetDataBody"/>, <see cref="ResourceIdentity"/>,
    /// <see cref="ResourcePropertyData"/>) live outside this guard and are unit-tested.
    /// </summary>
    internal static class ResourceTools
    {
        /// <summary>The MCP tool name for the resource finder (P4.1).</summary>
        internal const string ResourceFindToolName = "godot_open_mcp_resource_find";

        /// <summary>The MCP tool name for the resource data reader (P4.1).</summary>
        internal const string ResourceGetDataToolName = "godot_open_mcp_resource_get_data";

        // --- registration -----------------------------------------------------------

        /// <summary>
        /// Register the resource tool family. P4.1 adds two read-only tools — the exact-lookup /
        /// indexed-search <c>godot_open_mcp_resource_find</c> and the bounded property reader
        /// <c>godot_open_mcp_resource_get_data</c> (group <c>resource</c>, default gate <c>off</c> —
        /// read-only). Registered once at plugin enable; safe to call again on re-enable (the registry
        /// is idempotent).
        /// </summary>
        internal static void RegisterResourceTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ResourceFindToolName,
                isMutating: false,
                defaultGate: "off",
                group: "resource",
                handler: Find));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ResourceGetDataToolName,
                isMutating: false,
                defaultGate: "off",
                group: "resource",
                handler: GetData));
        }

        // --- godot_open_mcp_resource_find -------------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_resource_find</c>. Read-only. Two modes:
        /// <list type="bullet">
        /// <item><description>Direct lookup (uid or resource_path) — resolves a single resource and
        /// returns its identity (path, uid, type).</description></item>
        /// <item><description>Type-filtered scan (type_filter) — recursively scans
        /// <see cref="EditorFileSystem"/> for files whose importer-assigned type equals or derives from
        /// the filter, scoped to an optional directory.</description></item>
        /// </list>
        /// Adapted from Unity Open MCP's <c>search-assets</c> (adapt fidelity — bounded, paged search)
        /// and Godot-MCP's <c>Tool_Resource.Find</c> (behavior reference — UID resolution + EFS scan).
        ///
        /// <para>
        /// Selector precedence: uid &gt; resource_path &gt; type_filter. At least one is required.
        /// Direct lookup returns at most one item; search results are sorted by canonical path before
        /// paging.
        /// </para>
        ///
        /// Structured failures: <c>invalid_request</c>, <c>invalid_path</c>, <c>resource_not_found</c>,
        /// <c>resource_type_not_found</c>, <c>filesystem_unavailable</c>. Must not throw.
        /// </summary>
        internal static ToolDispatchResult Find(string body)
        {
            var request = ResourceFindBody.Parse(body);

            if (!request.HasAnySelector)
            {
                return ToolDispatchResult.Fail(
                    "invalid_request",
                    "resource_find requires at least one of 'uid', 'resource_path', or 'type_filter'.");
            }

            var efs = EditorInterface.Singleton.GetResourceFilesystem();
            if (efs == null || !efs.GetFilesystem().IsReady())
            {
                return ToolDispatchResult.Fail(
                    "filesystem_unavailable",
                    "The editor filesystem is not ready; wait for the import scan to finish and retry.");
            }

            // 1) UID direct lookup (priority 1).
            if (!string.IsNullOrWhiteSpace(request.Uid))
            {
                return ResolveByUid(request.Uid!);
            }

            // 2) Path direct lookup (priority 2).
            if (!string.IsNullOrWhiteSpace(request.ResourcePath))
            {
                return ResolveByPath(request.ResourcePath!);
            }

            // 3) Type-filtered indexed scan.
            return ScanByType(request.TypeFilter!, request.Directory, request.PageSize, request.Cursor);
        }

        static ToolDispatchResult ResolveByUid(string uidText)
        {
            if (!ResourcePathNormalizer.TryRequireUid(uidText, out var normalized, out var pathError))
            {
                return ToolDispatchResult.Fail("invalid_path", pathError);
            }

            int uidId;
            try
            {
                uidId = ResourceUid.TextToId(normalized);
            }
            catch (System.Exception)
            {
                return ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"UID '{normalized}' is not a recognized uid:// identifier.");
            }

            if (uidId == ResourceUid.InvalidId || !ResourceUid.HasId(uidId))
            {
                return ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"uid '{normalized}' does not resolve to any resource.");
            }

            string resPath;
            try
            {
                resPath = ResourceUid.GetIdPath(uidId);
            }
            catch (System.Exception)
            {
                return ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"uid '{normalized}' exists but its path could not be resolved.");
            }

            if (string.IsNullOrEmpty(resPath))
            {
                return ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"uid '{normalized}' resolves to an empty path.");
            }

            return SingleResult(BuildIdentity(resPath, null, out _));
        }

        static ToolDispatchResult ResolveByPath(string rawPath)
        {
            // A uid:// value passed as resource_path is mapped first (matches the MCP schema note).
            if (ResourcePathNormalizer.IsUid(rawPath))
            {
                return ResolveByUid(rawPath);
            }

            if (!ResourcePathNormalizer.TryRequireResFilePath(rawPath, out var resPath, out var pathError))
            {
                return ToolDispatchResult.Fail("invalid_path", pathError);
            }

            if (!ResourceLoader.Exists(resPath))
            {
                return ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"No resource exists at '{resPath}'.");
            }

            return SingleResult(BuildIdentity(resPath, null, out _));
        }

        static ToolDispatchResult ScanByType(string typeFilter, string? directory, int pageSize, string? cursor)
        {
            var efs = EditorInterface.Singleton.GetResourceFilesystem();

            // Resolve the directory scope to an EditorFileSystemDirectory.
            var scope = NormalizeDirectoryScope(directory, out var dirError);
            if (scope == null)
            {
                return ToolDispatchResult.Fail("invalid_path", dirError!);
            }

            var rootDir = efs.GetFilesystemPath(scope);
            if (rootDir == null)
            {
                return ToolDispatchResult.Fail(
                    "invalid_path",
                    $"Directory '{scope}' was not found in the project filesystem.");
            }

            // Collect all matches (sorted), then apply paging.
            var matches = new List<ResourceIdentity>();
            CollectByType(rootDir, typeFilter, matches);

            // Deterministic ordering by canonical path (ordinal).
            matches.Sort((a, b) => string.CompareOrdinal(a.ResourcePath, b.ResourcePath));

            return PagedResult(matches, pageSize, cursor);
        }

        /// <summary>Recursively walk <paramref name="dir"/> collecting every file whose
        /// importer-assigned type matches <paramref name="typeFilter"/> (exact or subclass via
        /// <see cref="ClassDB.IsParentClass"/>). Main-thread only.</summary>
        static void CollectByType(EditorFileSystemDirectory dir, string typeFilter, List<ResourceIdentity> sink)
        {
            var fileCount = dir.GetFileCount();
            for (int i = 0; i < fileCount; i++)
            {
                var filePath = dir.GetFilePath(i);
                // Only .tres/.res are in scope for the resource tools.
                if (!ResourcePathNormalizer.HasResourceExtension(filePath)) continue;

                var fileType = dir.GetFileType(i).ToString();
                if (TypeMatches(fileType, typeFilter))
                {
                    sink.Add(BuildIdentity(filePath, fileType, out _));
                }
            }

            var subCount = dir.GetSubdirCount();
            for (int i = 0; i < subCount; i++)
            {
                var sub = dir.GetSubdir(i);
                if (sub != null)
                    CollectByType(sub, typeFilter, sink);
            }
        }

        /// <summary>
        /// True when <paramref name="fileType"/> equals <paramref name="typeFilter"/> or derives from
        /// it. Uses <see cref="ClassDB.IsParentClass"/> when both are known engine classes; otherwise
        /// falls back to case-sensitive equality so custom/script types still match exactly. Lifted from
        /// Godot-MCP's <c>Tool_Resource.Find</c>.
        /// </summary>
        static bool TypeMatches(string fileType, string typeFilter)
        {
            if (string.IsNullOrEmpty(fileType)) return false;
            if (fileType == typeFilter) return true;
            try
            {
                if (ClassDB.ClassExists(fileType) && ClassDB.ClassExists(typeFilter))
                    return ClassDB.IsParentClass(typeFilter, fileType);
            }
            catch
            {
                // ClassDB calls are safe but defensive — fall through to false on any unexpected error.
            }
            return false;
        }

        // --- godot_open_mcp_resource_get_data ---------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_resource_get_data</c>. Read-only. Loads a single resource and
        /// returns a bounded, cycle-safe property tree. Adapted from Unity Open MCP's
        /// <c>read-asset</c> (adapt fidelity — compact/balanced/full profile drill-down) and
        /// Godot-MCP's <c>Tool_Resource.GetData</c> (behavior reference — resource resolution), with a
        /// greenfield first-party serializer (<see cref="GodotPropertySerializer"/>) replacing the
        /// disallowed ReflectorNet.
        ///
        /// <para>
        /// The result carries the resource identity (path, uid, type), the requested profile/depth, a
        /// list of property nodes, and truncation metadata. Object references that would create a cycle
        /// or an unbounded graph are represented by a descriptive reference leaf, never blindly
        /// traversed.
        /// </para>
        ///
        /// Structured failures: <c>missing_parameter</c>, <c>invalid_path</c>,
        /// <c>resource_not_found</c>, <c>resource_load_failed</c>, <c>serialization_failed</c>. Must
        /// not throw.
        /// </summary>
        internal static ToolDispatchResult GetData(string body)
        {
            var request = ResourceGetDataBody.Parse(body);

            if (!request.HasResourcePath)
            {
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "resource_get_data requires 'resource_path' (a res:// path or uid:// identifier).");
            }

            var rawPath = request.ResourcePath!;
            string resPath;

            // uid:// accepted and mapped to a res:// path first.
            if (ResourcePathNormalizer.IsUid(rawPath))
            {
                var uidResolved = ResolveUidToPath(rawPath);
                if (uidResolved == null)
                {
                    return ToolDispatchResult.Fail(
                        "resource_not_found",
                        $"uid '{rawPath}' does not resolve to any resource.");
                }
                resPath = uidResolved;
            }
            else
            {
                if (!ResourcePathNormalizer.TryRequireResFilePath(rawPath, out resPath, out var pathError))
                {
                    return ToolDispatchResult.Fail("invalid_path", pathError);
                }
            }

            if (!ResourceLoader.Exists(resPath))
            {
                return ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"No resource exists at '{resPath}'.");
            }

            Resource? resource;
            try
            {
                resource = ResourceLoader.Load(resPath);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "resource_load_failed",
                    $"Failed to load resource at '{resPath}': {e.Message}");
            }

            if (resource == null)
            {
                return ToolDispatchResult.Fail(
                    "resource_load_failed",
                    $"ResourceLoader.Load returned null for '{resPath}'.");
            }

            var identity = BuildIdentity(resPath, resource.GetClass(), out var uidText);

            // Serialize the property tree.
            List<ResourcePropertyData> properties;
            TruncationInfo truncation;
            try
            {
                properties = GodotPropertySerializer.Serialize(
                    resource,
                    request.EffectiveDepth,
                    request.CollectionPageSize,
                    out truncation);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "serialization_failed",
                    $"Property serialization failed for '{resPath}': {e.Message}");
            }

            // Optional property_path drill-down: navigate the serialized tree to the requested path.
            if (!string.IsNullOrWhiteSpace(request.PropertyPath))
            {
                properties = DrillDown(properties, request.PropertyPath!);
            }

            // Build the JSON result.
            var sb = new StringBuilder(512);
            sb.Append("{\"identity\":");
            identity.AppendJsonTo(sb);
            sb.Append(",\"profile\":").Append(BridgeJson.EscapeString(request.Profile));
            sb.Append(",\"maxDepth\":").Append(request.EffectiveDepth.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"properties\":[");
            for (int i = 0; i < properties.Count; i++)
            {
                if (i > 0) sb.Append(',');
                properties[i].AppendJsonTo(sb);
            }
            sb.Append("],\"truncation\":");
            truncation.AppendJsonTo(sb);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        /// <summary>
        /// Navigate the top-level property list to a dotted <c>property_path</c> (e.g.
        /// <c>albedo_color</c> or <c>metadata.player</c>) and return just that subtree. When the path
        /// does not match, return an empty list (the handler still reports the identity + truncation).
        /// </summary>
        static List<ResourcePropertyData> DrillDown(List<ResourcePropertyData> properties, string propertyPath)
        {
            var segments = propertyPath.Split('/');
            List<ResourcePropertyData> current = properties;
            for (int s = 0; s < segments.Length; s++)
            {
                var seg = segments[s];
                ResourcePropertyData? match = null;
                foreach (var p in current)
                {
                    if (p.Name == seg) { match = p; break; }
                }
                if (match == null) return new List<ResourcePropertyData>();

                if (s == segments.Length - 1)
                {
                    // Last segment: return the matched node as the single result.
                    return new List<ResourcePropertyData> { match };
                }
                // Descend into children.
                current = match.Children ?? new List<ResourcePropertyData>();
            }
            return current;
        }

        // --- shared helpers ---------------------------------------------------------

        /// <summary>uid:// text → res:// path, or null when the uid is unknown. Main-thread only.</summary>
        static string? ResolveUidToPath(string uidText)
        {
            int uidId;
            try
            {
                uidId = ResourceUid.TextToId(uidText);
            }
            catch
            {
                return null;
            }
            if (uidId == ResourceUid.InvalidId || !ResourceUid.HasId(uidId))
                return null;
            var path = ResourceUid.GetIdPath(uidId);
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>
        /// Build a <see cref="ResourceIdentity"/> for a resource path. When <paramref name="knownType"/>
        /// is null, the type is read from the editor filesystem's importer metadata (no eager load).
        /// Main-thread only.
        /// </summary>
        static ResourceIdentity BuildIdentity(string resPath, string? knownType, out string? uidText)
        {
            string? uid = null;
            try
            {
                var uidId = ResourceLoader.GetResourceUid(resPath);
                if (uidId != ResourceUid.InvalidId)
                    uid = ResourceUid.IdToText(uidId);
            }
            catch
            {
                // UID lookup is best-effort; leave null.
            }
            uidText = uid;

            string? type = knownType;
            if (string.IsNullOrEmpty(type))
            {
                try
                {
                    var fs = EditorInterface.Singleton.GetResourceFilesystem();
                    if (fs != null)
                    {
                        var fileType = fs.GetFileType(resPath);
                        type = string.IsNullOrEmpty(fileType) ? null : fileType;
                    }
                }
                catch
                {
                    type = null;
                }
            }

            return new ResourceIdentity
            {
                ResourcePath = resPath,
                Uid = uid,
                Type = type,
            };
        }

        /// <summary>
        /// Normalize a user-supplied directory argument to a <c>res://</c> directory path
        /// (trailing-slash form). Returns null + an error message when the input is not a valid
        /// <c>res://</c> directory.
        /// </summary>
        static string? NormalizeDirectoryScope(string? raw, out string? error)
        {
            var dir = (raw ?? string.Empty).Trim();
            if (dir.Length == 0 || dir == ResourcePathNormalizer.ResScheme)
            {
                error = null;
                return ResourcePathNormalizer.ResScheme;
            }

            if (!dir.StartsWith(ResourcePathNormalizer.ResScheme, System.StringComparison.Ordinal))
            {
                error = $"directory must be a '{ResourcePathNormalizer.ResScheme}' path (or empty for the project root); got '{raw}'.";
                return null;
            }

            // Reject parent traversal.
            foreach (var segment in dir.Split('/'))
            {
                if (segment == "..")
                {
                    error = $"directory must not contain a '..' parent-directory segment; got '{raw}'.";
                    return null;
                }
            }

            if (!dir.EndsWith("/", System.StringComparison.Ordinal))
                dir += "/";

            error = null;
            return dir;
        }

        // --- result builders --------------------------------------------------------

        /// <summary>Wrap a single identity into the find result envelope (count=1, no paging).</summary>
        static ToolDispatchResult SingleResult(ResourceIdentity identity)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"count\":1,\"resources\":[");
            identity.AppendJsonTo(sb);
            sb.Append("],\"pagination\":{\"nextCursor\":null,\"hasMore\":false}}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        /// <summary>
        /// Page a sorted list of identities into the find result envelope. The cursor is the index of
        /// the next item (opaque numeric string). <paramref name="pageSize"/> is clamped to the
        /// <see cref="ResourceFindBody.MaxPageSize"/> cap by the body parser.
        /// </summary>
        static ToolDispatchResult PagedResult(List<ResourceIdentity> all, int pageSize, string? cursor)
        {
            var skip = ParseCursor(cursor);
            if (skip < 0) skip = 0;
            if (skip >= all.Count)
            {
                // Past the end: empty page.
                return ToolDispatchResult.Ok("{\"count\":0,\"resources\":[],\"pagination\":{\"nextCursor\":null,\"hasMore\":false}}");
            }

            var take = System.Math.Min(pageSize, all.Count - skip);
            var hasMore = skip + take < all.Count;
            var nextCursor = hasMore ? (skip + take).ToString(CultureInfo.InvariantCulture) : null;

            var sb = new StringBuilder(256);
            sb.Append("{\"count\":").Append(take.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"resources\":[");
            for (int i = 0; i < take; i++)
            {
                if (i > 0) sb.Append(',');
                all[skip + i].AppendJsonTo(sb);
            }
            sb.Append("],\"total\":").Append(all.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"pagination\":{\"nextCursor\":")
              .Append(BridgeJson.EscapeString(nextCursor));
            sb.Append(",\"hasMore\":").Append(hasMore ? "true" : "false").Append('}');
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
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
    }
}
#endif
