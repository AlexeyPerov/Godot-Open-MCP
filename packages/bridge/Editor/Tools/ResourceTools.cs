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

        /// <summary>The MCP tool name for the resource creator (P4.2).</summary>
        internal const string ResourceCreateToolName = "godot_open_mcp_resource_create";

        /// <summary>The MCP tool name for the resource mutator (P4.2).</summary>
        internal const string ResourceModifyToolName = "godot_open_mcp_resource_modify";

        // --- registration -----------------------------------------------------------

        /// <summary>
        /// Register the resource tool family. P4.1 adds two read-only tools — the exact-lookup /
        /// indexed-search <c>godot_open_mcp_resource_find</c> and the bounded property reader
        /// <c>godot_open_mcp_resource_get_data</c> (group <c>resource</c>, default gate <c>off</c> —
        /// read-only). P4.2 adds two gated mutators — <c>godot_open_mcp_resource_create</c> and
        /// <c>godot_open_mcp_resource_modify</c> (group <c>resource</c>, default gate <c>enforce</c>
        /// — both write <c>.tres</c>/<c>.res</c> files to disk). Registered once at plugin enable;
        /// safe to call again on re-enable (the registry is idempotent).
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
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ResourceCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "resource",
                handler: Create));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ResourceModifyToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "resource",
                handler: Modify));
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

        // --- godot_open_mcp_resource_create -----------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_resource_create</c>. Mutating (default gate
        /// <c>enforce</c>). Instantiates a Godot <c>Resource</c> subclass via <c>ClassDB</c>,
        /// applies validated initial properties, and persists it at a new <c>res://</c> destination
        /// through <c>ResourceSaver</c>. Adapted from Unity Open MCP's
        /// <c>scriptableobject_create</c> (adapt fidelity — <c>ClassDB.Instantiate</c> +
        /// <c>ResourceSaver.Save</c> in place of <c>ScriptableObject.CreateInstance</c> +
        /// <c>AssetDatabase.CreateAsset</c>) and Godot-MCP's <c>Tool_Resource.Create</c> (behavior
        /// reference — type validation + save lifecycle).
        ///
        /// <para>
        /// <b>Atomicity.</b> Type validation and patch validation run BEFORE any mutation; a single
        /// failure leaves the in-memory object untouched and no file is written. Save failure
        /// produces a structured <c>resource_save_failed</c> error with the gate evidence preserved.
        /// </para>
        ///
        /// <para>
        /// <b>paths_hint.</b> The destination path must appear in <c>paths_hint</c> — this is the
        /// gate scope AND a handler-level guard that fires even when an agent overrides with
        /// <c>gate:"off"</c> (the execution plan mandates <c>paths_hint</c> is always required for
        /// these mutators).
        /// </para>
        ///
        /// Structured failures: <c>missing_parameter</c>, <c>paths_hint_required</c>,
        /// <c>invalid_path</c>, <c>resource_exists</c>, <c>resource_type_invalid</c>,
        /// <c>patch_invalid</c>, <c>no_changes</c> (empty patches after normalization — only when
        /// initial properties are supplied but all no-op), <c>resource_save_failed</c>,
        /// <c>filesystem_unavailable</c>. Must not throw.
        /// </summary>
        internal static ToolDispatchResult Create(string body)
        {
            var request = ResourceCreateBody.Parse(body);

            if (!request.HasResourcePath)
            {
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "resource_create requires 'resource_path' (the new res:// destination ending in .tres or .res).");
            }

            // Handler-level paths_hint guard — fires even under gate:"off".
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            var pathError = ValidatePathsHint(pathsHint, request.ResourcePath!, out var hintContainsPath);
            if (pathError != null)
                return ToolDispatchResult.Fail("paths_hint_required", pathError);
            if (!hintContainsPath)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    $"paths_hint must contain the destination resource path '{request.ResourcePath}'.");

            // Normalize the destination path.
            if (!ResourcePathNormalizer.TryRequireResFilePath(request.ResourcePath!, out var resPath, out var normError))
                return ToolDispatchResult.Fail("invalid_path", normError);

            // Destination must not already exist (P4.2: no overwrite flag).
            if (ResourceLoader.Exists(resPath))
                return ToolDispatchResult.Fail(
                    "resource_exists",
                    $"A resource already exists at '{resPath}'. resource_create does not overwrite — use resource_modify instead.");

            // Filesystem must be ready (we notify it after save).
            var efs = EditorInterface.Singleton.GetResourceFilesystem();
            if (efs == null || !efs.GetFilesystem().IsReady())
                return ToolDispatchResult.Fail(
                    "filesystem_unavailable",
                    "The editor filesystem is not ready; wait for the import scan to finish and retry.");

            // Validate the type: must exist, be a Resource subclass, and be instantiable.
            var typeClassName = request.EffectiveTypeClassName;
            if (!ValidateInstantiableResourceType(typeClassName, out var typeError))
                return ToolDispatchResult.Fail("resource_type_invalid", typeError);

            // Instantiate.
            Resource resource;
            try
            {
                var obj = ClassDB.Instantiate(typeClassName);
                resource = obj as Resource;
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "resource_type_invalid",
                    $"Failed to instantiate '{typeClassName}': {e.Message}");
            }

            if (resource == null)
                return ToolDispatchResult.Fail(
                    "resource_type_invalid",
                    $"'{typeClassName}' instantiated but is not a Resource.");

            // Validate + apply initial properties (all-or-nothing).
            List<ValidatedPatch> validated = new List<ValidatedPatch>();
            if (request.Properties.Count > 0)
            {
                validated = ResourcePropertyPatcher.ValidatePatches(resource, request.Properties, out var errors);
                if (errors.Count > 0)
                {
                    return ToolDispatchResult.Fail(
                        "patch_invalid",
                        $"Initial property validation failed: {string.Join("; ", errors)}");
                }

                if (validated.Count > 0 && !ResourcePropertyPatcher.HasEffectiveChanges(validated))
                    return ToolDispatchResult.Fail(
                        "no_changes",
                        "All initial properties already match the new instance's defaults — nothing to set.");
            }

            // Apply patches (before the first save — avoids a second gated call).
            if (validated.Count > 0)
                ResourcePropertyPatcher.ApplyPatches(validated);

            // Set the resource path so ResourceSaver writes to the requested destination.
            resource.ResourcePath = resPath;

            // Save.
            Error saveErr;
            try
            {
                saveErr = ResourceSaver.Save(resource, resPath);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "resource_save_failed",
                    $"ResourceSaver.Save threw for '{resPath}': {e.Message}");
            }

            if (saveErr != Error.Ok)
                return ToolDispatchResult.Fail(
                    "resource_save_failed",
                    $"ResourceSaver.Save returned {saveErr} for '{resPath}'.");

            // Notify the editor filesystem so the new file is picked up immediately.
            try
            {
                efs.UpdateFile(resPath);
                efs.Scan();
            }
            catch
            {
                // UpdateFile failure is non-fatal — the file is on disk; the next scan picks it up.
            }

            // Build the result. The identity (uid/type) is read back after save so the UID assigned
            // by ResourceSaver is reflected.
            var identity = BuildIdentity(resPath, resource.GetClass(), out _);
            var changed = new List<string>(validated.Count);
            var unchanged = new List<string>();
            foreach (var vp in validated)
            {
                if (VariantEqualityComparer.Equals(vp.ConvertedValue, vp.OriginalValue))
                    unchanged.Add(vp.Source.RawPath!);
                else
                    changed.Add(vp.Source.RawPath!);
            }

            var sb = new StringBuilder(256);
            sb.Append("{\"resource\":");
            identity.AppendJsonTo(sb);
            sb.Append(",\"changed\":[");
            for (int i = 0; i < changed.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(changed[i]));
            }
            sb.Append("],\"unchanged\":[");
            for (int i = 0; i < unchanged.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(unchanged[i]));
            }
            sb.Append("],\"saved\":true}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- godot_open_mcp_resource_modify -----------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_resource_modify</c>. Mutating (default gate
        /// <c>enforce</c>). Loads a <c>.tres</c>/<c>.res</c> resource, applies validated property
        /// patches through the first-party <see cref="ResourcePropertyPatcher"/>, and persists the
        /// result via <c>ResourceSaver</c>. Adapted from Unity Open MCP's <c>object_modify</c>
        /// (adapt fidelity — explicit property-path assignments in place of reflection-based field
        /// patches) and Godot-MCP's <c>Tool_Resource.Modify</c> (behavior reference — property
        /// setting via <c>Set</c>), but replaces the Godot-MCP best-effort per-patch logs with
        /// atomic all-or-nothing validation and gate evidence.
        ///
        /// <para>
        /// <b>Atomicity.</b> Every patch is resolved and its value converted BEFORE any mutation. A
        /// single failure leaves the resource untouched. No-op detection reports <c>no_changes</c>
        /// when all converted values already equal the originals (the save is skipped entirely).
        /// </para>
        ///
        /// <para>
        /// <b>Property-path grammar.</b> Slash-separated segments: <c>property</c>,
        /// <c>nested/property</c>, <c>array/[0]</c>, <c>dictionary/[key]</c>. The first segment
        /// must be a property name.
        /// </para>
        ///
        /// Structured failures: <c>missing_parameter</c>, <c>paths_hint_required</c>,
        /// <c>invalid_path</c>, <c>resource_not_found</c>, <c>resource_load_failed</c>,
        /// <c>resource_not_writable</c>, <c>patch_invalid</c>, <c>no_changes</c>,
        /// <c>resource_save_failed</c>, <c>filesystem_unavailable</c>. Must not throw.
        /// </summary>
        internal static ToolDispatchResult Modify(string body)
        {
            var request = ResourceModifyBody.Parse(body);

            if (!request.HasResourcePath)
            {
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "resource_modify requires 'resource_path' (a res:// path or uid:// identifier).");
            }

            if (!request.HasPatches)
            {
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "resource_modify requires a non-empty 'patches' array.");
            }

            // Handler-level paths_hint guard — fires even under gate:"off".
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            var pathError = ValidatePathsHint(pathsHint, request.ResourcePath!, out _);
            if (pathError != null)
                return ToolDispatchResult.Fail("paths_hint_required", pathError);

            // Normalize the target path (accept uid:// and map to res://).
            var rawPath = request.ResourcePath!;
            string resPath;
            if (ResourcePathNormalizer.IsUid(rawPath))
            {
                var uidResolved = ResolveUidToPath(rawPath);
                if (uidResolved == null)
                    return ToolDispatchResult.Fail(
                        "resource_not_found",
                        $"uid '{rawPath}' does not resolve to any resource.");
                resPath = uidResolved;
            }
            else
            {
                if (!ResourcePathNormalizer.TryRequireResFilePath(rawPath, out resPath, out var normError))
                    return ToolDispatchResult.Fail("invalid_path", normError);
            }

            if (!ResourceLoader.Exists(resPath))
                return ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"No resource exists at '{resPath}'.");

            // Reject imported/generated resources whose source of truth is external (e.g. a .import
            // sidecar owns the file). Only hand-authored .tres/.res are writable.
            if (IsImportedResource(resPath))
                return ToolDispatchResult.Fail(
                    "resource_not_writable",
                    $"'{resPath}' is an imported/generated resource; its source of truth is external and cannot be modified in place.");

            // Filesystem must be ready.
            var efs = EditorInterface.Singleton.GetResourceFilesystem();
            if (efs == null || !efs.GetFilesystem().IsReady())
                return ToolDispatchResult.Fail(
                    "filesystem_unavailable",
                    "The editor filesystem is not ready; wait for the import scan to finish and retry.");

            // Load.
            Resource resource;
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
                return ToolDispatchResult.Fail(
                    "resource_load_failed",
                    $"ResourceLoader.Load returned null for '{resPath}'.");

            // Validate all patches (all-or-nothing).
            var validated = ResourcePropertyPatcher.ValidatePatches(resource, request.Patches, out var errors);
            if (errors.Count > 0)
            {
                return ToolDispatchResult.Fail(
                    "patch_invalid",
                    $"Patch validation failed: {string.Join("; ", errors)}");
            }

            // No-op detection — skip the save when nothing actually changes.
            if (!ResourcePropertyPatcher.HasEffectiveChanges(validated))
            {
                return ToolDispatchResult.Fail(
                    "no_changes",
                    "All patches normalize to values that already match — nothing to write.");
            }

            // Apply.
            ResourcePropertyPatcher.ApplyPatches(validated);

            // Save.
            Error saveErr;
            try
            {
                saveErr = ResourceSaver.Save(resource, resPath);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "resource_save_failed",
                    $"ResourceSaver.Save threw for '{resPath}': {e.Message}");
            }

            if (saveErr != Error.Ok)
                return ToolDispatchResult.Fail(
                    "resource_save_failed",
                    $"ResourceSaver.Save returned {saveErr} for '{resPath}'.");

            // Notify the editor filesystem so cached copies are refreshed.
            try
            {
                efs.UpdateFile(resPath);
            }
            catch
            {
                // UpdateFile failure is non-fatal.
            }

            // Build the result.
            var identity = BuildIdentity(resPath, resource.GetClass(), out _);
            var changed = new List<string>(validated.Count);
            var unchanged = new List<string>();
            foreach (var vp in validated)
            {
                if (VariantEqualityComparer.Equals(vp.ConvertedValue, vp.OriginalValue))
                    unchanged.Add(vp.Source.RawPath!);
                else
                    changed.Add(vp.Source.RawPath!);
            }

            var sb = new StringBuilder(256);
            sb.Append("{\"resource\":");
            identity.AppendJsonTo(sb);
            sb.Append(",\"changed\":[");
            for (int i = 0; i < changed.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(changed[i]));
            }
            sb.Append("],\"unchanged\":[");
            for (int i = 0; i < unchanged.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(unchanged[i]));
            }
            sb.Append("],\"saved\":true}");
            return ToolDispatchResult.Ok(sb.ToString());
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

        // --- P4.2 mutation helpers --------------------------------------------------

        /// <summary>
        /// Validate that <paramref name="pathsHint"/> is non-empty (handler-level guard that fires
        /// even under <c>gate:"off"</c>). Returns null on success, or an error message when the hint
        /// is missing/empty. Also reports whether <paramref name="expectedPath"/> is present in the
        /// hint so the caller can enforce scope containment.
        /// </summary>
        static string? ValidatePathsHint(string[]? pathsHint, string expectedPath, out bool containsPath)
        {
            containsPath = false;
            if (pathsHint == null || pathsHint.Length == 0)
                return "paths_hint is required and must be non-empty.";

            foreach (var p in pathsHint)
            {
                if (p == expectedPath) { containsPath = true; break; }
            }
            return null;
        }

        /// <summary>
        /// Validate that <paramref name="typeClassName"/> is an instantiable <c>Resource</c>
        /// subclass: exists in <c>ClassDB</c>, can be instantiated, and is a (sub)class of
        /// <c>Resource</c>. Returns true on success; false + an error message otherwise. Main-thread
        /// only.
        /// </summary>
        static bool ValidateInstantiableResourceType(string typeClassName, out string error)
        {
            error = "";
            try
            {
                if (!ClassDB.ClassExists(typeClassName))
                {
                    error = $"Class '{typeClassName}' does not exist in ClassDB.";
                    return false;
                }
                if (!ClassDB.CanInstantiate(typeClassName))
                {
                    error = $"Class '{typeClassName}' cannot be instantiated (it may be abstract or a built-in native type).";
                    return false;
                }
                if (!ClassDB.IsParentClass("Resource", typeClassName))
                {
                    error = $"Class '{typeClassName}' is not a Resource subclass.";
                    return false;
                }
            }
            catch (System.Exception e)
            {
                error = $"ClassDB validation of '{typeClassName}' failed: {e.Message}";
                return false;
            }
            return true;
        }

        /// <summary>
        /// True when <paramref name="resPath"/> is an imported/generated resource whose source of
        /// truth is external (e.g. a <c>.png</c>, <c>.glb</c>, <c>.csv</c> that the importer turned
        /// into a <c>.import</c> + cached binary). Hand-authored <c>.tres</c>/<c>.res</c> files are
        /// writable; imported ones are not. Uses the editor filesystem's importer metadata.
        /// </summary>
        static bool IsImportedResource(string resPath)
        {
            try
            {
                var fs = EditorInterface.Singleton.GetResourceFilesystem();
                if (fs == null) return false;
                // Imported resources have a .import sidecar next to the source. We check by looking
                // at whether the path has a known non-tres/res source type. The simplest reliable
                // signal: imported assets are NOT in the .tres/.res text format on disk — but since
                // we already filtered to .tres/.res extensions, the real signal is whether an
                // .import sidecar exists (binary .res can be imported). Check for the sidecar.
                var importSidecar = resPath + ".import";
                return System.IO.File.Exists(importSidecar);
            }
            catch
            {
                return false;
            }
        }
    }
}
#endif
