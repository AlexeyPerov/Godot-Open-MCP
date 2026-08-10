#if TOOLS
#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Materials/Shaders domain pack (P16.2) — five typed tools for Godot 4.3+
    /// material and shader inspection/mutation:
    /// <c>godot_open_mcp_material_create</c> (create a material .tres),
    /// <c>godot_open_mcp_material_get_properties</c> (read-only property list),
    /// <c>godot_open_mcp_material_set_property</c> (set one property),
    /// <c>godot_open_mcp_material_set_shader</c> (assign a .gdshader), and
    /// <c>godot_open_mcp_shader_get_data</c> (read a .gdshader's uniforms). The
    /// second Phase 16 typed-editor-breadth family; mirrors the P16.1 settings
    /// pack's folder layout, registration shape, gate policy, and group
    /// assignment.
    ///
    /// <para>
    /// <b>Fidelity:</b> adapt — Unity Open MCP's <c>MaterialTools</c> + <c>ShaderTools</c>
    /// (TypedTools/MaterialTools.cs, TypedTools/ShaderTools.cs) supply the
    /// create/get-properties/set-property/set-shader shape and the shader uniform
    /// enumeration pattern. The deltas from the Unity pattern are:
    /// (1) Godot material classes replace Unity Material + Shader — three
    /// first-class families (<c>StandardMaterial3D</c> / <c>ORMMaterial3D</c> /
    /// <c>ShaderMaterial</c>) vs Unity's single Material + <c>Shader.Find</c>;
    /// (2) Godot's <c>.gdshader</c> uniform system replaces Unity's shader
    /// property reflection (<c>Shader.GetPropertyCount</c> / type / name) — the
    /// uniforms are enumerated via a temporary <c>ShaderMaterial</c>'s parameter
    /// list (the stable, version-portable surface for the uniform set);
    /// (3) Unity render-queue / SRP-batcher keyword APIs are intentionally NOT
    /// ported (the plan's skip fidelity tag);
    /// (4) no instance_id targeting — Godot materials are addressed by res:// path
    /// (the only durable handle a saved .tres carries), not by Unity's
    /// <c>GetInstanceID()</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Read path.</b> <c>material_get_properties</c> loads the .tres, walks
    /// <c>material.GetPropertyList()</c>, and serializes each property's current
    /// value via <see cref="Godot.Json.Stringify(Variant)"/> (Godot's own
    /// serializer — handles Color / Vector / texture-ref / enum uniformly). A
    /// property-list entry's <c>usage</c> flag is consulted to skip
    /// storage/category/group annotations (the ones Godot injects for the
    /// inspector, not real settable properties).
    /// </para>
    ///
    /// <para>
    /// <b>Write path.</b> <c>material_set_property</c> loads the .tres, validates
    /// the property exists on the material, re-parses the raw value token into a
    /// Variant via <c>Json.ParseString</c>, writes it via
    /// <c>material.Set(property, value)</c>, and persists with
    /// <c>ResourceSaver.Save</c>. <c>material_set_shader</c> loads the .tres,
    /// type-checks against <c>ShaderMaterial</c>, loads the shader, assigns it via
    /// <c>material.Shader = shader</c>, and saves — after which the shader's
    /// uniforms become settable properties reflected by
    /// <c>material_get_properties</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Shader uniform enumeration.</b> <c>shader_get_data</c> loads the
    /// .gdshader, creates a temporary <c>ShaderMaterial</c>, assigns the shader,
    /// and walks the material's <c>GetPropertyList()</c> — the ShaderMaterial
    /// surface exposes every uniform as a settable property with the uniform's
    /// name + type, which is the stable, always-available enumeration surface.
    /// The temporary material is freed before return.
    /// </para>
    ///
    /// <para>
    /// <b>Gate contract.</b> The three mutating handlers (<c>material_create</c> /
    /// <c>material_set_property</c> / <c>material_set_shader</c>) register with
    /// <see cref="BridgeToolEntry.DefaultGate"/> <c>"enforce"</c> and validate
    /// <c>paths_hint</c> themselves (mirrors the P4.x resource mutators and the
    /// P12.x/P16.1 domain mutators). The dispatch layer rejects an empty hint when
    /// the effective gate is not <c>off</c>; the handler-level guard ALSO fires
    /// when an agent overrides with <c>gate:"off"</c>, so <c>paths_hint</c> is
    /// always required for these tools. The read-only
    /// <c>material_get_properties</c> / <c>shader_get_data</c> have no gate
    /// surface. <c>paths_hint</c> for the mutators is the .tres path (or both the
    /// .tres + the .gdshader for <c>material_set_shader</c>).
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): every handler touches
    /// <see cref="ResourceLoader"/> / <see cref="ResourceSaver"/> /
    /// <see cref="Material"/> / <see cref="Shader"/> / <see cref="ShaderMaterial"/>.
    /// The pure-managed pieces (<see cref="MaterialCreateBody"/> /
    /// <see cref="MaterialGetPropertiesBody"/> / <see cref="MaterialSetPropertyBody"/>
    /// / <see cref="MaterialSetShaderBody"/> / <see cref="ShaderGetDataBody"/> /
    /// <see cref="MaterialKind"/> / <see cref="MaterialKindParser"/>) live outside
    /// this guard and are unit-tested.
    /// </summary>
    internal static class MaterialsTools
    {
        internal const string MaterialCreateToolName = "godot_open_mcp_material_create";
        internal const string MaterialGetPropertiesToolName = "godot_open_mcp_material_get_properties";
        internal const string MaterialSetPropertyToolName = "godot_open_mcp_material_set_property";
        internal const string MaterialSetShaderToolName = "godot_open_mcp_material_set_shader";
        internal const string ShaderGetDataToolName = "godot_open_mcp_shader_get_data";

        /// <summary>
        /// Register the materials/shaders tool family. The three mutators
        /// (<c>material_create</c> / <c>material_set_property</c> /
        /// <c>material_set_shader</c>) declare <c>defaultGate:"enforce"</c> and
        /// <c>isMutating:true</c>; the two read-only tools
        /// (<c>material_get_properties</c> / <c>shader_get_data</c>) are
        /// <c>off</c>. All five belong to the <c>materials</c> group. Registered
        /// once at plugin enable; idempotent (the registry replaces on
        /// re-register).
        /// </summary>
        internal static void RegisterMaterialsTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: MaterialCreateToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "materials",
                handler: MaterialCreate));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: MaterialGetPropertiesToolName,
                isMutating: false,
                defaultGate: "off",
                group: "materials",
                handler: MaterialGetProperties));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: MaterialSetPropertyToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "materials",
                handler: MaterialSetProperty));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: MaterialSetShaderToolName,
                isMutating: true,
                defaultGate: "enforce",
                group: "materials",
                handler: MaterialSetShader));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ShaderGetDataToolName,
                isMutating: false,
                defaultGate: "off",
                group: "materials",
                handler: ShaderGetData));
        }

        // ===========================================================================
        // 1. godot_open_mcp_material_create (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_material_create</c>. Instantiates a
        /// <c>StandardMaterial3D</c> / <c>ORMMaterial3D</c> / <c>ShaderMaterial</c>
        /// via <c>ClassDB.Instantiate</c> and persists it at the requested
        /// <c>res://</c> destination through <c>ResourceSaver.Save</c>. For the
        /// shader kind, an optional <c>shader_path</c> (res:// .gdshader) assigns
        /// the shader at create time so the material's uniforms are immediately
        /// settable.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>invalid_parameter</c> (unknown kind), <c>invalid_path</c>,
        /// <c>material_exists</c> (destination taken, no <c>overwrite:true</c>),
        /// <c>shader_not_found</c> (shader kind + bad/absent shader_path),
        /// <c>resource_type_invalid</c>, <c>resource_save_failed</c>,
        /// <c>filesystem_unavailable</c>.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult MaterialCreate(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "material_create is mutating; pass a non-empty paths_hint scoped to the " +
                    "destination .tres path.");

            var request = MaterialCreateBody.Parse(body);

            if (!request.HasResourcePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "material_create requires 'resource_path' (the new res:// destination " +
                    "ending in .tres or .res).");

            if (request.Kind == MaterialKind.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "material_create requires 'kind' to be one of: \"standard\", \"orm\", \"shader\".");

            // Normalize the destination path.
            if (!ResourcePathNormalizer.TryRequireResFilePath(request.ResourcePath!, out var resPath, out var normError))
                return ToolDispatchResult.Fail("invalid_path", normError);

            // paths_hint must contain the destination path (gate scope + handler guard).
            if (!HintContains(pathsHint, resPath))
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    $"paths_hint must contain the destination material path '{resPath}'.");

            // Filesystem must be ready.
            var efs = EditorInterface.Singleton.GetResourceFilesystem();
            if (efs == null || !efs.GetFilesystem().IsReady())
                return ToolDispatchResult.Fail(
                    "filesystem_unavailable",
                    "The editor filesystem is not ready; wait for the import scan to finish and retry.");

            // Destination existence check (unless overwrite was requested).
            if (ResourceLoader.Exists(resPath) && request.Overwrite != true)
                return ToolDispatchResult.Fail(
                    "material_exists",
                    $"A material already exists at '{resPath}'. material_create does not overwrite by " +
                    "default — pass overwrite:true to replace it.");

            var className = MaterialKindParser.ToClassName(request.Kind);
            if (!ValidateInstantiableResourceType(className, out var typeError))
                return ToolDispatchResult.Fail("resource_type_invalid", typeError);

            // Instantiate. ClassDB.Instantiate returns a Variant; unwrap to Resource.
            Resource? resource;
            try
            {
                var obj = ClassDB.Instantiate(className);
                resource = obj.As<Resource>();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "resource_type_invalid",
                    $"Failed to instantiate '{className}': {e.Message}");
            }

            if (resource == null)
                return ToolDispatchResult.Fail(
                    "resource_type_invalid",
                    $"'{className}' instantiated but is not a Resource.");

            if (!(resource is Material material))
            {
                resource.Free();
                return ToolDispatchResult.Fail(
                    "resource_type_invalid",
                    $"'{className}' instantiated but is not a Material.");
            }

            // Shader kind: assign the shader at create time when provided.
            Shader? shader = null;
            if (request.Kind == MaterialKind.Shader)
            {
                if (!request.HasShaderPath)
                {
                    material.Free();
                    return ToolDispatchResult.Fail(
                        "missing_parameter",
                        "material_create with kind:\"shader\" requires 'shader_path' (a res:// .gdshader).");
                }
                if (!ResourcePathNormalizer.TryRequireResFilePath(request.ShaderPath!, out var shaderPath, out var shaderPathError))
                {
                    material.Free();
                    return ToolDispatchResult.Fail("invalid_path", shaderPathError);
                }
                if (!ResourceLoader.Exists(shaderPath))
                {
                    material.Free();
                    return ToolDispatchResult.Fail(
                        "shader_not_found",
                        $"No shader exists at '{shaderPath}'.");
                }
                try
                {
                    shader = ResourceLoader.Load<Shader>(shaderPath);
                }
                catch (System.Exception e)
                {
                    material.Free();
                    return ToolDispatchResult.Fail(
                        "shader_not_found",
                        $"Failed to load shader at '{shaderPath}': {e.Message}");
                }
                if (shader == null)
                {
                    material.Free();
                    return ToolDispatchResult.Fail(
                        "shader_not_found",
                        $"ResourceLoader.Load<Shader> returned null for '{shaderPath}'.");
                }
                if (!(material is ShaderMaterial shaderMaterial))
                {
                    material.Free();
                    return ToolDispatchResult.Fail(
                        "resource_type_invalid",
                        "Internal error: ShaderMaterial kind did not produce a ShaderMaterial instance.");
                }
                shaderMaterial.Shader = shader;
            }

            // Set the resource path so ResourceSaver writes to the requested destination.
            material.ResourcePath = resPath;

            // Save.
            Error saveErr;
            try
            {
                saveErr = ResourceSaver.Save(material, resPath);
            }
            catch (System.Exception e)
            {
                material.Free();
                return ToolDispatchResult.Fail(
                    "resource_save_failed",
                    $"ResourceSaver.Save threw for '{resPath}': {e.Message}");
            }

            if (saveErr != Error.Ok)
            {
                material.Free();
                return ToolDispatchResult.Fail(
                    "resource_save_failed",
                    $"ResourceSaver.Save returned {saveErr} for '{resPath}'.");
            }

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

            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"resourcePath\":").Append(BridgeJson.EscapeString(resPath)).Append(',');
            sb.Append("\"kind\":").Append(BridgeJson.EscapeString(
                MaterialKindParser.ToSchemaString(request.Kind))).Append(',');
            sb.Append("\"type\":").Append(BridgeJson.EscapeString(material.GetClass())).Append(',');
            sb.Append("\"shader\":").Append(
                shader != null ? BridgeJson.EscapeString(shader.ResourcePath ?? "") : "null").Append(',');
            sb.Append("\"saved\":true}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 2. godot_open_mcp_material_get_properties (read-only)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_material_get_properties</c>. Loads a
        /// <c>.tres</c> material, walks <c>GetPropertyList()</c>, and returns each
        /// property's name + type + current value (serialized via Godot's own
        /// <c>Json.Stringify</c>). Property-list entries Godot injects for the
        /// inspector (storage annotations, category/group entries) are filtered out
        /// by their usage flags so only real settable properties appear.
        ///
        /// <para>
        /// Structured failures: <c>missing_parameter</c>, <c>invalid_path</c>,
        /// <c>resource_not_found</c>, <c>resource_load_failed</c>,
        /// <c>wrong_resource_type</c>, <c>execution_error</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult MaterialGetProperties(string body)
        {
            var request = MaterialGetPropertiesBody.Parse(body);

            if (!request.HasResourcePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "material_get_properties requires 'resource_path' (a res:// path or uid:// identifier).");

            if (!TryLoadMaterial(request.ResourcePath!, out var material, out var loadError))
                return loadError;

            try
            {
                var sb = new StringBuilder(256);
                sb.Append('{');
                sb.Append("\"resourcePath\":").Append(BridgeJson.EscapeString(material.ResourcePath ?? "")).Append(',');
                sb.Append("\"type\":").Append(BridgeJson.EscapeString(material.GetClass())).Append(',');
                sb.Append("\"properties\":[");
                int count = 0;
                Godot.Collections.Array props;
                try
                {
                    props = material.GetPropertyList();
                }
                catch (System.Exception e)
                {
                    return ToolDispatchResult.Fail("execution_error",
                        $"GetPropertyList failed: {e.Message}");
                }

                foreach (var propVariant in props)
                {
                    var propDict = propVariant.As<Godot.Collections.Dictionary>();
                    if (propDict == null) continue;

                    var nameVar = propDict["name"];
                    if (nameVar.VariantType != Variant.Type.String) continue;
                    var name = nameVar.AsString();
                    if (string.IsNullOrEmpty(name)) continue;

                    var usage = (uint)(long)propDict["usage"].AsInt64();

                    // Skip inspector-only entries: category/group/subgroup annotations and
                    // non-property storage entries. Godot flags these in the usage bitmask;
                    // real settable properties carry the PropertyUsageFlags.Storage flag.
                    const uint CategoryFlag = 0x2;       // PROPERTY_USAGE_CATEGORY
                    const uint GroupFlag = 0x4;          // PROPERTY_USAGE_GROUP
                    const uint SubgroupFlag = 0x8;       // PROPERTY_USAGE_SUBGROUP
                    if ((usage & (CategoryFlag | GroupFlag | SubgroupFlag)) != 0) continue;

                    // The "script" / "resource_local_to_scene" / "resource_name" base Resource
                    // properties always appear; include them (an agent may set resource_name),
                    // but skip the internal "Refs"/"Metadata"/"Resource"/"Script" groups by
                    // name to keep the output focused on the material's real properties.
                    if (name == "resource_path" || name == "script") continue;

                    Variant value;
                    try
                    {
                        value = material.Get(name);
                    }
                    catch (System.Exception)
                    {
                        // A Get failure on one property is non-fatal — render null and move on.
                        value = default;
                    }

                    var typeStr = propDict["type"].ToString();
                    if (count > 0) sb.Append(',');
                    sb.Append('{');
                    sb.Append("\"name\":").Append(BridgeJson.EscapeString(name)).Append(',');
                    sb.Append("\"type\":").Append(BridgeJson.EscapeString(typeStr)).Append(',');
                    sb.Append("\"value\":").Append(SerializeValue(value));
                    sb.Append('}');
                    count++;
                }
                sb.Append("],\"count\":").Append(count.ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ===========================================================================
        // 3. godot_open_mcp_material_set_property (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_material_set_property</c>. Loads a
        /// <c>.tres</c> material, validates the property exists on it, re-parses the
        /// raw value token into a Variant via <c>Json.ParseString</c>, writes it via
        /// <c>material.Set(property, value)</c>, and persists with
        /// <c>ResourceSaver.Save</c>. The value token is re-parsed verbatim so any
        /// Godot Variant type (Color / Vector / scalar / texture-ref / enum)
        /// round-trips.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>invalid_path</c>, <c>resource_not_found</c>, <c>resource_load_failed</c>,
        /// <c>wrong_resource_type</c>, <c>property_not_found</c>,
        /// <c>resource_save_failed</c>, <c>execution_error</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult MaterialSetProperty(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "material_set_property is mutating; pass a non-empty paths_hint scoped to the " +
                    "material .tres path.");

            var request = MaterialSetPropertyBody.Parse(body);

            if (!request.HasResourcePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "material_set_property requires 'resource_path' (a res:// path or uid:// identifier).");
            if (!request.HasProperty)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "material_set_property requires 'property' (the property name to set).");

            string resPath;
            if (!NormalizeResPath(request.ResourcePath!, out resPath, out var normError))
                return ToolDispatchResult.Fail("invalid_path", normError);

            if (!HintContains(pathsHint, resPath))
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    $"paths_hint must contain the material path '{resPath}'.");

            if (!TryLoadMaterial(request.ResourcePath!, out var material, out var loadError))
                return loadError;

            var property = request.Property!;

            // Validate the property exists on the material (reject typos / unknown names).
            if (!HasProperty(material, property))
                return ToolDispatchResult.Fail(
                    "property_not_found",
                    $"Property '{property}' does not exist on material of type '{material.GetClass()}'.");

            Variant value;
            try
            {
                value = ParseValueToken(request.ValueRaw);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "execution_error",
                    $"Could not parse value for property '{property}': {e.Message}");
            }

            try
            {
                material.Set(property, value);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "execution_error",
                    $"material.Set('{property}', value) failed: {e.Message}");
            }

            Error saveErr;
            try
            {
                saveErr = ResourceSaver.Save(material, resPath);
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
                EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(resPath);
            }
            catch
            {
                // UpdateFile failure is non-fatal.
            }

            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"resourcePath\":").Append(BridgeJson.EscapeString(resPath)).Append(',');
            sb.Append("\"property\":").Append(BridgeJson.EscapeString(property)).Append(',');
            sb.Append("\"value\":").Append(SerializeValue(value)).Append(',');
            sb.Append("\"saved\":true}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 4. godot_open_mcp_material_set_shader (mutating, gated)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_material_set_shader</c>. Loads a
        /// <c>.tres</c> material, type-checks it against <c>ShaderMaterial</c>,
        /// loads the <c>.gdshader</c> at <c>shader_path</c>, assigns it via
        /// <c>material.Shader = shader</c>, and persists with
        /// <c>ResourceSaver.Save</c>. After the assignment the shader's uniforms
        /// become settable properties, reflected by a subsequent
        /// <c>material_get_properties</c>.
        ///
        /// <para>
        /// Structured failures: <c>paths_hint_required</c>, <c>missing_parameter</c>,
        /// <c>invalid_path</c>, <c>resource_not_found</c>, <c>resource_load_failed</c>,
        /// <c>wrong_resource_type</c>, <c>shader_not_found</c>,
        /// <c>resource_save_failed</c>, <c>execution_error</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult MaterialSetShader(string body)
        {
            var pathsHint = BridgeRequestBody.ExtractPathsHint(body);
            if (pathsHint == null || pathsHint.Length == 0)
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    "material_set_shader is mutating; pass a non-empty paths_hint scoped to the " +
                    "material .tres path (and the .gdshader path).");

            var request = MaterialSetShaderBody.Parse(body);

            if (!request.HasResourcePath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "material_set_shader requires 'resource_path' (the ShaderMaterial .tres to mutate).");
            if (!request.HasShaderPath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "material_set_shader requires 'shader_path' (the res:// .gdshader to assign).");

            string resPath;
            if (!NormalizeResPath(request.ResourcePath!, out resPath, out var normError))
                return ToolDispatchResult.Fail("invalid_path", normError);

            if (!ResourcePathNormalizer.TryRequireResFilePath(request.ShaderPath!, out var shaderPath, out var shaderPathError))
                return ToolDispatchResult.Fail("invalid_path", shaderPathError);

            if (!HintContains(pathsHint, resPath))
                return ToolDispatchResult.Fail(
                    "paths_hint_required",
                    $"paths_hint must contain the material path '{resPath}'.");

            if (!TryLoadMaterial(request.ResourcePath!, out var material, out var loadError))
                return loadError;

            if (!(material is ShaderMaterial shaderMaterial))
                return ToolDispatchResult.Fail(
                    "wrong_resource_type",
                    $"'{resPath}' is a '{material.GetClass()}', not a ShaderMaterial — set_shader " +
                    "only applies to ShaderMaterial resources.");

            if (!ResourceLoader.Exists(shaderPath))
                return ToolDispatchResult.Fail(
                    "shader_not_found",
                    $"No shader exists at '{shaderPath}'.");

            Shader? shader;
            try
            {
                shader = ResourceLoader.Load<Shader>(shaderPath);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "shader_not_found",
                    $"Failed to load shader at '{shaderPath}': {e.Message}");
            }

            if (shader == null)
                return ToolDispatchResult.Fail(
                    "shader_not_found",
                    $"ResourceLoader.Load<Shader> returned null for '{shaderPath}'.");

            try
            {
                shaderMaterial.Shader = shader;
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "execution_error",
                    $"Assigning the shader failed: {e.Message}");
            }

            Error saveErr;
            try
            {
                saveErr = ResourceSaver.Save(shaderMaterial, resPath);
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

            try
            {
                EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(resPath);
            }
            catch
            {
                // UpdateFile failure is non-fatal.
            }

            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"resourcePath\":").Append(BridgeJson.EscapeString(resPath)).Append(',');
            sb.Append("\"shader\":").Append(BridgeJson.EscapeString(shaderPath)).Append(',');
            sb.Append("\"saved\":true}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // 5. godot_open_mcp_shader_get_data (read-only)
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_shader_get_data</c>. Loads a
        /// <c>.gdshader</c> resource, creates a temporary <c>ShaderMaterial</c>,
        /// assigns the shader (so the uniform set materializes as properties on the
        /// material), and walks the material's <c>GetPropertyList()</c> to enumerate
        /// the uniforms. The temporary material is freed before return. The result
        /// carries the shader path + a uniform array ({name, type}).
        ///
        /// <para>
        /// The ShaderMaterial parameter list is the stable, always-available surface
        /// for the uniform set — the Shader class's own uniform enumeration surface
        /// varies across Godot versions, while the ShaderMaterial parameter list is
        /// the same API an agent uses to set uniform values via
        /// <c>material_set_property</c>.
        /// </para>
        ///
        /// <para>
        /// Structured failures: <c>missing_parameter</c>, <c>invalid_path</c>,
        /// <c>resource_not_found</c>, <c>resource_load_failed</c>,
        /// <c>wrong_resource_type</c>, <c>shader_parse_error</c>,
        /// <c>execution_error</c>. Must not throw.
        /// </para>
        /// </summary>
        internal static ToolDispatchResult ShaderGetData(string body)
        {
            var request = ShaderGetDataBody.Parse(body);

            if (!request.HasShaderPath)
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "shader_get_data requires 'shader_path' (a res:// .gdshader).");

            if (!ResourcePathNormalizer.TryRequireResFilePath(request.ShaderPath!, out var shaderPath, out var normError))
                return ToolDispatchResult.Fail("invalid_path", normError);

            if (!ResourceLoader.Exists(shaderPath))
                return ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"No shader exists at '{shaderPath}'.");

            Shader? shader;
            try
            {
                shader = ResourceLoader.Load<Shader>(shaderPath);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail(
                    "resource_load_failed",
                    $"Failed to load shader at '{shaderPath}': {e.Message}");
            }

            if (shader == null)
                return ToolDispatchResult.Fail(
                    "resource_load_failed",
                    $"ResourceLoader.Load<Shader> returned null for '{shaderPath}'.");

            // Create a temporary ShaderMaterial to materialize the uniform set as properties.
            ShaderMaterial? probe = null;
            try
            {
                probe = new ShaderMaterial();
                probe.Shader = shader;
            }
            catch (System.Exception e)
            {
                if (probe != null) probe.Free();
                return ToolDispatchResult.Fail(
                    "shader_parse_error",
                    $"The shader at '{shaderPath}' could not be assigned to a ShaderMaterial " +
                    $"(it may have compile errors): {e.Message}");
            }

            try
            {
                Godot.Collections.Array props;
                try
                {
                    props = probe.GetPropertyList();
                }
                catch (System.Exception e)
                {
                    return ToolDispatchResult.Fail(
                        "shader_parse_error",
                        $"Enumerating the shader's uniforms failed (the shader may have compile " +
                        $"errors): {e.Message}");
                }

                var sb = new StringBuilder(256);
                sb.Append('{');
                sb.Append("\"shaderPath\":").Append(BridgeJson.EscapeString(shaderPath)).Append(',');
                sb.Append("\"uniforms\":[");

                int count = 0;
                foreach (var propVariant in props)
                {
                    var propDict = propVariant.As<Godot.Collections.Dictionary>();
                    if (propDict == null) continue;

                    var nameVar = propDict["name"];
                    if (nameVar.VariantType != Variant.Type.String) continue;
                    var name = nameVar.AsString();
                    if (string.IsNullOrEmpty(name)) continue;

                    var usage = (uint)(long)propDict["usage"].AsInt64();
                    const uint CategoryFlag = 0x2;
                    const uint GroupFlag = 0x4;
                    const uint SubgroupFlag = 0x8;
                    if ((usage & (CategoryFlag | GroupFlag | SubgroupFlag)) != 0) continue;

                    // Skip the base ShaderMaterial / Resource framework properties — only the
                    // shader's uniforms are relevant.
                    if (name == "shader" || name == "resource_local_to_scene" ||
                        name == "resource_path" || name == "resource_name" || name == "script")
                        continue;

                    var typeStr = propDict["type"].ToString();
                    if (count > 0) sb.Append(',');
                    sb.Append('{');
                    sb.Append("\"name\":").Append(BridgeJson.EscapeString(name)).Append(',');
                    sb.Append("\"type\":").Append(BridgeJson.EscapeString(typeStr));
                    sb.Append('}');
                    count++;
                }
                sb.Append("],\"count\":").Append(count.ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
            finally
            {
                probe.Free();
            }
        }

        // ===========================================================================
        // Shared helpers
        // ===========================================================================

        /// <summary>
        /// Serialize a Variant to a JSON token for property output. Uses Godot's
        /// <see cref="Godot.Json.Stringify(Variant)"/> so every Variant type an
        /// agent might read (Color, Vector2/3/4, Array, Dictionary, object refs)
        /// round-trips through Godot's own serializer. A Nil variant renders as
        /// <c>null</c>.
        /// </summary>
        static string SerializeValue(Variant value)
        {
            if (value.VariantType == Variant.Type.Nil) return "null";

            // A Resource (e.g. a Texture2D assigned to a material slot) renders as a
            // compact {type, resourcePath} leaf rather than Godot's opaque object dump —
            // an agent setting that slot needs the res:// path, not the native object id.
            if (value.VariantType == Variant.Type.Object)
            {
                var obj = value.AsGodotObject();
                if (obj is Resource res)
                {
                    var sb = new StringBuilder(96);
                    sb.Append('{');
                    sb.Append("\"type\":").Append(BridgeJson.EscapeString(res.GetClass())).Append(',');
                    sb.Append("\"resourcePath\":").Append(BridgeJson.EscapeString(res.ResourcePath ?? ""));
                    sb.Append('}');
                    return sb.ToString();
                }
                if (obj != null)
                {
                    var sb = new StringBuilder(64);
                    sb.Append('{');
                    sb.Append("\"type\":").Append(BridgeJson.EscapeString(obj.GetClass())).Append(',');
                    sb.Append("\"resourcePath\":null");
                    sb.Append('}');
                    return sb.ToString();
                }
                return "null";
            }

            return Godot.Json.Stringify(value, "", sortKeys: true, fullPrecision: true);
        }

        /// <summary>
        /// Re-parse a raw JSON value token into a Variant. A null/absent token
        /// yields a Nil variant (which clears the property when written via
        /// <c>Set</c>). Uses <c>Json.ParseString</c> so any JSON type (number,
        /// bool, string, object, array) round-trips into the matching Variant type.
        /// A bare token that is not valid JSON on its own (e.g. a color string
        /// "1,0,0,1" with no surrounding quotes) is wrapped in quotes first so a
        /// string property still lands. Mirrors the P16.1 settings pack's
        /// <c>ParseValueToken</c>.
        /// </summary>
        static Variant ParseValueToken(string? valueRaw)
        {
            if (string.IsNullOrEmpty(valueRaw)) return default;

            var parseErr = Godot.Json.ParseString(valueRaw, out var parsed);
            if (parseErr == Error.Ok) return parsed;

            // The raw token was not valid JSON on its own (e.g. a color "r,g,b,a" the
            // agent sent unquoted). Treat it as a string literal so a string property
            // still lands rather than rejecting the whole patch.
            var quoted = "\"" + valueRaw!.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            var err2 = Godot.Json.ParseString(quoted, out var parsedString);
            if (err2 == Error.Ok) return parsedString;

            // Last resort: store the literal string.
            return Variant.From(valueRaw);
        }

        /// <summary>
        /// Load a material from a <c>res://</c> or <c>uid://</c> path. Returns the
        /// material on success, or a structured failure
        /// (<c>resource_not_found</c> / <c>resource_load_failed</c> /
        /// <c>wrong_resource_type</c>) via <paramref name="error"/>.
        /// </summary>
        static bool TryLoadMaterial(string rawPath, out Material material, out ToolDispatchResult error)
        {
            material = null!;
            error = null!;

            if (!NormalizeResPath(rawPath, out var resPath, out var normError))
            {
                error = ToolDispatchResult.Fail("invalid_path", normError);
                return false;
            }

            if (!ResourceLoader.Exists(resPath))
            {
                error = ToolDispatchResult.Fail(
                    "resource_not_found",
                    $"No resource exists at '{resPath}'.");
                return false;
            }

            Material? loaded;
            try
            {
                loaded = ResourceLoader.Load<Material>(resPath);
            }
            catch (System.Exception e)
            {
                error = ToolDispatchResult.Fail(
                    "resource_load_failed",
                    $"Failed to load material at '{resPath}': {e.Message}");
                return false;
            }

            if (loaded == null)
            {
                error = ToolDispatchResult.Fail(
                    "wrong_resource_type",
                    $"'{resPath}' loaded but is not a Material.");
                return false;
            }

            material = loaded;
            return true;
        }

        /// <summary>
        /// Normalize a <c>res://</c> or <c>uid://</c> identifier to a canonical
        /// <c>res://</c> path. A <c>uid://</c> is resolved to its path via
        /// <c>ResourceUid</c>.
        /// </summary>
        static bool NormalizeResPath(string rawPath, out string resPath, out string? error)
        {
            if (ResourcePathNormalizer.IsUid(rawPath))
            {
                var resolved = ResolveUidToPath(rawPath);
                if (resolved == null)
                {
                    resPath = rawPath;
                    error = $"uid '{rawPath}' does not resolve to any resource.";
                    return false;
                }
                resPath = resolved;
                error = null;
                return true;
            }

            return ResourcePathNormalizer.TryRequireResFilePath(rawPath, out resPath, out error);
        }

        /// <summary>uid:// text → res:// path, or null when the uid is unknown.</summary>
        static string? ResolveUidToPath(string uidText)
        {
            try
            {
                var uidId = ResourceUid.TextToId(uidText);
                if (uidId == ResourceUid.InvalidId || !ResourceUid.HasId(uidId)) return null;
                var path = ResourceUid.GetIdPath(uidId);
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when <paramref name="pathsHint"/> contains <paramref name="path"/>.</summary>
        static bool HintContains(string[]? pathsHint, string path)
        {
            if (pathsHint == null) return false;
            foreach (var p in pathsHint)
            {
                if (p == path) return true;
            }
            return false;
        }

        /// <summary>
        /// True when <paramref name="obj"/> has a property named
        /// <paramref name="name"/>. Walks <c>GetPropertyList()</c> (the same surface
        /// <c>Set</c> consults) and honors quoted-string escapes. A non-existent
        /// property surfaces <c>property_not_found</c> from the caller.
        /// </summary>
        static bool HasProperty(GodotObject obj, string name)
        {
            try
            {
                Godot.Collections.Array props;
                props = obj.GetPropertyList();
                foreach (var propVariant in props)
                {
                    var propDict = propVariant.As<Godot.Collections.Dictionary>();
                    if (propDict == null) continue;
                    var nameVar = propDict["name"];
                    if (nameVar.VariantType != Variant.Type.String) continue;
                    if (nameVar.AsString() == name) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Validate that <paramref name="typeClassName"/> is an instantiable
        /// <c>Resource</c> subclass. Lifted from the P4.2 resource_create handler
        /// (the same validation every ClassDB.Instantiate path needs).
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
    }
}
#endif
