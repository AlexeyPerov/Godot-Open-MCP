#if TOOLS
#nullable enable
using System.Text;
using Godot;
// BridgeRequestBody + BridgeJson live in this same namespace (GodotOpenMcp.Bridge.Editor);
// no extra using is needed — they are internal siblings of the tool family.

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Tilemap domain pack (P12.1) — six typed tools for Godot 4.3+ <c>TileMapLayer</c>. The first
    /// Phase 12 domain pack and the reference implementation for P12.2–P12.5 (same folder layout,
    /// registration shape, gate policy, group assignment, and docs convention).
    ///
    /// <para>
    /// <b>Fidelity:</b> adapt — architecture (embedded domain handlers under
    /// <c>Tools/Extensions/&lt;Domain&gt;/</c>, one <c>Register*Tools()</c> called from
    /// <see cref="GodotOpenMcpPlugin"/>, every mutator declaring <c>isMutating:true</c> /
    /// <c>defaultGate:"enforce"</c> / <c>group:"tilemap"</c> and validating <c>paths_hint</c> at the
    /// handler level) is copied from Unity Open MCP's
    /// <c>TypedTools/Extensions/Tilemap/TilemapTools.cs</c>. The Godot deltas are: (1) the node is
    /// a single <c>TileMapLayer</c> (Godot 4.3+) — NOT Unity's Grid + Tilemap hierarchy, and NOT
    /// Godot's deprecated multi-layer <c>TileMap</c> API; (2) cells are addressed by the Godot
    /// atlas quadruple (source_id + atlas coords + alternative_tile) rather than a Unity Tile asset
    /// path; (3) no compile gate — <c>TileMapLayer</c> is an engine API present in every 4.3+ build;
    /// (4) no RuleTile / tile-asset-authoring analog in v1 (the Godot catalog lists six tools only).
    /// </para>
    ///
    /// <para>
    /// <b>Gate contract.</b> The five mutating handlers (<c>create</c> / <c>set_tileset</c> /
    /// <c>set_cell</c> / <c>erase_cell</c> / <c>clear</c>) register with
    /// <see cref="BridgeToolEntry.DefaultGate"/> <c>"enforce"</c> and validate <c>paths_hint</c>
    /// themselves (mirrors the P4.x resource/filesystem/editor mutators). The dispatch layer
    /// rejects an empty hint when the effective gate is not <c>off</c>; the handler-level guard
    /// ALSO fires when an agent overrides with <c>gate:"off"</c>, so <c>paths_hint</c> is always
    /// required for these tools. The read-only <c>get_used_cells</c> has no gate surface.
    /// </para>
    ///
    /// <para>
    /// <b>Owner / persistence.</b> <c>tilemap_create</c> sets the new node's <c>Owner</c> to the
    /// edited scene root so it persists in the <c>.tscn</c> on save — same step every node creator
    /// performs (Unity has no equivalent; a GameObject in a scene is saved implicitly). Every
    /// mutator that changes scene state calls
    /// <see cref="EditorInterface.MarkSceneAsUnsaved"/> +
    /// <see cref="SceneTools.MarkEditedSceneDirty"/> so the bridge-tracked dirty flag the
    /// <c>scene_open</c> guard consults stays honest.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): every handler touches <see cref="EditorInterface"/> and live
    /// <see cref="TileMapLayer"/> objects. The pure-managed pieces (<see cref="TilemapCreateBody"/>
    /// / <see cref="TilemapSetCellBody"/> / … / <see cref="TilemapCellData"/>) live outside this
    /// guard and are unit-tested.
    /// </summary>
    internal static class TilemapTools
    {
        internal const string TilemapCreateToolName = "godot_open_mcp_tilemap_create";
        internal const string TilemapSetTilesetToolName = "godot_open_mcp_tilemap_set_tileset";
        internal const string TilemapSetCellToolName = "godot_open_mcp_tilemap_set_cell";
        internal const string TilemapEraseCellToolName = "godot_open_mcp_tilemap_erase_cell";
        internal const string TilemapGetUsedCellsToolName = "godot_open_mcp_tilemap_get_used_cells";
        internal const string TilemapClearToolName = "godot_open_mcp_tilemap_clear";

        /// <summary>
        /// Register the tilemap tool family. The five mutators declare <c>defaultGate:"enforce"</c>
        /// and <c>isMutating:true</c>; the read-only <c>get_used_cells</c> is <c>off</c>. All six
        /// belong to the <c>tilemap</c> group (the P8 stub now filled). Registered once at plugin
        /// enable; idempotent (the registry replaces on re-register).
        /// </summary>
        internal static void RegisterTilemapTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: TilemapCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "tilemap",
                handler: Create));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: TilemapSetTilesetToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "tilemap",
                handler: SetTileset));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: TilemapSetCellToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "tilemap",
                handler: SetCell));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: TilemapEraseCellToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "tilemap",
                handler: EraseCell));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: TilemapGetUsedCellsToolName,
                isMutating: false,
                defaultGate: "off",
                group: "tilemap",
                handler: GetUsedCells));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: TilemapClearToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "tilemap",
                handler: Clear));
        }

        // --- godot_open_mcp_tilemap_create ----------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_tilemap_create</c>. Creates a <c>TileMapLayer</c> node in
        /// the currently edited scene and returns its NodeData (same shape as <c>node_create</c>)
        /// so an agent can chain <c>node_path</c> straight into <c>set_tileset</c> / <c>set_cell</c>
        /// without a second find. The new node's owner is the edited scene root; the scene is
        /// marked unsaved.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>no_edited_scene</c>,
        /// <c>parent_not_found</c>, <c>create_failed</c>. Must not throw — the dispatcher surfaces
        /// uncaught exceptions as <c>execution_error</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Create(string body)
        {
            // Handler-level paths_hint guard — fires even under gate:"off" (matches the P4.x
            // mutator convention). The dispatch layer also rejects an empty hint when the effective
            // gate is enforce/warn; this guard covers the gate:"off" override.
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "tilemap_create is mutating; pass a non-empty paths_hint scoped to the edited scene path (res://...tscn).");

            var request = TilemapCreateBody.Parse(body);

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling tilemap_create.");

            // Resolve the parent (default: edited scene root). A non-empty parent_node_path that
            // does not resolve is a hard error — creating under a phantom parent would silently
            // reparent to the root, masking intent. Reuses NodeTools' resolver so the same path
            // vocabulary as node_create is accepted (Main, Main/Player, /root/Main/Player, .).
            Node parent = root;
            if (!string.IsNullOrEmpty(request.ParentNodePath))
            {
                parent = NodeTools.ResolvePath(root, request.ParentNodePath!);
                if (parent == null)
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Parent node not found at path '{request.ParentNodePath}'.");
            }

            TileMapLayer layer;
            try
            {
                // TileMapLayer is a concrete engine node present in every Godot 4.3+ build — no
                // ClassExists guard needed (unlike the generic node_create path, which must guard
                // arbitrary class names). `new TileMapLayer()` is the canonical C# instantiate.
                layer = new TileMapLayer();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate TileMapLayer: {e.Message}");
            }

            if (!string.IsNullOrEmpty(request.Name))
                layer.Name = request.Name;

            try
            {
                parent.AddChild(layer);
            }
            catch (System.Exception e)
            {
                // AddChild can fault (e.g. a parent that rejects the child). Free the orphan so it
                // is not leaked into the SceneTree without an owner.
                layer.QueueFree();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add TileMapLayer to parent: {e.Message}");
            }

            // Owner = edited scene root so the node persists in the .tscn on save. Godot-specific
            // step (Unity has no equivalent). TileMapLayer has no sub-tree to recurse over, so no
            // SetOwnerRecursive is needed (unlike the generic node_create instanced-scene path).
            layer.Owner = root;

            // TileMapLayer derives from Node2D, so a position field applies when present. Reuse the
            // same TryParseVector2 idiom node_create uses; a malformed vector is silently ignored
            // (the layer is still created at origin) — surfaced only via the result's default
            // position, matching node_create's best-effort transform contract.
            if (layer is Node2D n2d && TryParseVector2(request.Position, out var pos))
                n2d.Position = pos;

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();
            EditorInterface.Singleton.EditNode(layer);

            return ToolDispatchResult.Ok(NodeTools.ToNodeData(layer).ToJsonString());
        }

        // --- godot_open_mcp_tilemap_set_tileset -----------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_tilemap_set_tileset</c>. Loads a <c>TileSet</c> resource
        /// from a <c>res://</c> path and assigns it to the target <c>TileMapLayer</c>. No TileSet
        /// authoring — the resource must already exist (P12.1 scope; agents point at an existing
        /// .tres/.res TileSet).
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>, <c>wrong_node_type</c>,
        /// <c>resource_not_found</c>, <c>resource_load_failed</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult SetTileset(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "tilemap_set_tileset is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = TilemapSetTilesetBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "tilemap_set_tileset requires 'node_path' (the TileMapLayer to assign).");
            if (!request.HasTilesetPath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "tilemap_set_tileset requires 'tileset_path' (a res:// TileSet resource).");

            if (!TryResolveLayer(request.NodePath!, out var layer, out var resolveError))
                return resolveError;

            var tilesetPath = request.TilesetPath!;
            if (!ResourceLoader.Exists(tilesetPath))
                return ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"TileSet resource not found at '{tilesetPath}'.");

            TileSet tileset;
            try
            {
                var loaded = ResourceLoader.Load<TileSet>(tilesetPath);
                if (loaded == null)
                    return ToolDispatchResult.Fail(
                        "resource_load_failed",
                        $"Resource at '{tilesetPath}' exists but is not a TileSet.");
                tileset = loaded;
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("resource_load_failed",
                    $"Failed to load TileSet from '{tilesetPath}': {e.Message}");
            }

            layer.TileSet = tileset;

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(layer.GetPath().ToString())).Append(',');
            sb.Append("\"tilesetPath\":").Append(BridgeJson.EscapeString(tilesetPath));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- godot_open_mcp_tilemap_set_cell --------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_tilemap_set_cell</c>. Paints one cell on the target
        /// <c>TileMapLayer</c> using Godot's atlas addressing quadruple. A layer without a TileSet
        /// is rejected with <c>tileset_required</c> — Godot's <c>SetCell</c> silently no-ops when
        /// there is no TileSet or the source id does not exist, so a guard here turns that silent
        /// failure into a structured error the agent can act on.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>, <c>wrong_node_type</c>,
        /// <c>tileset_required</c>, <c>invalid_parameter</c> (source id not present in the
        /// TileSet). Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult SetCell(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "tilemap_set_cell is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = TilemapSetCellBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "tilemap_set_cell requires 'node_path' (the TileMapLayer to paint on).");

            if (!TryResolveLayer(request.NodePath!, out var layer, out var resolveError))
                return resolveError;

            if (layer.TileSet == null)
                return ToolDispatchResult.Fail(
                    "tileset_required",
                    $"TileMapLayer '{request.NodePath}' has no TileSet; call tilemap_set_tileset before tilemap_set_cell.");

            // Guard the source id: Godot's SetCell silently does nothing when the source id is not
            // present in the TileSet. Surface that as a structured error so an agent does not see
            // a silent no-op.
            var tileSet = layer.TileSet;
            if (!tileSet.HasSource(request.SourceId))
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    $"TileSet has no source with id {request.SourceId}; valid source ids are 0..{tileSet.GetSourceCount() - 1}.");

            var coords = new Vector2I(request.X, request.Y);
            var atlasCoords = new Vector2I(request.AtlasX, request.AtlasY);
            layer.SetCell(coords, request.SourceId, atlasCoords, request.AlternativeTile);

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(layer.GetPath().ToString())).Append(',');
            sb.Append("\"x\":").Append(request.X.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"y\":").Append(request.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"sourceId\":").Append(request.SourceId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"atlasX\":").Append(request.AtlasX.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"atlasY\":").Append(request.AtlasY.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"alternativeTile\":").Append(request.AlternativeTile.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- godot_open_mcp_tilemap_erase_cell ------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_tilemap_erase_cell</c>. Erases one cell from the target
        /// <c>TileMapLayer</c>. Erasing an already-empty cell is a no-op success (echoed with
        /// <c>erased:true</c>) — mirrors Unity's Tilemap.SetTile(null) idempotent contract.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>, <c>wrong_node_type</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult EraseCell(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "tilemap_erase_cell is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = TilemapEraseCellBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "tilemap_erase_cell requires 'node_path' (the TileMapLayer to erase from).");

            if (!TryResolveLayer(request.NodePath!, out var layer, out var resolveError))
                return resolveError;

            layer.EraseCell(new Vector2I(request.X, request.Y));

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(layer.GetPath().ToString())).Append(',');
            sb.Append("\"x\":").Append(request.X.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"y\":").Append(request.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"erased\":true");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- godot_open_mcp_tilemap_get_used_cells (read-only) --------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_tilemap_get_used_cells</c>. Lists the used cells on the
        /// target <c>TileMapLayer</c>, bounded by <c>max_results</c> (default 256, hard cap 2000).
        /// Read-only — no gate surface. Each cell carries the full addressing quadruple so an
        /// agent can echo it back into <c>set_cell</c>. The remainder beyond the cap is reported
        /// in <c>truncated</c>.
        ///
        /// <para>
        /// Structured failures: <c>missing_parameter</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult GetUsedCells(string body)
        {
            var request = TilemapGetUsedCellsBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "tilemap_get_used_cells requires 'node_path' (the TileMapLayer to read).");

            if (!TryResolveLayer(request.NodePath!, out var layer, out var resolveError))
                return resolveError;

            var usedCells = layer.GetUsedCells();
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
                var data = new TilemapCellData
                {
                    X = cell.X,
                    Y = cell.Y,
                    SourceId = layer.GetCellSourceId(cell),
                    AtlasX = layer.GetCellAtlasCoords(cell).X,
                    AtlasY = layer.GetCellAtlasCoords(cell).Y,
                    AlternativeTile = layer.GetCellAlternativeTile(cell),
                };
                data.AppendJsonTo(sb);
            }
            sb.Append("],\"count\":").Append(returned);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- godot_open_mcp_tilemap_clear -----------------------------------------

        /// <summary>
        /// Handler for <c>godot_open_mcp_tilemap_clear</c>. Clears every cell on the target
        /// <c>TileMapLayer</c> while keeping its TileSet assignment. Idempotent on an empty layer
        /// (returns <c>cleared:true</c>).
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>, <c>wrong_node_type</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Clear(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "tilemap_clear is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = TilemapClearBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "tilemap_clear requires 'node_path' (the TileMapLayer to clear).");

            if (!TryResolveLayer(request.NodePath!, out var layer, out var resolveError))
                return resolveError;

            layer.Clear();

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(layer.GetPath().ToString())).Append(',');
            // Echo whether a TileSet is still assigned so an agent can confirm Clear preserved it
            // (the documented contract: clear empties cells, keeps the TileSet).
            sb.Append("\"tilesetPath\":").Append(BridgeJson.EscapeString(layer.TileSet?.ResourcePath ?? null));
            sb.Append(",\"cleared\":true");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --- shared helpers -------------------------------------------------------

        /// <summary>
        /// Resolve <paramref name="nodePath"/> to a live <c>TileMapLayer</c> in the edited scene.
        /// Fails with <c>no_edited_scene</c> / <c>node_not_found</c> / <c>wrong_node_type</c> via
        /// <paramref name="error"/>. Returns true + the layer on success. Reuses
        /// <see cref="NodeTools.ResolvePath"/> so the same path vocabulary as <c>node_find</c> /
        /// <c>node_create</c> is accepted (Main, Main/Player, /root/Main/Player, .).
        /// </summary>
        static bool TryResolveLayer(string nodePath, out TileMapLayer layer, out ToolDispatchResult error)
        {
            layer = null!;
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
                    $"TileMapLayer not found at path '{nodePath}'.");
                return false;
            }

            if (node is TileMapLayer resolved)
            {
                layer = resolved;
                return true;
            }

            error = ToolDispatchResult.Fail(
                "wrong_node_type",
                $"Node at '{nodePath}' is a '{node.GetClass()}', not a TileMapLayer. " +
                "Godot 4.3+ uses TileMapLayer (the deprecated TileMap node is not supported).");
            return false;
        }

        /// <summary>
        /// Parse a <c>"x,y"</c> string into a <see cref="Vector2"/>. Returns false on a malformed
        /// string (the caller treats that as "no position applied" rather than erroring). Verbatim
        /// from NodeTools.TryParseVector2 — duplicated rather than widening NodeTools' surface for
        /// one helper; if a third caller needs it, promote to a shared util.
        /// </summary>
        static bool TryParseVector2(string? text, out Vector2 v)
        {
            v = Vector2.Zero;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var parts = text!.Trim().Split(',');
            if (parts.Length < 2)
                return false;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return false;
            v = new Vector2(x, y);
            return true;
        }
    }
}
#endif
