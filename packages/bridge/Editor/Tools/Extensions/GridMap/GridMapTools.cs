#if TOOLS
#nullable enable
using System.Text;
using Godot;
// BridgeRequestBody + BridgeJson live in this same namespace (GodotOpenMcp.Bridge.Editor);
// no extra using is needed — they are internal siblings of the tool family.

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// GridMap domain pack — six typed tools for Godot 4.3+ <c>GridMap</c> (3D
    /// grid-based level construction). Mirrors the Phase 12 domain packs' folder
    /// layout, registration shape, gate policy, group assignment, and docs
    /// convention.
    ///
    /// <para>
    /// <b>Fidelity:</b> adapt — architecture (embedded domain handlers under
    /// <c>Tools/Extensions/GridMap/</c>, one <c>RegisterGridMapTools()</c>
    /// called from <see cref="GodotOpenMcpPlugin"/>, every mutator declaring
    /// <c>isMutating:true</c> / <c>defaultGate:"enforce"</c> /
    /// <c>group:"gridmap"</c> and validating <c>paths_hint</c> at the handler
    /// level) is copied from the P12.1 tilemap pack (itself adapted from Unity
    /// Open MCP's <c>TypedTools/Extensions/Tilemap/</c>). The Godot deltas are:
    /// (1) <c>GridMap</c> is a 3D <c>Node3D</c> (cells are addressed by a 3D
    /// integer coordinate + item + orientation, not the tilemap's 2D atlas
    /// quadruple); (2) the library is a <c>MeshLibrary</c> resource, not a
    /// <c>TileSet</c>; (3) erase sets the cell item to
    /// <c>GridMap.INVALID_CELL_ITEM</c> (-1) — Godot has no dedicated erase
    /// method; (4) no compile gate — <c>GridMap</c> is an engine API present in
    /// every 4.3+ build (no Unity twin).
    /// </para>
    ///
    /// <para>
    /// <b>Gate contract.</b> The five mutating handlers (<c>create</c> /
    /// <c>set_mesh_library</c> / <c>set_cell</c> / <c>erase_cell</c> /
    /// <c>clear</c>) register with <see cref="BridgeToolEntry.DefaultGate"/>
    /// <c>"enforce"</c> and validate <c>paths_hint</c> themselves (mirrors the
    /// P4.x resource/filesystem/editor mutators + P12 domain mutators). The
    /// dispatch layer rejects an empty hint when the effective gate is not
    /// <c>off</c>; the handler-level guard ALSO fires when an agent overrides
    /// with <c>gate:"off"</c>, so <c>paths_hint</c> is always required for these
    /// tools. The read-only <c>get_used_cells</c> has no gate surface.
    /// </para>
    ///
    /// <para>
    /// <b>Owner / persistence.</b> <c>gridmap_create</c> sets the new node's
    /// <c>Owner</c> to the edited scene root so it persists in the
    /// <c>.tscn</c> on save — same step every node creator performs. Every
    /// mutator that changes scene state calls
    /// <see cref="EditorInterface.MarkSceneAsUnsaved"/> +
    /// <see cref="SceneTools.MarkEditedSceneDirty"/> so the bridge-tracked dirty
    /// flag the <c>scene_open</c> guard consults stays honest.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): every handler touches
    /// <see cref="EditorInterface"/> and live <see cref="GridMap"/> objects. The
    /// pure-managed pieces (<see cref="GridMapCreateBody"/> /
    /// <see cref="GridMapSetCellBody"/> / … / <see cref="GridMapCellData"/>)
    /// live outside this guard and are unit-tested.
    /// </summary>
    internal static class GridMapTools
    {
        internal const string GridMapCreateToolName = "godot_open_mcp_gridmap_create";
        internal const string GridMapSetMeshLibraryToolName = "godot_open_mcp_gridmap_set_mesh_library";
        internal const string GridMapSetCellToolName = "godot_open_mcp_gridmap_set_cell";
        internal const string GridMapEraseCellToolName = "godot_open_mcp_gridmap_erase_cell";
        internal const string GridMapGetUsedCellsToolName = "godot_open_mcp_gridmap_get_used_cells";
        internal const string GridMapClearToolName = "godot_open_mcp_gridmap_clear";

        // GridMap uses -1 to denote an empty cell (GridMap.INVALID_CELL_ITEM in
        // GDScript). Godot has no dedicated EraseCell method — erasing is
        // SetCellItem with this sentinel. The literal is stable across Godot 4.x
        // and avoids a wrong C# symbol name failing the build.
        private const int InvalidCellItem = -1;

        /// <summary>
        /// Register the gridmap tool family. The five mutators declare
        /// <c>defaultGate:"enforce"</c> and <c>isMutating:true</c>; the
        /// read-only <c>get_used_cells</c> is <c>off</c>. All six belong to the
        /// <c>gridmap</c> group. Registered once at plugin enable; idempotent
        /// (the registry replaces on re-register).
        /// </summary>
        internal static void RegisterGridMapTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: GridMapCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "gridmap",
                handler: Create));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: GridMapSetMeshLibraryToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "gridmap",
                handler: SetMeshLibrary));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: GridMapSetCellToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "gridmap",
                handler: SetCell));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: GridMapEraseCellToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "gridmap",
                handler: EraseCell));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: GridMapGetUsedCellsToolName,
                isMutating: false,
                defaultGate: "off",
                group: "gridmap",
                handler: GetUsedCells));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: GridMapClearToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "gridmap",
                handler: Clear));
        }

        // ===========================================================================
        // 1. godot_open_mcp_gridmap_create
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_gridmap_create</c>. Creates a
        /// <c>GridMap</c> node in the currently edited scene and returns its
        /// NodeData (same shape as <c>node_create</c>) so an agent can chain
        /// <c>node_path</c> straight into <c>set_mesh_library</c> /
        /// <c>set_cell</c> without a second find. The new node's owner is the
        /// edited scene root; the scene is marked unsaved.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>no_edited_scene</c>, <c>parent_not_found</c>,
        /// <c>create_failed</c>. Must not throw — the dispatcher surfaces
        /// uncaught exceptions as <c>execution_error</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Create(string body)
        {
            // Handler-level paths_hint guard — fires even under gate:"off" (matches the
            // P4.x mutator convention + P12 domain packs). The dispatch layer also rejects
            // an empty hint when the effective gate is enforce/warn; this guard covers
            // the gate:"off" override.
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "gridmap_create is mutating; pass a non-empty paths_hint scoped to the edited scene path (res://...tscn).");

            var request = GridMapCreateBody.Parse(body);

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling gridmap_create.");

            // Resolve the parent (default: edited scene root). A non-empty parent_node_path
            // that does not resolve is a hard error. Reuses NodeTools' resolver so the same
            // path vocabulary as node_create is accepted (Main, Main/Level, /root/Main/Level, .).
            Node parent = root;
            if (!string.IsNullOrEmpty(request.ParentNodePath))
            {
                parent = NodeTools.ResolvePath(root, request.ParentNodePath!);
                if (parent == null)
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Parent node not found at path '{request.ParentNodePath}'.");
            }

            GridMap gridmap;
            try
            {
                // GridMap is a concrete engine node present in every Godot 4.3+ build — no
                // ClassExists guard needed. `new GridMap()` is the canonical C# instantiate.
                gridmap = new GridMap();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate GridMap: {e.Message}");
            }

            if (!string.IsNullOrEmpty(request.Name))
                gridmap.Name = request.Name;

            try
            {
                parent.AddChild(gridmap);
            }
            catch (System.Exception e)
            {
                // AddChild can fault (e.g. a parent that rejects the child). Free the orphan.
                gridmap.QueueFree();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add GridMap to parent: {e.Message}");
            }

            // Owner = edited scene root so the node persists in the .tscn on save.
            gridmap.Owner = root;

            // GridMap derives from Node3D, so a 3D position field applies when present.
            // A malformed vector is silently ignored (the gridmap is still created at origin).
            if (gridmap is Node3D n3d && TryParseVector3(request.Position, out var pos))
                n3d.Position = pos;

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();
            EditorInterface.Singleton.EditNode(gridmap);

            return ToolDispatchResult.Ok(NodeTools.ToNodeData(gridmap).ToJsonString());
        }

        // ===========================================================================
        // 2. godot_open_mcp_gridmap_set_mesh_library
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_gridmap_set_mesh_library</c>. Loads a
        /// <c>MeshLibrary</c> resource from a <c>res://</c> path and assigns it
        /// to the target <c>GridMap</c>. No MeshLibrary authoring — the resource
        /// must already exist (agents point at an existing .tres/.res
        /// MeshLibrary).
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>missing_parameter</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>,
        /// <c>resource_not_found</c>, <c>resource_load_failed</c>. Must not
        /// throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult SetMeshLibrary(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "gridmap_set_mesh_library is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = GridMapSetMeshLibraryBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "gridmap_set_mesh_library requires 'node_path' (the GridMap to assign).");
            if (!request.HasMeshLibraryPath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "gridmap_set_mesh_library requires 'mesh_library_path' (a res:// MeshLibrary resource).");

            if (!TryResolveGridMap(request.NodePath!, out var gridmap, out var resolveError))
                return resolveError;

            var meshLibraryPath = request.MeshLibraryPath!;
            if (!ResourceLoader.Exists(meshLibraryPath))
                return ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"MeshLibrary resource not found at '{meshLibraryPath}'.");

            MeshLibrary meshLibrary;
            try
            {
                var loaded = ResourceLoader.Load<MeshLibrary>(meshLibraryPath);
                if (loaded == null)
                    return ToolDispatchResult.Fail(
                        "resource_load_failed",
                        $"Resource at '{meshLibraryPath}' exists but is not a MeshLibrary.");
                meshLibrary = loaded;
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("resource_load_failed",
                    $"Failed to load MeshLibrary from '{meshLibraryPath}': {e.Message}");
            }

            gridmap.MeshLibrary = meshLibrary;

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(gridmap.GetPath().ToString())).Append(',');
            sb.Append("\"meshLibraryPath\":").Append(BridgeJson.EscapeString(meshLibraryPath));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 3. godot_open_mcp_gridmap_set_cell
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_gridmap_set_cell</c>. Paints one cell on
        /// the target <c>GridMap</c> using Godot's 3D grid addressing (x, y, z)
        /// plus a <c>MeshLibrary</c> item id and an orientation (0–23 orthonormal
        /// rotations). A GridMap without a MeshLibrary is rejected with
        /// <c>mesh_library_required</c> — Godot's <c>SetCellItem</c> silently
        /// no-ops without one, so a guard here turns that silent failure into a
        /// structured error.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>missing_parameter</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>,
        /// <c>mesh_library_required</c>, <c>invalid_parameter</c> (item id not
        /// present in the MeshLibrary). Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult SetCell(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "gridmap_set_cell is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = GridMapSetCellBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "gridmap_set_cell requires 'node_path' (the GridMap to paint on).");

            if (!TryResolveGridMap(request.NodePath!, out var gridmap, out var resolveError))
                return resolveError;

            if (gridmap.MeshLibrary == null)
                return ToolDispatchResult.Fail(
                    "mesh_library_required",
                    $"GridMap '{request.NodePath}' has no MeshLibrary; call gridmap_set_mesh_library before gridmap_set_cell.");

            // Guard the item id: Godot's SetCellItem silently does nothing when the item id
            // is not present in the MeshLibrary. Surface that as a structured error so an
            // agent does not see a silent no-op. Item ids are NOT necessarily contiguous.
            var meshLibrary = gridmap.MeshLibrary;
            var validItems = meshLibrary.GetItemList();
            if (System.Array.IndexOf(validItems, request.Item) < 0)
            {
                var sb2 = new System.Text.StringBuilder();
                for (int i = 0; i < validItems.Length; i++)
                {
                    if (i > 0) sb2.Append(", ");
                    sb2.Append(validItems[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    $"MeshLibrary has no item with id {request.Item}; present item ids are: {sb2}.");
            }

            var coords = new Vector3I(request.X, request.Y, request.Z);
            gridmap.SetCellItem(coords, request.Item, request.Orientation);

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(gridmap.GetPath().ToString())).Append(',');
            sb.Append("\"x\":").Append(request.X.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"y\":").Append(request.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"z\":").Append(request.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"item\":").Append(request.Item.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"orientation\":").Append(request.Orientation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 4. godot_open_mcp_gridmap_erase_cell
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_gridmap_erase_cell</c>. Erases one cell
        /// from the target <c>GridMap</c> by setting its item to
        /// <c>INVALID_CELL_ITEM</c> (-1). Erasing an already-empty cell is a
        /// no-op success (echoed with <c>erased:true</c>).
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>missing_parameter</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult EraseCell(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "gridmap_erase_cell is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = GridMapEraseCellBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "gridmap_erase_cell requires 'node_path' (the GridMap to erase from).");

            if (!TryResolveGridMap(request.NodePath!, out var gridmap, out var resolveError))
                return resolveError;

            // Godot GridMap has no dedicated erase method — setting the item to the
            // INVALID_CELL_ITEM sentinel (-1) clears the cell (matches GetCellItem's
            // "empty" return value). Orientation is irrelevant on an empty cell; pass 0.
            gridmap.SetCellItem(new Vector3I(request.X, request.Y, request.Z), InvalidCellItem);

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(gridmap.GetPath().ToString())).Append(',');
            sb.Append("\"x\":").Append(request.X.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"y\":").Append(request.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"z\":").Append(request.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"erased\":true");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 5. godot_open_mcp_gridmap_get_used_cells (read-only)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_gridmap_get_used_cells</c>. Lists the
        /// used cells on the target <c>GridMap</c>, bounded by
        /// <c>max_results</c> (default 256, hard cap 2000). Read-only — no gate
        /// surface. Each cell carries the 3D coordinate plus its item id and
        /// orientation so an agent can echo it back into <c>set_cell</c>. The
        /// remainder beyond the cap is reported in <c>truncated</c>.
        ///
        /// <para>
        /// Structured failures: <c>missing_parameter</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>,
        /// <c>wrong_node_type</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult GetUsedCells(string body)
        {
            var request = GridMapGetUsedCellsBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "gridmap_get_used_cells requires 'node_path' (the GridMap to read).");

            if (!TryResolveGridMap(request.NodePath!, out var gridmap, out var resolveError))
                return resolveError;

            var usedCells = gridmap.GetUsedCells();
            int total = usedCells.Count;
            int cap = request.EffectiveMaxResults;
            int returned = total > cap ? cap : total;
            int truncated = total - returned;

            var sb = new StringBuilder(256);
            sb.Append("{\"cells\":[");
            for (int i = 0; i < returned; i++)
            {
                var cell = usedCells[i];
                if (i > 0) sb.Append(',');
                var data = new GridMapCellData
                {
                    X = cell.X,
                    Y = cell.Y,
                    Z = cell.Z,
                    Item = gridmap.GetCellItem(cell),
                    Orientation = gridmap.GetCellItemOrientation(cell),
                };
                data.AppendJsonTo(sb);
            }
            sb.Append("],\"count\":").Append(returned);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 6. godot_open_mcp_gridmap_clear
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_gridmap_clear</c>. Clears every cell on
        /// the target <c>GridMap</c> while keeping its MeshLibrary assignment.
        /// Idempotent on an empty grid (returns <c>cleared:true</c>).
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>missing_parameter</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Clear(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "gridmap_clear is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = GridMapClearBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "gridmap_clear requires 'node_path' (the GridMap to clear).");

            if (!TryResolveGridMap(request.NodePath!, out var gridmap, out var resolveError))
                return resolveError;

            gridmap.Clear();

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(gridmap.GetPath().ToString())).Append(',');
            // Echo whether a MeshLibrary is still assigned so an agent can confirm Clear
            // preserved it (the documented contract: clear empties cells, keeps the library).
            sb.Append("\"meshLibraryPath\":").Append(BridgeJson.EscapeString(gridmap.MeshLibrary?.ResourcePath ?? null));
            sb.Append(",\"cleared\":true");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // Shared helpers
        // ===========================================================================

        /// <summary>
        /// Resolve <paramref name="nodePath"/> to a live <c>GridMap</c> in the
        /// edited scene. Fails with <c>no_edited_scene</c> /
        /// <c>node_not_found</c> / <c>wrong_node_type</c> via
        /// <paramref name="error"/>. Returns true + the gridmap on success.
        /// Reuses <see cref="NodeTools.ResolvePath"/> so the same path vocabulary
        /// as <c>node_find</c> / <c>node_create</c> is accepted.
        /// </summary>
        static bool TryResolveGridMap(string nodePath, out GridMap gridmap, out ToolDispatchResult error)
        {
            gridmap = null!;
            error = null!;

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
            {
                error = ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn first.");
                return false;
            }

            var node = NodeTools.ResolvePath(root, nodePath);
            if (node == null)
            {
                error = ToolDispatchResult.Fail(
                    "node_not_found",
                    $"GridMap not found at path '{nodePath}'.");
                return false;
            }

            if (node is GridMap resolved)
            {
                gridmap = resolved;
                return true;
            }

            error = ToolDispatchResult.Fail(
                "wrong_node_type",
                $"Node at '{nodePath}' is a '{node.GetClass()}', not a GridMap.");
            return false;
        }

        /// <summary>
        /// Parse an <c>"x,y,z"</c> string into a <see cref="Vector3"/>. Returns
        /// false on a malformed string (the caller treats that as "no position
        /// applied" rather than erroring). Verbatim from the CSG/Navigation
        /// packs — duplicated rather than widening another type's surface for
        /// one helper.
        /// </summary>
        static bool TryParseVector3(string? text, out Vector3 v)
        {
            v = Vector3.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text!.Trim().Split(',');
            if (parts.Length < 3) return false;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return false;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }
    }
}
#endif
