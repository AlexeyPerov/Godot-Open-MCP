#if TOOLS
#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// UI domain pack (P16.5) — five typed tools for Godot 4.3+ UI controls,
    /// containers, and themes:
    /// <c>godot_open_mcp_control_create</c> (create a <c>Control</c> subclass by
    /// <c>type</c> with starter full-rect anchors), <c>godot_open_mcp_control_modify</c>
    /// (bulk-patch allow-listed control scalars), <c>godot_open_mcp_container_add</c>
    /// (add a <c>VBoxContainer</c> / <c>HBoxContainer</c> / <c>GridContainer</c> /
    /// <c>MarginContainer</c> / <c>ScrollContainer</c>), <c>godot_open_mcp_container_set_layout</c>
    /// (patch container layout properties), and <c>godot_open_mcp_theme_apply</c>
    /// (assign a <c>Theme</c> resource to a control subtree). The fifth and final
    /// Phase 16 typed-editor-breadth family; mirrors the P16.x packs' folder
    /// layout, registration shape, gate policy, and group assignment.
    ///
    /// <para>
    /// <b>Fidelity:</b> adapt — Unity Open MCP's <c>UITools</c>
    /// (TypedTools/Extensions/UI/UITools.cs — ui_element_add /
    /// ui_element_modify / ui_layout_group_add) supplies the type-enum create +
    /// container-add + typed-modify pattern. The deltas from the Unity pattern
    /// are:
    /// (1) Godot UI is a tree of <c>Control</c> nodes (each a
    /// <c>CanvasItem</c>), not Unity uGUI components attached to a GameObject
    /// under a Canvas — create makes a node, not a component add; there is no
    /// separate Canvas / EventSystem step (Godot has implicit viewport-level
    /// input dispatch);
    /// (2) Godot's anchor / offset / size_flags layout model replaces Unity's
    /// <c>RectTransform</c> (anchorMin / anchorMax / sizeDelta / pivot) — the
    /// typed modify surface speaks Godot's vocabulary (<c>anchors_preset</c> /
    /// <c>offset_left</c> / <c>offset_right</c> / <c>offset_top</c> /
    /// <c>offset_bottom</c> / <c>custom_minimum_size</c> /
    /// <c>size_flags_horizontal</c> / <c>size_flags_vertical</c>);
    /// (3) Godot's <c>Theme</c> resource model replaces Unity uGUI's per-Control
    /// component properties — <c>theme_apply</c> loads a <c>.tres</c> Theme and
    /// assigns it to a <c>Control</c>'s <c>Theme</c> property (children inherit
    /// it);
    /// (4) Unity <c>renderMode</c> / <c>CanvasScaler</c> /
    /// <c>GraphicRaycaster</c> / <c>EventSystem</c> are NOT ported (no Godot
    /// equivalents in the same form — the viewport is the canvas);
    /// (5) Unity layout-group <c>padding</c> (<c>RectOffset</c>) / <c>spacing</c>
    /// (<c>Vector2</c>) / <c>childControlWidth</c> / <c>childForceExpand</c> are
    /// mapped to the Godot container vocabulary (<c>BoxContainer</c>
    /// <c>separation</c>, <c>GridContainer</c> <c>columns</c> + separation,
    /// <c>MarginContainer</c> <c>margin_*</c>, <c>ScrollContainer</c> follows the
    /// box separation).
    /// </para>
    ///
    /// <para>
    /// <b>Create path.</b> <c>control_create</c> resolves the parent (edited
    /// scene root by default), instantiates the control node via <c>new T()</c>,
    /// sets the name, parents it, assigns the owner (so it persists on save),
    /// applies the starter anchors (full-rect by default so the control is
    /// visible without manual layout), applies the optional starter text, and
    /// marks the scene unsaved.
    /// </para>
    ///
    /// <para>
    /// <b>Modify path.</b> <c>control_modify</c> resolves the node, type-checks
    /// it against <c>Control</c>, then walks the <c>fields</c> map top-level,
    /// applying each field through the allow-listed + clamped path,
    /// accumulating per-field results (applied + errors) so a single bad entry
    /// does not abort the batch.
    /// </para>
    ///
    /// <para>
    /// <b>Container path.</b> <c>container_add</c> resolves the parent,
    /// instantiates the container node (<c>VBoxContainer</c> /
    /// <c>HBoxContainer</c> / <c>GridContainer</c> / <c>MarginContainer</c> /
    /// <c>ScrollContainer</c>), parents it, assigns the owner, applies starter
    /// full-rect anchors, and marks the scene unsaved. <c>container_set_layout</c>
    /// resolves the node, type-checks it against <c>Container</c>, and patches
    /// the layout properties (separation for Box/Scroll containers, columns for
    /// GridContainer, margins for MarginContainer, alignment for all).
    /// </para>
    ///
    /// <para>
    /// <b>Theme path.</b> <c>theme_apply</c> resolves the target Control,
    /// loads the <c>Theme</c> resource at <c>theme_path</c> (a res://
    /// <c>.tres</c>), assigns it to the Control's <c>Theme</c> property, and
    /// (when <c>recursive:true</c>) also assigns it to every descendant Control
    /// explicitly. Marks the scene unsaved.
    /// </para>
    ///
    /// <para>
    /// <b>Gate contract.</b> All five handlers register with
    /// <see cref="BridgeToolEntry.DefaultGate"/> <c>"enforce"</c> and validate
    /// <c>paths_hint</c> themselves (mirrors the P4.x resource mutators and the
    /// P12.x / P16.x domain mutators). The dispatch layer rejects an empty hint
    /// when the effective gate is not <c>off</c>; the handler-level guard ALSO
    /// fires when an agent overrides with <c>gate:"off"</c>, so
    /// <c>paths_hint</c> is always required for these tools. <c>paths_hint</c>
    /// for the five mutators is the edited scene path (the <c>.tscn</c>).
    /// </para>
    ///
    /// <para>
    /// <b>Owner / persistence.</b> <c>control_create</c> + <c>container_add</c>
    /// set the new node's <c>Owner</c> to the edited scene root so it persists in
    /// the <c>.tscn</c> on save — same step every node creator performs. Every
    /// mutator calls <see cref="EditorInterface.MarkSceneAsUnsaved"/> +
    /// <see cref="SceneTools.MarkEditedSceneDirty"/> so the bridge-tracked dirty
    /// flag the <c>scene_open</c> guard consults stays honest.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): every handler touches
    /// <see cref="EditorInterface"/> and live <c>Control</c> / <c>Container</c> /
    /// <c>Theme</c> node objects. The pure-managed pieces
    /// (<see cref="ControlCreateBody"/> / <see cref="ControlModifyBody"/> /
    /// <see cref="ContainerAddBody"/> / <see cref="ContainerSetLayoutBody"/> /
    /// <see cref="ThemeApplyBody"/> / <see cref="ControlKind"/> /
    /// <see cref="ControlKindParser"/> / <see cref="ContainerKind"/> /
    /// <see cref="ContainerKindParser"/> / <see cref="UiPropertyClamp"/>) live
    /// outside this guard and are unit-tested.
    /// </summary>
    internal static class UiTools
    {
        internal const string ControlCreateToolName = "godot_open_mcp_control_create";
        internal const string ControlModifyToolName = "godot_open_mcp_control_modify";
        internal const string ContainerAddToolName = "godot_open_mcp_container_add";
        internal const string ContainerSetLayoutToolName = "godot_open_mcp_container_set_layout";
        internal const string ThemeApplyToolName = "godot_open_mcp_theme_apply";

        /// <summary>
        /// Register the UI tool family. All five handlers are mutating and declare
        /// <c>defaultGate:"enforce"</c> + <c>isMutating:true</c>, and all five
        /// belong to the <c>ui</c> group. Registered once at plugin enable;
        /// idempotent (the registry replaces on re-register).
        /// </summary>
        internal static void RegisterUiTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ControlCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "ui",
                handler: ControlCreate));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ControlModifyToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "ui",
                handler: ControlModify));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ContainerAddToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "ui",
                handler: ContainerAdd));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ContainerSetLayoutToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "ui",
                handler: ContainerSetLayout));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ThemeApplyToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "ui",
                handler: ThemeApply));
        }

        // ===========================================================================
        // 1. godot_open_mcp_control_create (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_control_create</c>. Resolves the parent
        /// (edited scene root by default), instantiates the control node
        /// (<c>Button</c> / <c>Label</c> / <c>LineEdit</c> / ... via
        /// <c>new T()</c>), parents it, assigns the owner, applies the starter
        /// anchors (full-rect by default), applies the optional starter text, and
        /// marks the scene unsaved. Returns the new node's NodeData so an agent
        /// can chain into <c>control_modify</c> / <c>node_modify</c>.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>invalid_parameter</c> (unknown type), <c>no_edited_scene</c>,
        /// <c>parent_not_found</c>, <c>create_failed</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult ControlCreate(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "control_create is mutating; pass a non-empty paths_hint scoped to the edited scene path (res://...tscn).");

            var request = ControlCreateBody.Parse(body);

            if (request.Kind == ControlKind.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "control_create requires 'type' to be one of: \"button\", \"label\", \"lineedit\", \"textedit\", \"texturerect\", \"colorrect\", \"progressbar\", \"checkbox\", \"checkbutton\", \"slider\", \"spinbox\", \"optionbutton\", \"separator\", \"ninepatchrect\", \"richtextlabel\".");

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling control_create.");

            Node parent = root;
            if (!string.IsNullOrEmpty(request.ParentNodePath))
            {
                parent = NodeTools.ResolvePath(root, request.ParentNodePath!);
                if (parent == null)
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Parent node not found at path '{request.ParentNodePath}'.");
            }

            // Instantiate the right control node class for the kind.
            Node node;
            try
            {
                node = CreateControlNode(request.Kind);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate control of kind '{ControlKindParser.ToSchemaString(request.Kind)}': {e.Message}");
            }

            if (!string.IsNullOrEmpty(request.Name))
                node.Name = request.Name;

            try
            {
                parent.AddChild(node);
            }
            catch (System.Exception e)
            {
                node.QueueFree();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add control node to parent: {e.Message}");
            }

            node.Owner = root;

            var applied = new List<string>();
            var warnings = new List<string>();

            // Apply starter anchors (full-rect by default so the control is
            // visible without manual layout). Control subclass required.
            if (node is Control ctrl)
            {
                ApplyAnchorsPreset(ctrl, request.AnchorsPreset, applied, warnings);
            }

            // Apply the optional starter text through the typed surface (Button /
            // Label / LineEdit / TextEdit / RichTextLabel / CheckBox / CheckButton
            // all expose a Text property; the others ignore it).
            if (!string.IsNullOrEmpty(request.Text))
                ApplyControlText(node, request.Text!, applied, warnings);

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();
            EditorInterface.Singleton.EditNode(node);

            // NodeData + the applied echo so an agent sees what landed.
            var nodeData = NodeTools.ToNodeData(node);
            var sb = new StringBuilder(256);
            nodeData.AppendJsonTo(sb);
            // Splice the applied + warnings into the NodeData JSON object.
            var json = sb.ToString();
            json = json.Substring(0, json.Length - 1); // strip trailing '}'
            json += ",\"applied\":";
            json += JsonStringArray(applied);
            json += ",\"warnings\":";
            json += JsonStringArray(warnings);
            json += "}";
            return ToolDispatchResult.Ok(json);
        }

        // ===========================================================================
        // 2. godot_open_mcp_control_modify (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_control_modify</c>. Bulk patch multiple
        /// allow-listed control scalars in one call. Resolves the control node,
        /// type-checks it against <c>Control</c>, then walks the <c>fields</c>
        /// map top-level, applying each field through the same validation +
        /// clamping path, accumulating per-field results (applied + errors) so a
        /// single bad entry does not abort the batch.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>missing_parameter</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult ControlModify(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "control_modify is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = ControlModifyBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "control_modify requires 'node_path' (the control node to mutate).");
            if (!request.HasFields)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "control_modify requires 'fields' (a JSON object of {field: value} entries).");

            if (!TryResolveControl(request.NodePath!, out var node, out var resolveError))
                return resolveError;

            // Walk the fields map top-level, extracting each {field → verbatim value} pair.
            var entries = ExtractFieldsMapEntries(request.FieldsRaw!);

            var applied = new List<string>();
            var errors = new List<string>();
            foreach (var (field, valueRaw) in entries)
            {
                var beforeCount = applied.Count;
                var localWarnings = new List<string>();
                ApplyControlField(node, field, valueRaw, applied, localWarnings);
                if (applied.Count == beforeCount)
                {
                    // The field was recognized but the value could not be applied.
                    errors.Add($"{field}: invalid_property_value" +
                        (localWarnings.Count > 0 ? $" ({localWarnings[0]})" : ""));
                }
            }

            if (applied.Count > 0)
            {
                EditorInterface.Singleton.MarkSceneAsUnsaved();
                SceneTools.MarkEditedSceneDirty();
            }

            var sb = new StringBuilder(192);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(node.GetPath().ToString())).Append(',');
            sb.Append("\"applied\":").Append(JsonStringArray(applied));
            if (errors.Count > 0)
            {
                sb.Append(",\"errors\":").Append(JsonStringArray(errors));
            }
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 3. godot_open_mcp_container_add (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_container_add</c>. Resolves the parent
        /// (edited scene root by default), instantiates the container node
        /// (<c>VBoxContainer</c> / <c>HBoxContainer</c> / <c>GridContainer</c> /
        /// <c>MarginContainer</c> / <c>ScrollContainer</c>) via <c>new T()</c>,
        /// parents it, assigns the owner, applies the starter anchors (full-rect
        /// by default), and marks the scene unsaved. Returns the new node's
        /// NodeData so an agent can chain into <c>container_set_layout</c>.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>invalid_parameter</c> (unknown type), <c>no_edited_scene</c>,
        /// <c>parent_not_found</c>, <c>create_failed</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult ContainerAdd(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "container_add is mutating; pass a non-empty paths_hint scoped to the edited scene path (res://...tscn).");

            var request = ContainerAddBody.Parse(body);

            if (request.Kind == ContainerKind.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "container_add requires 'type' to be one of: \"vbox\", \"hbox\", \"grid\", \"margin\", \"scroll\".");

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling container_add.");

            Node parent = root;
            if (!string.IsNullOrEmpty(request.ParentNodePath))
            {
                parent = NodeTools.ResolvePath(root, request.ParentNodePath!);
                if (parent == null)
                    return ToolDispatchResult.Fail(
                        "parent_not_found",
                        $"Parent node not found at path '{request.ParentNodePath}'.");
            }

            Node node;
            try
            {
                node = CreateContainerNode(request.Kind);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to instantiate container of kind '{ContainerKindParser.ToSchemaString(request.Kind)}': {e.Message}");
            }

            if (!string.IsNullOrEmpty(request.Name))
                node.Name = request.Name;

            try
            {
                parent.AddChild(node);
            }
            catch (System.Exception e)
            {
                node.QueueFree();
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to add container node to parent: {e.Message}");
            }

            node.Owner = root;

            var applied = new List<string>();
            var warnings = new List<string>();

            // Containers are Controls — apply starter anchors.
            if (node is Container container)
            {
                ApplyAnchorsPreset(container, request.AnchorsPreset, applied, warnings);
            }

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();
            EditorInterface.Singleton.EditNode(node);

            var nodeData = NodeTools.ToNodeData(node);
            var sb = new StringBuilder(256);
            nodeData.AppendJsonTo(sb);
            var json = sb.ToString();
            json = json.Substring(0, json.Length - 1); // strip trailing '}'
            json += ",\"applied\":";
            json += JsonStringArray(applied);
            json += ",\"warnings\":";
            json += JsonStringArray(warnings);
            json += "}";
            return ToolDispatchResult.Ok(json);
        }

        // ===========================================================================
        // 4. godot_open_mcp_container_set_layout (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_container_set_layout</c>. Bulk patch
        /// multiple allow-listed container layout scalars in one call. Resolves
        /// the container node, type-checks it against <c>Container</c>, then
        /// walks the <c>fields</c> map top-level, applying each field through the
        /// same validation + clamping path, accumulating per-field results.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>missing_parameter</c>, <c>no_edited_scene</c>,
        /// <c>node_not_found</c>, <c>wrong_node_type</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult ContainerSetLayout(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "container_set_layout is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = ContainerSetLayoutBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "container_set_layout requires 'node_path' (the container node to mutate).");
            if (!request.HasFields)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "container_set_layout requires 'fields' (a JSON object of {field: value} entries).");

            if (!TryResolveContainer(request.NodePath!, out var node, out var resolveError))
                return resolveError;

            // Walk the fields map top-level, extracting each {field → verbatim value} pair.
            var entries = ExtractFieldsMapEntries(request.FieldsRaw!);

            var applied = new List<string>();
            var errors = new List<string>();
            foreach (var (field, valueRaw) in entries)
            {
                var beforeCount = applied.Count;
                var localWarnings = new List<string>();
                ApplyContainerField(node, field, valueRaw, applied, localWarnings);
                if (applied.Count == beforeCount)
                {
                    errors.Add($"{field}: invalid_property_value" +
                        (localWarnings.Count > 0 ? $" ({localWarnings[0]})" : ""));
                }
            }

            if (applied.Count > 0)
            {
                EditorInterface.Singleton.MarkSceneAsUnsaved();
                SceneTools.MarkEditedSceneDirty();
            }

            var sb = new StringBuilder(192);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(node.GetPath().ToString())).Append(',');
            sb.Append("\"applied\":").Append(JsonStringArray(applied));
            if (errors.Count > 0)
            {
                sb.Append(",\"errors\":").Append(JsonStringArray(errors));
            }
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 5. godot_open_mcp_theme_apply (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_theme_apply</c>. Resolves the target
        /// Control, loads the <c>Theme</c> resource at <c>theme_path</c> (a
        /// res:// <c>.tres</c>), assigns it to the Control's <c>Theme</c>
        /// property, and (when <c>recursive:true</c>) also assigns it to every
        /// descendant Control explicitly. Marks the scene unsaved.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>,
        /// <c>missing_parameter</c>, <c>invalid_path</c>,
        /// <c>no_edited_scene</c>, <c>node_not_found</c>,
        /// <c>wrong_node_type</c>, <c>resource_not_found</c>,
        /// <c>resource_load_failed</c>, <c>wrong_resource_type</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult ThemeApply(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "theme_apply is mutating; pass a non-empty paths_hint scoped to the edited scene path.");

            var request = ThemeApplyBody.Parse(body);

            if (!request.HasNodePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "theme_apply requires 'node_path' (the Control that receives the theme).");
            if (!request.HasThemePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "theme_apply requires 'theme_path' (a res:// Theme .tres to assign).");

            string themePath;
            if (!ResourcePathNormalizer.TryRequireResFilePath(request.ThemePath!, out themePath, out var normError))
                return ToolDispatchResult.Fail("invalid_path", normError);

            if (!TryResolveControl(request.NodePath!, out var node, out var resolveError))
                return resolveError;

            // Load the Theme resource.
            if (!ResourceLoader.Exists(themePath))
                return ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"No Theme resource exists at '{themePath}'.");

            Theme? theme;
            try
            {
                theme = ResourceLoader.Load<Theme>(themePath);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "resource_load_failed",
                    $"Failed to load Theme at '{themePath}': {e.Message}");
            }

            if (theme == null)
                return ToolDispatchResult.Fail(
                    "wrong_resource_type",
                    $"'{themePath}' loaded but is not a Theme resource.");

            // Assign the theme to the root target.
            node.Theme = theme;
            int assignedCount = 1;

            // When recursive, also assign the theme to every descendant Control
            // explicitly (overrides per-child themes).
            if (request.Recursive == true)
                assignedCount += AssignThemeRecursive(node, theme);

            EditorInterface.Singleton.MarkSceneAsUnsaved();
            SceneTools.MarkEditedSceneDirty();

            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"nodePath\":").Append(BridgeJson.EscapeString(node.GetPath().ToString())).Append(',');
            sb.Append("\"themePath\":").Append(BridgeJson.EscapeString(themePath)).Append(',');
            sb.Append("\"recursive\":").Append(request.Recursive == true ? "true" : "false").Append(',');
            sb.Append("\"assignedCount\":").Append(assignedCount).Append(',');
            sb.Append("\"assigned\":true}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // Shared helpers — node creation
        // ===========================================================================

        /// <summary>
        /// Instantiate the concrete control node for a kind. The control node
        /// classes are concrete engine nodes — no ClassExists guard needed
        /// (mirrors the lighting pack's <c>new T()</c> path).
        /// </summary>
        static Node CreateControlNode(ControlKind kind)
        {
            switch (kind)
            {
                case ControlKind.Button: return new Button();
                case ControlKind.Label: return new Label();
                case ControlKind.LineEdit: return new LineEdit();
                case ControlKind.TextEdit: return new TextEdit();
                case ControlKind.TextureRect: return new TextureRect();
                case ControlKind.ColorRect: return new ColorRect();
                case ControlKind.ProgressBar: return new ProgressBar();
                case ControlKind.CheckBox: return new CheckBox();
                case ControlKind.CheckButton: return new CheckButton();
                case ControlKind.Slider: return new HSlider();
                case ControlKind.SpinBox: return new SpinBox();
                case ControlKind.OptionButton: return new OptionButton();
                case ControlKind.Separator: return new VSeparator();
                case ControlKind.NinePatchRect: return new NinePatchRect();
                case ControlKind.RichTextLabel: return new RichTextLabel();
                default:
                    throw new System.ArgumentException(
                        $"Unknown control kind '{kind}' (cannot instantiate).");
            }
        }

        /// <summary>
        /// Instantiate the concrete container node for a kind. The container
        /// node classes are concrete engine nodes.
        /// </summary>
        static Node CreateContainerNode(ContainerKind kind)
        {
            switch (kind)
            {
                case ContainerKind.VBox: return new VBoxContainer();
                case ContainerKind.HBox: return new HBoxContainer();
                case ContainerKind.Grid: return new GridContainer();
                case ContainerKind.Margin: return new MarginContainer();
                case ContainerKind.Scroll: return new ScrollContainer();
                default:
                    throw new System.ArgumentException(
                        $"Unknown container kind '{kind}' (cannot instantiate).");
            }
        }

        // ===========================================================================
        // Shared helpers — node resolution
        // ===========================================================================

        /// <summary>
        /// Resolve <paramref name="nodePath"/> to a Control node in the edited
        /// scene. Fails with <c>no_edited_scene</c> / <c>node_not_found</c> /
        /// <c>wrong_node_type</c> (the node is not a <c>Control</c>) via
        /// <paramref name="error"/>.
        /// </summary>
        static bool TryResolveControl(string nodePath, out Control node, out ToolDispatchResult error)
        {
            node = null!;
            error = null!;

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
            {
                error = ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn first.");
                return false;
            }

            var resolved = NodeTools.ResolvePath(root, nodePath);
            if (resolved == null)
            {
                error = ToolDispatchResult.Fail(
                    "node_not_found",
                    $"Node not found at path '{nodePath}'.");
                return false;
            }

            if (!(resolved is Control ctrl))
            {
                error = ToolDispatchResult.Fail(
                    "wrong_node_type",
                    $"Node at '{nodePath}' is a '{resolved.GetClass()}', not a Control.");
                return false;
            }

            node = ctrl;
            return true;
        }

        /// <summary>
        /// Resolve <paramref name="nodePath"/> to a Container node in the edited
        /// scene. Fails with <c>no_edited_scene</c> / <c>node_not_found</c> /
        /// <c>wrong_node_type</c> (the node is not a <c>Container</c>) via
        /// <paramref name="error"/>.
        /// </summary>
        static bool TryResolveContainer(string nodePath, out Container node, out ToolDispatchResult error)
        {
            node = null!;
            error = null!;

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
            {
                error = ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn first.");
                return false;
            }

            var resolved = NodeTools.ResolvePath(root, nodePath);
            if (resolved == null)
            {
                error = ToolDispatchResult.Fail(
                    "node_not_found",
                    $"Node not found at path '{nodePath}'.");
                return false;
            }

            if (!(resolved is Container container))
            {
                error = ToolDispatchResult.Fail(
                    "wrong_node_type",
                    $"Node at '{nodePath}' is a '{resolved.GetClass()}', not a Container.");
                return false;
            }

            node = container;
            return true;
        }

        // ===========================================================================
        // Shared helpers — anchors + text (starter layout)
        // ===========================================================================

        /// <summary>
        /// Apply a named anchors preset to a Control. Maps the preset name to
        /// Godot's <c>LayoutPreset</c> enum and calls
        /// <c>SetAnchorsAndOffsetsPreset</c> so both anchors AND offsets land
        /// (offsets matter — <c>SetAnchorsPreset</c> alone leaves the offsets at
        /// the default 0, which would collapse a full-rect control to nothing on
        /// a sized parent). Defaults to <c>full_rect</c> when the preset is null
        /// / empty / unrecognized (the common case — make the control visible
        /// without manual layout).
        /// </summary>
        static void ApplyAnchorsPreset(Control ctrl, string? preset,
            List<string> applied, List<string> warnings)
        {
            var p = ResolveAnchorsPreset(preset);
            if (p == null)
            {
                warnings.Add($"anchors_preset: unrecognized preset '{preset}' (ignored; the control keeps its default anchors).");
                return;
            }
            ctrl.SetAnchorsAndOffsetsPreset(p.Value);
            applied.Add("anchors_preset");
        }

        /// <summary>
        /// Resolve a preset name to a <c>LayoutPreset</c>. Returns null for an
        /// unrecognized name. When <paramref name="preset"/> is null / empty /
        /// whitespace, returns <c>FullRect</c> (the starter-layout default).
        /// </summary>
        static Control.LayoutPreset? ResolveAnchorsPreset(string? preset)
        {
            if (string.IsNullOrWhiteSpace(preset)) return Control.LayoutPreset.FullRect;
            switch (preset!.Trim().ToLowerInvariant())
            {
                case "full_rect": return Control.LayoutPreset.FullRect;
                case "top_left": return Control.LayoutPreset.TopLeft;
                case "top_right": return Control.LayoutPreset.TopRight;
                case "bottom_right": return Control.LayoutPreset.BottomRight;
                case "bottom_left": return Control.LayoutPreset.BottomLeft;
                case "center_left": return Control.LayoutPreset.CenterLeft;
                case "center_top": return Control.LayoutPreset.CenterTop;
                case "center_right": return Control.LayoutPreset.CenterRight;
                case "center_bottom": return Control.LayoutPreset.CenterBottom;
                case "center": return Control.LayoutPreset.Center;
                case "left_wide": return Control.LayoutPreset.LeftWide;
                case "top_wide": return Control.LayoutPreset.TopWide;
                case "right_wide": return Control.LayoutPreset.RightWide;
                case "bottom_wide": return Control.LayoutPreset.BottomWide;
                case "vcenter_wide": return Control.LayoutPreset.VCenterWide;
                case "hcenter_wide": return Control.LayoutPreset.HCenterWide;
                default: return null;
            }
        }

        /// <summary>
        /// Apply starter text to a control that exposes a <c>Text</c> property.
        /// Non-aborting — a control with no Text property surfaces a warning
        /// (the node is still created).
        /// </summary>
        static void ApplyControlText(Node node, string text,
            List<string> applied, List<string> warnings)
        {
            switch (node)
            {
                case Button b: b.Text = text; applied.Add("text"); return;
                case Label l: l.Text = text; applied.Add("text"); return;
                case LineEdit le: le.Text = text; applied.Add("text"); return;
                case TextEdit te: te.Text = text; applied.Add("text"); return;
                case RichTextLabel rtl: rtl.Text = text; applied.Add("text"); return;
                case CheckBox cb: cb.Text = text; applied.Add("text"); return;
                case CheckButton cbtn: cbtn.Text = text; applied.Add("text"); return;
                default:
                    warnings.Add($"text: node '{node.GetClass()}' has no Text property (ignored).");
                    return;
            }
        }

        // ===========================================================================
        // Shared helpers — control field application (the heart of the allow-list)
        // ===========================================================================

        /// <summary>
        /// Apply one allow-listed control scalar field to a Control node,
        /// appending the field name to <paramref name="applied"/> on success or a
        /// message to <paramref name="warnings"/> on a parse/type failure.
        /// Non-aborting. Centralized so <c>control_modify</c> shares one
        /// validation + clamping path.
        ///
        /// <para>
        /// Allow-listed fields:
        /// <list type="bullet">
        /// <item><description><c>text</c> (string) — every control with a Text
        /// property (Button / Label / LineEdit / ...).</description></item>
        /// <item><description><c>tooltip_text</c> (string) — every
        /// Control.</description></item>
        /// <item><description><c>disabled</c> (bool) — BaseButton subclasses
        /// (Button / CheckBox / CheckButton / OptionButton) only.
        /// </description></item>
        /// <item><description><c>color</c> (Color "r,g,b[,a]") — every Control
        /// (modulate).</description></item>
        /// <item><description><c>custom_minimum_size</c> (Vector2 "x,y", clamped
        /// non-negative) — every Control.</description></item>
        /// <item><description><c>offset_left</c> / <c>offset_right</c> /
        /// <c>offset_top</c> / <c>offset_bottom</c> (float) — every
        /// Control.</description></item>
        /// <item><description><c>size_flags_horizontal</c> /
        /// <c>size_flags_vertical</c> (int bitmask, or name "fill" / "expand" /
        /// "shrink_center" / "shrink_end") — every Control.</description></item>
        /// <item><description><c>anchors_preset</c> (name) — every Control (sets
        /// anchors AND offsets together).</description></item>
        /// <item><description><c>value</c> (float) — ProgressBar / Slider /
        /// SpinBox (the concrete Control clamps against its own min/max). Also
        /// accepts SpinBox (float written to Value).</description></item>
        /// </list>
        /// </para>
        /// </summary>
        static void ApplyControlField(Control node, string field, string? valueRaw,
            List<string> applied, List<string> warnings)
        {
            if (valueRaw == null)
            {
                warnings.Add($"{field}: value is null/absent (skipped).");
                return;
            }

            switch (field)
            {
                case "text":
                {
                    var text = StripQuotes(valueRaw);
                    switch (node)
                    {
                        case Button b: b.Text = text; applied.Add(field); return;
                        case Label l: l.Text = text; applied.Add(field); return;
                        case LineEdit le: le.Text = text; applied.Add(field); return;
                        case TextEdit te: te.Text = text; applied.Add(field); return;
                        case RichTextLabel rtl: rtl.Text = text; applied.Add(field); return;
                        case CheckBox cb: cb.Text = text; applied.Add(field); return;
                        case CheckButton cbtn: cbtn.Text = text; applied.Add(field); return;
                        default:
                            warnings.Add($"{field}: node '{node.GetClass()}' has no Text property.");
                            return;
                    }
                }
                case "tooltip_text":
                    node.TooltipText = StripQuotes(valueRaw);
                    applied.Add(field);
                    return;
                case "disabled":
                {
                    if (TryParseBool(StripQuotes(valueRaw), out var disabled))
                    {
                        // Disabled lives on BaseButton (Button / CheckBox /
                        // CheckButton / OptionButton), not on the base Control.
                        if (node is BaseButton btn) { btn.Disabled = disabled; applied.Add(field); return; }
                        warnings.Add($"{field}: node '{node.GetClass()}' has no disabled property (BaseButton subclasses only).");
                        return;
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as bool.");
                    return;
                }
                case "color":
                {
                    if (TryParseColor(StripQuotes(valueRaw), out var color))
                    {
                        node.Modulate = color;
                        applied.Add(field);
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as 'r,g,b[,a]'.");
                    return;
                }
                case "custom_minimum_size":
                {
                    if (TryParseVector2(StripQuotes(valueRaw), out var v))
                    {
                        var clamped = new Vector2(
                            UiPropertyClamp.ClampNonNegativeFloat(v.X),
                            UiPropertyClamp.ClampNonNegativeFloat(v.Y));
                        node.CustomMinimumSize = clamped;
                        applied.Add(field);
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as 'x,y'.");
                    return;
                }
                case "offset_left":
                {
                    if (TryParseFloat(StripQuotes(valueRaw), out var f))
                    {
                        node.OffsetLeft = f;
                        applied.Add(field);
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as float.");
                    return;
                }
                case "offset_right":
                {
                    if (TryParseFloat(StripQuotes(valueRaw), out var f))
                    {
                        node.OffsetRight = f;
                        applied.Add(field);
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as float.");
                    return;
                }
                case "offset_top":
                {
                    if (TryParseFloat(StripQuotes(valueRaw), out var f))
                    {
                        node.OffsetTop = f;
                        applied.Add(field);
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as float.");
                    return;
                }
                case "offset_bottom":
                {
                    if (TryParseFloat(StripQuotes(valueRaw), out var f))
                    {
                        node.OffsetBottom = f;
                        applied.Add(field);
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as float.");
                    return;
                }
                case "size_flags_horizontal":
                {
                    if (TryResolveSizeFlags(StripQuotes(valueRaw), out var flags))
                    {
                        node.SizeFlagsHorizontal = flags;
                        applied.Add(field);
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' (int bitmask or fill/expand/shrink_center/shrink_end).");
                    return;
                }
                case "size_flags_vertical":
                {
                    if (TryResolveSizeFlags(StripQuotes(valueRaw), out var flags))
                    {
                        node.SizeFlagsVertical = flags;
                        applied.Add(field);
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' (int bitmask or fill/expand/shrink_center/shrink_end).");
                    return;
                }
                case "anchors_preset":
                {
                    var p = ResolveAnchorsPreset(StripQuotes(valueRaw));
                    if (p == null)
                    {
                        warnings.Add($"{field}: unrecognized preset '{valueRaw}'.");
                        return;
                    }
                    node.SetAnchorsAndOffsetsPreset(p.Value);
                    applied.Add(field);
                    return;
                }
                case "value":
                {
                    if (TryParseFloat(StripQuotes(valueRaw), out var f))
                    {
                        switch (node)
                        {
                            case ProgressBar pb: pb.Value = f; applied.Add(field); return;
                            case Slider sl: sl.Value = f; applied.Add(field); return;
                            case SpinBox sb: sb.Value = f; applied.Add(field); return;
                            default:
                                warnings.Add($"{field}: node '{node.GetClass()}' has no value property (ProgressBar / Slider / SpinBox only).");
                                return;
                        }
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as float.");
                    return;
                }
                default:
                    warnings.Add($"{field}: unsupported_field (not in the control allow-list).");
                    return;
            }
        }

        // ===========================================================================
        // Shared helpers — container field application
        // ===========================================================================

        /// <summary>
        /// Apply one allow-listed container layout field to a Container node,
        /// appending the field name to <paramref name="applied"/> on success or a
        /// message to <paramref name="warnings"/> on a failure. Non-aborting.
        ///
        /// <para>
        /// Allow-listed fields:
        /// <list type="bullet">
        /// <item><description><c>separation</c> (int, clamped non-negative) —
        /// BoxContainer (VBox / HBox) only (the box's child spacing theme
        /// constant).</description></item>
        /// <item><description><c>columns</c> (int, clamped non-negative) —
        /// GridContainer only.</description></item>
        /// <item><description><c>alignment</c> (name begin / center / end) —
        /// BoxContainer (VBox / HBox) only (BoxContainer.AlignmentMode).
        /// </description></item>
        /// <item><description><c>margin_left</c> / <c>margin_right</c> /
        /// <c>margin_top</c> / <c>margin_bottom</c> (int, clamped non-negative) —
        /// MarginContainer only.</description></item>
        /// <item><description><c>anchors_preset</c> (name) — every Container
        /// (inherited from Control; sets anchors AND offsets
        /// together).</description></item>
        /// <item><description><c>custom_minimum_size</c> (Vector2 "x,y", clamped
        /// non-negative) — every Container (inherited from Control).
        /// </description></item>
        /// </list>
        /// </para>
        /// </summary>
        static void ApplyContainerField(Container node, string field, string? valueRaw,
            List<string> applied, List<string> warnings)
        {
            if (valueRaw == null)
            {
                warnings.Add($"{field}: value is null/absent (skipped).");
                return;
            }

            switch (field)
            {
                case "separation":
                {
                    if (TryParseInt(StripQuotes(valueRaw), out var sep))
                    {
                        var clamped = UiPropertyClamp.ClampNonNegativeInt(sep);
                        // separation is a BoxContainer theme constant (VBox /
                        // HBox). ScrollContainer is NOT a BoxContainer (it
                        // inherits Container directly) — it does not apply a
                        // box separation.
                        if (node is BoxContainer box) { box.AddThemeConstantOverride("separation", clamped); applied.Add(field); return; }
                        warnings.Add($"{field}: node '{node.GetClass()}' has no separation (BoxContainer: VBox/HBox only).");
                        return;
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as int.");
                    return;
                }
                case "columns":
                {
                    if (node is GridContainer grid)
                    {
                        if (TryParseInt(StripQuotes(valueRaw), out var cols))
                        {
                            grid.Columns = UiPropertyClamp.ClampNonNegativeInt(cols);
                            applied.Add(field);
                        }
                        else warnings.Add($"{field}: could not parse '{valueRaw}' as int.");
                        return;
                    }
                    warnings.Add($"{field}: node '{node.GetClass()}' has no columns (GridContainer only).");
                    return;
                }
                case "alignment":
                {
                    // alignment lives on BoxContainer (VBox / HBox). Other
                    // containers (Grid / Margin / Scroll) have no alignment.
                    if (node is BoxContainer box)
                    {
                        var mode = ResolveAlignment(StripQuotes(valueRaw));
                        if (mode == null)
                        {
                            warnings.Add($"{field}: unrecognized alignment '{valueRaw}' (begin / center / end).");
                            return;
                        }
                        box.Alignment = mode.Value;
                        applied.Add(field);
                        return;
                    }
                    warnings.Add($"{field}: node '{node.GetClass()}' has no alignment (BoxContainer: VBox/HBox only).");
                    return;
                }
                case "margin_left":
                case "margin_right":
                case "margin_top":
                case "margin_bottom":
                {
                    if (node is MarginContainer margin)
                    {
                        if (TryParseInt(StripQuotes(valueRaw), out var m))
                        {
                            var clamped = UiPropertyClamp.ClampNonNegativeInt(m);
                            if (field == "margin_left") { margin.AddThemeConstantOverride("margin_left", clamped); applied.Add(field); }
                            else if (field == "margin_right") { margin.AddThemeConstantOverride("margin_right", clamped); applied.Add(field); }
                            else if (field == "margin_top") { margin.AddThemeConstantOverride("margin_top", clamped); applied.Add(field); }
                            else { margin.AddThemeConstantOverride("margin_bottom", clamped); applied.Add(field); }
                            return;
                        }
                        else warnings.Add($"{field}: could not parse '{valueRaw}' as int.");
                        return;
                    }
                    warnings.Add($"{field}: node '{node.GetClass()}' has no margin (MarginContainer only).");
                    return;
                }
                case "anchors_preset":
                {
                    var p = ResolveAnchorsPreset(StripQuotes(valueRaw));
                    if (p == null)
                    {
                        warnings.Add($"{field}: unrecognized preset '{valueRaw}'.");
                        return;
                    }
                    node.SetAnchorsAndOffsetsPreset(p.Value);
                    applied.Add(field);
                    return;
                }
                case "custom_minimum_size":
                {
                    if (TryParseVector2(StripQuotes(valueRaw), out var v))
                    {
                        var clamped = new Vector2(
                            UiPropertyClamp.ClampNonNegativeFloat(v.X),
                            UiPropertyClamp.ClampNonNegativeFloat(v.Y));
                        node.CustomMinimumSize = clamped;
                        applied.Add(field);
                    }
                    else warnings.Add($"{field}: could not parse '{valueRaw}' as 'x,y'.");
                    return;
                }
                default:
                    warnings.Add($"{field}: unsupported_field (not in the container layout allow-list).");
                    return;
            }
        }

        // ===========================================================================
        // Shared helpers — fields-map walking (mirrors lighting pack verbatim)
        // ===========================================================================

        /// <summary>
        /// Walk a raw JSON object slice (the inner text between the outer braces
        /// of a <c>fields</c> map) and yield each top-level {field → verbatim
        /// value} pair. Each pair's value is the verbatim JSON token the per-field
        /// apply path re-parses. Honors backslash escapes + nested objects/arrays
        /// so a comma inside a nested string or object does not split the entry
        /// early. Verbatim from the P16.3 lighting pack.
        /// </summary>
        static List<(string field, string? valueRaw)> ExtractFieldsMapEntries(string slice)
        {
            var entries = new List<(string, string?)>();
            int i = 0;
            while (i < slice.Length)
            {
                // Skip whitespace + commas between entries.
                while (i < slice.Length && (char.IsWhiteSpace(slice[i]) || slice[i] == ',')) i++;
                if (i >= slice.Length) break;

                // Expect a quoted key.
                if (slice[i] != '"') { i++; continue; }
                var key = SliceQuotedString(slice, i, out var keyEnd);
                i = keyEnd;

                // Skip whitespace + the colon.
                while (i < slice.Length && (char.IsWhiteSpace(slice[i]) || slice[i] == ':')) i++;
                if (i >= slice.Length) break;

                // Slice the value (verbatim token).
                var value = SliceValueToken(slice, i, out var valueEnd);
                i = valueEnd;

                if (!string.IsNullOrEmpty(key))
                    entries.Add((key!, value));
            }
            return entries;
        }

        /// <summary>
        /// Slice a quoted JSON string starting at <paramref name="start"/>
        /// (the opening quote), returning the unescaped content + the index just
        /// past the closing quote (in <paramref name="end"/>). Verbatim from the
        /// P16.3 lighting pack.
        /// </summary>
        static string SliceQuotedString(string s, int start, out int end)
        {
            end = start + 1;
            var sb = new StringBuilder();
            while (end < s.Length)
            {
                var c = s[end];
                if (c == '\\' && end + 1 < s.Length)
                {
                    var nxt = s[end + 1];
                    switch (nxt)
                    {
                        case '"': sb.Append('"'); end += 2; continue;
                        case '\\': sb.Append('\\'); end += 2; continue;
                        case '/': sb.Append('/'); end += 2; continue;
                        case 'n': sb.Append('\n'); end += 2; continue;
                        case 'r': sb.Append('\r'); end += 2; continue;
                        case 't': sb.Append('\t'); end += 2; continue;
                        default: sb.Append(nxt); end += 2; continue;
                    }
                }
                if (c == '"') { end++; return sb.ToString(); }
                sb.Append(c);
                end++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Slice a verbatim JSON value token starting at
        /// <paramref name="start"/>. Verbatim from the P16.3 lighting pack.
        /// </summary>
        static string? SliceValueToken(string s, int start, out int end)
        {
            end = start;
            if (start >= s.Length) return null;

            // Quoted string.
            if (s[start] == '"')
            {
                var i = start + 1;
                while (i < s.Length)
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { i += 2; continue; }
                    if (s[i] == '"') { i++; break; }
                    i++;
                }
                end = i;
                return s.Substring(start, i - start);
            }

            // Object / array — balanced slice.
            if (s[start] == '{' || s[start] == '[')
            {
                char open = s[start];
                char close = open == '{' ? '}' : ']';
                int depth = 0;
                int i = start;
                bool inString = false;
                while (i < s.Length)
                {
                    var c = s[i];
                    if (inString)
                    {
                        if (c == '\\' && i + 1 < s.Length) { i += 2; continue; }
                        if (c == '"') inString = false;
                        i++;
                        continue;
                    }
                    if (c == '"') { inString = true; i++; continue; }
                    if (c == open) depth++;
                    else if (c == close)
                    {
                        depth--;
                        if (depth == 0) { i++; break; }
                    }
                    i++;
                }
                end = i;
                return s.Substring(start, i - start);
            }

            // Bare primitive — up to the next comma or close brace/bracket.
            var pe = start;
            while (pe < s.Length && s[pe] != ',' && s[pe] != '}' && s[pe] != ']') pe++;
            end = pe;
            return s.Substring(start, pe - start).Trim();
        }

        // ===========================================================================
        // Shared helpers — theme recursion
        // ===========================================================================

        /// <summary>
        /// Assign a Theme to every descendant Control under <c>root</c>,
        /// returning the count assigned (excludes the root — the caller already
        /// assigned it). Used by <c>theme_apply</c> when <c>recursive:true</c>.
        /// Excludes internal children (editor-only helpers).
        /// </summary>
        static int AssignThemeRecursive(Node root, Theme theme)
        {
            int count = 0;
            int childCount = root.GetChildCount(includeInternal: false);
            for (int i = 0; i < childCount; i++)
            {
                var child = root.GetChild(i, includeInternal: false);
                if (child == null) continue;
                if (child is Control c)
                {
                    c.Theme = theme;
                    count++;
                }
                count += AssignThemeRecursive(child, theme);
            }
            return count;
        }

        // ===========================================================================
        // Shared helpers — enum + size-flags resolution
        // ===========================================================================

        /// <summary>
        /// Resolve a size-flags value from either an int bitmask (the raw
        /// <c>Control.SizeFlags</c> value) or a friendly name (<c>fill</c> /
        /// <c>expand</c> / <c>shrink_center</c> / <c>shrink_end</c>). Accepts
        /// combinations by name as a "+"/"|" separated list (e.g.
        /// "fill+expand"). Returns false on an unparseable token.
        /// </summary>
        static bool TryResolveSizeFlags(string raw, out int flags)
        {
            flags = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            // Try int first (the raw bitmask).
            if (TryParseInt(raw, out var asInt))
            {
                flags = asInt;
                return true;
            }
            // Friendly names — accept "fill|expand" or "fill+expand".
            var parts = raw.Split('+', '|');
            bool any = false;
            foreach (var part in parts)
            {
                switch (part.Trim().ToLowerInvariant())
                {
                    case "fill": flags |= (int)Control.SizeFlags.Fill; any = true; break;
                    case "expand": flags |= (int)Control.SizeFlags.Expand; any = true; break;
                    case "shrink_center": flags |= (int)Control.SizeFlags.ShrinkCenter; any = true; break;
                    case "shrink_end": flags |= (int)Control.SizeFlags.ShrinkEnd; any = true; break;
                    default: return false;
                }
            }
            return any;
        }

        /// <summary>
        /// Resolve an alignment name (begin / center / end) to a
        /// <c>BoxContainer.AlignmentMode</c>. Returns null on an unrecognized
        /// name. Alignment lives on BoxContainer (VBox / HBox), not the base
        /// Container.
        /// </summary>
        static BoxContainer.AlignmentMode? ResolveAlignment(string raw)
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "begin":
                case "left":
                case "top":
                    return BoxContainer.AlignmentMode.Begin;
                case "center":
                    return BoxContainer.AlignmentMode.Center;
                case "end":
                case "right":
                case "bottom":
                    return BoxContainer.AlignmentMode.End;
                default: return null;
            }
        }

        // ===========================================================================
        // Shared helpers — parsing + formatting primitives (verbatim from P16.x)
        // ===========================================================================

        /// <summary>Parse a "x,y" string into a Vector2. Verbatim from NodeTools.
        /// Returns false on a malformed string.</summary>
        static bool TryParseVector2(string? text, out Vector2 v)
        {
            v = Vector2.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text!.Trim().Split(',');
            if (parts.Length < 2) return false;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var y)) return false;
            v = new Vector2(x, y);
            return true;
        }

        /// <summary>Parse a "r,g,b[,a]" string (0–1 floats) into a Color. Verbatim
        /// from NodeTools.TryParseColor. Returns false on a malformed string.
        /// Accepts 3-component input by filling alpha with 1.</summary>
        static bool TryParseColor(string? text, out Color c)
        {
            c = Colors.White;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text!.Trim().Split(',');
            if (parts.Length < 3) return false;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var r)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var g)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var b)) return false;
            float a = 1f;
            if (parts.Length >= 4 &&
                float.TryParse(parts[3].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var parsedA))
                a = parsedA;
            c = new Color(r, g, b, a);
            return true;
        }

        /// <summary>Parse a float with the invariant culture.</summary>
        static bool TryParseFloat(string? text, out float v)
        {
            v = 0f;
            if (string.IsNullOrWhiteSpace(text)) return false;
            return float.TryParse(text!.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out v);
        }

        /// <summary>Parse an int with the invariant culture.</summary>
        static bool TryParseInt(string? text, out int v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            return int.TryParse(text!.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out v);
        }

        /// <summary>Parse a bool from a raw string value. Accepts JSON bare
        /// tokens true/false (case-insensitive to tolerate "True").</summary>
        static bool TryParseBool(string raw, out bool value)
        {
            var t = raw.Trim().Trim('"');
            if (string.Equals(t, "true", System.StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
            if (string.Equals(t, "false", System.StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
            value = false;
            return false;
        }

        /// <summary>Strip surrounding quotes from a verbatim JSON string token
        /// (if present). Non-string tokens pass through unchanged.</summary>
        static string StripQuotes(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw!.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
                return raw.Substring(1, raw.Length - 2);
            return raw;
        }

        /// <summary>Render a string list as a JSON array of escaped strings.</summary>
        static string JsonStringArray(List<string> items)
        {
            var sb = new StringBuilder(64);
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(items[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
#endif
